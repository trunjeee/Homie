using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace Homie;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Homie.trj.SingleInstance";
    private static Mutex? _singleInstance;
    private HostWindow? _host;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Одна копия — иначе в трее будет два домика и горячие клавиши будут конфликтовать.
        _singleInstance = new Mutex(false, SingleInstanceMutexName);
        bool acquired;
        try { acquired = _singleInstance.WaitOne(Environment.GetCommandLineArgs().Contains("--restarted") ? 5000 : 0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            Exit();
            return;
        }

        // Своего окна на экране нет: иконка в трее, панель по клику, настройки по запросу.
        _host = new HostWindow();
    }

    public static void Restart()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance = null;
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
    }
}
