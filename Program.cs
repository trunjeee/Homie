using Homie.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Homie;

/// <summary>
/// Точка входа вместо сгенерированной WinUI (DISABLE_XAML_GENERATED_MAIN): Velopack должен отработать
/// первым — при установке, обновлении и удалении он запускает exe со своими аргументами и сразу выходит.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            // Удаляют программу — убираем и автозапуск (настройки в %LOCALAPPDATA%\Homie не трогаем).
            .OnBeforeUninstallFastCallback(_ => StartupService.SetEnabled(false))
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
