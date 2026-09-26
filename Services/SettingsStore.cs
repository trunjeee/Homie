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

public sealed class AppSettings
{
    /// <summary>ClientID своего приложения на oauth.yandex.ru — у каждого пользователя свой, в код не зашивается.</summary>
    public string ClientId { get; set; } = "";
    public string? HouseholdId { get; set; }
    public List<HotkeyBinding> Hotkeys { get; set; } = [];
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>%LOCALAPPDATA%\Homie: settings.json и зашифрованный token.bin.</summary>
public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
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
