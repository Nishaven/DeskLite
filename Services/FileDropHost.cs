using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskLite.Services;

/// <summary>
/// v1.3 拖放根因修复：WinUI 3 非打包应用里，仅设置 XAML 的 AllowDrop 不会让主窗口
/// 成为系统拖放目标（实测窗口树既没有 OleDropTargetInterface，也没有 WS_EX_ACCEPTFILES）。
/// 这里在 HWND 层补一条原生通道：DragAcceptFiles + WM_DROPFILES + DragQueryFileW，
/// 与 XAML 的 OLE 通道并存，任一可用即可接收资源管理器拖入的文件/文件夹。
/// </summary>
public static class FileDropHost
{
    public const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint WM_COPYDATA = 0x004A;
    private const uint MSGFLT_ALLOW = 1;

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint GMEM_SHARE = 0x2000;

    /// <summary>拖放落点为窗口客户区坐标，可用于把文件定位到指定标签页。</summary>
    public sealed class DropPayload
    {
        public required IReadOnlyList<string> Paths { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
    }

    /// <summary>由 WndProc 在 UI 线程触发；订阅方直接调用 AppController 即可。</summary>
    public static event Action<DropPayload>? FilesDropped;

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFiles
    {
        public uint pFiles;
        public int ptX;
        public int ptY;
        public int fNC;
        public int fWide;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, IntPtr lpszFile, uint cch);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFileBuf(IntPtr hDrop, uint iFile,
        [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private static IntPtr _hwnd;

    /// <summary>把主窗口登记为系统拖放目标；UIPI 消息过滤用于高低完整性级别不同的场景。</summary>
    public static bool Install(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            DragAcceptFiles(hwnd, true);
            ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            AppLog.Drop("通道已启用：DragAcceptFiles + WM_DROPFILES + 消息过滤");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("FileDropHost.Install", ex);
            return false;
        }
    }

    /// <summary>由 TrayHost 的 WndProc 在收到 WM_DROPFILES 时调用；返回 true 表示消息已消费。</summary>
    public static bool HandleDropFiles(IntPtr hDrop)
    {
        IReadOnlyList<string> paths = Array.Empty<string>();
        int x = 0, y = 0;
        try
        {
            if (hDrop == IntPtr.Zero) return true;
            var locked = GlobalLock(hDrop);
            if (locked != IntPtr.Zero)
            {
                try
                {
                    var header = Marshal.PtrToStructure<DropFiles>(locked);
                    x = header.ptX;
                    y = header.ptY;
                }
                finally { GlobalUnlock(hDrop); }
            }

            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, IntPtr.Zero, 0);
            var list = new List<string>((int)Math.Min(count, 256));
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, IntPtr.Zero, 0);
                if (len == 0 || len > 32768) continue;
                var sb = new StringBuilder((int)len + 2);
                DragQueryFileBuf(hDrop, i, sb, (uint)sb.Capacity);
                var p = sb.ToString();
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
            }
            paths = list;
            AppLog.Drop($"WM_DROPFILES 收到 {list.Count} 项，落点 ({x},{y})：{string.Join(" | ", list)}");
            if (list.Count > 0)
                FilesDropped?.Invoke(new DropPayload { Paths = list, X = x, Y = y });
            else
                AppLog.Drop("警告：HDROP 中未解析出有效路径");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write("FileDropHost.OnDrop", ex);
            return true;
        }
        finally
        {
            try { DragFinish(hDrop); } catch { /* 句柄可能已释放 */ }
        }
    }

    /// <summary>
    /// 端到端自测：在同进程内构造合法 HDROP 并投递 WM_DROPFILES，走与资源管理器完全相同的
    /// WndProc → DragQueryFileW → FilesDropped → AddDroppedPaths 链路。仅由命令行参数触发。
    /// </summary>
    public static bool SelfTest(IntPtr hwnd, string path)
    {
        try
        {
            // 支持 "|" 分隔的多个路径，用于验证一次拖入多项时 DragQueryFileW 的循环解析
            var parts = (path ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
            var payload = Encoding.Unicode.GetBytes(string.Join("\0", parts) + "\0\0");
            int headerSize = Marshal.SizeOf<DropFiles>();
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_SHARE, (nuint)(headerSize + payload.Length));
            if (hGlobal == IntPtr.Zero) { AppLog.Drop("自测失败：GlobalAlloc 返回空"); return false; }

            var ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); AppLog.Drop("自测失败：GlobalLock 返回空"); return false; }
            try
            {
                var header = new DropFiles { pFiles = (uint)headerSize, ptX = 240, ptY = 380, fNC = 0, fWide = 1 };
                Marshal.StructureToPtr(header, ptr, false);
                Marshal.Copy(payload, 0, ptr + headerSize, payload.Length);
            }
            finally { GlobalUnlock(hGlobal); }

            AppLog.Drop($"自测：投递 HDROP，{parts.Length} 项 = {path}");
            return PostMessage(hwnd, WM_DROPFILES, hGlobal, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Write("FileDropHost.SelfTest", ex);
            return false;
        }
    }
}
