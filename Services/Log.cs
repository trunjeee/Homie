namespace Homie.Services;

/// <summary>Короткий журнал ошибок: %LOCALAPPDATA%\Homie\homie.log (не больше ~200 КБ).</summary>
public static class Log
{
    private static readonly string File_ = Path.Combine(SettingsStore.Dir, "homie.log");
    private static readonly object Lock = new();

    public static void Write(string what, Exception? ex = null)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(SettingsStore.Dir);
                if (File.Exists(File_) && new FileInfo(File_).Length > 200_000) File.Delete(File_);
                File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what}{(ex is null ? "" : $": {ex.GetType().Name}: {ex.Message}")}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // журнал не должен ронять программу
        }
    }
}
