using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskLite.Models;

namespace DeskLite.Services;

/// <summary>
/// 配置持久化：原子写 + .bak 回退 + 200 ms 合并写盘。
/// v1.5 改善 1：数据根目录额外保存「快捷方式清单.json」，待回收区域每条记录留独立快照文件。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpt = new(AppConfigContext.Default.Options)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _dir;
    private readonly string _file;
    private readonly string _bak;
    private readonly string _manifest;
    private readonly string _recycleDir;
    private bool _dirty;
    private DateTime _requestedAt;

    public AppConfig Config { get; private set; }

    /// <summary>数据根目录：%APPDATA%\DeskLite，逻辑自测时由 DESKLITE_CONFIG_DIR 重定向。</summary>
    public string RootDir => _dir;
    /// <summary>待回收区域目录：被删除的快捷方式在此留一份独立快照，到期随记录一起清除。</summary>
    public string RecycleDir => _recycleDir;
    public string ManifestPath => _manifest;

    public ConfigStore()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("DESKLITE_CONFIG_DIR");
        _dir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLite")
            : overrideDir;
        _file = Path.Combine(_dir, "config.json");
        _bak = Path.Combine(_dir, "config.bak.json");
        _manifest = Path.Combine(_dir, "快捷方式清单.json");
        _recycleDir = Path.Combine(_dir, "待回收区域");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_recycleDir);
        Config = Load();
        EnsureDefaults();
        // 启动时就落地一份清单，避免「没有任何配置变更」时根目录缺少快捷方式记录。
        WriteManifest();
        // v1.7：旧版 tabs → Modules 的迁移必须在启动即写回 config.json，
        // 否则手动模式（启动时不请求保存）会一直保留旧格式，直到用户首次改动才升级。
        Save();
    }

    private AppConfig Load()
    {
        foreach (var path in new[] { _file, _bak })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpt);
                if (cfg != null) return cfg;
            }
            catch { /* 损坏则尝试备份 / 默认值 */ }
        }
        return new AppConfig();
    }

    private void EnsureDefaults()
    {
        // v1.7：无论来自哪个历史版本，落盘一律升到 Schema=3（模块结构）。
        Config.Schema = 3;
        Config.Icon.Mode = Config.Icon.Mode == "manual" ? "manual" : "auto";
        Config.Icon.BubbleMs = Math.Clamp(Config.Icon.BubbleMs, 500, 20000);
        Config.Icon.ConfirmMs = Math.Clamp(Config.Icon.ConfirmMs, 500, 60000);
        Config.Icon.ManualPx = Math.Clamp(Config.Icon.ManualPx, 32, 96);
        Config.Icon.LastSyncedPx = Math.Clamp(Config.Icon.LastSyncedPx, 32, 96);
        Config.Icon.PollMs = Math.Clamp(Config.Icon.PollMs, 500, 10000);
        Config.Recent.Rows = Math.Clamp(Config.Recent.Rows, 1, 5);
        Config.Recent.Cols = Math.Clamp(Config.Recent.Cols, 2, 10);
        Config.Recent.Cap = Math.Clamp(Config.Recent.Cap, 10, 1024);
        Config.Recent.Items ??= new List<RecentItem>();
        // v1.5：旧版本配置没有 recycle 字段，这里补齐并按 1..3650 天钳制保留期。
        if (Config.Recycle == null) Config.Recycle = new RecycleConfig();
        Config.Recycle.Entries ??= new List<RecycleEntry>();
        Config.Recycle.RetentionDays = Math.Clamp(Config.Recycle.RetentionDays, 1, 3650);

        // ---- v1.7 模块迁移 ----
        // 旧配置的顶层 tabs 已由 ShortcutConfig.Tabs 的 setter 自动包装成第一个模块；
        // 全新配置（或手工删空 modules）则在这里补建一个默认模块，保证永远至少一个。
        if (Config.Shortcuts.Modules == null) Config.Shortcuts.Modules = new List<ShortcutModule>();
        if (Config.Shortcuts.Modules.Count == 0) Config.Shortcuts.Modules.Add(new ShortcutModule());
        foreach (var m in Config.Shortcuts.Modules)
        {
            if (string.IsNullOrWhiteSpace(m.Id)) m.Id = ShortcutModule.NewId();
            if (string.IsNullOrWhiteSpace(m.Name)) m.Name = "快捷方式管理";
            m.Tabs ??= new List<ShortcutTab>();
            if (m.Tabs.Count == 0)
                foreach (var name in new[] { "工作", "开发", "娱乐", "素材", "临时" })
                    m.Tabs.Add(new ShortcutTab { Name = name });
            if (m.Tabs.All(t => t.Id != m.ActiveTabId)) m.ActiveTabId = m.Tabs[0].Id;
            // 旧配置或手工编辑过的 JSON 可能缺字段；补齐后再交给 UI 遍历，避免空引用。
            foreach (var tab in m.Tabs)
            {
                tab.Items ??= new List<ShortcutItem>();
                foreach (var item in tab.Items) item.Children ??= new List<ShortcutItem>();
            }
        }
    }

    /// <summary>标记需要保存，由统一计时器调用 FlushIfNeeded 合并落盘。</summary>
    public void RequestSave()
    {
        _dirty = true;
        _requestedAt = DateTime.UtcNow;
    }

    public void FlushIfNeeded()
    {
        if (!_dirty) return;
        if ((DateTime.UtcNow - _requestedAt).TotalMilliseconds < 200) return;
        Save();
    }

    public void Save()
    {
        try
        {
            var tmp = _file + ".tmp";
            // 无 BOM，便于任何 JSON 工具直接读取
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOpt), new UTF8Encoding(false));
            if (File.Exists(_file)) File.Copy(_file, _bak, true);
            File.Move(tmp, _file, true);
            _dirty = false;
            // 改善 1：根目录下始终保存一份当前快捷方式清单（人类可读、可迁移、可核对）。
            WriteManifest();
        }
        catch { /* 写盘失败不阻断运行 */ }
    }

    /// <summary>
    /// 在数据根目录写一份当前所有快捷方式的清单；用 Utf8JsonWriter 手写，保持零反射。
    /// v1.7：按模块分组输出，每个模块自带名称、大类与条目；顶层同时回显第一模块的 tabs 供旧版本读取。
    /// </summary>
    private void WriteManifest()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("app", "DeskLite v1.7");
                w.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O"));
                w.WriteNumber("moduleCount", Config.Shortcuts.Modules.Count);
                // 兼容旧口径：tabCount 统计所有模块的大类总数。
                w.WriteNumber("tabCount", Config.Shortcuts.Modules.Sum(m => m.Tabs.Count));
                w.WriteNumber("recycleCount", Config.Recycle.Entries.Count);
                w.WriteNumber("recycleRetentionDays", Config.Recycle.RetentionDays);
                w.WriteStartArray("modules");
                foreach (var module in Config.Shortcuts.Modules)
                {
                    w.WriteStartObject();
                    w.WriteString("id", module.Id);
                    w.WriteString("name", module.Name);
                    w.WriteStartArray("tabs");
                    foreach (var tab in module.Tabs)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", tab.Id);
                        w.WriteString("name", tab.Name);
                        w.WriteStartArray("shortcuts");
                        foreach (var item in tab.Items)
                        {
                            w.WriteStartObject();
                            w.WriteString("name", item.DisplayName);
                            w.WriteString("type", item.Type);
                            w.WriteString("path", item.Type == "folder" ? item.SourceDir ?? "" : item.Target);
                            w.WriteNumber("children", item.Children?.Count ?? 0);
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllText(_manifest, Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
        }
        catch { /* 清单写失败不影响主配置 */ }
    }

    /// <summary>待回收区快照文件名，供 UI 展示与自测核对。</summary>
    public string SnapshotPath(string entryId) => Path.Combine(_recycleDir, entryId + ".json");

    /// <summary>每移入一条待回收记录，就在根目录的「待回收区域」下留一份独立快照。</summary>
    public void WriteRecycleSnapshot(RecycleEntry e)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("kind", e.Kind);
                w.WriteString("name", e.Name);
                w.WriteNumber("deletedAt", e.DeletedAt);
                w.WriteString("deletedAtLocal",
                    DateTimeOffset.FromUnixTimeMilliseconds(e.DeletedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                // v1.7：标注来源模块，模块级记录额外记录其大类 / 条目总数。
                w.WriteString("moduleName", e.ModuleName ?? "");
                w.WriteString("from", string.IsNullOrWhiteSpace(e.ModuleName)
                    ? (string.IsNullOrWhiteSpace(e.ParentName) ? e.TabName : e.TabName + " › " + e.ParentName)
                    : e.ModuleName + " › " + (string.IsNullOrWhiteSpace(e.ParentName) ? e.TabName : e.TabName + " › " + e.ParentName));
                w.WriteString("sourcePath", e.SourcePath ?? "");
                w.WriteNumber("retainedDays", Config.Recycle.RetentionDays);
                if (e.Tab != null) w.WriteNumber("containedItems", e.Tab.Items?.Count ?? 0);
                if (e.Module != null)
                {
                    w.WriteNumber("containedTabs", e.Module.Tabs?.Count ?? 0);
                    w.WriteNumber("containedItems",
                        e.Module.Tabs?.Sum(t => t.Items?.Count ?? 0) ?? 0);
                }
                if (e.Item != null)
                {
                    w.WriteString("itemType", e.Item.Type);
                    w.WriteString("itemPath", e.Item.Type == "folder" ? e.Item.SourceDir ?? "" : e.Item.Target);
                    w.WriteNumber("children", e.Item.Children?.Count ?? 0);
                }
                w.WriteEndObject();
            }
            File.WriteAllText(SnapshotPath(e.Id), Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
        }
        catch { /* 快照写失败不影响内存中的记录 */ }
    }

    public void DeleteRecycleSnapshot(string entryId)
    {
        try { File.Delete(SnapshotPath(entryId)); }
        catch { /* 文件可能已被手工删除 */ }
    }
}

/// <summary>
/// 需求 0：读取系统桌面图标显示尺寸档位。
/// 主检测用 SPI_GETICONMETRICS 的图标水平间距，并按 DPI 归一化量化为 32 / 48 / 64 / 96 px。
/// </summary>
public static class IconSizeMonitor
{
    private const uint SPI_GETICONMETRICS = 0x002D;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet,
                     lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ICONMETRICS
    {
        public int cbSize, iHorzSpacing, iVertSpacing, iTitleWrap;
        public LOGFONT lfFont;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ICONMETRICS pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>返回量化后的软件内图标像素；读取失败返回 0，由调用方沿用上次值。</summary>
    public static int ReadDesktopIconPx()
    {
        try
        {
            var m = new ICONMETRICS { lfFont = new LOGFONT { lfFaceName = string.Empty } };
            m.cbSize = Marshal.SizeOf<ICONMETRICS>();
            if (!SystemParametersInfo(SPI_GETICONMETRICS, (uint)m.cbSize, ref m, 0)) return 0;
            if (m.iHorzSpacing <= 0) return 0;

            double dpi = GetDpiForSystem() / 96.0;
            double spacing = m.iHorzSpacing / Math.Max(1.0, dpi);
            int px = (int)(Math.Round(spacing * 0.64 / 16.0) * 16);
            return Math.Clamp(px, 32, 96);
        }
        catch { return 0; }
    }
}

/// <summary>需求 1：最近常用 MRU（仅统计从本软件打开）。</summary>
public sealed class RecentStore
{
    private readonly RecentConfig _cfg;
    private readonly Action _onChanged;

    public RecentStore(RecentConfig cfg, Action onChanged)
    {
        _cfg = cfg;
        _onChanged = onChanged;
    }

    public IReadOnlyList<RecentItem> Items => _cfg.Items;

    /// <summary>有效展示上限 = min(N×10, 总上限, 实际记录数)。</summary>
    public int EffectiveMax => Math.Min(Math.Clamp(_cfg.Rows, 1, 5) * 10, Math.Min(Math.Clamp(_cfg.Cap, 10, 1024), _cfg.Items.Count));

    /// <summary>可见网格 = N×M。</summary>
    public int VisibleCount => Math.Clamp(_cfg.Rows, 1, 5) * Math.Clamp(_cfg.Cols, 2, 10);

    public void Record(string path, string title)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hit = _cfg.Items.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
        if (hit != null)
        {
            _cfg.Items.Remove(hit);
            hit.LastOpenedAt = now;
            hit.OpenCount++;
            _cfg.Items.Insert(0, hit);
        }
        else
        {
            _cfg.Items.Insert(0, new RecentItem
            {
                Path = path,
                Title = title,
                LastOpenedAt = now,
                OpenCount = 1,
                ColorIndex = Math.Abs(path.GetHashCode()) % Palette.Colors.Length
            });
        }
        Trim();
        _onChanged();
    }

    /// <summary>列表按最近打开倒序，超出 cap 时移除末尾（LRU / FIFO 在此排序下等价）。</summary>
    public void Trim()
    {
        int cap = Math.Clamp(_cfg.Cap, 10, 1024);
        if (_cfg.Items.Count > cap) _cfg.Items.RemoveRange(cap, _cfg.Items.Count - cap);
    }

    public void Clear() { _cfg.Items.Clear(); _onChanged(); }
}

/// <summary>需求 2 / 2.1 / 2.2：快捷方式解析、文件夹判定与启动。</summary>
public static class ShortcutService
{
    private static readonly string[] ShortcutExts = { ".lnk", ".url" };

    public static bool IsShortcutFile(string path) =>
        ShortcutExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool IsIgnorable(string path)
    {
        try
        {
            if (string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Q12 建议口径：文件夹直接子项全部为 .lnk / .url 快捷方式（忽略隐藏项与 desktop.ini），
    /// 含子文件夹或普通文件则不算「纯快捷方式文件夹」。
    /// </summary>
    public static bool IsPureShortcutFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var entries = Directory.GetFileSystemEntries(dir).Where(p => !IsIgnorable(p)).ToArray();
            return entries.Length > 0 && entries.All(p => !Directory.Exists(p) && IsShortcutFile(p));
        }
        catch { return false; }
    }

    /// <summary>拖入项的显示名：文件夹取目录名，文件取无扩展名文件名。同名判定以此为准。</summary>
    public static string DropDisplayName(string path)
    {
        string p = (path ?? "").Trim();
        return Directory.Exists(p)
            ? Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(p);
    }

    /// <summary>
    /// v1.4 功能完善 2：拖入文件夹的准入判定。
    /// 返回 null 表示通过（纯快捷方式文件夹）；否则返回阻止原因，UI 需弹窗拦截且不生成条目。
    /// 隐藏项与 desktop.ini 按既有口径忽略；空文件夹同样视为不合格。
    /// </summary>
    public static string? FolderRejectReason(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return "文件夹不存在或无法访问";
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir).Where(p => !IsIgnorable(p)).ToArray();
            }
            catch (Exception ex) { return "无法读取文件夹内容（" + ex.GetType().Name + "）"; }

            if (entries.Length == 0) return "文件夹内没有任何快捷方式（.lnk / .url）";
            var bad = entries
                .Where(p => Directory.Exists(p) || !IsShortcutFile(p))
                .Select(p => (Directory.Exists(p) ? "[目录] " : "") + Path.GetFileName(p))
                .ToList();
            if (bad.Count == 0) return null;
            var preview = bad.Take(4).ToList();
            string more = bad.Count > preview.Count ? $" 等 {bad.Count} 项" : "";
            return "内含非快捷方式内容：" + string.Join("、", preview) + more;
        }
        catch (Exception ex) { return "文件夹检查失败（" + ex.GetType().Name + "）"; }
    }

    public static List<ShortcutItem> BuildChildren(string dir)
    {
        var result = new List<ShortcutItem>();
        try
        {
            foreach (var p in Directory.GetFileSystemEntries(dir).Where(p => !IsIgnorable(p)))
            {
                if (Directory.Exists(p) || !IsShortcutFile(p)) continue;
                result.Add(new ShortcutItem
                {
                    Type = "link",
                    Name = Path.GetFileNameWithoutExtension(p),
                    Target = p,
                    ColorIndex = Math.Abs(p.GetHashCode()) % Palette.Colors.Length
                });
            }
        }
        catch { /* 无权限目录返回空列表 */ }
        return result;
    }

    /// <summary>Q13 建议口径：中文按当前区域规则、不区分大小写，升序取第一项作为默认图标。</summary>
    public static string? DefaultIconName(ShortcutItem folder, string rule)
    {
        if (folder.Children.Count == 0) return null;
        var ordered = rule == "alpha-first"
            ? folder.Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            : folder.Children.AsEnumerable();
        return ordered.First().Name;
    }

    public static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty
            });
        }
        catch { /* 目标失效时由 UI 层灰化提示 */ }
    }
}

public static class Palette
{
    /// <summary>图标占位色（DeskBox 深色面板上的蓝紫 / 暖色系）。</summary>
    public static readonly string[] Colors =
    {
        "#6C8CFF", "#E0794F", "#4CC38A", "#B45CF0", "#4AB6D8", "#E5B14C",
        "#EC6C9E", "#7B8CA8", "#58C6B0", "#C87BE0", "#8D9EF0", "#D9A65E"
    };

    public static string Of(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];

    public static string Initial(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "F";
        var s = name.Trim();
        return s.Length >= 2 ? s[..2].ToUpperInvariant() : s[..1].ToUpperInvariant();
    }
}
