using Homie.Native;
using Homie.Services;
using Homie.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Homie;

/// <summary>
/// Панель умного дома над треем — как всплывающие панели Windows 11: открывается по клику
/// на домик, закрывается кликом мимо или Esc.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const double WidthDip = 380, MaxHeightDip = 640;

    private readonly nint _hwnd;
    private readonly Win32.SUBCLASSPROC _wndProc;
    private readonly Action _openSettings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;

    public HomeViewModel Home { get; }

    public FlyoutWindow(HomeViewModel home, Action openSettings)
    {
        Home = home;
        _openSettings = openSettings;
        InitializeComponent();
        Bindings.Update();
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        AppWindow.SetPresenter(presenter);
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "home.ico"));

        // Без системной рамки, скругление 14 рисуем сами — как у остальных окон.
        int corner = Win32.DWMWCP_DONOTROUND;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int border = Win32.DWMWA_COLOR_NONE;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        _wndProc = (h, m, w, l, _, _) => m == Win32.WM_NCCALCSIZE && w != 0 ? 0 : Win32.DefSubclassProc(h, m, w, l);
        Win32.SetWindowSubclass(_hwnd, _wndProc, 1, 0);
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) UpdateRegion(); };

        PlaceAboveTray();
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);

        // Пока панель открыта, состояние устройств обновляется само.
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(20);
        _refreshTimer.Tick += (_, _) => _ = Home.RefreshAsync();
        _refreshTimer.Start();
        _ = Home.RefreshAsync();

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close(); // клик мимо
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            Win32.RemoveWindowSubclass(_hwnd, _wndProc, 1);
        };
    }

    /// <summary>Правый нижний угол основного монитора, над панелью задач.</summary>
    private void PlaceAboveTray()
    {
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new PointInt32(area.X, area.Y)); // сначала на монитор, чтобы масштаб был его
        double scale = Win32.GetDpiForWindow(_hwnd) / 96d;
        int gap = (int)(12 * scale);
        int w = (int)(WidthDip * scale);
        int h = (int)Math.Min(MaxHeightDip * scale, area.Height - 2 * gap);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - gap, area.Y + area.Height - h - gap, w, h));
    }

    private void UpdateRegion()
    {
        var size = AppWindow.Size;
        int d = (int)Math.Round(Root.CornerRadius.TopLeft * 2 * Win32.GetDpiForWindow(_hwnd) / 96d);
        Win32.SetWindowRgn(_hwnd, Win32.CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, d, d), true);
    }

    // ---------- функции для x:Bind ----------

    private Visibility NotSignedIn(bool signedIn) => signedIn ? Visibility.Collapsed : Visibility.Visible;
    private Visibility HasText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    private Visibility HasItems(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    private string SignInHint(string? error) =>
        error ?? "Впиши Client ID своего приложения Яндекса и войди — список устройств и сценариев появится здесь";

    // ---------- действия ----------

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = Home.RefreshAsync();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openSettings();
    }

    private void Scenario_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Scenario scenario) _ = Home.RunScenarioAsync(scenario);
    }

    private void Household_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is Household household && household != Home.SelectedHousehold)
            Home.SelectedHousehold = household;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) Close();
    }
}
