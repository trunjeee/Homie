using Windows.Media.Core;
using Windows.Media.Playback;

namespace Homie.Services;

/// <summary>
/// Ответы голосом заранее записанными фразами (mp3/wav/ogg). Файлы копируются в
/// %LOCALAPPDATA%\Homie\sounds, чтобы не зависеть от того, где лежали оригиналы.
/// </summary>
public sealed class ReplyPlayer : IDisposable
{
    public static readonly string SoundsDir = Path.Combine(SettingsStore.Dir, "sounds");

    /// <summary>Приставка для записей из комплекта Homie: путь не ломается, если папку с программой перенести.</summary>
    public const string BuiltIn = "builtin:";

    public static string Resolve(string path) => path.StartsWith(BuiltIn)
        ? Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", path[BuiltIn.Length..])
        : path;

    public static bool Exists(string path) => File.Exists(Resolve(path));

    /// <summary>Разделитель нескольких своих ответов у одной команды.</summary>
    public const string Separator = "|";

    public static IEnumerable<string> Split(string? paths) =>
        (paths ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Имя для списка в настройках: «Готово» (стандартные помечены).</summary>
    public static string DisplayName(string path) =>
        Path.GetFileNameWithoutExtension(Resolve(path)) + (path.StartsWith(BuiltIn) ? " · стандартный" : "");

    private readonly AppSettings _settings;
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Speech };
    private readonly Random _random = new();

    /// <summary>Звук начал/закончил играть — чтобы микрофон не принял ответ за команду.</summary>
    public event Action<bool>? PlayingChanged;

    public ReplyPlayer(AppSettings settings)
    {
        _settings = settings;
        // Ответы Homie — не «мультимедиа»: иначе Windows делает Homie текущим плеером, и клавиша
        // «плей/пауза» повторяет последний ответ вместо того, чтобы продолжить видео или музыку.
        _player.CommandManager.IsEnabled = false;
        _player.SystemMediaTransportControls.IsEnabled = false;
        _player.MediaEnded += (_, _) => Finished();
        _player.MediaFailed += (_, _) => Finished();
    }

    public static string Title(ReplyEvent e) => e switch
    {
        ReplyEvent.Wake => "Услышала «Хоуми»",
        ReplyEvent.On => "Включила",
        ReplyEvent.Off => "Выключила",
        ReplyEvent.Done => "Готово (яркость, цвет, звук…)",
        ReplyEvent.Scenario => "Запустила сценарий",
        ReplyEvent.App => "Открыла программу",
        ReplyEvent.NotUnderstood => "Не поняла команду",
        ReplyEvent.NotHeard => "Не расслышала",
        ReplyEvent.Failed => "Не получилось",
        ReplyEvent.Countdown => "Выключение через 5 секунд",
        ReplyEvent.Cancelled => "Отменено",
        ReplyEvent.Reminder => "Таймер или напоминание сработало",
        _ => e.ToString(),
    };

    public static string Example(ReplyEvent e) => e switch
    {
        ReplyEvent.Wake => "«Да?», «Слушаю» или короткий звук",
        ReplyEvent.On => "«Включаю», «Сейчас будет светло»",
        ReplyEvent.Off => "«Выключаю», «Готово, темно»",
        ReplyEvent.Done => "«Готово», «Сделано», «Есть»",
        ReplyEvent.Scenario => "«Запускаю сценарий»",
        ReplyEvent.App => "«Открываю»",
        ReplyEvent.NotUnderstood => "«Не поняла, повтори»",
        ReplyEvent.NotHeard => "«Не расслышала»",
        ReplyEvent.Failed => "«Не получилось, устройство не отвечает»",
        ReplyEvent.Countdown => "«Выключаю компьютер через пять секунд»",
        ReplyEvent.Cancelled => "«Отменила»",
        ReplyEvent.Reminder => "«Напоминание», «Время вышло»",
        _ => "",
    };

    /// <summary>Сыграть случайный файл события. Вернёт true, если что-то играет.</summary>
    public bool Play(ReplyEvent e, string? own = null)
    {
        // У команды может быть несколько своих ответов («Запускаю, удачной игры» | «Погнали!») — случайный.
        var ownFiles = Split(own).Where(Exists).ToList();
        if (ownFiles.Count > 0) return PlayFile(ownFiles[_random.Next(ownFiles.Count)]);
        if (!_settings.ReplySounds.TryGetValue(e, out var files)) return false;
        var existing = files.Where(Exists).ToList();
        return existing.Count > 0 && PlayFile(existing[_random.Next(existing.Count)]);
    }

    /// <summary>Проиграть файл (в т.ч. «Прослушать» в настройках, даже если ответы выключены).</summary>
    public bool PlayFile(string path, bool force = false)
    {
        if (!force && !_settings.VoiceReplies) return false;
        try
        {
            _player.Volume = Math.Clamp(_settings.ReplyVolume, 0, 1);
            _player.Source = MediaSource.CreateFromUri(new Uri(Resolve(path)));
            PlayingChanged?.Invoke(true);
            _player.Play();
            return true;
        }
        catch (Exception)
        {
            PlayingChanged?.Invoke(false);
            return false;
        }
    }

    private TaskCompletionSource? _done;

    /// <summary>Звук доиграл (или его прервали) — отпускаем микрофон и того, кто ждёт конца.</summary>
    private void Finished()
    {
        PlayingChanged?.Invoke(false);
        _done?.TrySetResult();
    }

    /// <summary>Проиграть файл и дождаться конца (будильник таймера звучит всегда, даже если ответы выключены).</summary>
    public async Task PlayFileAndWaitAsync(string path)
    {
        if (!Exists(path)) return;
        _done?.TrySetResult();
        var done = _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PlayFile(path, force: true)) return;
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>Случайный файл события — для будильника таймера.</summary>
    public string? Pick(ReplyEvent e)
    {
        if (!_settings.ReplySounds.TryGetValue(e, out var files)) return null;
        var existing = files.Where(Exists).ToList();
        return existing.Count == 0 ? null : existing[_random.Next(existing.Count)];
    }

    /// <summary>Озвучить mp3 и дождаться конца — для ответа нейросети по предложениям.</summary>
    public async Task PlayMp3AndWaitAsync(byte[] mp3)
    {
        _done?.TrySetResult();
        var done = _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!await PlayMp3Async(mp3)) return;
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
    }

    /// <summary>Замолчать (клик по окошку).</summary>
    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
        Finished();
    }

    /// <summary>Озвучить готовый mp3 (ответ нейросети голосом «Светланы»).</summary>
    public async Task<bool> PlayMp3Async(byte[] mp3)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(mp3));
            stream.Seek(0);
            _player.Volume = Math.Clamp(_settings.ReplyVolume, 0, 1);
            _player.Source = MediaSource.CreateFromStream(stream, "audio/mpeg");
            PlayingChanged?.Invoke(true);
            _player.Play();
            return true;
        }
        catch (Exception)
        {
            PlayingChanged?.Invoke(false);
            return false;
        }
    }

    /// <summary>Скопировать выбранный файл к себе и вернуть новый путь.</summary>
    public static string Import(string source)
    {
        Directory.CreateDirectory(SoundsDir);
        string name = Path.GetFileNameWithoutExtension(source), ext = Path.GetExtension(source);
        string target = Path.Combine(SoundsDir, name + ext);
        for (int i = 2; File.Exists(target) && !SameFile(source, target); i++)
            target = Path.Combine(SoundsDir, $"{name} ({i}){ext}");
        if (!SameFile(source, target)) File.Copy(source, target, overwrite: false);
        return target;

        static bool SameFile(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _player.Dispose();
}
