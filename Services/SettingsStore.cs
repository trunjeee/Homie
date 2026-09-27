using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homie.Services;

public enum HotkeyTarget { Scenario, DeviceToggle }

/// <summary>Своё сочетание клавиш: запустить сценарий или переключить устройство.</summary>
public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }   // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public uint Key { get; set; }         // виртуальный код клавиши
    public HotkeyTarget Target { get; set; }
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
}

/// <summary>Что делает своя голосовая команда.</summary>
public enum PcAction
{
    OpenApp, Shutdown, Restart, Sleep, Hibernate, Lock, SignOut, MonitorOff,
    Mute, VolumeUp, VolumeDown, PlayPause, NextTrack, PreviousTrack,
}

/// <summary>Своя голосовая команда: фразы через запятую → действие с компьютером.</summary>
public sealed class VoiceShortcut
{
    public string Phrases { get; set; } = "";
    public PcAction Action { get; set; }
    /// <summary>Для «Открыть»: программа, файл или ссылка.</summary>
    public string Path { get; set; } = "";
    /// <summary>Свой звук ответа («Запускаю, удачной игры»); пусто — общий из «Ответов голосом».</summary>
    public string ReplySound { get; set; } = "";
}

/// <summary>Когда Homie отвечает голосом (заранее записанные файлы).</summary>
public enum ReplyEvent { Wake, On, Off, Done, Scenario, App, NotUnderstood, NotHeard, Failed, Countdown, Cancelled }

public sealed class AppSettings
{
    /// <summary>ClientID своего приложения на oauth.yandex.ru — у каждого пользователя свой, в код не зашивается.</summary>
    public string ClientId { get; set; } = "";
    public string? HouseholdId { get; set; }
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    /// <summary>Вид панели: плитки (как в TouchControl) или список.</summary>
    public bool TilesView { get; set; } = true;

    /// <summary>Порядок комнат сверху вниз (ID комнат; «группы» и «без комнаты» — служебные ключи).</summary>
    public List<string> RoomOrder { get; set; } = [];
    public List<string> HiddenRooms { get; set; } = [];

    // ---------- голос ----------

    /// <summary>Выключен, слушает «Хоуми», только по клавише или и то и другое.</summary>
    public VoiceMode VoiceMode { get; set; } = VoiceMode.Off;
    /// <summary>Клавиша «нажми и говори»: можно одну клавишу, можно сочетание.</summary>
    public uint VoiceModifiers { get; set; }
    public uint VoiceKey { get; set; }
    /// <summary>Комната, где стоит этот ПК: команды без комнаты выполняются в ней.</summary>
    public string? PcRoomId { get; set; }
    /// <summary>Как распознаётся слово вызова — модель может слышать «хоуми» по-разному.</summary>
    public string WakeWords { get; set; } = "хоуми, хоми, хауми, хоуме, хоу ми, хоум ми, хаоми";

    /// <summary>Свои команды для компьютера. null — ещё не создавались (тогда кладём стандартные).</summary>
    public List<VoiceShortcut>? VoiceShortcuts { get; set; }

    /// <summary>Отвечать записанными фразами.</summary>
    public bool VoiceReplies { get; set; } = true;
    public double ReplyVolume { get; set; } = 0.8;
    /// <summary>Файлы ответов по событиям; из нескольких выбирается случайный.</summary>
    public Dictionary<ReplyEvent, List<string>> ReplySounds { get; set; } = [];

    /// <summary>Стандартные ответы — записи, которые идут вместе с Homie (Assets\Sounds).</summary>
    public static Dictionary<ReplyEvent, List<string>> DefaultReplySounds()
    {
        static List<string> B(params string[] names) => [.. names.Select(n => ReplyPlayer.BuiltIn + n + ".mp3")];
        return new()
        {
            [ReplyEvent.Wake] = B("Слушаю", "Пиип"),
            [ReplyEvent.On] = B("Включаю", "Сейчас будет светло", "Готово, включила"),
            [ReplyEvent.Off] = B("Выключаю", "Выключила", "Готово, темно"),
            [ReplyEvent.Done] = B("Готово", "Сделано", "Есть", "Конечно"),
            [ReplyEvent.Scenario] = B("Запускаю сценарий"),
            [ReplyEvent.App] = B("Запускаю, удачной игры", "Погнали! Удачи в катке"),
            [ReplyEvent.NotUnderstood] = B("Не поняла, повтори", "Прости, не поняла"),
            [ReplyEvent.NotHeard] = B("Не поняла, повтори"),
            [ReplyEvent.Failed] = B("Не получилось", "Ой, что-то пошло не так", "Не получилось, устройство не отвечает"),
            [ReplyEvent.Countdown] = B("Выключаю компьютер через пять секунд"),
            [ReplyEvent.Cancelled] = B("Отменено"),
        };
    }

    public static List<VoiceShortcut> DefaultShortcuts()
    {
        var list = new List<VoiceShortcut>
        {
            new() { Action = PcAction.Shutdown, Phrases = "выключи компьютер, выключи комп, выключи пк, выключи пека, выключи писи, выруби комп, выруби компьютер, выключи компик" },
            new() { Action = PcAction.Restart, ReplySound = ReplyPlayer.BuiltIn + "Перезагружаю компьютер через пять секунд.mp3", Phrases = "перезагрузи компьютер, перезагрузи комп, перезагрузи пк, перезагрузка, перезагрузи" },
            new() { Action = PcAction.Sleep, ReplySound = ReplyPlayer.BuiltIn + "Спокойной ночи.mp3", Phrases = "спящий режим, усыпи компьютер, режим сна" },
            new() { Action = PcAction.Lock, ReplySound = ReplyPlayer.BuiltIn + "Блокирую. Возвращайся скорее.mp3", Phrases = "заблокируй компьютер, блокировка, заблокируй" },
            new() { Action = PcAction.MonitorOff, Phrases = "выключи экран, выключи монитор, погаси экран" },
            new() { Action = PcAction.Mute, Phrases = "выключи звук, включи звук, без звука" },
            new() { Action = PcAction.VolumeUp, Phrases = "громче, сделай громче" },
            new() { Action = PcAction.VolumeDown, Phrases = "тише, сделай тише" },
            new() { Action = PcAction.PlayPause, Phrases = "пауза, продолжи, поставь на паузу" },
            new() { Action = PcAction.NextTrack, Phrases = "следующий трек, следующая песня, дальше" },
        };
        const string faceit = @"C:\Program Files\FACEIT AC\faceitclient.exe";
        if (File.Exists(faceit))
            list.Insert(0, new() { Action = PcAction.OpenApp, Path = faceit, Phrases = "давай поиграем, включи античит, запусти античит, фейсит античит, включи фейсит, запусти фейсит" });
        return list;
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(VoiceShortcut))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>%LOCALAPPDATA%\Homie: settings.json и зашифрованный token.bin.</summary>
public static class SettingsStore
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homie");
    private static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
    private static readonly string TokenFile = Path.Combine(Dir, "token.bin");
    private static readonly byte[] Entropy = "Homie.Yandex.SmartHome"u8.ToArray();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), SettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
    }

    // ---------- токен: шифруется DPAPI, расшифровать может только этот пользователь на этом ПК ----------

    public static string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenFile)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SaveToken(string token)
    {
        Directory.CreateDirectory(Dir);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(TokenFile, bytes);
    }

    public static void DeleteToken()
    {
        try { File.Delete(TokenFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Страница входа Яндекса: после подтверждения она покажет токен (redirect на verification_code).</summary>
    public static string AuthorizeUrl(string clientId) =>
        $"https://oauth.yandex.ru/authorize?response_type=token&client_id={Uri.EscapeDataString(clientId.Trim())}";
}
