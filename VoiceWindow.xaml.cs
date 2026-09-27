using System.Numerics;
using Homie.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Colors = Microsoft.UI.Colors;
using WinRT.Interop;

namespace Homie;

/// <summary>
/// Окошко над треем, как у Алисы: видно, что идёт запись, распознанный текст на лету и результат.
/// Фокус не забирает — можно говорить прямо из игры. Клик по окошку — отменить.
/// </summary>
public sealed partial class VoiceWindow : Window
{
    private const double WidthDip = 380, HeightDip = 118;

    private readonly nint _hwnd;
    private readonly Win32.SUBCLASSPROC _wndProc;
    private readonly DispatcherQueueTimer _pulse;
    private readonly DispatcherQueueTimer _hide;
    private bool _listening, _shown, _tall;
    private float _level;
    private double _phase;

    /// <summary>Окошко закрыли кликом — отменить запись.</summary>
    public event Action? Dismissed;

    public VoiceWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        AppWindow.SetPresenter(presenter);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;

        nint ex = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE);
        ex = (ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE) & ~(nint)Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_EXSTYLE, ex);

        int corner = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int border = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        _wndProc = (h, m, w, l, _, _) => m switch
        {
            Win32.WM_MOUSEACTIVATE => Win32.MA_NOACTIVATE,
            Win32.WM_NCCALCSIZE when w != 0 => 0,
            _ => Win32.DefSubclassProc(h, m, w, l),
        };
        Win32.SetWindowSubclass(_hwnd, _wndProc, 1, 0);
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) UpdateRegion(); };
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);

        Ring.CenterPoint = new Vector3(28, 28, 0);
        Ring.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(110) };

        _pulse = DispatcherQueue.CreateTimer();
        _pulse.Interval = TimeSpan.FromMilliseconds(60);
        _pulse.Tick += (_, _) => Pulse();

        _hide = DispatcherQueue.CreateTimer();
        _hide.IsRepeating = false;
        _hide.Tick += (_, _) => HideNow();

        // XAML начинает рисовать только после первой активации — делаем её заранее за экраном
        // (окно создаётся при запуске Homie), чтобы потом показывать без фокуса.
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 10, 10));
        Activate();
        AppWindow.Hide();
    }

    // ---------- состояния ----------

    public void ShowListening()
    {
        _hide.Stop();
        ResetSize();
        _listening = true;
        SetLook(Colors.White, "", Colors.Black);
        RecDot.Visibility = Visibility.Visible;
        StatusText.Text = "Слушаю…";
        SpeechText.Text = "Говорите";
        SpeechText.Opacity = 0.5;
        ResultText.Visibility = Visibility.Collapsed;
        ShowWindow();
        _pulse.Start();
    }

    public void SetText(string text)
    {
        if (!_listening || text.Length == 0) return;
        SpeechText.Text = Capitalize(text);
        SpeechText.Opacity = 1;
    }

    public void SetLevel(float level) => _level = Math.Max(_level, level);

    public void ShowProcessing(string text)
    {
        _listening = false;
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = "Выполняю…";
        SpeechText.Text = Capitalize(text);
        SpeechText.Opacity = 1;
        SetLook(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), "", Colors.White);
        ShowWindow();
    }

    public void ShowResult(bool ok, string answer)
    {
        _listening = false;
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = ok ? "Готово" : "Не получилось";
        ResultText.Text = answer;
        ResultText.Visibility = Visibility.Visible;
        SetLook(ok ? Color.FromArgb(255, 0x34, 0xC7, 0x59) : Color.FromArgb(255, 0xFF, 0x9F, 0x0A),
            ok ? "" : "", Colors.White);
        ShowWindow();
        HideAfter(ok ? 3 : 5);
    }

    /// <summary>Нейросеть думает над вопросом.</summary>
    public void ShowThinking(string question)
    {
        _listening = false;
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = "Думаю…";
        SpeechText.Text = Capitalize(question);
        SpeechText.Opacity = 0.6;
        ResultText.Visibility = Visibility.Collapsed;
        SetLook(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), "", Colors.White);
        ShowWindow();
    }

    /// <summary>Ответ нейросети: окошко выше, текст крупнее, висит столько, сколько нужно прочитать.</summary>
    public void ShowAnswer(string question, string answer)
    {
        _listening = false;
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = Capitalize(question);
        SpeechText.Text = answer;
        SpeechText.Opacity = 1;
        SpeechText.MaxLines = 6;
        SpeechText.FontSize = 15;
        ResultText.Visibility = Visibility.Collapsed;
        SetLook(Colors.White, "", Colors.Black);
        _tall = true;
        _shown = false; // пересчитать размер
        ShowWindow();
        HideAfter(Math.Clamp(2 + answer.Length * 0.09, 7, 30)); // голос начинается чуть позже текста
    }

    /// <summary>Этот ответ всё ещё на экране (не закрыли и не начали новую команду).</summary>
    public bool IsShowingAnswer(string answer) => _shown && _tall && SpeechText.Text == answer;

    /// <summary>Выключение/перезагрузка: отсчёт, клик по окошку отменяет.</summary>
    public void ShowCountdown(string doing, int secondsLeft)
    {
        _hide.Stop();
        _listening = false;
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = "Клик по окошку или «Хоуми, отмена» — отменить";
        SpeechText.Text = $"{doing} через {secondsLeft}…";
        SpeechText.Opacity = 1;
        ResultText.Visibility = Visibility.Collapsed;
        SetLook(Color.FromArgb(255, 0xFF, 0x45, 0x3A), "", Colors.White);
        ShowWindow();
    }


    public void ShowCancelled(string reason)
    {
        _listening = false;
        if (reason.Length == 0) { HideNow(); return; }
        RecDot.Visibility = Visibility.Collapsed;
        StatusText.Text = reason;
        SetLook(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), "", Colors.White);
        HideAfter(1.8);
    }

    /// <summary>После ответа нейросети — обратно в обычный размер.</summary>
    private void ResetSize()
    {
        if (!_tall) return;
        _tall = false;
        _shown = false;
        SpeechText.MaxLines = 2;
        SpeechText.FontSize = 17;
    }

    public void HideNow()
    {
        _hide.Stop();
        _pulse.Stop();
        _listening = false;
        _shown = false;
        AppWindow.Hide();
        ResetSize();
    }

    // ---------- оформление ----------

    private void SetLook(Color core, string glyph, Color iconColor)
    {
        Core.Fill = new SolidColorBrush(core);
        StateIcon.Glyph = glyph;
        StateIcon.Foreground = new SolidColorBrush(iconColor);
        if (!_listening) Ring.Scale = Vector3.One;
    }

    /// <summary>Кольцо слегка «дышит» и растёт от громкости голоса.</summary>
    private void Pulse()
    {
        if (!_listening) { Ring.Scale = Vector3.One; _pulse.Stop(); return; }
        _phase += 0.06 * 5;
        float s = 1f + 0.05f * (float)Math.Sin(_phase) + Math.Min(_level, 1f) * 0.35f;
        Ring.Scale = new Vector3(s, s, 1);
        _level *= 0.6f;
        RecDot.Opacity = 0.55 + 0.45 * Math.Abs(Math.Sin(_phase / 2));
    }

    private void HideAfter(double seconds)
    {
        _hide.Interval = TimeSpan.FromSeconds(seconds);
        _hide.Start();
    }

    private void ShowWindow()
    {
        if (_shown) return;
        _shown = true;
        PlaceAboveTray();
        AppWindow.Show(false);
        Win32.SetWindowPos(_hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void PlaceAboveTray()
    {
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new PointInt32(area.X, area.Y)); // сначала на монитор, чтобы масштаб был его
        double scale = Win32.GetDpiForWindow(_hwnd) / 96d;
        int gap = (int)(12 * scale), w = (int)(WidthDip * scale), h = (int)((_tall ? 190 : HeightDip) * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - gap, area.Y + area.Height - h - gap, w, h));
    }

    private void UpdateRegion()
    {
        var size = AppWindow.Size;
        int d = (int)Math.Round(14 * 2 * Win32.GetDpiForWindow(_hwnd) / 96d);
        Win32.SetWindowRgn(_hwnd, Win32.CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, d, d), true);
    }

    private void Root_Tapped(object sender, TappedRoutedEventArgs e)
    {
        HideNow();
        Dismissed?.Invoke(); // отменить запись/отсчёт и остановить речь
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}
