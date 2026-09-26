using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Homie.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Homie.ViewModels;

/// <summary>Устройство или группа в панели: переключатель, яркость, цвет, показания датчиков.</summary>
public sealed partial class DeviceItem : ObservableObject
{
    private readonly HomeViewModel _home;
    private bool _syncing;
    private CancellationTokenSource? _brightnessDebounce;

    public Device Device { get; private set; }
    public string Name => Device.Name;
    public bool HasOnOff => Device.OnOff is not null;
    public bool HasBrightness => Device.Range("brightness") is not null;
    public bool HasColor => Device.Color?.SupportsColor == true;
    public bool HasWhite => Device.Color?.SupportsWhite == true;
    public bool HasColorOrWhite => HasColor || HasWhite;
    /// <summary>Есть что показать в «подробностях» плитки (правый клик).</summary>
    public bool HasDetails => HasBrightness || HasColorOrWhite;

    [ObservableProperty] public partial bool IsOn { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; }
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial string StateText { get; set; } = "";
    [ObservableProperty] public partial Brush? ColorBrush { get; set; }

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
        StateText = BuildStateText(device);
        var color = CurrentColor(device);
        ColorBrush = color is { } c ? new SolidColorBrush(c) : null;
    }

    /// <summary>Клик по плитке: включить/выключить.</summary>
    public void Toggle()
    {
        if (HasOnOff) IsOn = !IsOn;
    }

    public Task SetColorAsync(Color color) => _home.SetColorAsync(this, color);
    public Task SetWhiteAsync(int kelvin) => _home.SetWhiteAsync(this, kelvin);

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) _ = _home.SetOnOffAsync(this, value);
    }

    partial void OnBrightnessChanged(double value)
    {
        if (_syncing) return;
        // Ползунок шлёт команду, только когда его остановили (0,4 с без изменений).
        _brightnessDebounce?.Cancel();
        var cts = _brightnessDebounce = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(_ => _home.Dispatch(() => _ = _home.SetBrightnessAsync(this, value)),
            cts.Token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private static List<string> Readings(Device d) => d.Properties.Where(p => p.Value is not null).Select(p =>
    {
        string v = p.Value!.Value.ToString("0.#", CultureInfo.InvariantCulture);
        return p.Instance switch
        {
            "temperature" => $"{v} °C",
            "humidity" => $"{v}%",
            "power" => $"{v} Вт",
            "battery_level" => $"🔋 {v}%",
            "co2_level" => $"CO₂ {v}",
            _ => v,
        };
    }).ToList();

    private static string BuildSubtitle(Device d)
    {
        var parts = Readings(d);
        if (d.Range("brightness")?.NumberValue is double b && d.OnOff?.OnValue == true) parts.Insert(0, $"{b:0}%");
        if (d.Range("temperature")?.NumberValue is double t) parts.Insert(0, $"→ {t:0} °C");
        return string.Join(" · ", parts);
    }

    /// <summary>Строка на плитке: «Вкл · 70%», «Выкл» или показания датчика.</summary>
    private static string BuildStateText(Device d)
    {
        if (d.OnOff?.OnValue is bool on)
        {
            if (!on) return "Выкл";
            return d.Range("brightness")?.NumberValue is double b ? $"Вкл · {b:0}%" : "Вкл";
        }
        return string.Join(" · ", Readings(d));
    }

    /// <summary>Текущий цвет лампы для кружка: hsv, rgb или оттенок белого.</summary>
    private static Color? CurrentColor(Device d)
    {
        if (d.Color?.Value is not { } v) return null;
        switch (d.Color.Instance)
        {
            case "rgb" when v.ValueKind == JsonValueKind.Number:
                int rgb = v.GetInt32();
                return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            case "hsv" when v.ValueKind == JsonValueKind.Object:
                double h = v.TryGetProperty("h", out var hh) ? hh.GetDouble() : 0;
                double s = v.TryGetProperty("s", out var ss) ? ss.GetDouble() / 100 : 0;
                return FromHsv(h, s, 1);
            case "temperature_k" when v.ValueKind == JsonValueKind.Number:
                return WhiteFor(v.GetInt32());
            default:
                return null;
        }
    }

    public static Color WhiteFor(int kelvin) => kelvin switch
    {
        <= 3200 => Color.FromArgb(255, 0xFF, 0xD6, 0x9A), // тёплый
        <= 5000 => Color.FromArgb(255, 0xFF, 0xF1, 0xDE), // нейтральный
        _ => Color.FromArgb(255, 0xE3, 0xEE, 0xFF),       // холодный
    };

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

public sealed record RoomGroup(string Key, string Name, ObservableCollection<DeviceItem> Devices);

/// <summary>Комната в настройках порядка: ключ, название и видна ли она.</summary>
public sealed record RoomEntry(string Key, string Name, bool IsHidden);

/// <summary>Состояние дома для панели, меню трея и горячих клавиш.</summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    public const string GroupsKey = "__groups", NoRoomKey = "__none";

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
    [ObservableProperty] public partial bool TilesView { get; set; }

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
        TilesView = settings.TilesView;
    }

    public void Dispatch(Action action) => _dispatcher.TryEnqueue(() => action());

    partial void OnTilesViewChanged(bool value)
    {
        if (_settings.TilesView == value) return;
        _settings.TilesView = value;
        SettingsStore.Save(_settings);
    }

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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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
        // Списки меняем, только если они правда изменились — иначе панель перерисовывается и скролл прыгает наверх.
        if (!Households.Select(h => h.Id).SequenceEqual(data.Households.Select(h => h.Id)))
        {
            Households.Clear();
            foreach (var h in data.Households) Households.Add(h);
            OnPropertyChanged(nameof(HasMultipleHouseholds));
        }

        // По умолчанию — дом, где больше всего устройств (а не служебный «Портативный»).
        var selected = data.Households.FirstOrDefault(h => h.Id == _settings.HouseholdId)
            ?? data.Households.OrderByDescending(h => data.Devices.Count(d => d.HouseholdId == h.Id)).FirstOrDefault();
        if (SelectedHousehold?.Id != selected?.Id) SelectedHousehold = Households.FirstOrDefault(h => h.Id == selected?.Id);
        else BuildRooms();

        var scenarios = data.Scenarios.Where(s => s.IsActive).OrderBy(s => s.Name).ToList();
        if (!Scenarios.Select(s => (s.Id, s.Name)).SequenceEqual(scenarios.Select(s => (s.Id, s.Name))))
        {
            Scenarios.Clear();
            foreach (var s in scenarios) Scenarios.Add(s);
        }
    }

    partial void OnSelectedHouseholdChanged(Household? value)
    {
        if (value is not null && value.Id != _settings.HouseholdId)
        {
            _settings.HouseholdId = value.Id;
            SettingsStore.Save(_settings);
        }
        BuildRooms(force: true);
    }

    private static string KeyOf(Device d) => d.IsGroup ? GroupsKey : d.RoomId ?? NoRoomKey;

    private string NameOf(string key) => key switch
    {
        GroupsKey => "Группы",
        NoRoomKey => "Без комнаты",
        _ => _data?.Rooms.FirstOrDefault(r => r.Id == key)?.Name ?? "Без комнаты",
    };

    /// <summary>Устройства выбранного дома, которые стоит показывать (есть вкл/выкл или показания).</summary>
    private List<Device> VisibleDevices()
    {
        if (_data is null) return [];
        string? household = SelectedHousehold?.Id;
        return _data.Devices
            .Where(d => household is null || d.HouseholdId is null || d.HouseholdId == household)
            .Where(d => d.OnOff is not null || d.Properties.Count > 0)
            .ToList();
    }

    /// <summary>Комнаты в пользовательском порядке: сначала по RoomOrder, потом остальные по алфавиту.</summary>
    private List<string> OrderedKeys(IEnumerable<string> keys) => keys
        .OrderBy(k => _settings.RoomOrder.IndexOf(k) is var i && i >= 0 ? i : int.MaxValue)
        .ThenBy(k => k == GroupsKey ? 1 : k == NoRoomKey ? 2 : 0)
        .ThenBy(NameOf)
        .ToList();

    /// <summary>Строит комнаты. Если состав не изменился — только обновляет состояния строк/плиток на месте.</summary>
    public void BuildRooms(bool force = false)
    {
        if (_data is null) return;
        var devices = VisibleDevices();
        var keys = OrderedKeys(devices.Select(KeyOf).Distinct()).Where(k => !_settings.HiddenRooms.Contains(k)).ToList();
        var layout = keys.Select(k => (k, devices.Where(d => KeyOf(d) == k).OrderBy(d => d.Name).Select(d => d.Id).ToList())).ToList();

        bool sameLayout = !force && Rooms.Count == layout.Count && Rooms.Zip(layout).All(p =>
            p.First.Key == p.Second.k && p.First.Devices.Select(d => d.Device.Id).SequenceEqual(p.Second.Item2));

        var byId = devices.ToDictionary(d => d.Id);
        if (sameLayout)
        {
            foreach (var item in Rooms.SelectMany(r => r.Devices)) item.Update(byId[item.Device.Id]);
            return;
        }

        var existing = Rooms.SelectMany(r => r.Devices).ToDictionary(d => d.Device.Id);
        Rooms.Clear();
        foreach (var (key, ids) in layout)
        {
            var items = new ObservableCollection<DeviceItem>();
            foreach (var id in ids)
            {
                if (existing.TryGetValue(id, out var item)) item.Update(byId[id]);
                else item = new DeviceItem(this, byId[id]);
                items.Add(item);
            }
            Rooms.Add(new RoomGroup(key, NameOf(key), items));
        }
    }

    // ---------- порядок и видимость комнат (настройки) ----------

    public List<RoomEntry> GetRoomEntries()
    {
        var keys = OrderedKeys(VisibleDevices().Select(KeyOf).Distinct());
        return keys.Select(k => new RoomEntry(k, NameOf(k), _settings.HiddenRooms.Contains(k))).ToList();
    }

    public void MoveRoom(string key, int delta)
    {
        var order = GetRoomEntries().Select(r => r.Key).ToList();
        int i = order.IndexOf(key), j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _settings.RoomOrder = order;
        SettingsStore.Save(_settings);
        BuildRooms(force: true);
    }

    public void SetRoomHidden(string key, bool hidden)
    {
        _settings.HiddenRooms.Remove(key);
        if (hidden) _settings.HiddenRooms.Add(key);
        SettingsStore.Save(_settings);
        BuildRooms(force: true);
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

    public async Task SetColorAsync(DeviceItem item, Color color)
    {
        await RunAsync(() => _api.SetColorAsync(item.Device, color), $"{item.Name}: не удалось сменить цвет");
        await RefreshSoonAsync();
    }

    public async Task SetWhiteAsync(DeviceItem item, int kelvin)
    {
        await RunAsync(() => _api.SetWhiteAsync(item.Device, kelvin), $"{item.Name}: не удалось сменить оттенок");
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
