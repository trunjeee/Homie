using Microsoft.Win32;

namespace Homie.Services;

/// <summary>
/// Автозапуск вместе с Windows для текущего пользователя (без прав администратора).
/// Учитывает и выключатель в «Диспетчер задач → Автозагрузка», который хранится отдельно в StartupApproved.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Homie";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string command ||
                !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase))
                return false;

            // Первый байт: 2 — включено, 3 — выключено в Диспетчере задач.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) run.SetValue(ValueName, Command);
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        // Сбрасываем отметку «отключено», иначе Windows проигнорирует запись в Run.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
