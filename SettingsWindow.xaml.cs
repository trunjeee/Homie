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

/// <summary>Подключение к умному дому (Client ID и токен) и свои горячие клавиши.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HomeViewModel _home;
    private readonly Func<IReadOnlyList<HotkeyBinding>> _applyHotkeys; // вернёт сочетания, которые занял кто-то другой
    private readonly ObservableCollection<HotkeyBinding> _hotkeys;
    private uint _capturedModifiers, _capturedKey;

    public SettingsWindow(AppSettings settings, HomeViewModel home, Func<IReadOnlyList<HotkeyBinding>> applyHotkeys)
    {
        _settings = settings;
        _home = home;
        _applyHotkeys = applyHotkeys;
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

    // ---------- горячие клавиши ----------

    private async Task LoadTargetsAsync()
    {
        if (_home.Data is null && _home.IsSignedIn) await _home.RefreshAsync();
        BuildRoomOrder();
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
