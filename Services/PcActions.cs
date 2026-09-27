using System.Diagnostics;
using System.Runtime.InteropServices;
using Homie.Native;

namespace Homie.Services;

/// <summary>Свои голосовые команды: поиск фразы и действия с компьютером.</summary>
public static class PcActions
{
    /// <summary>Эти действия выполняются только после отсчёта, который можно отменить.</summary>
    public static bool NeedsCountdown(PcAction a) => a is PcAction.Shutdown or PcAction.Restart or PcAction.SignOut or PcAction.Hibernate;

    public static string Title(PcAction a) => a switch
    {
        PcAction.OpenApp => "Открыть программу / файл / ссылку",
        PcAction.Shutdown => "Выключить компьютер",
        PcAction.Restart => "Перезагрузить",
        PcAction.Sleep => "Спящий режим",
        PcAction.Hibernate => "Гибернация",
        PcAction.Lock => "Заблокировать",
        PcAction.SignOut => "Выйти из учётной записи",
        PcAction.MonitorOff => "Выключить экраны",
        PcAction.Mute => "Звук вкл/выкл",
        PcAction.VolumeUp => "Громче",
        PcAction.VolumeDown => "Тише",
        PcAction.PlayPause => "Пауза / продолжить",
        PcAction.NextTrack => "Следующий трек",
        PcAction.PreviousTrack => "Предыдущий трек",
        _ => a.ToString(),
    };

    /// <summary>Что показать в окошке: «Выключаю компьютер», «Открываю faceitclient».</summary>
    public static string Doing(VoiceShortcut s) => s.Action switch
    {
        PcAction.OpenApp => "Открываю " + DisplayName(s.Path),
        PcAction.Shutdown => "Выключаю компьютер",
        PcAction.Restart => "Перезагружаю компьютер",
        PcAction.SignOut => "Выхожу из учётной записи",
        PcAction.Hibernate => "Гибернация",
        _ => Title(s.Action),
    };

    public static string DisplayName(string path)
    {
        if (path.Contains("://")) return path;
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("faceitclient", StringComparison.OrdinalIgnoreCase) ? "FACEIT AC" : name;
    }

    /// <summary>Своя команда, все слова одной из фраз которой есть в сказанном (в любом падеже). Длиннее фраза — точнее.</summary>
    public static VoiceShortcut? Find(string text, IEnumerable<VoiceShortcut> shortcuts)
    {
        var words = VoiceCommands.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        VoiceShortcut? best = null;
        int bestLength = 0;
        foreach (var s in shortcuts)
            foreach (var phrase in s.Phrases.Split(',', ';'))
            {
                var parts = VoiceCommands.Normalize(phrase).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || parts.Length <= bestLength) continue;
                if (parts.All(p => words.Any(w => w == p || VoiceCommands.SameWord(w, p))))
                {
                    best = s;
                    bestLength = parts.Length;
                }
            }
        return best;
    }

    /// <summary>Выполнить. Вернёт текст ошибки или null.</summary>
    public static string? Run(VoiceShortcut s)
    {
        try
        {
            switch (s.Action)
            {
                case PcAction.OpenApp:
                    if (string.IsNullOrWhiteSpace(s.Path)) return "Не выбрано, что открывать";
                    Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true });
                    break;
                case PcAction.Shutdown: Shutdown("/s /t 0"); break;
                case PcAction.Restart: Shutdown("/r /t 0"); break;
                case PcAction.SignOut: Shutdown("/l"); break;
                case PcAction.Sleep: SetSuspendState(false, false, false); break;
                case PcAction.Hibernate: SetSuspendState(true, false, false); break;
                case PcAction.Lock: LockWorkStation(); break;
                case PcAction.MonitorOff:
                    Win32.PostMessageW(0xFFFF /* HWND_BROADCAST */, 0x0112 /* WM_SYSCOMMAND */, 0xF170 /* SC_MONITORPOWER */, 2);
                    break;
                case PcAction.Mute: Win32.SendChord(0xAD); break;
                case PcAction.VolumeUp: for (int i = 0; i < 5; i++) Win32.SendChord(0xAF); break; // +10%
                case PcAction.VolumeDown: for (int i = 0; i < 5; i++) Win32.SendChord(0xAE); break;
                case PcAction.PlayPause: Win32.SendChord(0xB3); break;
                case PcAction.NextTrack: Win32.SendChord(0xB0); break;
                case PcAction.PreviousTrack: Win32.SendChord(0xB1); break;
            }
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Запуск отменён"; // нажали «Нет» в окне UAC
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
