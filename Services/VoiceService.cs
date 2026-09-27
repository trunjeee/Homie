using System.IO.Compression;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using NAudio.Wave;
using Vosk;

namespace Homie.Services;

public enum VoiceMode { Off, WakeWord, PushToTalk, Both }

/// <summary>
/// Офлайн-распознавание речи (Vosk, русская модель ~45 МБ) с микрофона по умолчанию.
/// Два способа начать команду: слово «Хоуми» (микрофон слушает всё время)
/// и клавиша «нажми и говори» (микрофон включается только на время нажатия).
/// Все события приходят в UI-поток.
/// </summary>
public sealed class VoiceService : IDisposable
{
    public const string ModelName = "vosk-model-small-ru-0.22";
    private const string ModelUrl = "https://alphacephei.com/vosk/models/" + ModelName + ".zip";
    public static readonly string ModelDir = Path.Combine(SettingsStore.Dir, ModelName);
    public static bool IsModelInstalled => Directory.Exists(Path.Combine(ModelDir, "am"));

    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(6);   // «Хоуми» сказали, а команды нет
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private enum State { Idle, Command, PushToTalk }

    private readonly DispatcherQueue _ui;
    private readonly object _lock = new();
    private Model? _model;
    private VoskRecognizer? _rec;
    private WaveInEvent? _mic;
    private State _state;
    private VoiceMode _mode;
    private bool _paused;
    private string[] _wakeWords = [];
    private DateTime _commandStarted, _lastSpeech;
    private string _shown = "", _pttText = "";

    /// <summary>Началась запись команды — показать окошко.</summary>
    public event Action? ListeningStarted;
    /// <summary>Распознанный на лету текст команды.</summary>
    public event Action<string>? PartialText;
    /// <summary>Громкость 0..1 — для «дышащего» кружка.</summary>
    public event Action<float>? Level;
    public event Action<string>? CommandRecognized;
    public event Action<string>? Cancelled;
    /// <summary>Всё, что услышано в режиме ожидания (в настройках — чтобы подобрать варианты «Хоуми»).</summary>
    public event Action<string>? Heard;

    public VoiceService(DispatcherQueue ui) => _ui = ui;

    private bool WakeEnabled => _mode is VoiceMode.WakeWord or VoiceMode.Both && !_paused;
    private volatile bool _suppressed;
    private DateTime _suppressUntil;

    /// <summary>Играет ответ Homie — звук из колонок не должен попасть в распознавание.</summary>
    public void Suppress(bool on)
    {
        _suppressed = on;
        // После конца звука ещё немного игнорируем эхо; и страховка, если «конец» не придёт.
        _suppressUntil = DateTime.Now + (on ? TimeSpan.FromSeconds(8) : TimeSpan.FromMilliseconds(150));
    }

    /// <summary>Сейчас записывается команда.</summary>
    public bool IsListening => _state != State.Idle;
    public bool PushToTalkEnabled => _mode is VoiceMode.PushToTalk or VoiceMode.Both;
    public bool HasWakeWord => _mode is VoiceMode.WakeWord or VoiceMode.Both;

    /// <summary>Временно не слушать «Хоуми» (из меню трея), клавиша при этом работает.</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; UpdateMic(); }
    }

    /// <summary>Применить режим. Вернёт текст ошибки или null.</summary>
    public async Task<string?> ConfigureAsync(VoiceMode mode, string wakeWords)
    {
        _wakeWords = wakeWords.Split(',', ';').Select(w => VoiceCommands.Normalize(w).Replace(" ", "")).Where(w => w.Length > 1).ToArray();
        _mode = mode;
        if (mode == VoiceMode.Off)
        {
            CancelListening(silent: true);
            StopMic();
            return null;
        }
        if (!IsModelInstalled)
        {
            StopMic();
            return "Сначала скачай модель распознавания речи";
        }
        try
        {
            if (_model is null)
            {
                // Загрузка модели занимает пару секунд — не в UI-потоке.
                var model = await Task.Run(() =>
                {
                    Vosk.Vosk.SetLogLevel(-1);
                    return new Model(ModelDir);
                });
                lock (_lock)
                {
                    _model = model;
                    _rec = new VoskRecognizer(model, 16000f);
                }
            }
        }
        catch (Exception ex)
        {
            return "Не удалось загрузить модель: " + ex.Message;
        }
        return UpdateMic();
    }

    // ---------- клавиша «нажми и говори» ----------

    public void PushToTalkDown()
    {
        if (!PushToTalkEnabled || _rec is null) return;
        lock (_lock)
        {
            _rec.Reset();
            _state = State.PushToTalk;
            _pttText = _shown = "";
            _commandStarted = _lastSpeech = DateTime.Now;
        }
        if (StartMic() is { } error)
        {
            lock (_lock) _state = State.Idle;
            Post(() => Cancelled?.Invoke(error));
            return;
        }
        ListeningStarted?.Invoke();
    }

    /// <summary>Клавишу отпустили. Короткое нажатие — слушаем дальше до паузы, как после «Хоуми».</summary>
    public void PushToTalkUp(bool shortTap)
    {
        lock (_lock)
        {
            if (_state != State.PushToTalk || _rec is null) return;
            if (shortTap)
            {
                _state = State.Command;
                _lastSpeech = DateTime.Now;
                return;
            }
            string text = Join(_pttText, TextOf(_rec.FinalResult(), "text"));
            if (text.Length == 0) Cancel("Ничего не услышал");
            else Finish(text);
        }
    }

    /// <summary>Закрыли окошко — команду не выполняем.</summary>
    public void CancelListening(bool silent = false)
    {
        lock (_lock)
        {
            if (_state == State.Idle) return;
            _state = State.Idle;
            _rec?.Reset();
        }
        if (!silent) Post(() => Cancelled?.Invoke(""));
        Post(() => UpdateMic());
    }

    // ---------- микрофон ----------

    /// <summary>Микрофон работает, пока слушаем «Хоуми» или идёт команда.</summary>
    private string? UpdateMic()
    {
        if (_rec is not null && (WakeEnabled || _state != State.Idle)) return StartMic();
        StopMic();
        return null;
    }

    private string? StartMic()
    {
        if (_mic is not null) return null;
        try
        {
            if (WaveInEvent.DeviceCount == 0) return "Микрофон не найден";
            var mic = new WaveInEvent
            {
                DeviceNumber = -1, // WAVE_MAPPER — микрофон по умолчанию в Windows
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
            };
            mic.DataAvailable += OnAudio;
            mic.StartRecording();
            _mic = mic;
            return null;
        }
        catch (Exception ex)
        {
            return "Не удалось открыть микрофон: " + ex.Message;
        }
    }

    private void StopMic()
    {
        var mic = _mic;
        _mic = null;
        if (mic is null) return;
        mic.DataAvailable -= OnAudio;
        try { mic.StopRecording(); } catch { }
        mic.Dispose();
    }

    // ---------- распознавание (поток записи) ----------

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_rec is null) return;
            if (_state != State.Idle)
            {
                float level = Rms(e.Buffer, e.BytesRecorded);
                Post(() => Level?.Invoke(level));
            }

            var now = DateTime.Now;
            if (_suppressed || now < _suppressUntil)
            {
                if (now > _suppressUntil) _suppressed = false;
                _lastSpeech = now; // пауза на ответ не считается молчанием пользователя
                return;
            }

            bool final = _rec.AcceptWaveform(e.Buffer, e.BytesRecorded);
            string text = final ? TextOf(_rec.Result(), "text") : TextOf(_rec.PartialResult(), "partial");

            switch (_state)
            {
                case State.Idle:
                    if (text.Length == 0) return;
                    if (final) Post(() => Heard?.Invoke(text));
                    if (!WakeEnabled || FindWake(text) is not { } rest) return;
                    _state = State.Command;
                    _commandStarted = _lastSpeech = now;
                    _shown = rest;
                    Post(() => { ListeningStarted?.Invoke(); PartialText?.Invoke(rest); });
                    if (final && rest.Length > 0) Finish(rest);
                    break;

                case State.Command:
                    // «Хоуми» может ещё быть в начале этой же фразы — отрезаем.
                    string command = FindWake(text) ?? text;
                    if (command.Length > 0 && command != _shown)
                    {
                        _shown = command;
                        _lastSpeech = now;
                        Post(() => PartialText?.Invoke(command));
                    }
                    if (final && command.Length > 0) Finish(command);
                    else if (now - _lastSpeech > SilenceTimeout || now - _commandStarted > CommandTimeout) Cancel("Не расслышал");
                    break;

                case State.PushToTalk:
                    // Пока клавиша зажата, фраз может быть несколько (с паузами) — копим.
                    if (final && text.Length > 0) _pttText = Join(_pttText, text);
                    string shown = final ? _pttText : Join(_pttText, text);
                    if (shown != _shown)
                    {
                        _shown = shown;
                        Post(() => PartialText?.Invoke(shown));
                    }
                    if (now - _commandStarted > TimeSpan.FromSeconds(30)) Cancel("Слишком долго");
                    break;
            }
        }
    }

    private void Finish(string command)
    {
        _state = State.Idle;
        _rec?.Reset();
        Post(() => { CommandRecognized?.Invoke(command); UpdateMic(); });
    }

    private void Cancel(string reason)
    {
        _state = State.Idle;
        _rec?.Reset();
        Post(() => { Cancelled?.Invoke(reason); UpdateMic(); });
    }

    /// <summary>Нашли слово вызова — вернёт текст после него (может быть пустым), иначе null.</summary>
    private string? FindWake(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            for (int len = 1; len <= 2 && i + len <= words.Length; len++)
            {
                string candidate = string.Concat(words.Skip(i).Take(len));
                if (_wakeWords.Any(w => Distance(candidate, w) <= (w.Length >= 5 ? 1 : 0)))
                    return string.Join(' ', words.Skip(i + len));
            }
        return null;
    }

    // ---------- модель: скачивание по кнопке в настройках ----------

    public static async Task DownloadModelAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(SettingsStore.Dir);
        string zip = Path.Combine(SettingsStore.Dir, ModelName + ".zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 46_000_000;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report((double)done / total);
            }
        }

        await Task.Run(() =>
        {
            string unpack = Path.Combine(SettingsStore.Dir, "model-unpack");
            if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
            ZipFile.ExtractToDirectory(zip, unpack);
            if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true);
            Directory.Move(Path.Combine(unpack, ModelName), ModelDir);
            Directory.Delete(unpack, true);
            File.Delete(zip);
        }, ct);
    }

    // ---------- помощники ----------

    private static string TextOf(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var v) ? VoiceCommands.Normalize(v.GetString() ?? "") : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    private static float Rms(byte[] buffer, int bytes)
    {
        double sum = 0;
        int samples = bytes / 2;
        for (int i = 0; i < samples; i++)
        {
            double s = BitConverter.ToInt16(buffer, i * 2) / 32768.0;
            sum += s * s;
        }
        return samples == 0 ? 0 : (float)Math.Min(1, Math.Sqrt(sum / samples) * 6);
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private void Post(Action action) => _ui.TryEnqueue(() => action());

    public void Dispose()
    {
        StopMic();
        lock (_lock)
        {
            _rec?.Dispose();
            _model?.Dispose();
            _rec = null;
            _model = null;
        }
    }
}
