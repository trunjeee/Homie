using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Homie.Services;

/// <summary>
/// Обновления с GitHub Releases (Velopack): новая версия скачивается в фоне — дельтой, только изменения, —
/// и ставится по кнопке в меню трея или сама при выходе из Homie.
/// Работает только в установленной версии (через HomieApp-win-Setup.exe), в сборке для разработки молчит.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/trunjeee/Homie";
    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _ready;

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "?";

    /// <summary>Скачанная и готовая к установке версия (или null).</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public enum CheckResult { UpToDate, Downloaded, AlreadyReady, Failed, NotInstalled }

    /// <summary>
    /// Проверить и скачать в фоне. onFound вызывается, когда новая версия найдена и начинает качаться
    /// (чтобы показать «Скачиваю 1.2.0…»).
    /// </summary>
    public async Task<CheckResult> CheckAndDownloadAsync(Action<string>? onFound = null)
    {
        if (!IsInstalled) return CheckResult.NotInstalled;
        if (_ready is not null) return CheckResult.AlreadyReady;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return CheckResult.UpToDate;
            onFound?.Invoke(info.TargetFullRelease.Version.ToString());
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            return CheckResult.Downloaded;
        }
        catch (Exception ex)
        {
            Log.Write("Обновления: проверка", ex);
            return CheckResult.Failed; // нет интернета или GitHub недоступен — попробуем в следующий раз
        }
    }

    /// <summary>Поставить скачанное обновление и перезапустить Homie.</summary>
    public void ApplyAndRestart()
    {
        if (_ready is not null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    /// <summary>При выходе: обновление встанет само, тихо, к следующему запуску.</summary>
    public void ApplyOnExit()
    {
        if (_ready is not null) _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
    }
}
