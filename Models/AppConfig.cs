using System.Text.Json.Serialization;

namespace DeskLite.Models;

/// <summary>配置根对象，对应 %APPDATA%\DeskLite\config.json。</summary>
public sealed class AppConfig
{
    /// <summary>v1.7 起 Schema=3：快捷方式管理升级为可拓展的 N 个同构模块。</summary>
    public int Schema { get; set; } = 3;
    public IconConfig Icon { get; set; } = new();
    public RecentConfig Recent { get; set; } = new();
    public ShortcutConfig Shortcuts { get; set; } = new();
    /// <summary>v1.5 改善 1：删除的快捷方式暂存区，保留 7 天后自动清除。</summary>
    public RecycleConfig Recycle { get; set; } = new();
    public PerfConfig Perf { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
}

/// <summary>待回收区域：删除的条目在此暂存 RetentionDays 天，可还原或提前彻底删除。</summary>
public sealed class RecycleConfig
{
    /// <summary>保留天数，默认 7 天；到期由启动时与低频定时检查自动清除。</summary>
    public int RetentionDays { get; set; } = 7;
    public List<RecycleEntry> Entries { get; set; } = new();
}

/// <summary>
/// 一条待回收记录。Item 保存一层条目或二层子项的完整快照，Tab 保存整个大类的快照，
/// v1.7 起 Module 保存整个快捷方式管理模块的快照，因此还原时能精确回到原模块 /
/// 原大类 / 原父文件夹 / 原位置。
/// </summary>
public sealed class RecycleEntry
{
    public string Id { get; set; } = ShortcutTab.NewId();
    /// <summary>item = 一层条目；child = 二层子项；tab = 整个大类；module = 整个模块。</summary>
    public string Kind { get; set; } = "item";
    public string Name { get; set; } = "";
    /// <summary>删除时刻，UTC Unix 毫秒。</summary>
    public long DeletedAt { get; set; }
    public string TabId { get; set; } = "";
    public string TabName { get; set; } = "";
    /// <summary>v1.7：该记录所属模块；旧版本记录为空，还原时降级到第一个模块。</summary>
    public string? ModuleId { get; set; }
    public string? ModuleName { get; set; }
    public string? ParentId { get; set; }
    public string? ParentName { get; set; }
    /// <summary>在原列表中的位置，还原时尽量插回。</summary>
    public int Index { get; set; }
    /// <summary>该快捷方式对应的原始磁盘路径（仅作展示，不改写磁盘）。</summary>
    public string? SourcePath { get; set; }
    public ShortcutItem? Item { get; set; }
    public ShortcutTab? Tab { get; set; }
    /// <summary>v1.7：Kind=module 时保存整个模块（含全部大类与条目）。</summary>
    public ShortcutModule? Module { get; set; }

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        "module" => "管理模块",
        "tab" => "大类",
        "child" => "二层子项",
        _ => "一层条目"
    };
}

/// <summary>需求 0：图标大小与同步参数。</summary>
public sealed class IconConfig
{
    /// <summary>auto = 跟随系统桌面；manual = 固定使用 ManualPx。</summary>
    public string Mode { get; set; } = "auto";
    public int ManualPx { get; set; } = 48;
    public int LastSyncedPx { get; set; } = 48;
    /// <summary>气泡显示时长，结束后弹出同步请求（默认 3 秒）。</summary>
    public int BubbleMs { get; set; } = 3000;
    /// <summary>同步请求等待时长，超时按 TimeoutAction 处理（默认 5 秒）。</summary>
    public int ConfirmMs { get; set; } = 5000;
    public string TimeoutAction { get; set; } = "accept";
    public int DebounceMs { get; set; } = 300;
    public int PollMs { get; set; } = 2000;
}

/// <summary>需求 1：最近常用。</summary>
public sealed class RecentConfig
{
    /// <summary>N：可见行数。</summary>
    public int Rows { get; set; } = 2;
    /// <summary>M：可见列数。</summary>
    public int Cols { get; set; } = 6;
    /// <summary>总数据上限，可在设置中修改，默认 128。</summary>
    public int Cap { get; set; } = 128;
    /// <summary>lru | fifo。</summary>
    public string Policy { get; set; } = "lru";
    public List<RecentItem> Items { get; set; } = new();
}

public sealed class RecentItem
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public long LastOpenedAt { get; set; }
    public int OpenCount { get; set; }
    public int ColorIndex { get; set; }
}

/// <summary>需求 2 / 2.1 / 2.2：快捷方式管理。v1.7 起支持在其下拓展 N 个同构模块。</summary>
public sealed class ShortcutConfig
{
    /// <summary>
    /// v1.7：N 个功能相同、可独立命名的「快捷方式管理」模块，垂直堆叠在主窗口内。
    /// 每个模块持有自己的大类树与交互状态（当前大类 / 打开的二层文件夹 / 二层滚轮偏移），彼此完全隔离。
    /// 全局设置（Grid2Rows / Grid2Cols / IconRule / AutoOpenFirst）由所有模块共享，保证「功能相同」。
    /// 声明顺序必须早于 Tabs：反序列化新配置时先装载 Modules，随后回显的 Tabs 才不会误触发迁移。
    /// </summary>
    public List<ShortcutModule> Modules { get; set; } = new();

    /// <summary>
    /// 兼容字段：v1.6 及更早的配置只有顶层 tabs，反序列化时由 setter 自动迁移成第一个模块；
    /// 写出时回显第一个模块的大类，使旧版本仍能读回主模块数据。
    /// 运行时代码一律通过 Modules / AppController 访问，不依赖本属性。
    /// </summary>
    public List<ShortcutTab> Tabs
    {
        get => Modules.FirstOrDefault()?.Tabs ?? _legacyTabs;
        set
        {
            _legacyTabs = value ?? new List<ShortcutTab>();
            if (Modules.Count == 0)
                Modules.Add(new ShortcutModule { Tabs = _legacyTabs });
        }
    }

    [JsonIgnore] private List<ShortcutTab> _legacyTabs = new();

    public int Grid2Rows { get; set; } = 3;
    public int Grid2Cols { get; set; } = 3;
    /// <summary>alpha-first = 按名称升序取第一项图标；first = 文件系统顺序第一项。</summary>
    public string IconRule { get; set; } = "alpha-first";
    /// <summary>首次拖入文件夹自动打开二级窗口。</summary>
    public bool AutoOpenFirst { get; set; } = true;
}

/// <summary>
/// v1.7：一个完整的「快捷方式管理」模块实例。可复制 N 份、独立命名、独立维护大类与条目。
/// ActiveTabId / ActiveFolderId / T2Offset 是该模块自己的视图状态，随窗口记忆一起持久化。
/// </summary>
public sealed class ShortcutModule
{
    public string Id { get; set; } = NewId();
    /// <summary>模块显示名，用户可在标题栏「改名」中修改。</summary>
    public string Name { get; set; } = "快捷方式管理";
    public List<ShortcutTab> Tabs { get; set; } = new();
    /// <summary>该模块当前选中的一层大类。</summary>
    public string ActiveTabId { get; set; } = "";
    /// <summary>该模块当前打开的二层 folder；null 表示未打开。</summary>
    public string? ActiveFolderId { get; set; }
    /// <summary>该模块二层收纳的滚轮偏移。</summary>
    public int T2Offset { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

public sealed class ShortcutTab
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "新大类";
    public List<ShortcutItem> Items { get; set; } = new();
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

public sealed class ShortcutItem
{
    public string Id { get; set; } = ShortcutTab.NewId();
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    /// <summary>link = 单个快捷方式；folder = 纯快捷方式文件夹聚合。</summary>
    public string Type { get; set; } = "link";
    public string? SourceDir { get; set; }
    /// <summary>「…」设置覆盖的图标色号；null 表示使用默认规则。</summary>
    public int? IconOverride { get; set; }
    /// <summary>「…」设置覆盖的显示名称；null 表示使用 Name。</summary>
    public string? NameOverride { get; set; }
    public bool AutoOpenedOnce { get; set; }
    public List<ShortcutItem> Children { get; set; } = new();
    public int ColorIndex { get; set; }

    [JsonIgnore]
    public string DisplayName => NameOverride ?? Name;
}

public sealed class PerfConfig
{
    public int IconCacheMax { get; set; } = 256;
    public bool Animation { get; set; } = true;
}

public sealed class UiConfig
{
    /// <summary>system | light | dark。</summary>
    public string Theme { get; set; } = "system";
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>v1.5 可行性分析落地：调用快捷方式自身的系统图标；关闭则回退零开销彩色占位图标。</summary>
    public bool UseRealIcons { get; set; } = true;
    /// <summary>v1.6：提取图标时隐藏 .lnk / .url 左下角的快捷方式箭头角标（默认隐藏，设置页可关）。</summary>
    public bool HideShortcutOverlay { get; set; } = true;
    // 窗口位置记忆（v1.2）；-1 表示尚未记录，启动时沿用默认尺寸
    public int WinX { get; set; } = -1;
    public int WinY { get; set; } = -1;
    public int WinW { get; set; } = -1;
    public int WinH { get; set; } = -1;
}

/// <summary>
/// JSON source generation：去掉运行时反射（启动更快、裁剪友好），
/// 也为将来补齐 MSVC 链接器后启用 Native AOT 预留通路。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class AppConfigContext : JsonSerializerContext
{
}
