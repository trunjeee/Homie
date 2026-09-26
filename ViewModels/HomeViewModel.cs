using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Homie.Services;
using Microsoft.UI.Dispatching;

namespace Homie.ViewModels;

/// <summary>Устройство или группа в панели: переключатель, яркость, показания датчиков.</summary>
public sealed partial class DeviceItem : ObservableObject
{
    private readonly HomeViewModel _home;
    private bool _syncing;
    private CancellationTokenSource? _brightnessDebounce;

    public Device Device { get; private set; }
    public string Name => Device.Name;
    public bool HasOnOff => Device.OnOff is not null;
    public bool HasBrightness => Device.Range("brightness") is not null;

    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial string Subtitle { get; set; } = "";

    public DeviceItem(HomeViewModel home, Device device)
    {
        _home = home;
        Device = device;
        Update(device);
    }

    /// <summary>Иконка Segoe Fluent по типу устройства Яндекса.</summary>
    public string Glyph => Device.Type switch
    {
        var t when t.StartsWith("devices.types.light") => "",          // свет (солнце яркости)
        var t when t.StartsWith("devices.types.socket") => "",         // розетка — кнопка питания
        var t when t.StartsWith("devices.types.switch") => "",
        var t when t.StartsWith("devices.types.media_device") => "",   // ТВ
        var t when t.StartsWith("devices.types.thermostat") => "",     // климат
        var t when t.StartsWith("devices.types.sensor") => "",         // датчик
        _ => Device.IsGroup ? "" : "",                            // группа / дом
    };

    public void Update(Device device)
    {
        Device = device;
        _syncing = true; // обновление с сервера не должно отправлять команды обратно
        IsOn = device.OnOff?.OnValue ?? false;
        Brightness = device.Range("brightness")?.NumberValue ?? Brightness;
        _syncing = false;
        Subtitle = BuildSubtitle(device);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) _ = _home.SetOnOffAsync(this, value);
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_syncing) return;
        // Ползунок шлёт команду, только когда его отпустили/остановили (0,4 с без изменений).
        _brightnessDebounce?.Cancel();
        var cts = _brightnessDebounce = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(_ => _home.Dispatch(() => _ = _home.SetBrightnessAsync(this, value)),
            cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private static string BuildSubtitle(Device d)
    {
        var parts = new List<string>();
        foreach (var p in d.Properties.Where(p => p.Value is not null))
        {
            string v = p.Value!.Value.ToString("0.#", CultureInfo.InvariantCulture);
            parts.Add(p.Instance switch
            {
                "temperature" => $"{v} °C",
                "humidity" => $"{v}%",
                "power" => $"{v} Вт",
                "battery_level" => $"🔋 {v}%",
                "co2_level" => $"CO₂ {v}",
                _ => v,
            });
        }
        if (d.Range("brightness")?.NumberValue is double b && d.OnOff?.OnValue == true) parts.Insert(0, $"{b:0}%");
        if (d.Range("temperature")?.NumberValue is double t) parts.Insert(0, $"→ {t:0} °C");
        return string.Join(" · ", parts);
    }
}

public sealed record RoomGroup(string Name, ObservableCollection<DeviceItem> Devices);

/// <summary>Состояние дома для панели, меню трея и горячих клавиш.</summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;
    private readonly SmartHomeApi _api;
    private HomeData? _data;

    public ObservableCollection<Scenario> Scenarios { get; } = [];
    public ObservableCollection<RoomGroup> Rooms { get; } = [];
    public ObservableCollection<Household> Households { get; } = [];

    [ObservableProperty] public partial bool IsSignedIn { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial Household? SelectedHousehold { get; set; }

    public bool HasMultipleHouseholds => Households.Count > 1;
    public HomeData? Data => _data;

    /// <summary>Что-то сообщить пользователю (уведомлением трея).</summary>
    public event Action<string>? Notify;

    public HomeViewModel(DispatcherQueue dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _api = new SmartHomeApi(SettingsStore.LoadToken);
        IsSignedIn = SettingsStore.LoadToken() is not null;
    }

    public void Dispatch(Action action) => _dispatcher.TryEnqueue(() => action());

    public async Task RefreshAsync()
    {
        if (SettingsStore.LoadToken() is null)
        {
            IsSignedIn = false;
            return;
        }

        IsLoading = true;
        try
        {
            _data = await _api.GetHomeAsync();
            IsSignedIn = true;
            ErrorText = null;
            Apply(_data);
        }
        catch (UnauthorizedException)
        {
            IsSignedIn = false;
            ErrorText = "Токен истёк или недействителен — войди заново в настройках";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            ErrorText = "Не удалось связаться с умным домом. Проверь интернет";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(HomeData data)
    {
        Households.Clear();
        foreach (var h in data.Households) Households.Add(h);
        OnPropertyChanged(nameof(HasMultipleHouseholds));

        var selected = data.Households.FirstOrDefault(h => h.Id == _settings.HouseholdId) ?? data.Households.FirstOrDefault();
        if (SelectedHousehold?.Id != selected?.Id) SelectedHousehold = selected;
        else BuildRooms();

        Scenarios.Clear();
        foreach (var s in data.Scenarios.Where(s => s.IsActive).OrderBy(s => s.Name)) Scenarios.Add(s);
    }

    partial void OnSelectedHouseholdChanged(Household? value)
    {
        if (value is not null && value.Id != _settings.HouseholdId)
        {
            _settings.HouseholdId = value.Id;
            SettingsStore.Save(_settings);
        }
        BuildRooms();
    }

    /// <summary>Устройства выбранного дома по комнатам; уже созданные строки обновляем, а не пересоздаём.</summary>
    private void BuildRooms()
    {
        if (_data is null) return;
        var existing = Rooms.SelectMany(r => r.Devices).ToDictionary(d => d.Device.Id);
        string? household = SelectedHousehold?.Id;

        var devices = _data.Devices
            .Where(d => household is null || d.HouseholdId is null || d.HouseholdId == household)
            .Where(d => d.OnOff is not null || d.Properties.Count > 0) // остальное (например, колонки) панели не нужно
            .ToList();

        var groups = devices
            .GroupBy(d => d.IsGroup ? "Группы" : _data.Rooms.FirstOrDefault(r => r.Id == d.RoomId)?.Name ?? "Без комнаты")
            .OrderBy(g => g.Key == "Группы" ? 1 : g.Key == "Без комнаты" ? 2 : 0).ThenBy(g => g.Key);

        Rooms.Clear();
        foreach (var g in groups)
        {
            var items = new ObservableCollection<DeviceItem>();
            foreach (var d in g.OrderBy(d => d.Name))
            {
                if (existing.TryGetValue(d.Id, out var item)) item.Update(d);
                else item = new DeviceItem(this, d);
                items.Add(item);
            }
            Rooms.Add(new RoomGroup(g.Key, items));
        }
    }

    // ---------- действия ----------

    public async Task SetOnOffAsync(DeviceItem item, bool on)
    {
        await RunAsync(() => _api.SetOnOffAsync(item.Device, on), $"{item.Name}: не удалось {(on ? "включить" : "выключить")}");
        await RefreshSoonAsync();
    }

    public async Task SetBrightnessAsync(DeviceItem item, double value)
    {
        var range = item.Device.Range("brightness");
        double clamped = range is null ? value : Math.Clamp(Math.Round(value / range.Precision) * range.Precision, range.Min, range.Max);
        await RunAsync(() => _api.SetRangeAsync(item.Device, "brightness", clamped), $"{item.Name}: не удалось изменить яркость");
        await RefreshSoonAsync();
    }

    public async Task RunScenarioAsync(Scenario scenario)
    {
        if (await RunAsync(() => _api.RunScenarioAsync(scenario.Id), $"Сценарий «{scenario.Name}» не запустился"))
            Notify?.Invoke($"Сценарий «{scenario.Name}» запущен");
    }

    /// <summary>Для горячих клавиш: переключить устройство по ID (свежее состояние берём с сервера).</summary>
    public async Task ToggleDeviceAsync(string deviceId)
    {
        await RefreshAsync();
        var device = _data?.Devices.FirstOrDefault(d => d.Id == deviceId);
        if (device?.OnOff is null)
        {
            Notify?.Invoke("Устройство не найдено — проверь горячие клавиши в настройках");
            return;
        }
        bool on = !(device.OnOff.OnValue ?? false);
        if (await RunAsync(() => _api.SetOnOffAsync(device, on), $"{device.Name}: не удалось переключить"))
            Notify?.Invoke($"{device.Name}: {(on ? "включено" : "выключено")}");
        await RefreshSoonAsync();
    }

    public async Task RunScenarioByIdAsync(string scenarioId)
    {
        var scenario = _data?.Scenarios.FirstOrDefault(s => s.Id == scenarioId);
        if (scenario is null) { await RefreshAsync(); scenario = _data?.Scenarios.FirstOrDefault(s => s.Id == scenarioId); }
        if (scenario is null) Notify?.Invoke("Сценарий не найден — проверь горячие клавиши в настройках");
        else await RunScenarioAsync(scenario);
    }

    private async Task<bool> RunAsync(Func<Task> action, string errorText)
    {
        try
        {
            await action();
            return true;
        }
        catch (UnauthorizedException)
        {
            IsSignedIn = false;
            ErrorText = "Токен истёк или недействителен — войди заново в настройках";
            Notify?.Invoke(ErrorText);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Notify?.Invoke(errorText);
        }
        return false;
    }

    /// <summary>Устройства применяют команду не мгновенно — перечитываем состояние чуть позже.</summary>
    private async Task RefreshSoonAsync()
    {
        await Task.Delay(1200);
        await RefreshAsync();
    }

    public void Dispose() => _api.Dispose();
}
