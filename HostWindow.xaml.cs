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
    private const int VoiceHotkeyId = 0x5000;

    private readonly nint _hwnd;
    private readonly Win32.SUBCLASSPROC _wndProc;
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly HomeViewModel _home;
    private readonly TrayIcon _tray;
    private readonly List<HotkeyBinding> _registered = [];
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private DateTime _flyoutClosedAt;
    private readonly VoiceService _voice;
    private VoiceWindow? _voiceWindow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _pttWatch;
    private DateTime _pttPressedAt;
    private bool _voiceKeyRegistered;
    private bool _pttStarting;
    private readonly ReplyPlayer _replies;
    private string? _voiceError;

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

        _voice = new VoiceService(DispatcherQueue);
        // Первый запуск: стандартные записанные ответы из комплекта Homie.
        if (_settings.ReplySounds.Count == 0)
        {
            _settings.ReplySounds = AppSettings.DefaultReplySounds();
            SettingsStore.Save(_settings);
        }
        _replies = new ReplyPlayer(_settings);
        _replies.PlayingChanged += playing => _voice.Suppress(playing);
        _voice.ListeningStarted += () =>
        {
            VoiceUi().ShowListening();
            if (!_pttStarting) _replies.Play(ReplyEvent.Wake); // на клавишу не отвечаем — чтобы не заглушать начало фразы
        };
        _voice.PartialText += text => _voiceWindow?.SetText(text);
        _voice.Level += level => _voiceWindow?.SetLevel(level);
        _voice.Cancelled += reason =>
        {
            _voiceWindow?.ShowCancelled(reason);
            if (reason.Length > 0) _replies.Play(ReplyEvent.NotHeard);
        };
        _voice.CommandRecognized += OnVoiceCommand;
        _pttWatch = DispatcherQueue.CreateTimer();
        _pttWatch.Interval = TimeSpan.FromMilliseconds(30);
        _pttWatch.Tick += (_, _) => WatchVoiceKey();
        _ = ApplyVoiceAsync();

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
        _settingsWindow = new SettingsWindow(_settings, _home, ApplyHotkeys, _voice, ApplyVoiceAsync, _replies);
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
        if (_voice.HasWakeWord && _voiceError is null)
            items.Add(new("Слушать «Хоуми»", () => _voice.Paused = !_voice.Paused, !_voice.Paused));
        else if (_voiceError is not null && _settings.VoiceMode != VoiceMode.Off)
            items.Add(TrayMenuItem.Info("Голос: " + _voiceError));
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

    // ---------- голос ----------

    private VoiceWindow VoiceUi()
    {
        if (_voiceWindow is null)
        {
            _voiceWindow = new VoiceWindow();
            _voiceWindow.Dismissed += () =>
            {
                _voice.CancelListening(silent: true);
                CancelCountdown();
            };
        }
        return _voiceWindow;
    }

    /// <summary>Применить режим голоса и клавишу из настроек. Вернёт текст проблемы или null.</summary>
    private async Task<string?> ApplyVoiceAsync()
    {
        if (_voiceKeyRegistered) Win32.UnregisterHotKey(_hwnd, VoiceHotkeyId);
        _voiceKeyRegistered = false;

        var mode = _settings.VoiceMode;
        string? error = await _voice.ConfigureAsync(mode, _settings.WakeWords);
        if (error is null && _voice.PushToTalkEnabled)
        {
            if (_settings.VoiceKey == 0) error = "Назначь клавишу для голоса";
            else if (!(_voiceKeyRegistered = Win32.RegisterHotKey(_hwnd, VoiceHotkeyId,
                         _settings.VoiceModifiers | Win32.MOD_NOREPEAT, _settings.VoiceKey)))
                error = $"{HotkeyText.Format(_settings.VoiceModifiers, _settings.VoiceKey)} уже занято другой программой";
        }
        if (mode != VoiceMode.Off && error is null) VoiceUi(); // окошко готовим заранее, чтобы появлялось мгновенно
        _voiceError = error;
        return error;
    }

    private void OnVoiceKeyDown()
    {
        if (_pttWatch.IsRunning) return;
        _pttPressedAt = DateTime.Now;
        _pttStarting = true;
        _voice.PushToTalkDown();
        _pttStarting = false;
        _pttWatch.Start();
    }

    /// <summary>RegisterHotKey сообщает только о нажатии — отпускание ловим опросом клавиши.</summary>
    private void WatchVoiceKey()
    {
        if ((Win32.GetAsyncKeyState((int)_settings.VoiceKey) & 0x8000) != 0) return;
        _pttWatch.Stop();
        _voice.PushToTalkUp(shortTap: DateTime.Now - _pttPressedAt < TimeSpan.FromMilliseconds(350));
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _countdown;
    private VoiceShortcut? _pending;
    private int _secondsLeft;

    private async void OnVoiceCommand(string text)
    {
        var window = VoiceUi();

        // «Хоуми, отмена» во время отсчёта выключения.
        if (_pending is not null && VoiceCommands.Normalize(text).Split(' ').Any(w => w.StartsWith("отмен") || w is "стоп" or "нет" or "стой"))
        {
            CancelCountdown();
            window.ShowResult(true, "Отменено");
            _replies.Play(ReplyEvent.Cancelled);
            return;
        }

        // Свои команды для компьютера — раньше умного дома («выключи компьютер» ≠ «выключи свет»).
        _settings.VoiceShortcuts ??= AppSettings.DefaultShortcuts();
        if (PcActions.Find(text, _settings.VoiceShortcuts) is { } shortcut)
        {
            window.ShowProcessing(text);
            if (PcActions.NeedsCountdown(shortcut.Action)) StartCountdown(shortcut);
            else
            {
                string? error = PcActions.Run(shortcut);
                window.ShowResult(error is null, error ?? PcActions.Doing(shortcut));
                if (error is not null) _replies.Play(ReplyEvent.Failed);
                else _replies.Play(shortcut.Action == PcAction.OpenApp ? ReplyEvent.App : ReplyEvent.Done, shortcut.ReplySound);
            }
            return;
        }

        window.ShowProcessing(text);
        var (ok, answer, reply) = await _home.ExecuteVoiceAsync(text, _settings.PcRoomId);
        if (reply is { } r) _replies.Play(r);
        window.ShowResult(ok, answer);
    }

    /// <summary>Выключение и перезагрузка — через 5 секунд, чтобы случайно услышанная фраза не выключила ПК.</summary>
    private void StartCountdown(VoiceShortcut shortcut)
    {
        _pending = shortcut;
        _secondsLeft = 5;
        if (_countdown is null)
        {
            _countdown = DispatcherQueue.CreateTimer();
            _countdown.Interval = TimeSpan.FromSeconds(1);
            _countdown.Tick += (_, _) => CountdownTick();
        }
        VoiceUi().ShowCountdown(PcActions.Doing(shortcut), _secondsLeft);
        _replies.Play(ReplyEvent.Countdown, shortcut.ReplySound);
        _countdown.Start();
    }

    private void CountdownTick()
    {
        if (_pending is null) { _countdown?.Stop(); return; }
        _secondsLeft--;
        if (_secondsLeft > 0)
        {
            // Пока идёт запись «Хоуми, отмена» — окошко показывает её, отсчёт идёт дальше.
            if (!_voice.IsListening) VoiceUi().ShowCountdown(PcActions.Doing(_pending), _secondsLeft);
            return;
        }
        var shortcut = _pending;
        CancelCountdown();
        string? error = PcActions.Run(shortcut);
        VoiceUi().ShowResult(error is null, error ?? PcActions.Doing(shortcut));
    }

    private void CancelCountdown()
    {
        _pending = null;
        _countdown?.Stop();
        _voiceWindow?.EndCountdown();
    }

    // ---------- окно ----------

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            if ((int)wParam == VoiceHotkeyId) OnVoiceKeyDown();
            else OnHotkey((int)wParam);
            return 0;
        }
        return _tray?.HandleMessage(msg, wParam, lParam) == true ? 0 : Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void Quit()
    {
        for (int i = 0; i < _registered.Count; i++) Win32.UnregisterHotKey(_hwnd, HotkeyIdBase + i);
        if (_voiceKeyRegistered) Win32.UnregisterHotKey(_hwnd, VoiceHotkeyId);
        _voice.Dispose();
        _replies.Dispose();
        _voiceWindow?.Close();
        _tray.Dispose();
        _home.Dispose();
        Win32.RemoveWindowSubclass(_hwnd, _wndProc, 1);
        Application.Current.Exit();
    }
}
