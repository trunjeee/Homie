using Homie.Native;

namespace Homie.Services;

public sealed record TrayMenuItem(string Text, Action OnClick, bool IsChecked = false)
{
    public static readonly TrayMenuItem Separator = new("-", () => { });

    /// <summary>Неактивный пункт (серый) — для строк статуса вроде «Качается: CS2 — 63%».</summary>
    public bool IsEnabled { get; init; } = true;

    public static TrayMenuItem Info(string text) => new(text, () => { }) { IsEnabled = false };

    /// <summary>Пункт с подменю (сам по себе не нажимается).</summary>
    public IReadOnlyList<TrayMenuItem>? Children { get; init; }

    public static TrayMenuItem Submenu(string text, IReadOnlyList<TrayMenuItem> children) =>
        new(text, () => { }) { Children = children };
}

/// <summary>
/// Иконка в области уведомлений с нативным (тёмным) контекстным меню.
/// Сообщения приходят в окно-владелец; его оконная процедура должна вызывать <see cref="HandleMessage"/>.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const uint CallbackMessage = Win32.WM_APP + 1;
    private static readonly uint TaskbarCreated = Win32.RegisterWindowMessageW("TaskbarCreated");

    private readonly nint _hwnd;
    private string _iconPath;
    private string _tooltip;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _buildMenu;
    private nint _hIcon;

    public event Action? LeftClick;
    public event Action? DoubleClick;

    public TrayIcon(nint hwnd, string iconPath, string tooltip, Func<IReadOnlyList<TrayMenuItem>> buildMenu)
    {
        _hwnd = hwnd;
        _iconPath = iconPath;
        _tooltip = tooltip;
        _buildMenu = buildMenu;

        try
        {
            Win32.SetPreferredAppMode(2); // ForceDark — тёмное меню, как в самой Windows 11
            Win32.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }

        Add();
    }

    private Win32.NOTIFYICONDATAW CreateData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private void Add()
    {
        LoadIcon();
        var data = CreateData();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref data);

        data.uVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIconW(Win32.NIM_SETVERSION, ref data);
    }

    /// <summary>Меняет иконку (например, пустая/полная корзина). Одинаковый путь — ничего не делает.</summary>
    public void SetIcon(string iconPath)
    {
        if (iconPath == _iconPath) return;
        _iconPath = iconPath;
        LoadIcon();
        var data = CreateData();
        data.uFlags = Win32.NIF_ICON;
        data.hIcon = _hIcon;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>Подсказка при наведении (до 127 символов).</summary>
    public void SetTooltip(string tooltip)
    {
        tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (tooltip == _tooltip) return;
        _tooltip = tooltip;
        var data = CreateData();
        data.uFlags = Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    /// <summary>Стандартное уведомление Windows от иконки в трее (без звука).</summary>
    public void ShowNotification(string title, string text)
    {
        var data = CreateData();
        data.uFlags = Win32.NIF_INFO;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = Win32.NIIF_NOSOUND;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref data);
    }

    private void LoadIcon()
    {
        if (_hIcon != 0) Win32.DestroyIcon(_hIcon);
        int size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, Win32.GetDpiForWindow(_hwnd));
        _hIcon = Win32.LoadImageW(0, _iconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
    }

    /// <summary>Возвращает true, если сообщение обработано.</summary>
    public bool HandleMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg == TaskbarCreated)
        {
            Add(); // Проводник перезапустился — иконку нужно добавить заново
            return true;
        }
        if (msg != CallbackMessage) return false;

        uint evt = (uint)(lParam & 0xFFFF);
        switch (evt)
        {
            case Win32.WM_CONTEXTMENU:
                // В NOTIFYICON_VERSION_4 координаты курсора приходят в wParam.
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                break;
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                LeftClick?.Invoke();
                break;
            case Win32.WM_LBUTTONDBLCLK:
                DoubleClick?.Invoke();
                break;
        }
        return true;
    }

    private void ShowMenu(int x, int y) =>
        ShowMenu(_buildMenu(), x, y, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_BOTTOMALIGN);

    /// <summary>Такое же тёмное нативное меню, но у курсора — например, по правому клику на кнопке панели.</summary>
    public void ShowMenuAtCursor(IReadOnlyList<TrayMenuItem> items)
    {
        Win32.GetCursorPos(out var p);
        ShowMenu(items, p.X, p.Y, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY);
    }

    private void ShowMenu(IReadOnlyList<TrayMenuItem> items, int x, int y, uint flags)
    {
        var commands = new List<TrayMenuItem>(); // номер команды = индекс + 1, общий для всех уровней меню
        nint menu = BuildMenu(items, commands);
        try
        {
            // Без SetForegroundWindow меню не закрывается по клику мимо него.
            Win32.SetForegroundWindow(_hwnd);
            int cmd = Win32.TrackPopupMenuEx(menu, flags, x, y, _hwnd, 0);
            if (cmd > 0) commands[cmd - 1].OnClick();
        }
        finally
        {
            Win32.DestroyMenu(menu); // уничтожает и все вложенные подменю
        }
    }

    private static nint BuildMenu(IReadOnlyList<TrayMenuItem> items, List<TrayMenuItem> commands)
    {
        nint menu = Win32.CreatePopupMenu();
        foreach (var item in items)
        {
            if (item == TrayMenuItem.Separator)
            {
                Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
            }
            else if (item.Children is { } children)
            {
                Win32.AppendMenuW(menu, Win32.MF_POPUP, (nuint)BuildMenu(children, commands), item.Text);
            }
            else
            {
                commands.Add(item);
                uint flags = Win32.MF_STRING | (item.IsChecked ? Win32.MF_CHECKED : 0) | (item.IsEnabled ? 0 : Win32.MF_GRAYED);
                Win32.AppendMenuW(menu, flags, (nuint)commands.Count, item.Text);
            }
        }
        return menu;
    }

    public void Dispose()
    {
        var data = CreateData();
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref data);
        if (_hIcon != 0) Win32.DestroyIcon(_hIcon);
        _hIcon = 0;
    }
}
