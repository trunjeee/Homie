using System.Collections.ObjectModel;
using System.Diagnostics;
using Homie.Native;
using Homie.Services;
using Homie.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Homie;

/// <summary>Вариант для списка «Что делать» у горячей клавиши.</summary>
public sealed record TargetOption(string Title, HotkeyTarget Target, string Id, string Name);

/// <summary>Вариант для списка действий своей голосовой команды.</summary>
public sealed record ActionOption(PcAction Action, string Title);

/// <summary>Подключение к умному дому (Client ID и токен) и свои горячие клавиши.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HomeViewModel _home;
    private readonly Func<IReadOnlyList<HotkeyBinding>> _applyHotkeys; // вернёт сочетания, которые занял кто-то другой
    private readonly ObservableCollection<HotkeyBinding> _hotkeys;
    private readonly VoiceService _voice;
    private readonly Func<Task<string?>> _applyVoice;
    private uint _capturedModifiers, _capturedKey;
    private bool _loadingVoice;
    private CancellationTokenSource? _download;

    public SettingsWindow(AppSettings settings, HomeViewModel home, Func<IReadOnlyList<HotkeyBinding>> applyHotkeys,
        VoiceService voice, Func<Task<string?>> applyVoice)
    {
        _settings = settings;
        _home = home;
        _applyHotkeys = applyHotkeys;
        _voice = voice;
        _applyVoice = applyVoice;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "home.ico"));
        nint hwnd = WindowNative.GetWindowHandle(this);
        double scale = Win32.GetDpiForWindow(hwnd) / 96d;
        var area = DisplayArea.Primary.WorkArea;
        int w = (int)(600 * scale), h = (int)Math.Min(760 * scale, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));

        ClientIdBox.Text = settings.ClientId;
        _hotkeys = new ObservableCollection<HotkeyBinding>(settings.Hotkeys);
        HotkeyList.ItemsSource = _hotkeys;
        UpdateStatus();
        LoadVoice();
        _ = LoadTargetsAsync();
    }

    // ---------- подключение ----------

    private void ClientIdBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        _settings.ClientId = ClientIdBox.Text.Trim();
        SettingsStore.Save(_settings);
        SignInButton.IsEnabled = _settings.ClientId.Length > 0;
    }

    private void SignIn_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(SettingsStore.AuthorizeUrl(_settings.ClientId)) { UseShellExecute = true });

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        SettingsStore.SaveToken(token);
        TokenBox.Password = "";
        StatusText.Text = "Проверяю…";
        await _home.RefreshAsync();
        UpdateStatus();
        await LoadTargetsAsync();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.DeleteToken();
        _home.IsSignedIn = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        SignInButton.IsEnabled = _settings.ClientId.Length > 0;
        SignOutButton.Visibility = _home.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _home.IsSignedIn && _home.Data is { } data
            ? $"✓ Подключено: устройств — {data.Devices.Count(d => !d.IsGroup)}, сценариев — {data.Scenarios.Count}"
            : _home.IsSignedIn ? "✓ Подключено" : _home.ErrorText ?? "Не подключено";
    }

    // ---------- комнаты: порядок и видимость ----------

    private void BuildRoomOrder()
    {
        RoomOrderList.Children.Clear();
        var rooms = _home.GetRoomEntries();
        if (rooms.Count == 0)
        {
            RoomOrderList.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = "Появятся после подключения", FontSize = 12, Opacity = 0.6 });
            return;
        }

        for (int i = 0; i < rooms.Count; i++)
        {
            var room = rooms[i];
            var row = new Microsoft.UI.Xaml.Controls.Grid
            {
                Padding = new Thickness(12, 4, 4, 4),
                ColumnSpacing = 4,
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            };
            foreach (var w in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new Microsoft.UI.Xaml.Controls.ColumnDefinition { Width = w });

            var name = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = room.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Opacity = room.IsHidden ? 0.45 : 1,
            };
            row.Children.Add(name);
            row.Children.Add(Arrow("", "Выше", 1, i > 0, () => _home.MoveRoom(room.Key, -1)));
            row.Children.Add(Arrow("", "Ниже", 2, i < rooms.Count - 1, () => _home.MoveRoom(room.Key, +1)));

            var visible = new Microsoft.UI.Xaml.Controls.ToggleSwitch
            {
                IsOn = !room.IsHidden, OnContent = "", OffContent = "", MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(visible, $"Показывать {room.Name}");
            visible.Toggled += (_, _) => { _home.SetRoomHidden(room.Key, !visible.IsOn); name.Opacity = visible.IsOn ? 1 : 0.45; };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(visible, 3);
            row.Children.Add(visible);

            RoomOrderList.Children.Add(row);
        }

        Microsoft.UI.Xaml.Controls.Button Arrow(string glyph, string label, int column, bool enabled, Action move)
        {
            var b = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0), IsEnabled = enabled,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = glyph, FontSize = 12 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => { move(); BuildRoomOrder(); };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(b, column);
            return b;
        }
    }

    // ---------- голос ----------

    private void LoadVoice()
    {
        _loadingVoice = true;
        VoiceModeBox.SelectedIndex = (int)_settings.VoiceMode;
        VoiceKeyBox.Text = _settings.VoiceKey == 0 ? "" : HotkeyText.Format(_settings.VoiceModifiers, _settings.VoiceKey);
        WakeWordsBox.Text = _settings.WakeWords;
        _loadingVoice = false;
        UpdateVoicePanels();
        UpdateModelStatus();
        BuildShortcuts();

        // Что слышит микрофон в режиме ожидания — чтобы подобрать варианты «Хоуми».
        Action<string> heard = text => HeardText.Text = $"Услышал: «{text}»";
        _voice.Heard += heard;
        Closed += (_, _) =>
        {
            _voice.Heard -= heard;
            _download?.Cancel();
        };
    }

    private void UpdateVoicePanels()
    {
        var mode = _settings.VoiceMode;
        VoiceKeyPanel.Visibility = mode is VoiceMode.PushToTalk or VoiceMode.Both ? Visibility.Visible : Visibility.Collapsed;
        WakePanel.Visibility = mode is VoiceMode.WakeWord or VoiceMode.Both ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateModelStatus()
    {
        bool installed = VoiceService.IsModelInstalled;
        ModelStatus.Text = installed ? "✓ Модель распознавания речи установлена" : "Нужна модель распознавания русской речи (~45 МБ, скачивается один раз)";
        ModelButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        ModelButton.IsEnabled = false;
        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.Value = 0;
        ModelStatus.Text = "Скачиваю модель…";
        _download = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                ModelProgress.Value = p;
                ModelStatus.Text = p < 1 ? $"Скачиваю модель… {p:P0}" : "Распаковываю…";
            });
            await VoiceService.DownloadModelAsync(progress, _download.Token);
            UpdateModelStatus();
            await ApplyVoiceAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ModelStatus.Text = "Не удалось скачать: " + ex.Message;
        }
        finally
        {
            ModelButton.IsEnabled = true;
            ModelProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void VoiceMode_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingVoice || VoiceModeBox.SelectedIndex < 0) return;
        _settings.VoiceMode = (VoiceMode)VoiceModeBox.SelectedIndex;
        SettingsStore.Save(_settings);
        UpdateVoicePanels();
        await ApplyVoiceAsync();
    }

    private async void VoiceKeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (HotkeyText.IsModifier(e.Key)) return;
        uint mods = CurrentModifiers();
        _settings.VoiceModifiers = mods;
        _settings.VoiceKey = (uint)e.Key;
        SettingsStore.Save(_settings);
        VoiceKeyBox.Text = HotkeyText.Format(mods, (uint)e.Key);
        string? error = await ApplyVoiceAsync();
        if (error is null && mods == 0 && e.Key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9 or VirtualKey.Space)
            VoiceError.Text = "Эта клавиша перестанет печатать в других программах — лучше выбрать F13–F24, Pause или сочетание";
    }

    private void PcRoomBox_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingVoice || PcRoomBox.SelectedItem is not Room room) return;
        _settings.PcRoomId = room.Id.Length == 0 ? null : room.Id;
        SettingsStore.Save(_settings);
    }

    private async void WakeWordsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (WakeWordsBox.Text == _settings.WakeWords) return;
        _settings.WakeWords = WakeWordsBox.Text;
        SettingsStore.Save(_settings);
        await ApplyVoiceAsync();
    }

    // ---------- свои команды для компьютера ----------

    private void BuildShortcuts()
    {
        _settings.VoiceShortcuts ??= AppSettings.DefaultShortcuts();
        ShortcutList.Children.Clear();
        var actions = Enum.GetValues<PcAction>().Select(a => new ActionOption(a, PcActions.Title(a))).ToList();

        foreach (var shortcut in _settings.VoiceShortcuts)
        {
            var s = shortcut;
            var card = new Microsoft.UI.Xaml.Controls.Grid
            {
                Padding = new Thickness(12, 10, 6, 10),
                RowSpacing = 8,
                ColumnSpacing = 8,
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            };
            card.RowDefinitions.Add(new() { Height = GridLength.Auto });
            card.RowDefinitions.Add(new() { Height = GridLength.Auto });
            card.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

            var actionBox = new Microsoft.UI.Xaml.Controls.ComboBox
            {
                ItemsSource = actions, DisplayMemberPath = nameof(ActionOption.Title),
                SelectedItem = actions.First(a => a.Action == s.Action), MinWidth = 240,
            };
            var pathBox = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = s.Path, PlaceholderText = "программа, файл или ссылка (steam://, https://…)",
                MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var browse = new Microsoft.UI.Xaml.Controls.Button { Content = "Обзор…", CornerRadius = new CornerRadius(8) };
            var pathRow = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 6 };
            pathRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            pathRow.Children.Add(pathBox);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(browse, 1);
            pathRow.Children.Add(browse);

            var top = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 6 };
            top.Children.Add(actionBox);
            top.Children.Add(pathRow);
            void UpdatePath() => pathRow.Visibility = s.Action == PcAction.OpenApp ? Visibility.Visible : Visibility.Collapsed;
            UpdatePath();

            var phrases = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Text = s.Phrases, PlaceholderText = "фразы через запятую: давай поиграем, включи античит",
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = false,
            };
            Microsoft.UI.Xaml.Controls.Grid.SetRow(phrases, 1);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(phrases, 2);

            var remove = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
                Content = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = "", FontSize = 12 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "Удалить команду");
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(remove, 1);

            actionBox.SelectionChanged += (_, _) =>
            {
                if (actionBox.SelectedItem is ActionOption o) s.Action = o.Action;
                UpdatePath();
                SaveShortcuts();
            };
            pathBox.LostFocus += (_, _) => { s.Path = pathBox.Text.Trim(); SaveShortcuts(); };
            phrases.LostFocus += (_, _) => { s.Phrases = phrases.Text.Trim(); SaveShortcuts(); };
            browse.Click += async (_, _) =>
            {
                if (await PickFileAsync() is { } file)
                {
                    pathBox.Text = s.Path = file;
                    SaveShortcuts();
                }
            };
            remove.Click += (_, _) =>
            {
                _settings.VoiceShortcuts!.Remove(s);
                SaveShortcuts();
                BuildShortcuts();
            };

            card.Children.Add(top);
            card.Children.Add(remove);
            card.Children.Add(phrases);
            ShortcutList.Children.Add(card);
        }
    }

    private void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        _settings.VoiceShortcuts ??= AppSettings.DefaultShortcuts();
        _settings.VoiceShortcuts.Add(new VoiceShortcut { Action = PcAction.OpenApp });
        SaveShortcuts();
        BuildShortcuts();
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        _settings.VoiceShortcuts = AppSettings.DefaultShortcuts();
        SaveShortcuts();
        BuildShortcuts();
    }

    private void SaveShortcuts() => SettingsStore.Save(_settings);

    private async Task<string?> PickFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".lnk");
        picker.FileTypeFilter.Add(".url");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void LoadPcRooms()
    {
        if (_home.Data is not { } data) return;
        string? household = _home.SelectedHousehold?.Id;
        var rooms = new List<Room> { new("", "Не выбрана — во всём доме", null) };
        rooms.AddRange(data.Rooms.Where(r => household is null || r.HouseholdId is null || r.HouseholdId == household).OrderBy(r => r.Name));
        _loadingVoice = true;
        PcRoomBox.ItemsSource = rooms;
        PcRoomBox.SelectedItem = rooms.FirstOrDefault(r => r.Id == (_settings.PcRoomId ?? "")) ?? rooms[0];
        _loadingVoice = false;
    }

    private async Task<string?> ApplyVoiceAsync()
    {
        VoiceError.Text = "";
        string? error = await _applyVoice();
        VoiceError.Text = error ?? "";
        return error;
    }

    private static uint CurrentModifiers()
    {
        uint mods = 0;
        if (IsDown(VirtualKey.Control)) mods |= Win32.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) mods |= Win32.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) mods |= Win32.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= Win32.MOD_WIN;
        return mods;

        static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    // ---------- горячие клавиши ----------

    private async Task LoadTargetsAsync()
    {
        if (_home.Data is null && _home.IsSignedIn) await _home.RefreshAsync();
        BuildRoomOrder();
        LoadPcRooms();
        var options = new List<TargetOption>();
        if (_home.Data is { } data)
        {
            options.AddRange(data.Scenarios.OrderBy(s => s.Name)
                .Select(s => new TargetOption($"Сценарий: {s.Name}", HotkeyTarget.Scenario, s.Id, s.Name)));
            options.AddRange(data.Devices.Where(d => d.OnOff is not null).OrderBy(d => d.Name)
                .Select(d => new TargetOption($"Вкл/выкл: {d.Name}", HotkeyTarget.DeviceToggle, d.Id, d.Name)));
        }
        TargetBox.ItemsSource = options;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (HotkeyText.IsModifier(e.Key)) return;

        uint mods = 0;
        if (IsDown(VirtualKey.Control)) mods |= Win32.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) mods |= Win32.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) mods |= Win32.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= Win32.MOD_WIN;

        (_capturedModifiers, _capturedKey) = (mods, (uint)e.Key);
        HotkeyBox.Text = HotkeyText.Format(mods, (uint)e.Key);
        HotkeyError.Text = mods == 0 ? "Добавь Ctrl, Alt, Shift или Win — одиночная клавиша помешает печатать" : "";

        static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    private void AddHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedKey == 0 || _capturedModifiers == 0) { HotkeyError.Text = "Сначала нажми сочетание в поле «Сочетание»"; return; }
        if (TargetBox.SelectedItem is not TargetOption target) { HotkeyError.Text = "Выбери, что должно происходить"; return; }
        if (_hotkeys.Any(h => h.Modifiers == _capturedModifiers && h.Key == _capturedKey))
        {
            HotkeyError.Text = "Это сочетание уже занято другой командой";
            return;
        }

        var binding = new HotkeyBinding
        {
            Modifiers = _capturedModifiers, Key = _capturedKey,
            Target = target.Target, TargetId = target.Id, TargetName = target.Name,
        };
        _hotkeys.Add(binding);
        SaveHotkeys();

        var failed = _applyHotkeys();
        HotkeyError.Text = failed.Contains(binding)
            ? $"{HotkeyText.Format(binding.Modifiers, binding.Key)} уже занято другой программой — выбери другое сочетание"
            : "";
        (_capturedModifiers, _capturedKey) = (0, 0);
        HotkeyBox.Text = "";
    }

    private void RemoveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HotkeyBinding binding)
        {
            _hotkeys.Remove(binding);
            SaveHotkeys();
            _applyHotkeys();
        }
    }

    private void SaveHotkeys()
    {
        _settings.Hotkeys = [.. _hotkeys];
        SettingsStore.Save(_settings);
    }
}
