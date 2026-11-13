using System.Runtime.InteropServices;
using DeskLite.Models;

namespace DeskLite.Services;

/// <summary>
/// 系统托盘 + 单实例唤起 + 窗口位置记忆。
/// WinUI 3 没有原生托盘控件，这里用 Win32 Shell_NotifyIcon 并对主窗口 WndProc 做子类化：
/// 所有消息仍转发给 WinUI 原始 WndProc，只在 WM_CLOSE / WM_DESTROY / 托盘回调上做拦截。
/// 不引入第三方依赖，不额外创建线程或窗口，符合轻量要求。
/// </summary>
public sealed class TrayHost : IDisposable
{
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_APP = 0x8000;
    private const uint CallbackMessage = WM_APP + 1;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x00000002;
    private const uint TPM_RETURNCMD = 0x00000100;
    private const uint TPM_NONOTIFY = 0x00000080;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const int IDI_APPLICATION = 32512;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInst, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    /// <summary>单实例广播消息：第二实例启动后请求已有实例显示窗口。</summary>
    public static readonly uint ShowMessage = InitShowMessage();

    private static uint InitShowMessage()
    {
        try
        {
            uint m = RegisterWindowMessage("DeskLite_ShowWindow_v1_2");
            return m != 0 ? m : WM_APP + 2;
        }
        catch { return WM_APP + 2; }
    }

    public static void BroadcastShow() => PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);

    public static void BringToFront(IntPtr hWnd)
    {
        ShowWindow(hWnd, 9);  // SW_RESTORE
        ShowWindow(hWnd, 5);  // SW_SHOW
        SetForegroundWindow(hWnd);
    }

    private readonly IntPtr _hwnd;
    private readonly IntPtr _oldProc;
    private readonly IntPtr _hIcon;
    // 必须保持引用，否则委托被 GC 回收后 WndProc 指针失效导致崩溃
    private readonly WndProcDelegate _newProc;
    private readonly Action _onShow;
    private readonly Action _onSettings;
    private readonly Action _onExit;
    private readonly Action _onHide;
    private readonly Action _onSystemChange;
    private readonly Func<bool> _getStartup;
    private readonly Action<bool> _setStartup;
    private bool _iconAdded;
    private bool _forceClose;
    private bool _disposed;

    public bool LastIconAddSucceeded { get; private set; }

    public TrayHost(IntPtr hwnd, Action onShow, Action onSettings, Action onExit, Action onHide,
        Action onSystemChange, Func<bool> getStartup, Action<bool> setStartup)
    {
        _hwnd = hwnd;
        _onShow = onShow;
        _onSettings = onSettings;
        _onExit = onExit;
        _onHide = onHide;
        _onSystemChange = onSystemChange;
        _getStartup = getStartup;
        _setStartup = setStartup;

        _hIcon = LoadAppIcon();
        _iconAdded = AddIcon();
        LastIconAddSucceeded = _iconAdded;

        _newProc = WindowProc;
        _oldProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newProc));
    }

    private static IntPtr LoadAppIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "DeskLite.ico");
            if (File.Exists(path))
            {
                var h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { /* 回退系统图标 */ }
        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private bool AddIcon()
    {
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = CallbackMessage,
                hIcon = _hIcon,
                szTip = "桌面助手 · DeskLite v1.7",
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };
            bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
            if (!ok)
                AppLog.Write("TrayIcon", new InvalidOperationException(
                    "Shell_NotifyIcon(NIM_ADD) 失败，Win32Error=" + Marshal.GetLastWin32Error() +
                    "，cbSize=" + data.cbSize));
            return ok;
        }
        catch (Exception ex) { AppLog.Write("TrayIcon", ex); return false; }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch { /* 忽略 */ }
        _iconAdded = false;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // v1.3：资源管理器拖入的文件由 Win32 WM_DROPFILES 通道接收（XAML 通道作为并存兜底）
            if (msg == FileDropHost.WM_DROPFILES)
            {
                FileDropHost.HandleDropFiles(wParam);
                return IntPtr.Zero;
            }
            if (msg == CallbackMessage)
            {
                var ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_LBUTTONDBLCLK) _onShow();
                else if (ev == WM_RBUTTONUP) ShowMenu();
                return IntPtr.Zero;
            }
            if (msg == ShowMessage)
            {
                _onShow();
                return IntPtr.Zero;
            }
            // v1.4-5：无边框窗口没有系统关闭按钮，但 Alt+F4 / 任务栏关闭仍会发出 WM_CLOSE。
            // 除托盘「退出」放行外，一律隐藏到托盘，保证「只能在设置中退出」的口径。
            if (msg == WM_CLOSE && !_forceClose)
            {
                ShowWindow(hWnd, 0); // SW_HIDE
                _onHide();
                return IntPtr.Zero;  // 拦截关闭，进程继续驻留托盘
            }
            if (msg == WM_SETTINGCHANGE)
            {
                // 系统主题 / 桌面图标间距变化会广播此消息；只置标志，由主计时器节流处理
                _onSystemChange();
            }
            if (msg == WM_DESTROY || msg == WM_QUERYENDSESSION)
            {
                RemoveIcon();
            }
        }
        catch (Exception ex) { AppLog.Write("TrayWndProc", ex); }

        return _oldProc != IntPtr.Zero
            ? CallWindowProc(_oldProc, hWnd, msg, wParam, lParam)
            : IntPtr.Zero;
    }

    private void ShowMenu()
    {
        try
        {
            GetCursorPos(out var p);
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            AppendMenu(menu, MF_STRING, new UIntPtr(1), "显示主界面");
            AppendMenu(menu, MF_STRING, new UIntPtr(2), "打开设置");
            AppendMenu(menu, MF_STRING, new UIntPtr(5), "隐藏主界面");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenu(menu, MF_STRING | (_getStartup() ? MF_CHECKED : 0), new UIntPtr(3), "开机自启");
            AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            AppendMenu(menu, MF_STRING, new UIntPtr(4), "退出");
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                p.X, p.Y, 0, _hwnd, IntPtr.Zero);
            DestroyMenu(menu);
            switch (cmd)
            {
                case 1: _onShow(); break;
                case 2: _onSettings(); break;
                case 3: _setStartup(!_getStartup()); break;
                case 4: _onExit(); break;
                case 5: ShowWindow(_hwnd, 0); _onHide(); break;
            }
        }
        catch (Exception ex) { AppLog.Write("TrayMenu", ex); }
    }

    /// <summary>托盘菜单「退出」：放行 WM_CLOSE，让 Closed 事件走正常清理。</summary>
    public void AllowClose()
    {
        _forceClose = true;
        RemoveIcon();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveIcon();
        try
        {
            if (_oldProc != IntPtr.Zero && _hwnd != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldProc);
        }
        catch { /* 窗口可能已销毁 */ }
        // 图标由进程退出时系统回收；LoadIcon 取得的共享图标不能 DestroyIcon
    }
}

/// <summary>窗口位置 / 尺寸记忆：仅保存正常态，最大化时不覆盖坐标。</summary>
public static class WindowState
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    public static void Restore(IntPtr hwnd, UiConfig ui)
    {
        try
        {
            if (ui.WinW < 640 || ui.WinH < 420) return;
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            if (sw <= 0 || sh <= 0) return;
            int w = Math.Min(ui.WinW, sw + 16), h = Math.Min(ui.WinH, sh + 16);
            int x = Math.Clamp(ui.WinX, -8, Math.Max(0, sw - 240));
            int y = Math.Clamp(ui.WinY, 0, Math.Max(0, sh - 160));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
        }
        catch { /* 位置恢复失败不影响启动 */ }
    }

    public static void Save(IntPtr hwnd, UiConfig ui)
    {
        try
        {
            if (IsZoomed(hwnd)) return;
            if (!GetWindowRect(hwnd, out var r)) return;
            ui.WinX = r.Left;
            ui.WinY = r.Top;
            ui.WinW = r.Right - r.Left;
            ui.WinH = r.Bottom - r.Top;
        }
        catch { /* 忽略 */ }
    }
}
