using Homie.Native;
using Homie.Services;
using Homie.ViewModels;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Homie;

/// <summary>
/// Невидимое окно-хозяин: домик в трее (клик — панель, правый клик — сценарии),
/// глобальные горячие клавиши и уведомления.
/// </summary>
public sealed partial class HostWindow : Window
{
    private const int HotkeyIdBase = 0x4000;

    private readonly nint _hwnd;
    private readonly Win32.SUBCLASSPROC _wndProc;
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly HomeViewModel _home;
    private readonly TrayIcon _tray;
    private readonly List<HotkeyBinding> _registered = [];
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private DateTime _flyoutClosedAt;

    public HostWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _wndProc = WndProc;
        Win32.SetWindowSubclass(_hwnd, _wndProc, 1, 0);

        _home = new HomeViewModel(DispatcherQueue, _settings);
        _home.Notify += text => _tray?.ShowNotification("Homie", text);

        _tray = new TrayIcon(_hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "home.ico"), "Homie", BuildMenu);
        _tray.LeftClick += ToggleFlyout;

        ApplyHotkeys();
        _ = _home.RefreshAsync(); // чтобы меню со сценариями было готово сразу

        // Первый запуск без подключения — сразу открываем настройки.
        if (SettingsStore.LoadToken() is null) OpenSettings();
    }

    // ---------- панель и настройки ----------

    private void ToggleFlyout()
    {
        if (_flyout is not null)
        {
            _flyout.Close();
            return;
        }
        // Клик по иконке сначала закрывает панель (она теряет фокус), не открываем её тут же снова.
        if (DateTime.Now - _flyoutClosedAt < TimeSpan.FromMilliseconds(300)) return;

        _flyout = new FlyoutWindow(_home, OpenSettings);
        _flyout.Closed += (_, _) => { _flyout = null; _flyoutClosedAt = DateTime.Now; };
        _flyout.Activate();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _home, ApplyHotkeys);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    // ---------- меню трея ----------

    private IReadOnlyList<TrayMenuItem> BuildMenu()
    {
        var items = new List<TrayMenuItem>();
        if (!_home.IsSignedIn)
        {
            items.Add(new("Подключить умный дом…", OpenSettings));
        }
        else if (_home.Data is { } data)
        {
            var scenarios = data.Scenarios.Where(s => s.IsActive).OrderBy(s => s.Name).ToList();
            if (scenarios.Count == 0) items.Add(TrayMenuItem.Info("Сценариев пока нет"));
            foreach (var s in scenarios.Take(15)) items.Add(new($"▶  {s.Name}", () => _ = _home.RunScenarioAsync(s)));
        }
        else
        {
            items.Add(TrayMenuItem.Info("Загружаю…"));
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new("Открыть панель", ToggleFlyout));
        items.Add(new("Настройки…", OpenSettings));
        items.Add(TrayMenuItem.Separator);
        items.Add(new("Запускать вместе с Windows", () => StartupService.SetEnabled(!StartupService.IsEnabled), StartupService.IsEnabled));
        items.Add(new("Выход", Quit));
        return items;
    }

    // ---------- горячие клавиши ----------

    /// <summary>Перерегистрирует все сочетания; возвращает те, что заняты другой программой.</summary>
    private IReadOnlyList<HotkeyBinding> ApplyHotkeys()
    {
        for (int i = 0; i < _registered.Count; i++) Win32.UnregisterHotKey(_hwnd, HotkeyIdBase + i);
        _registered.Clear();

        var failed = new List<HotkeyBinding>();
        foreach (var binding in _settings.Hotkeys)
        {
            int id = HotkeyIdBase + _registered.Count;
            if (Win32.RegisterHotKey(_hwnd, id, binding.Modifiers | Win32.MOD_NOREPEAT, binding.Key)) _registered.Add(binding);
            else failed.Add(binding);
        }
        return failed;
    }

    private void OnHotkey(int id)
    {
        int index = id - HotkeyIdBase;
        if (index < 0 || index >= _registered.Count) return;
        var binding = _registered[index];
        _ = binding.Target == HotkeyTarget.Scenario
            ? _home.RunScenarioByIdAsync(binding.TargetId)
            : _home.ToggleDeviceAsync(binding.TargetId);
    }

    // ---------- окно ----------

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            OnHotkey((int)wParam);
            return 0;
        }
        return _tray?.HandleMessage(msg, wParam, lParam) == true ? 0 : Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void Quit()
    {
        for (int i = 0; i < _registered.Count; i++) Win32.UnregisterHotKey(_hwnd, HotkeyIdBase + i);
        _tray.Dispose();
        _home.Dispose();
        Win32.RemoveWindowSubclass(_hwnd, _wndProc, 1);
        Application.Current.Exit();
    }
}
