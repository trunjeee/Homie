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
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _wakeReplyTimer;
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
        // Пока звонит будильник, микрофон не глушим — чтобы было слышно «Хоуми, стоп».
        _replies.PlayingChanged += playing => _voice.Suppress(playing && _ringing is null);

        // Сказал «Хоуми» и молчишь 3 секунды — тогда отвечаем «Слушаю», иначе не перебиваем.
        _wakeReplyTimer = DispatcherQueue.CreateTimer();
        _wakeReplyTimer.Interval = TimeSpan.FromSeconds(3);
        _wakeReplyTimer.IsRepeating = false;
        _wakeReplyTimer.Tick += (_, _) =>
        {
            if (_voice.IsListening) _replies.Play(ReplyEvent.Wake);
        };

        _voice.ListeningStarted += () =>
        {
            VoiceUi().ShowListening();
            // «Слушаю» — только если после «Хоуми» пауза: сразу продолжил фразу — не перебиваем.
            if (!_pttStarting) _wakeReplyTimer.Start();
        };
        _voice.PartialText += text =>
        {
            if (text.Length > 0) _wakeReplyTimer.Stop();
            _voiceWindow?.SetText(text);
        };
        _voice.Level += level => _voiceWindow?.SetLevel(level);
        _voice.Cancelled += reason =>
        {
            _wakeReplyTimer.Stop();
            _voiceWindow?.ShowCancelled(reason);
            if (reason.Length > 0) _replies.Play(ReplyEvent.NotHeard);
        };
        _voice.CommandRecognized += text => { _wakeReplyTimer.Stop(); OnVoiceCommand(text); };

        _pttWatch = DispatcherQueue.CreateTimer();
        _pttWatch.Interval = TimeSpan.FromMilliseconds(30);
        _pttWatch.Tick += (_, _) => WatchVoiceKey();
        _ = ApplyVoiceAsync();

        // Сообщения от Алисы через свой навык «Хоуми».
        _relay = new AliceRelay(DispatcherQueue);
        _relay.MessageReceived += OnAliceMessage;
        ApplyRelay();

        // Таймеры и напоминания.
        _timers = new TimerService(DispatcherQueue);
        _timers.Fired += (entry, missed) => _ = RingAsync(entry, missed);
        InitNotifications();

        // Обновления: первая проверка через минуту после запуска, дальше раз в 6 часов.
        _updateTimer = DispatcherQueue.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(1);
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = TimeSpan.FromHours(6); _ = CheckUpdatesAsync(manual: false); };
        _updateTimer.Start();

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

        _flyout = new FlyoutWindow(_home, OpenSettings, _timers);
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
        _settingsWindow = new SettingsWindow(_settings, _home, ApplyHotkeys, _voice, ApplyVoiceAsync, _replies, _relay, ApplyRelay, _timers);
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
        if (_updates.ReadyVersion is { } ready)
            items.Add(new($"⬆  Обновить до {ready} (перезапуск)", _updates.ApplyAndRestart));
        else if (_updates.IsInstalled)
            items.Add(new("Проверить обновления", () => _ = CheckUpdatesAsync(manual: true)));
        items.Add(TrayMenuItem.Info($"Homie {_updates.CurrentVersion}"));
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
                StopRinging(); // клик по окошку — «стоп» для будильника
                _replies.Stop();
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

        // Таймеры и напоминания: звонит — «стоп» / «отложи»; иначе «таймер на…», «напомни…», «сколько осталось».
        if (await HandleTimerCommandAsync(text, window)) return;

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
                string? error = await PcActions.RunAsync(shortcut);
                window.ShowResult(error is null, error ?? PcActions.Doing(shortcut));
                if (error is not null) _replies.Play(ReplyEvent.Failed);
                else _replies.Play(shortcut.Action == PcAction.OpenApp ? ReplyEvent.App : ReplyEvent.Done, shortcut.ReplySound);
            }
            return;
        }

        window.ShowProcessing(text);
        bool aiReady = _settings.AiEnabled && AiService.HasKey;
        if (!_home.IsSignedIn && aiReady)
        {
            await AskAiAsync(text); // умный дом не подключён — всё, что не команда ПК, это вопрос
            return;
        }

        var (ok, answer, reply) = await _home.ExecuteVoiceAsync(text, _settings.PcRoomId);
        if (reply == ReplyEvent.NotUnderstood && aiReady)
        {
            await AskAiAsync(text); // не команда — спрашиваем нейросеть
            return;
        }
        if (reply is { } r) _replies.Play(r);
        window.ShowResult(ok, answer);
    }

    // ---------- таймеры и напоминания ----------

    private readonly TimerService _timers;
    private TimerEntry? _ringing;
    public TimerService Timers => _timers;

    private static readonly string[] StopWords = ["стоп", "хватит", "выключи", "выключить", "готово", "отключи", "тихо", "спасибо", "ок", "окей", "понял", "поняла", "достаточно"];

    /// <summary>Голосовые команды таймеров. true — фраза была про них и обработана.</summary>
    private async Task<bool> HandleTimerCommandAsync(string text, VoiceWindow window)
    {
        var words = VoiceCommands.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Звонит будильник: «стоп» — замолчать, «отложи (на 10 минут)» — отложить.
        if (_ringing is { } ringing)
        {
            if (words.Any(w => w.StartsWith("отлож")))
            {
                int minutes = words.Select(w => int.TryParse(w, out int n) ? n : 0).FirstOrDefault(n => n > 0);
                minutes = minutes > 0 ? minutes : _settings.SnoozeMinutes;
                StopRinging();
                _timers.Snooze(ringing, minutes);
                window.ShowResult(true, $"Отложила на {TimerText.Duration(TimeSpan.FromMinutes(minutes))}");
                _replies.Play(ReplyEvent.Done);
                return true;
            }
            if (words.Any(w => StopWords.Contains(w)))
            {
                StopRinging();
                window.HideNow();
                return true;
            }
        }

        var intent = TimerCommands.Parse(text, DateTime.Now);
        if (intent is null) return false;
        TimerKind? kind = words.Any(w => w.StartsWith("напомин") || w == "напомни") ? TimerKind.Reminder
            : words.Any(w => w.StartsWith("таймер")) ? TimerKind.Timer : null;

        if (intent.Error is not null)
        {
            window.ShowResult(false, intent.Error);
            _replies.Play(ReplyEvent.NotUnderstood);
            return true;
        }

        switch (intent.Action)
        {
            case TimerAction.SetTimer or TimerAction.SetReminder when intent.Entry is { } entry:
                _timers.Add(entry);
                string what = entry.Label.Length > 0 ? $" · {entry.Label}" : "";
                window.ShowResult(true, entry.Kind == TimerKind.Timer
                    ? $"Таймер на {TimerText.Duration(TimeSpan.FromSeconds(entry.DurationSeconds))} — до {entry.Due:H:mm}{what}"
                    : $"Напомню {TimerText.When(entry, DateTime.Now)}{what}");
                _replies.Play(ReplyEvent.Done);
                return true;

            case TimerAction.Remaining:
            {
                var next = _timers.Find("", TimerKind.Timer) ?? _timers.Find("", null);
                string answer = next is null ? "Таймеров нет"
                    : $"{(next.Label.Length > 0 ? next.Label + ": " : "")}осталось {TimerText.Duration(next.Due - DateTime.Now)}";
                window.ShowResult(next is not null, answer);
                await SayAsync(answer);
                return true;
            }

            case TimerAction.List:
            {
                var items = _timers.Items.Where(t => kind is null || t.Kind == kind).ToList();
                if (items.Count == 0)
                {
                    window.ShowResult(true, kind == TimerKind.Reminder ? "Напоминаний нет" : "Таймеров нет");
                    await SayAsync(kind == TimerKind.Reminder ? "Напоминаний нет" : "Таймеров нет");
                    return true;
                }
                var now = DateTime.Now;
                var lines = items.Take(4).Select(t => t.Kind == TimerKind.Timer
                    ? $"{(t.Label.Length > 0 ? t.Label : "таймер")} — через {TimerText.Duration(t.Due - now)}"
                    : $"{(t.Label.Length > 0 ? t.Label : "напоминание")} — {TimerText.When(t, now)}");
                string list = string.Join("; ", lines) + (items.Count > 4 ? $" и ещё {items.Count - 4}" : "");
                window.ShowResult(true, list);
                await SayAsync(list);
                return true;
            }

            case TimerAction.Cancel:
            {
                var found = _timers.Find(intent.Query, kind);
                if (found is null)
                {
                    window.ShowResult(false, intent.Query.Length > 0 ? $"Не нашла: {intent.Query}" : "Отменять нечего");
                    _replies.Play(ReplyEvent.Failed);
                    return true;
                }
                _timers.Remove(found.Id);
                window.ShowResult(true, $"Отменила {(found.Kind == TimerKind.Timer ? "таймер" : "напоминание")}{(found.Label.Length > 0 ? " · " + found.Label : "")}");
                _replies.Play(ReplyEvent.Cancelled);
                return true;
            }

            case TimerAction.CancelAll:
                _timers.RemoveAll(kind);
                window.ShowResult(true, kind == TimerKind.Reminder ? "Все напоминания отменены" : kind == TimerKind.Timer ? "Все таймеры отменены" : "Всё отменено");
                _replies.Play(ReplyEvent.Cancelled);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Сработало: колокольчик → «Напоминание» (своя запись) → голосом, о чём; по кругу, пока не остановят
    /// («Хоуми, стоп», клик по окошку, «Готово» в уведомлении) — но не дольше трёх кругов.
    /// </summary>
    private async Task RingAsync(TimerEntry entry, bool missed)
    {
        StopRinging();
        _ringing = entry;
        string title = entry.Kind == TimerKind.Timer ? "Время вышло" : "Напоминание";
        string about = entry.Label.Length > 0 ? entry.Label : entry.Kind == TimerKind.Timer
            ? $"таймер на {TimerText.Duration(TimeSpan.FromSeconds(Math.Max(1, entry.DurationSeconds)))}" : "";
        string shown = about + (missed ? $" (пропущено в {entry.Due:H:mm})" : "");

        ShowAlarmNotification(entry, title, shown);
        var window = VoiceUi();
        window.HideNow();
        window.ShowAnswer(title, shown.Length > 0 ? shown : title);
        int session = window.Session;

        string speech = entry.Kind == TimerKind.Timer
            ? (entry.Label.Length > 0 ? $"Время вышло: {entry.Label}" : "Время вышло")
            : (entry.Label.Length > 0 ? entry.Label : "Напоминание");
        byte[]? voice = null;
        try { voice = await EdgeVoice.SynthesizeAsync(speech, _settings.AiVoice); }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or HttpRequestException) { }

        for (int round = 0; round < 3 && _ringing == entry; round++)
        {
            await _replies.PlayFileAndWaitAsync(_settings.AlarmSound);
            if (_ringing != entry) break;
            if (_replies.Pick(ReplyEvent.Reminder) is { } phrase) await _replies.PlayFileAndWaitAsync(phrase);
            if (_ringing != entry) break;
            if (voice is not null && (entry.Label.Length > 0 || _replies.Pick(ReplyEvent.Reminder) is null))
                await _replies.PlayMp3AndWaitAsync(voice);
            if (_ringing != entry) break;
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        if (_ringing == entry) _ringing = null; // отзвенело — уведомление остаётся в Центре уведомлений
        if (window.IsActive(session)) window.HideAfterAnswer(10);
    }

    private void StopRinging()
    {
        if (_ringing is null) return;
        RemoveAlarmNotification(_ringing);
        _ringing = null;
        _replies.Stop();
    }

    private async Task SayAsync(string text)
    {
        if (!_settings.VoiceReplies) return;
        try { await _replies.PlayMp3AndWaitAsync(await EdgeVoice.SynthesizeAsync(text, _settings.AiVoice)); }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or HttpRequestException) { }
    }

    // ---------- уведомления Windows с кнопками «Отложить» / «Готово» ----------

    private readonly Dictionary<string, TimerEntry> _notified = [];

    private void InitNotifications()
    {
        try
        {
            var manager = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            manager.NotificationInvoked += (_, args) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!args.Arguments.TryGetValue("id", out var id) || !_notified.TryGetValue(id, out var entry)) return;
                bool wasRinging = _ringing?.Id == entry.Id;
                StopRinging();
                if (wasRinging) _voiceWindow?.HideNow();
                if (args.Arguments.TryGetValue("action", out var action) && action == "snooze")
                    _timers.Snooze(entry, _settings.SnoozeMinutes);
                _notified.Remove(id);
            });
            manager.Register();
        }
        catch (Exception)
        {
            // без уведомлений Windows — останется окошко над треем и голос
        }
    }

    private void ShowAlarmNotification(TimerEntry entry, string title, string text)
    {
        try
        {
            _notified[entry.Id] = entry;
            var builder = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddArgument("id", entry.Id)
                .SetTag(entry.Id)
                .SetScenario(Microsoft.Windows.AppNotifications.Builder.AppNotificationScenario.Reminder)
                .MuteAudio() // звук — свой (колокольчик и голос)
                .AddText($"⏰ {title}");
            if (text.Length > 0) builder.AddText(text);
            builder
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton($"Отложить на {_settings.SnoozeMinutes} мин")
                    .AddArgument("id", entry.Id).AddArgument("action", "snooze"))
                .AddButton(new Microsoft.Windows.AppNotifications.Builder.AppNotificationButton("Готово")
                    .AddArgument("id", entry.Id).AddArgument("action", "done"));
            Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception)
        {
        }
    }

    private void RemoveAlarmNotification(TimerEntry entry)
    {
        try { _ = Microsoft.Windows.AppNotifications.AppNotificationManager.Default.RemoveByTagAsync(entry.Id); }
        catch (Exception) { }
    }

    private readonly UpdateService _updates = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _updateTimer;

    /// <summary>Скачать новую версию в фоне и сказать об этом уведомлением.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        string? version = await _updates.CheckAndDownloadAsync();
        if (version is not null)
            _tray.ShowNotification("Homie", $"Готово обновление {version} — «Обновить» в меню трея или просто при следующем запуске");
        else if (manual)
            _tray.ShowNotification("Homie", _updates.ReadyVersion is null ? $"У тебя последняя версия — {_updates.CurrentVersion}" : $"Обновление {_updates.ReadyVersion} уже скачано");
    }

    private readonly AiService _ai = new();
    private readonly AliceRelay _relay;

    /// <summary>Включить/выключить приём сообщений от Алисы по настройкам.</summary>
    private void ApplyRelay()
    {
        if (_settings.AliceRelayEnabled && SettingsStore.LoadRelayKey() is { } key) _relay.Start(key);
        else _relay.Stop();
    }

    /// <summary>«Алиса, попроси Хоуми передать на балкон: …» — показываем и говорим голосом.</summary>
    private async void OnAliceMessage(string text)
    {
        var window = VoiceUi();
        window.HideNow(); // сброс прошлого показа (размер, озвучка)
        window.ShowAnswer("Сообщение через Алису", text);
        int session = window.Session;

        if (_settings.VoiceReplies)
        {
            try
            {
                var speech = await EdgeVoice.SynthesizeAsync("Тебе передали: " + text, _settings.AiVoice);
                if (window.IsActive(session)) await _replies.PlayMp3AndWaitAsync(speech);
            }
            catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or HttpRequestException)
            {
                _replies.Play(ReplyEvent.Wake); // без интернета голоса нет — хотя бы короткий звук
            }
        }
        if (window.IsActive(session)) window.HideAfterAnswer(Math.Max(6, text.Length * 0.09));
    }

    private async Task AskAiAsync(string question)
    {
        var window = VoiceUi();
        window.ShowThinking(question);
        int session = window.Session;
        bool speak = _settings.AiSpeak && _settings.VoiceReplies;

        // Озвучка по кусочкам: каждое готовое предложение сразу синтезируется (параллельно),
        // а звучат они строго по порядку, пока нейросеть дописывает остальное.
        var queue = System.Threading.Channels.Channel.CreateUnbounded<Task<byte[]?>>();
        Task speaking = speak ? SpeakQueueAsync(queue.Reader, window, session) : Task.CompletedTask;
        int spokenUpTo = 0;
        string raw = "";
        void Enqueue(IEnumerable<string> sentences)
        {
            if (!speak) return;
            foreach (var sentence in sentences) queue.Writer.TryWrite(SynthesizeOrNull(sentence));
        }

        string answer;
        try
        {
            answer = await _ai.AskAsync(question, _settings, text =>
            {
                if (!window.IsActive(session)) return; // окошко закрыли — не показываем и не озвучиваем
                raw = text;
                window.ShowAnswer(question, text);
                Enqueue(AiService.TakeSentences(text, ref spokenUpTo, final: false));
            });
        }
        catch (AiException ex)
        {
            queue.Writer.TryComplete();
            _replies.Play(ReplyEvent.Failed);
            window.ShowResult(false, ex.Message);
            return;
        }

        if (!window.IsActive(session)) { queue.Writer.TryComplete(); return; }
        window.ShowAnswer(question, answer);
        // Последний кусок (без точки в конце) — из того же сырого текста, по которому считались позиции.
        Enqueue(AiService.TakeSentences(raw.Length > 0 ? raw : answer, ref spokenUpTo, final: true));
        queue.Writer.TryComplete();

        if (!speak)
        {
            window.HideAfterAnswer(Math.Clamp(answer.Length * 0.09, 6, 30));
            return;
        }
        await speaking;
        if (window.IsActive(session)) window.HideAfterAnswer(3);
    }

    private async Task<byte[]?> SynthesizeOrNull(string sentence)
    {
        try { return await EdgeVoice.SynthesizeAsync(sentence, _settings.AiVoice); }
        catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or OperationCanceledException or HttpRequestException)
        {
            return null; // голос недоступен — ответ остаётся текстом
        }
    }

    private async Task SpeakQueueAsync(System.Threading.Channels.ChannelReader<Task<byte[]?>> queue, VoiceWindow window, int session)
    {
        await foreach (var synthesis in queue.ReadAllAsync())
        {
            var mp3 = await synthesis;
            if (!window.IsActive(session)) return; // закрыли кликом — замолкаем
            if (mp3 is not null) await _replies.PlayMp3AndWaitAsync(mp3);
        }
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

    private async void CountdownTick()
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
        string? error = await PcActions.RunAsync(shortcut);
        VoiceUi().ShowResult(error is null, error ?? PcActions.Doing(shortcut));
    }

    private void CancelCountdown()
    {
        _pending = null;
        _countdown?.Stop();
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
        _ai.Dispose();
        _relay.Dispose();
        _voiceWindow?.Close();
        _updateTimer.Stop();
        _timers.Stop();
        StopRinging();
        try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister(); } catch (Exception) { }
        _updates.ApplyOnExit(); // скачанное обновление встанет само к следующему запуску
        _tray.Dispose();
        _home.Dispose();
        Win32.RemoveWindowSubclass(_hwnd, _wndProc, 1);
        Application.Current.Exit();
    }
}
