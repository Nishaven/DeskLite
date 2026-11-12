using Microsoft.UI.Xaml.Media.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage.Streams;

namespace DeskLite.Services;

/// <summary>
/// v1.5 起调用快捷方式「自身」的系统图标；v1.6 起默认隐藏左下角快捷方式箭头角标。
///
/// 技术链路（全程纯 Win32 + 纯托管，无新增 NuGet 依赖、无反射）：
///   1. v1.6 无角标通道（默认）：SHGetFileInfoW(SHGFI_SYSICONINDEX) 取系统图像列表
///      句柄与图标索引，再 ImageList_GetIcon(ILD_TRANSPARENT) 取原图标——实测与
///      目标程序本体图标逐像素一致（含 alpha），无快捷方式箭头角标，.lnk / .url 通用。
///   2. 回退通道：SHGetFileInfoW(SHGFI_ICON) 取得快捷方式解析后目标程序的 HICON；
///      Shell 会自动解析 .lnk 的 IconLocation / .url 的关联图标，并叠加快捷方式角标。
///   2. GetIconInfo 取 color/mask 位图，GetDIBits 以 32bpp BI_RGB 读出 BGRA 像素。
///   3. 转成 RGBA 并用 ZLibStream + 自写 PNG chunk/CRC32 编码成 PNG 字节。
///   4. BitmapImage + InMemoryRandomAccessStream 交给 WinUI 的 Image 控件显示。
///
/// 缓存：按「路径 + 最后写入时间 + 长度」做 key，PNG 字节与 BitmapImage 双层缓存，
/// 失败也缓存 null（负缓存），避免每次重绘重复调用 Shell API。任何环节失败均回退
/// 到原来的彩色占位图标，绝不影响主流程。
/// </summary>
public static class ShortcutIconProvider
{
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    private const uint ILD_TRANSPARENT = 0x00000001;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint COINIT_APARTMENTTHREADED = 0x00000002;

    /// <summary>缓存上限与设置页「图标缓存」口径一致，超出后整体清空重建。</summary>
    public static int MaxEntries { get; set; } = 256;

    /// <summary>
    /// v1.6：隐藏快捷方式左下角箭头角标。true 时优先走系统图像列表取无角标原图标，
    /// 任何失败自动回退原通道；开关切换时调用方应 Clear() 缓存并重绘。
    /// </summary>
    public static bool HideOverlay { get; set; } = true;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, byte[]?> PngCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BitmapImage?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    static ShortcutIconProvider()
    {
        // Shell API 要求 COM 已初始化；WinUI 3 的 UI 线程本身是 STA，这里再兜一层。
        // 返回 S_FALSE 表示当前线程已初始化，不进行 CoUninitialize，避免破坏宿主线程。
        try { CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED); }
        catch { /* 初始化失败时由后续调用按失败回退 */ }
    }

    /// <summary>缓存键：文件内容变化后自动失效；文件不存在返回空串（调用方跳过）。</summary>
    public static string CacheKey(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var fi = new FileInfo(path);
            if (!fi.Exists) return "";
            return path + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length + "|" + (HideOverlay ? "noarrow" : "arrow");
        }
        catch { return ""; }
    }

    /// <summary>纯字节接口：不触碰 XAML 对象，可在无窗口的逻辑自测中调用。</summary>
    public static byte[]? GetPng(string path)
    {
        string key = CacheKey(path);
        if (key.Length == 0) return null;
        lock (Gate)
        {
            if (PngCache.TryGetValue(key, out var cached)) return cached;
            byte[]? png = null;
            try { png = ExtractPng(path); }
            catch { png = null; }
            if (PngCache.Count >= MaxEntries) PngCache.Clear();
            PngCache[key] = png;
            return png;
        }
    }

    /// <summary>UI 接口：返回可共享的 BitmapImage；必须在 UI 线程调用。</summary>
    public static BitmapImage? GetIcon(string path)
    {
        string key = CacheKey(path);
        if (key.Length == 0) return null;
        lock (Gate)
        {
            if (ImageCache.TryGetValue(key, out var hit)) return hit;
            BitmapImage? image = null;
            byte[]? png = GetPng(path);
            if (png != null)
            {
                try { image = Decode(png); }
                catch { image = null; }
            }
            if (ImageCache.Count >= MaxEntries)
            {
                ImageCache.Clear();
                PngCache.Clear();
            }
            ImageCache[key] = image;
            return image;
        }
    }

    public static void Clear()
    {
        lock (Gate) { PngCache.Clear(); ImageCache.Clear(); }
    }

    private static BitmapImage Decode(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            // 内存流的 StoreAsync 立即完成，不会切回 UI 线程，故同步等待无死锁风险。
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        image.SetSource(stream);
        return image;
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 520)] public byte[] szDisplayName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)] public byte[] szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public short biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    private static SHFILEINFO NewInfo() => new()
    {
        szDisplayName = new byte[520],
        szTypeName = new byte[160]
    };

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int cb, ref BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private static byte[]? ExtractPng(string path)
    {
        // v1.6：优先走系统图像列表索引（SYSICONINDEX + ImageList_GetIcon）取「无箭头」
        // 原图标——实测与目标程序本体图标逐像素一致（含 alpha 通道）；任一环节失败
        // 自动回退到下面的原通道（带快捷方式角标），再由调用方回退彩色占位图标。
        if (HideOverlay)
        {
            var idx = NewInfo();
            IntPtr himl = SHGetFileInfo(path, 0, ref idx, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
            if (himl != IntPtr.Zero)
            {
                IntPtr raw = ImageList_GetIcon(himl, idx.iIcon, ILD_TRANSPARENT);
                if (raw != IntPtr.Zero)
                {
                    byte[]? clean = HiconToPng(raw);
                    if (clean != null) return clean;
                }
            }
        }

        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        var sfi = NewInfo();
        IntPtr ret = SHGetFileInfo(path, 0, ref sfi, size, SHGFI_ICON);
        if (ret == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
        {
            // 少数路径（如系统目录下的 exe）直接探测失败，改用文件属性推断再试一次。
            sfi = NewInfo();
            ret = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref sfi, size,
                SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
        }
        if (ret == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
        return HiconToPng(sfi.hIcon);
    }

    /// <summary>
    /// v1.6：HICON → PNG 公共转换（GetIconInfo → GetDIBits 32bpp → BGRA 转 RGBA → 最小 PNG 编码）。
    /// 方法内负责销毁 HICON 与 GDI 位图句柄，对带角标 / 无角标两个来源一视同仁。
    /// </summary>
    private static byte[]? HiconToPng(IntPtr hIcon)
    {
        try
        {
            if (!GetIconInfo(hIcon, out ICONINFO ii) || ii.hbmColor == IntPtr.Zero) return null;
            try
            {
                var bm = new BITMAP();
                if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm) == 0) return null;
                int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
                if (w <= 0 || h <= 0 || w > 512 || h > 512) return null;

                var bgra = new byte[w * h * 4];
                var bi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,          // top-down 请求
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0       // BI_RGB
                };
                IntPtr hdc = GetDC(IntPtr.Zero);
                int lines;
                try { lines = GetDIBits(hdc, ii.hbmColor, 0, (uint)h, bgra, ref bi, 0); }
                finally { ReleaseDC(IntPtr.Zero, hdc); }
                if (lines == 0) return null;

                // 图标 color bitmap 为自下而上存储时按行翻转，与实测口径一致。
                bool flip = bm.bmHeight > 0;
                var rgba = new byte[w * h * 4];
                long alphaSum = 0;
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (flip ? h - 1 - y : y) * w * 4;
                    int dstRow = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int s = srcRow + x * 4, d = dstRow + x * 4;
                        byte b = bgra[s], g = bgra[s + 1], r = bgra[s + 2], a = bgra[s + 3];
                        alphaSum += a;
                        rgba[d] = r; rgba[d + 1] = g; rgba[d + 2] = b; rgba[d + 3] = a;
                    }
                }
                // 少数 32bpp 图标 color bitmap 的 alpha 全 0，用 mask 兜底为完全不透明。
                if (alphaSum == 0)
                    for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

                return EncodePng(rgba, w, h);
            }
            finally
            {
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            }
        }
        finally { DestroyIcon(hIcon); }
    }

    // ---------------- 最小 PNG 编码器（RGBA8 / 非隔行 / filter 0） ----------------

    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteInt32BE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt32BE(len, 0, data.Length);
        s.Write(len, 0, 4);
        byte[] tag = Encoding.ASCII.GetBytes(type);
        s.Write(tag, 0, 4);
        s.Write(data, 0, data.Length);
        var crcInput = new byte[4 + data.Length];
        System.Buffer.BlockCopy(tag, 0, crcInput, 0, 4);
        System.Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
        uint crc = Crc32(crcInput);
        var cb = new byte[4];
        WriteInt32BE(cb, 0, (int)crc);
        s.Write(cb, 0, 4);
    }

    private static byte[] EncodePng(byte[] rgba, int w, int h)
    {
        int stride = w * 4;
        var raw = new byte[h * (stride + 1)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (stride + 1)] = 0; // filter: None
            System.Buffer.BlockCopy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        byte[] zlib;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            zlib = ms.ToArray();
        }

        var ihdr = new byte[13];
        WriteInt32BE(ihdr, 0, w);
        WriteInt32BE(ihdr, 4, h);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;

        using var output = new MemoryStream();
        output.Write(PngSignature, 0, PngSignature.Length);
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", zlib);
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }
}
