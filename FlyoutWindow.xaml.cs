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
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _focusWatch;
    private readonly nint _initialForeground;
    private bool _wasForeground;

    /// <summary>Закрыться, когда пользователь ушёл в другое окно (кликнул мимо панели).</summary>
    private void WatchFocus()
    {
        nint fg = Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOTOWNER);
        if (fg == _hwnd) { _wasForeground = true; return; }
        if (_wasForeground || (fg != 0 && fg != _initialForeground)) Close();
    }

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

        // Окно, открытое из трея, Windows может не сделать активным — тогда «клик мимо» не придёт.
        // Поэтому явно забираем фокус и дополнительно следим: фокус ушёл в другое окно — закрываемся.
        _initialForeground = Win32.GetAncestor(Win32.GetForegroundWindow(), Win32.GA_ROOTOWNER);
        _focusWatch = DispatcherQueue.CreateTimer();
        _focusWatch.Interval = TimeSpan.FromMilliseconds(250);
        _focusWatch.Tick += (_, _) => WatchFocus();
        _focusWatch.Start();
        Root.Loaded += (_, _) => Win32.SetForegroundWindow(_hwnd);
        Closed += (_, _) =>
        {
            Home.PropertyChanged -= OnHomeChanged;
            _refreshTimer.Stop();
            _focusWatch.Stop();
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

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e) => ((Grid)sender).Background = Res("GlassHoverBrush");
    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e) => ((Grid)sender).Background = Res("GlassBrush");

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

    private void ColorDot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is DeviceItem item) ShowDetails(item, (FrameworkElement)sender, withBrightness: false);
    }

    /// <summary>Клик по плитке (не по переключателю) — подробности: яркость, цвет, показания датчиков.</summary>
    private void Tile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        for (var el = e.OriginalSource as DependencyObject; el is not null && el != sender; el = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el))
            if (el is ToggleSwitch) return; // переключатель сам включает/выключает

        if ((sender as FrameworkElement)?.Tag is DeviceItem { HasDetails: true } item)
            ShowDetails(item, (FrameworkElement)sender, withBrightness: true);
    }

    /// <summary>Правый клик (или долгое нажатие пальцем) по устройству — то же самое.</summary>
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
        var dim = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = item.Name, FontSize = 14,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFontSemiBold"],
        });

        var flyout = new Flyout
        {
            Content = panel,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["GlassFlyoutPresenter"],
        };

        // Показания датчиков: подпись слева, значение справа.
        if (withBrightness && item.Readings.Count > 0)
        {
            var table = new Grid { ColumnSpacing = 24, RowSpacing = 6, MinWidth = 220 };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < item.Readings.Count; i++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = item.Readings[i].Label, FontSize = 13, Foreground = dim };
                var value = new TextBlock
                {
                    Text = item.Readings[i].Value, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right,
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFontSemiBold"],
                };
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                table.Children.Add(label);
                table.Children.Add(value);
            }
            panel.Children.Add(table);
        }

        if (withBrightness && item.HasBrightness)
        {
            panel.Children.Add(new TextBlock { Text = "Яркость", FontSize = 12, Foreground = dim });
            var slider = new Slider { Minimum = 1, Maximum = 100, Value = item.Brightness, Width = 274 };
            slider.ValueChanged += (_, args) => item.Brightness = args.NewValue; // отправится, когда ползунок остановится
            panel.Children.Add(slider);
        }

        if (item.HasColor)
        {
            panel.Children.Add(new TextBlock { Text = "Цвет", FontSize = 12, Foreground = dim });
            var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (name, color) in Palette)
                colors.Children.Add(Swatch(name, color, () => { flyout.Hide(); _ = item.SetColorAsync(color); }));
            panel.Children.Add(colors);
        }

        if (item.HasWhite)
        {
            panel.Children.Add(new TextBlock { Text = "Белый", FontSize = 12, Foreground = dim });
            var whites = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var (name, kelvin) in Whites)
                whites.Children.Add(Swatch(name, DeviceItem.WhiteFor(kelvin), () => { flyout.Hide(); _ = item.SetWhiteAsync(kelvin); }));
            panel.Children.Add(whites);
        }

        flyout.ShowAt(anchor);
    }

    /// <summary>Аккуратный кружок палитры; название — во всплывающей подсказке.</summary>
    private static FrameworkElement Swatch(string name, Windows.UI.Color color, Action onClick)
    {
        var fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        var button = new Button
        {
            Width = 26, Height = 26, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = fill,
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
        };
        // Наведение не должно перекрашивать кружок в серый — оставляем его цвет.
        button.Resources["ButtonBackgroundPointerOver"] = fill;
        button.Resources["ButtonBackgroundPressed"] = fill;
        button.Resources["ButtonBorderBrushPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
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
