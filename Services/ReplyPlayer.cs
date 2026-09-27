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
        _player.MediaEnded += (_, _) => PlayingChanged?.Invoke(false);
        _player.MediaFailed += (_, _) => PlayingChanged?.Invoke(false);
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
        _ => "",
    };

    /// <summary>Сыграть случайный файл события. Вернёт true, если что-то играет.</summary>
    public bool Play(ReplyEvent e, string? own = null)
    {
        if (!string.IsNullOrWhiteSpace(own) && Exists(own)) return PlayFile(own);
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
