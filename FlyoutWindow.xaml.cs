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

        ApplyRoomTemplate();
        Home.PropertyChanged += OnHomeChanged;

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
            Home.PropertyChanged -= OnHomeChanged;
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

    // ---------- вид: плитки или список ----------

    private void OnHomeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.TilesView)) ApplyRoomTemplate();
    }

    private void ApplyRoomTemplate() =>
        RoomsList.ItemTemplate = (DataTemplate)Root.Resources[Home.TilesView ? "RoomTilesTemplate" : "RoomListTemplate"];

    private void ViewMode_Click(object sender, RoutedEventArgs e) => Home.TilesView = !Home.TilesView;

    /// <summary>Кнопка показывает, на какой вид переключит: из плиток — в список, из списка — в плитки.</summary>
    private string ViewModeGlyph(bool tiles) => tiles ? "" : "";

    // ---------- плитки: наведение светлее, как у кнопок ----------

    private static readonly Microsoft.UI.Xaml.Media.Brush TileBrush =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x21, 0x24, 0x26));
    private static readonly Microsoft.UI.Xaml.Media.Brush TileHoverBrush =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => ((Grid)sender).Background = TileHoverBrush;
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => ((Grid)sender).Background = TileBrush;

    // ---------- цвет и яркость ----------

    private static readonly (string Name, Windows.UI.Color Color)[] Palette =
    [
        ("Красный", Windows.UI.Color.FromArgb(255, 0xFF, 0x3B, 0x30)),
        ("Оранжевый", Windows.UI.Color.FromArgb(255, 0xFF, 0x95, 0x00)),
        ("Жёлтый", Windows.UI.Color.FromArgb(255, 0xFF, 0xD6, 0x0A)),
        ("Зелёный", Windows.UI.Color.FromArgb(255, 0x34, 0xC7, 0x59)),
        ("Бирюзовый", Windows.UI.Color.FromArgb(255, 0x30, 0xD5, 0xC8)),
        ("Синий", Windows.UI.Color.FromArgb(255, 0x0A, 0x84, 0xFF)),
        ("Фиолетовый", Windows.UI.Color.FromArgb(255, 0xBF, 0x5A, 0xF2)),
        ("Розовый", Windows.UI.Color.FromArgb(255, 0xFF, 0x37, 0x8C)),
    ];

    private static readonly (string Name, int Kelvin)[] Whites = [("Тёплый", 2700), ("Нейтральный", 4500), ("Холодный", 6500)];

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceItem item) ShowDetails(item, (FrameworkElement)sender, withBrightness: false);
    }

    /// <summary>Правый клик (или долгое нажатие пальцем) по устройству — яркость и цвет.</summary>
    private void Device_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceItem { HasDetails: true } item)
        {
            e.Handled = true;
            ShowDetails(item, (FrameworkElement)sender, withBrightness: true);
        }
    }

    private void ShowDetails(DeviceItem item, FrameworkElement anchor, bool withBrightness)
    {
        var panel = new StackPanel { Spacing = 12, Width = 232 };
        panel.Children.Add(new TextBlock { Text = item.Name, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFontSemiBold"], FontSize = 14 });

        if (withBrightness && item.HasBrightness)
        {
            var slider = new Slider { Header = "Яркость", Minimum = 1, Maximum = 100, Value = item.Brightness };
            slider.ValueChanged += (_, args) => item.Brightness = args.NewValue; // отправится с задержкой, когда ползунок остановится
            panel.Children.Add(slider);
        }

        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };

        if (item.HasColor)
        {
            panel.Children.Add(new TextBlock { Text = "Цвет", FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
            var colors = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4, ItemWidth = 58, ItemHeight = 40 };
            foreach (var (name, color) in Palette)
                colors.Children.Add(Swatch(name, color, () => { flyout.Hide(); _ = item.SetColorAsync(color); }));
            panel.Children.Add(colors);
        }

        if (item.HasWhite)
        {
            panel.Children.Add(new TextBlock { Text = "Белый", FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) });
            var whites = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (name, kelvin) in Whites)
                whites.Children.Add(Swatch(name, DeviceItem.WhiteFor(kelvin), () => { flyout.Hide(); _ = item.SetWhiteAsync(kelvin); }, labeled: true));
            panel.Children.Add(whites);
        }

        flyout.ShowAt(anchor);
    }

    /// <summary>Кружок палитры (для белого — с подписью).</summary>
    private static FrameworkElement Swatch(string name, Windows.UI.Color color, Action onClick, bool labeled = false)
    {
        var button = new Button
        {
            Width = labeled ? 72 : 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.5),
            Content = labeled ? new TextBlock { Text = name, FontSize = 11, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) } : null,
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => onClick();
        return button;
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
