using DeskLite.Models;
using DeskLite.Services;

namespace DeskLite.Core;

public enum SyncPhase { Idle, Detected, Bubble, Awaiting, Manual }

/// <summary>
/// 业务中枢：需求 0 状态机、需求 1 MRU、需求 2 快捷方式树。
/// 由 UI 层的单一 DispatcherTimer 每 200 ms 调用 Tick()，不自行持有计时器。
/// </summary>
public sealed class AppController
{
    private readonly ConfigStore _store;
    private int _lastObservedPx;
    private int _candidatePx;
    private int _recentOffset;
    /// <summary>v1.7：当前焦点模块。交互（点击/拖放/滚轮）前 UI 会先 SetActiveModule。</summary>
    private string _activeModuleId = "";
    private DateTime _phaseStart = DateTime.UtcNow;
    private DateTime _lastPoll = DateTime.UtcNow;
    private DateTime _lastRecycleCheck = DateTime.UtcNow;

    public AppConfig Config => _store.Config;
    public RecentStore Recent { get; }
    public SyncPhase Phase { get; private set; }
    public int AppPx { get; private set; }
    public int PendingOldPx { get; private set; }
    public int PendingNewPx => _candidatePx;
    public double ConfirmRemainingMs { get; private set; }
    public double ConfirmTotalMs => Math.Max(1, Config.Icon.ConfirmMs);
    public string Status { get; private set; } = "空闲";

    // v1.7：以下三个视图状态不再全局单例，而是落在各自的模块上，模块之间互不干扰。
    public string ActiveTabId
    {
        get => ActiveModule().ActiveTabId;
        private set => ActiveModule().ActiveTabId = value;
    }
    public string? ActiveFolderId
    {
        get => ActiveModule().ActiveFolderId;
        private set => ActiveModule().ActiveFolderId = value;
    }
    public int RecentOffset => _recentOffset;
    public int T2Offset
    {
        get => ActiveModule().T2Offset;
        private set => ActiveModule().T2Offset = value;
    }

    /// <summary>数据 / 布局变化，需要重绘网格。</summary>
    public event Action? Changed;
    /// <summary>仅最近常用可视窗口变化（轮动条拖动），避免重建整窗。</summary>
    public event Action? RecentViewChanged;
    /// <summary>仅二层收纳可视窗口变化。</summary>
    public event Action? FolderViewChanged;
    /// <summary>title, text, kind(ok|warn|info)。</summary>
    public event Action<string, string, string>? ToastRequested;
    /// <summary>状态机推进，只需刷新状态文字 / 进度条。</summary>
    public event Action? StateChanged;

    public AppController()
    {
        _store = new ConfigStore();
        var cfg = _store.Config;
        Recent = new RecentStore(cfg.Recent, () => { _store.RequestSave(); Changed?.Invoke(); });

        _lastObservedPx = IconSizeMonitor.ReadDesktopIconPx();
        if (_lastObservedPx <= 0) _lastObservedPx = cfg.Icon.LastSyncedPx;
        _candidatePx = _lastObservedPx;

        if (cfg.Icon.Mode == "manual")
        {
            Phase = SyncPhase.Manual;
            AppPx = cfg.Icon.ManualPx;
            Status = $"手动模式 · 固定 {AppPx} px";
        }
        else
        {
            // Q6 建议口径：自动模式启动时直接校准到系统档位，不弹历史请求。
            Phase = SyncPhase.Idle;
            AppPx = _lastObservedPx;
            cfg.Icon.LastSyncedPx = _lastObservedPx;
            Status = "空闲";
            _store.RequestSave();
        }
        PendingOldPx = AppPx;
        // v1.7：ConfigStore 已保证至少一个模块；先校准每个模块的当前大类，再选中第一个模块。
        foreach (var m in cfg.Shortcuts.Modules)
            if (m.Tabs.Count == 0 || m.Tabs.All(t => t.Id != m.ActiveTabId))
                m.ActiveTabId = m.Tabs.FirstOrDefault()?.Id ?? "";
        _activeModuleId = cfg.Shortcuts.Modules[0].Id;

        // v1.5 改善 1：启动即清理过期项（此时 Toast 尚未订阅，故不通知），
        // 并清掉配置里已不存在记录的孤立快照文件。
        _lastRecycleCheck = DateTime.UtcNow;
        PurgeExpiredRecycle(false);
        SweepOrphanSnapshots();
    }

    /// <summary>统一计时器驱动：合并写盘 + 7 天过期复查 + 检测 + 状态机推进。</summary>
    public void Tick()
    {
        _store.FlushIfNeeded();
        var now = DateTime.UtcNow;

        // 过期清理是纯内存比较，低频（60 s）复查即可，几乎不占 CPU。
        if ((now - _lastRecycleCheck).TotalSeconds >= 60)
        {
            _lastRecycleCheck = now;
            PurgeExpiredRecycle(true);
        }

        if (Config.Icon.Mode == "manual")
        {
            if (Phase != SyncPhase.Manual)
            {
                Phase = SyncPhase.Manual;
                AppPx = Config.Icon.ManualPx;
                Status = $"手动模式 · 固定 {AppPx} px";
                Changed?.Invoke();
            }
            StateChanged?.Invoke();
            return;
        }

        // 检测：事件订阅由窗口激活/可见性变化承担，此处为 2 s 低频轮询兜底。
        if ((now - _lastPoll).TotalMilliseconds >= Config.Icon.PollMs)
        {
            _lastPoll = now;
            int px = IconSizeMonitor.ReadDesktopIconPx();
            if (px > 0 && px != _lastObservedPx)
            {
                _lastObservedPx = px;
                _candidatePx = px;
                if (Phase == SyncPhase.Idle || Phase == SyncPhase.Bubble)
                {
                    Phase = SyncPhase.Detected;
                    _phaseStart = now;
                }
            }
        }

        switch (Phase)
        {
            case SyncPhase.Detected:
                if ((now - _phaseStart).TotalMilliseconds >= Config.Icon.DebounceMs)
                {
                    int again = IconSizeMonitor.ReadDesktopIconPx();
                    if (again > 0) { _candidatePx = again; _lastObservedPx = again; }
                    if (_candidatePx == AppPx)
                    {
                        Phase = SyncPhase.Idle;
                        break;
                    }
                    PendingOldPx = AppPx;
                    Phase = SyncPhase.Bubble;
                    _phaseStart = now;
                    ToastRequested?.Invoke(
                        "检测到桌面图标大小变化",
                        $"桌面图标：{PendingOldPx} px → {PendingNewPx} px；{Config.Icon.BubbleMs / 1000} 秒后将请求同步。",
                        "warn");
                    StateChanged?.Invoke();
                }
                break;

            case SyncPhase.Bubble:
                if ((now - _phaseStart).TotalMilliseconds >= Config.Icon.BubbleMs)
                {
                    Phase = SyncPhase.Awaiting;
                    _phaseStart = now;
                    ConfirmRemainingMs = Config.Icon.ConfirmMs;
                    StateChanged?.Invoke();
                }
                break;

            case SyncPhase.Awaiting:
                ConfirmRemainingMs = Math.Max(0, Config.Icon.ConfirmMs - (now - _phaseStart).TotalMilliseconds);
                if (ConfirmRemainingMs <= 0)
                    FinishSync(Config.Icon.TimeoutAction == "accept", "超时默认");
                else
                    StateChanged?.Invoke();
                break;
        }
    }

    /// <summary>窗口重新激活时立即校准一次（事件驱动补充）。</summary>
    /// <summary>
    /// 请求下一次 Tick 立即重新采样。
    /// v1.2 修正：不再在此处预读并更新基准值，否则窗口激活 / 系统设置事件
    /// 会把刚发生的图标变化「吞掉」，导致需求 0 的气泡与同步请求不触发。
    /// </summary>
    public void RecheckNow()
    {
        _lastPoll = DateTime.UtcNow - TimeSpan.FromMilliseconds(Config.Icon.PollMs);
    }

    public void ConfirmSync() => FinishSync(true, "用户确认");
    public void DismissSync() => FinishSync(false, "用户选择暂不同步");

    private void FinishSync(bool accept, string how)
    {
        if (Phase != SyncPhase.Awaiting) return;
        if (accept)
        {
            AppPx = _candidatePx;
            Config.Icon.LastSyncedPx = _candidatePx;
            _store.RequestSave();
            Status = $"已同步 {AppPx} px";
            ToastRequested?.Invoke("已同步图标大小", $"来源：{how}；软件内图标尺寸已更新为 {AppPx} px。", "ok");
        }
        else
        {
            Status = "空闲（本次不同步）";
            ToastRequested?.Invoke("已放弃本次同步", $"来源：{how}；保持 {AppPx} px，桌面新尺寸 {_candidatePx} px 仅记录不应用。", "warn");
        }
        Phase = SyncPhase.Idle;
        ConfirmRemainingMs = 0;
        _lastPoll = DateTime.UtcNow;
        Changed?.Invoke();
        StateChanged?.Invoke();
    }

    public void SwitchMode(string mode)
    {
        Config.Icon.Mode = mode == "manual" ? "manual" : "auto";
        if (Config.Icon.Mode == "manual")
        {
            Phase = SyncPhase.Manual;
            AppPx = Config.Icon.ManualPx;
            Status = $"手动模式 · 固定 {AppPx} px";
        }
        else
        {
            Phase = SyncPhase.Idle;
            int px = IconSizeMonitor.ReadDesktopIconPx();
            if (px > 0)
            {
                _lastObservedPx = px;
                _candidatePx = px;
                AppPx = px;
                Config.Icon.LastSyncedPx = px;
            }
            Status = "空闲";
        }
        _lastPoll = DateTime.UtcNow;
        _store.RequestSave();
        Changed?.Invoke();
    }

    public void SetManualPx(int px)
    {
        Config.Icon.ManualPx = Math.Clamp(px, 32, 96);
        if (Config.Icon.Mode == "manual")
        {
            AppPx = Config.Icon.ManualPx;
            Status = $"手动模式 · 固定 {AppPx} px";
            Changed?.Invoke();
        }
        _store.RequestSave();
    }

    public void SetRows(int n) { Config.Recent.Rows = Math.Clamp(n, 1, 5); _recentOffset = 0; SaveChanged(); }
    public void SetCols(int m) { Config.Recent.Cols = Math.Clamp(m, 2, 10); _recentOffset = 0; SaveChanged(); }
    public void SetCap(int cap)
    {
        Config.Recent.Cap = Math.Clamp(cap, 10, 1024);
        Recent.Trim();
        _recentOffset = 0;
        SaveChanged();
    }
    public void SetBubbleSeconds(int s) { Config.Icon.BubbleMs = Math.Clamp(s, 1, 20) * 1000; SaveChanged(); }
    public void SetConfirmSeconds(int s) { Config.Icon.ConfirmMs = Math.Clamp(s, 1, 30) * 1000; SaveChanged(); }
    public void SetTimeoutAction(string a) { Config.Icon.TimeoutAction = a; SaveChanged(); }
    public void SetPolicy(string p) { Config.Recent.Policy = p == "fifo" ? "fifo" : "lru"; SaveChanged(); }
    public void SetIconRule(string r) { Config.Shortcuts.IconRule = r; SaveChanged(); }
    public void SetAutoOpenFirst(bool b) { Config.Shortcuts.AutoOpenFirst = b; SaveChanged(); }

    private void SaveChanged() { _store.RequestSave(); Changed?.Invoke(); }

    // ---------- 需求 1 ----------

    public void OpenRecentFile(RecentItem item)
    {
        Recent.Record(item.Path, item.Title);
        ShortcutService.Launch(item.Path);
    }

    public void SetRecentPosition(double p)
    {
        int span = Math.Max(0, Recent.EffectiveMax - Recent.VisibleCount);
        int byPage = Recent.VisibleCount;
        _recentOffset = byPage <= 0 ? 0 : (int)Math.Round(p * span / byPage) * byPage;
        _recentOffset = Math.Clamp(_recentOffset, 0, span);
        RecentViewChanged?.Invoke();
    }

    /// <summary>返回当前 N×M 可见窗口的数据（不足补 null）。</summary>
    public List<RecentItem?> RecentWindow()
    {
        int max = Recent.EffectiveMax, view = Recent.VisibleCount;
        _recentOffset = Math.Clamp(_recentOffset, 0, Math.Max(0, max - view));
        var list = Config.Recent.Items.Take(max).Skip(_recentOffset).Take(view).Cast<RecentItem?>().ToList();
        while (list.Count < view) list.Add(null);
        return list;
    }

    // ---------- v1.7：快捷方式管理模块（可在下方复制 N 份） ----------

    /// <summary>活动模块的大类列表。所有原有业务方法经此访问，天然落在当前模块。</summary>
    private List<ShortcutTab> Tabs => ActiveModule().Tabs;

    public IReadOnlyList<ShortcutModule> Modules => Config.Shortcuts.Modules;
    public string ActiveModuleId => _activeModuleId;
    public int Grid2View =>
        Math.Clamp(Config.Shortcuts.Grid2Rows, 1, 5) * Math.Clamp(Config.Shortcuts.Grid2Cols, 1, 5);

    public ShortcutModule ActiveModule() =>
        Config.Shortcuts.Modules.FirstOrDefault(m => m.Id == _activeModuleId)
        ?? Config.Shortcuts.Modules[0];

    public ShortcutModule? FindModule(string id) =>
        Config.Shortcuts.Modules.FirstOrDefault(m => m.Id == id);

    /// <summary>切换焦点模块；UI 在任何模块内交互前先调用，保证业务方法作用于正确模块。</summary>
    public void SetActiveModule(string id)
    {
        if (_activeModuleId == id) return;
        if (!Config.Shortcuts.Modules.Any(m => m.Id == id)) return;
        _activeModuleId = id;
        Changed?.Invoke();
    }

    private void EnsureActiveModuleValid()
    {
        if (Config.Shortcuts.Modules.Count == 0)
            Config.Shortcuts.Modules.Add(NewBlankModule());
        if (!Config.Shortcuts.Modules.Any(m => m.Id == _activeModuleId))
            _activeModuleId = Config.Shortcuts.Modules[0].Id;
    }

    /// <summary>还原回收记录时静默定位到原模块，不触发重绘（后面会统一 SaveChanged）。</summary>
    private void SwitchModuleSilently(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && Config.Shortcuts.Modules.Any(m => m.Id == id))
            _activeModuleId = id;
    }

    // 按模块的纯查询：UI 为每个模块独立渲染，不依赖「当前焦点模块」。
    public ShortcutTab ActiveTabOf(ShortcutModule m) =>
        m.Tabs.FirstOrDefault(t => t.Id == m.ActiveTabId) ?? m.Tabs[0];

    public ShortcutItem? ActiveFolderOf(ShortcutModule m) =>
        m.ActiveFolderId == null ? null :
        m.Tabs.SelectMany(t => t.Items).FirstOrDefault(i => i.Id == m.ActiveFolderId && i.Type == "folder");

    public List<ShortcutItem?> FolderWindowFor(ShortcutModule m)
    {
        var folder = ActiveFolderOf(m);
        int view = Grid2View;
        if (folder == null) return Enumerable.Repeat<ShortcutItem?>(null, view).ToList();
        int span = Math.Max(0, folder.Children.Count - view);
        m.T2Offset = Math.Clamp(m.T2Offset, 0, span);
        var list = folder.Children.Skip(m.T2Offset).Take(view).Cast<ShortcutItem?>().ToList();
        while (list.Count < view) list.Add(null);
        return list;
    }

    public ShortcutTab? FindTab(string id) =>
        Config.Shortcuts.Modules.SelectMany(m => m.Tabs).FirstOrDefault(t => t.Id == id);

    /// <summary>按大类 Id 反查所属模块，供拖放落点定位。</summary>
    public ShortcutModule? ModuleOfTab(string tabId) =>
        Config.Shortcuts.Modules.FirstOrDefault(m => m.Tabs.Any(t => t.Id == tabId));

    private static ShortcutModule NewBlankModule(string? name = null)
    {
        var m = new ShortcutModule { Name = name ?? "快捷方式管理" };
        foreach (var n in new[] { "工作", "开发", "娱乐", "素材", "临时" })
            m.Tabs.Add(new ShortcutTab { Name = n });
        m.ActiveTabId = m.Tabs[0].Id;
        return m;
    }

    private static ShortcutItem CloneItem(ShortcutItem i) => new()
    {
        Name = i.Name, Target = i.Target, Type = i.Type, SourceDir = i.SourceDir,
        IconOverride = i.IconOverride, NameOverride = i.NameOverride,
        AutoOpenedOnce = i.AutoOpenedOnce, ColorIndex = i.ColorIndex,
        Children = i.Children.Select(CloneItem).ToList()
    };

    private static ShortcutModule CloneModule(ShortcutModule src, string? name = null)
    {
        var copy = new ShortcutModule { Name = name ?? src.Name + " · 副本" };
        foreach (var t in src.Tabs)
        {
            var nt = new ShortcutTab { Name = t.Name };
            foreach (var i in t.Items) nt.Items.Add(CloneItem(i));
            copy.Tabs.Add(nt);
        }
        copy.ActiveTabId = copy.Tabs.FirstOrDefault()?.Id ?? "";
        // 视图状态不复制：新模块从「未打开二层 / 滚轮归零」开始。
        return copy;
    }

    /// <summary>
    /// 新增模块。copyFrom=true 时以 sourceId（默认当前模块）为模板连全部大类与条目复制，
    /// 内部 Id 全部重新生成，之后两个模块互不影响；否则创建带 5 个默认大类的空白模块。
    /// </summary>
    public string AddModule(bool copyFrom, string? sourceId = null)
    {
        var src = sourceId != null ? FindModule(sourceId) : ActiveModule();
        var m = copyFrom && src != null
            ? CloneModule(src)
            : NewBlankModule($"快捷方式管理 {Config.Shortcuts.Modules.Count + 1}");
        Config.Shortcuts.Modules.Add(m);
        _activeModuleId = m.Id;
        SaveChanged();
        return m.Id;
    }

    /// <summary>以指定模块为模板复制一份并插入到其后。</summary>
    public string DuplicateModule(string id)
    {
        var src = FindModule(id);
        if (src == null) return "";
        var copy = CloneModule(src);
        int at = Math.Clamp(Config.Shortcuts.Modules.IndexOf(src) + 1, 0, Config.Shortcuts.Modules.Count);
        Config.Shortcuts.Modules.Insert(at, copy);
        _activeModuleId = copy.Id;
        SaveChanged();
        return copy.Id;
    }

    public bool RenameModule(string id, string name)
    {
        var m = FindModule(id);
        if (m == null || string.IsNullOrWhiteSpace(name)) return false;
        m.Name = name.Trim();
        SaveChanged();
        return true;
    }

    /// <summary>上移 / 下移：delta 为 -1 或 +1，越界自动钳制。</summary>
    public void MoveModule(string id, int delta)
    {
        var list = Config.Shortcuts.Modules;
        int i = list.FindIndex(m => m.Id == id);
        if (i < 0) return;
        int j = Math.Clamp(i + delta, 0, list.Count - 1);
        if (i == j) return;
        (list[i], list[j]) = (list[j], list[i]);
        SaveChanged();
    }

    /// <summary>删除整个模块（连同全部大类与条目）→ 待回收区，7 天内可整体还原；至少保留一个。</summary>
    public bool DeleteModule(string id)
    {
        if (Config.Shortcuts.Modules.Count <= 1)
        {
            ToastRequested?.Invoke("无法删除模块", "至少需要保留一个「快捷方式管理」模块。", "warn");
            return false;
        }
        var m = FindModule(id);
        if (m == null) return false;
        AddToRecycle(new RecycleEntry
        {
            Kind = "module", Name = m.Name,
            DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Index = Config.Shortcuts.Modules.IndexOf(m),
            ModuleId = m.Id, ModuleName = m.Name, Module = m
        });
        Config.Shortcuts.Modules.Remove(m);
        EnsureActiveModuleValid();
        SaveChanged();
        return true;
    }

    // ---------- 需求 2 ----------

    public ShortcutTab ActiveTab() =>
        Tabs.FirstOrDefault(t => t.Id == ActiveTabId) ?? Tabs[0];

    public ShortcutItem? ActiveFolder() =>
        ActiveFolderId == null ? null :
        Tabs.SelectMany(t => t.Items).FirstOrDefault(i => i.Id == ActiveFolderId && i.Type == "folder");

    public void SetActiveTab(string id) { ActiveTabId = id; Changed?.Invoke(); }

    public void AddTab(string? name = null)
    {
        var tab = new ShortcutTab { Name = name ?? $"新大类 {Tabs.Count + 1}" };
        Tabs.Add(tab);
        ActiveTabId = tab.Id;
        SaveChanged();
    }

    public void OpenShortcut(ShortcutItem item)
    {
        if (item.Type == "folder")
        {
            ActiveFolderId = item.Id;
            T2Offset = 0;
            Changed?.Invoke();
            return;
        }
        Recent.Record(item.Target, item.DisplayName);
        ShortcutService.Launch(item.Target);
    }

    public void CloseFolder() { ActiveFolderId = null; T2Offset = 0; Changed?.Invoke(); }

    /// <summary>2.2 的「…」：覆盖显示名称与显示图标，不改写源文件夹。</summary>
    public void ApplyFolderSettings(string id, string? name, int? iconIndex)
    {
        var item = Tabs.SelectMany(t => t.Items).FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        item.NameOverride = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        item.IconOverride = iconIndex;
        SaveChanged();
    }

    /// <summary>2.2 的默认图标名称（字母排序第一项），用于聚合图标文字。</summary>
    public string DefaultIconLabel(ShortcutItem folder)
    {
        if (folder.IconOverride.HasValue) return Palette.Initial(folder.DisplayName);
        return Palette.Initial(ShortcutService.DefaultIconName(folder, Config.Shortcuts.IconRule) ?? folder.DisplayName);
    }

    public List<ShortcutItem?> FolderWindow() => FolderWindowFor(ActiveModule());

    public void SetFolderPosition(double p)
    {
        var folder = ActiveFolder();
        if (folder == null) return;
        int view = Math.Clamp(Config.Shortcuts.Grid2Rows, 1, 5) * Math.Clamp(Config.Shortcuts.Grid2Cols, 1, 5);
        int span = Math.Max(0, folder.Children.Count - view);
        T2Offset = Math.Clamp((int)Math.Round(p * span), 0, span);
        FolderViewChanged?.Invoke();
    }

    /// <summary>v1.4 功能完善 4：鼠标滚轮按「一行」线性滚动，clamp 到有效区间。</summary>
    public void ScrollRecentBy(int items)
    {
        int span = Math.Max(0, Recent.EffectiveMax - Recent.VisibleCount);
        int next = Math.Clamp(_recentOffset + items, 0, span);
        if (next == _recentOffset) return;
        _recentOffset = next;
        RecentViewChanged?.Invoke();
    }

    /// <summary>v1.4 功能完善 4：二层收纳滚轮按一行（默认 3 项）滚动。</summary>
    public void ScrollFolderBy(int items)
    {
        var folder = ActiveFolder();
        if (folder == null) return;
        int view = Math.Clamp(Config.Shortcuts.Grid2Rows, 1, 5) * Math.Clamp(Config.Shortcuts.Grid2Cols, 1, 5);
        int span = Math.Max(0, folder.Children.Count - view);
        int next = Math.Clamp(T2Offset + items, 0, span);
        if (next == T2Offset) return;
        T2Offset = next;
        FolderViewChanged?.Invoke();
    }

    public int FolderChildCount() => ActiveFolder()?.Children.Count ?? 0;
    public int FolderChildCountOf(ShortcutModule m) => ActiveFolderOf(m)?.Children.Count ?? 0;

    // ---------- v1.4 功能完善 1 / 2 / 3 ----------

    private ShortcutTab ResolveTab(string? targetTabId) =>
        Tabs.FirstOrDefault(t => t.Id == (targetTabId ?? ActiveTabId))
        ?? Tabs[0];

    private static bool SameName(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 功能完善 1：返回本次拖放中与目标标签页已有项同名的显示名列表。
    /// 已被功能完善 2 判定为阻止的文件夹不参与同名询问。UI 据此弹窗询问「合并 / 保留」。
    /// </summary>
    public List<string> FindSameNameConflicts(IEnumerable<string> paths, string? targetTabId = null)
    {
        var tab = ResolveTab(targetTabId);
        var existing = tab.Items
            .SelectMany(i => new[] { i.DisplayName, i.Name })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path) && ShortcutService.FolderRejectReason(path) != null) continue;
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            string name = ShortcutService.DropDisplayName(path);
            if (existing.Contains(name.Trim()) && !result.Any(n => SameName(n, name)))
                result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// 2.1 / 2.2 + v1.4 功能完善 1 / 2：接收拖入的路径（文件或文件夹）。
    /// 不合格文件夹一律阻止且不生成条目（UI 层已先行过滤，此处为兜底）；
    /// mergeSameName=true 时同名项并入已有条目，false 时保留为两份独立条目。
    /// </summary>
    public void AddDroppedPaths(IEnumerable<string> paths, string? targetTabId = null, bool mergeSameName = false)
    {
        var tab = ResolveTab(targetTabId);
        bool firstFolderAutoOpened = false;
        int added = 0, merged = 0, blocked = 0;

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                string? reason = ShortcutService.FolderRejectReason(path);
                if (reason != null)
                {
                    blocked++;
                    string dirName = ShortcutService.DropDisplayName(path);
                    ToastRequested?.Invoke("已阻止文件夹加入",
                        $"「{dirName}」{reason}。仅支持全部由 .lnk / .url 快捷方式组成的文件夹。", "warn");
                    AppLog.Drop($"已阻止文件夹：{path} · {reason}");
                    continue;
                }

                var candidate = new ShortcutItem
                {
                    Type = "folder",
                    Name = ShortcutService.DropDisplayName(path),
                    SourceDir = path,
                    Children = ShortcutService.BuildChildren(path),
                    ColorIndex = Math.Abs(path.GetHashCode()) % Palette.Colors.Length
                };
                var same = mergeSameName
                    ? tab.Items.FirstOrDefault(i => SameName(i.DisplayName, candidate.Name) || SameName(i.Name, candidate.Name))
                    : null;
                if (same != null) { MergeInto(same, candidate); merged++; continue; }

                tab.Items.Add(candidate);
                added++;
                if (!candidate.AutoOpenedOnce)
                {
                    candidate.AutoOpenedOnce = true;
                    if (Config.Shortcuts.AutoOpenFirst && !firstFolderAutoOpened)
                    {
                        ActiveTabId = tab.Id;
                        ActiveFolderId = candidate.Id;
                        firstFolderAutoOpened = true;
                    }
                }
                var firstChild = ShortcutService.DefaultIconName(candidate, Config.Shortcuts.IconRule);
                ToastRequested?.Invoke("已生成二层快捷收纳图标",
                    $"「{candidate.DisplayName}」含 {candidate.Children.Count} 个快捷方式，默认图标取字母排序第一项：{firstChild ?? "无"}。", "ok");
            }
            else if (File.Exists(path))
            {
                var candidate = new ShortcutItem
                {
                    Type = "link",
                    Name = ShortcutService.DropDisplayName(path),
                    Target = path,
                    ColorIndex = Math.Abs(path.GetHashCode()) % Palette.Colors.Length
                };
                var same = mergeSameName
                    ? tab.Items.FirstOrDefault(i => SameName(i.DisplayName, candidate.Name) || SameName(i.Name, candidate.Name))
                    : null;
                if (same != null) { MergeInto(same, candidate); merged++; continue; }

                tab.Items.Add(candidate);
                added++;
                ToastRequested?.Invoke("快捷方式已加入标签页", $"「{Path.GetFileName(path)}」已定位到「{tab.Name}」标签页。", "ok");
            }
        }

        if (merged > 0)
            ToastRequested?.Invoke("同名项已合并", $"本次 {merged} 个同名项已并入已有条目，未生成重复图标。", "ok");
        if (blocked > 0)
            ToastRequested?.Invoke("本次有文件夹被阻止",
                $"{blocked} 个文件夹因含非快捷方式内容未加入；其余 {added + merged} 项已处理。", "warn");
        if (added + merged + blocked > 0) SaveChanged();
    }

    /// <summary>同名合并：两个文件夹按 Target 去重并入子项；类型不同或均为 link 时复用条目并更新内容。</summary>
    private static void MergeInto(ShortcutItem target, ShortcutItem incoming)
    {
        if (target.Type == "folder" && incoming.Type == "folder")
        {
            var seen = new HashSet<string>(target.Children.Select(c => c.Target), StringComparer.OrdinalIgnoreCase);
            foreach (var child in incoming.Children)
            {
                if (string.IsNullOrWhiteSpace(child.Target) || !seen.Add(child.Target)) continue;
                target.Children.Add(child);
            }
            if (!string.IsNullOrWhiteSpace(incoming.SourceDir)) target.SourceDir = incoming.SourceDir;
            return;
        }
        target.Type = incoming.Type;
        target.Name = incoming.Name;
        target.Target = incoming.Target;
        target.SourceDir = incoming.SourceDir;
        target.Children = incoming.Children;
        target.ColorIndex = incoming.ColorIndex;
        // 用户改过的显示名 / 图标色 / 首次展开标记保留，不被合并覆盖。
    }

    /// <summary>功能完善 3：按 Id 查找一层条目或二层子项。</summary>
    public ShortcutItem? FindItem(string id) =>
        Tabs
            .SelectMany(t => t.Items.SelectMany(i => new[] { i }.Concat(i.Children)))
            .FirstOrDefault(i => i.Id == id);

    /// <summary>功能完善 3：修改显示名、目标路径（文件夹为源目录）、图标色。</summary>
    public bool UpdateItem(string id, string? name, string? target, int? colorIndex)
    {
        var item = FindItem(id);
        if (item == null) return false;
        if (name != null) item.NameOverride = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (target != null)
        {
            if (item.Type == "folder") item.SourceDir = target.Trim();
            else item.Target = target.Trim();
        }
        if (colorIndex.HasValue)
        {
            int idx = ((colorIndex.Value % Palette.Colors.Length) + Palette.Colors.Length) % Palette.Colors.Length;
            item.ColorIndex = idx;
            item.IconOverride = idx;   // 与 v1.3 的「…」设置同一口径，保证一层图标立即变色
        }
        SaveChanged();
        return true;
    }

    /// <summary>
    /// v1.5 改善 1：删除一层条目或二层子项 → 移入待回收区（保留快照、7 天内可还原），
    /// 不直接丢弃，也绝不删除磁盘上的文件 / 快捷方式。
    /// </summary>
    public bool DeleteItem(string id)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var tab in Tabs)
        {
            var hit = tab.Items.FirstOrDefault(i => i.Id == id);
            if (hit != null)
            {
                int index = tab.Items.IndexOf(hit);
                AddToRecycle(new RecycleEntry
                {
                    Kind = "item", Name = hit.DisplayName, DeletedAt = now,
                    TabId = tab.Id, TabName = tab.Name, Index = index,
                    SourcePath = hit.Type == "folder" ? hit.SourceDir : hit.Target,
                    Item = hit
                });
                tab.Items.Remove(hit);
                if (ActiveFolderId == id) { ActiveFolderId = null; T2Offset = 0; }
                SaveChanged();
                return true;
            }
            foreach (var folder in tab.Items.Where(i => i.Children.Count > 0).ToList())
            {
                var child = folder.Children.FirstOrDefault(c => c.Id == id);
                if (child == null) continue;
                int index = folder.Children.IndexOf(child);
                AddToRecycle(new RecycleEntry
                {
                    Kind = "child", Name = child.DisplayName, DeletedAt = now,
                    TabId = tab.Id, TabName = tab.Name,
                    ParentId = folder.Id, ParentName = folder.DisplayName, Index = index,
                    SourcePath = child.Target, Item = child
                });
                folder.Children.Remove(child);
                int view = Math.Clamp(Config.Shortcuts.Grid2Rows, 1, 5) * Math.Clamp(Config.Shortcuts.Grid2Cols, 1, 5);
                T2Offset = Math.Clamp(T2Offset, 0, Math.Max(0, folder.Children.Count - view));
                SaveChanged();
                return true;
            }
        }
        return false;
    }

    /// <summary>功能完善 3：重命名一层标签页大类。</summary>
    public bool RenameTab(string id, string name)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab == null || string.IsNullOrWhiteSpace(name)) return false;
        tab.Name = name.Trim();
        SaveChanged();
        return true;
    }

    /// <summary>v1.5：删除大类（至少保留一个）→ 整个大类进待回收区，还原时整体恢复。</summary>
    public bool DeleteTab(string id)
    {
        if (Tabs.Count <= 1)
        {
            ToastRequested?.Invoke("无法删除大类", "至少需要保留一个标签页大类。", "warn");
            return false;
        }
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab == null) return false;
        AddToRecycle(new RecycleEntry
        {
            Kind = "tab", Name = tab.Name, DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TabId = tab.Id, TabName = tab.Name, Index = Tabs.IndexOf(tab),
            Tab = tab
        });
        var owned = tab.Items.Select(i => i.Id).ToHashSet();
        if (ActiveFolderId != null && owned.Contains(ActiveFolderId)) { ActiveFolderId = null; T2Offset = 0; }
        Tabs.Remove(tab);
        if (ActiveTabId == id) ActiveTabId = Tabs[0].Id;
        SaveChanged();
        return true;
    }

    // ---------- v1.5 改善 1：待回收区域 ----------

    public int RecycleCount => Config.Recycle.Entries.Count;
    public IReadOnlyList<RecycleEntry> RecycleEntries => Config.Recycle.Entries;
    public int RecycleRetentionDays => Math.Clamp(Config.Recycle.RetentionDays, 1, 3650);
    public string DataRootDir => _store.RootDir;
    public string RecycleDir => _store.RecycleDir;

    public void SetRecycleRetentionDays(int days)
    {
        Config.Recycle.RetentionDays = Math.Clamp(days, 1, 30);
        SaveChanged();
    }

    /// <summary>该记录距自动清除还剩多少天（向下取 1 位小数）。</summary>
    public double RecycleRemainingDays(RecycleEntry e)
    {
        long ttl = (long)RecycleRetentionDays * 86_400_000L;
        long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - e.DeletedAt;
        return Math.Max(0, Math.Round((ttl - elapsed) / 86_400_000.0, 1));
    }

    private void AddToRecycle(RecycleEntry e)
    {
        // v1.7：无论删除的是条目、大类还是模块，都记下所属模块，还原时精确归位。
        if (string.IsNullOrWhiteSpace(e.ModuleId))
        {
            e.ModuleId = ActiveModule().Id;
            e.ModuleName = ActiveModule().Name;
        }
        Config.Recycle.Entries.Add(e);
        _store.WriteRecycleSnapshot(e);
    }

    /// <summary>清除超过保留期的记录及其磁盘快照；启动时与每 60 秒各执行一次。</summary>
    public int PurgeExpiredRecycle(bool notify = true)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long ttl = (long)RecycleRetentionDays * 86_400_000L;
        var expired = Config.Recycle.Entries.Where(e => now - e.DeletedAt >= ttl).ToList();
        if (expired.Count == 0) return 0;
        foreach (var e in expired)
        {
            Config.Recycle.Entries.Remove(e);
            _store.DeleteRecycleSnapshot(e.Id);
        }
        SaveChanged();
        if (notify)
            ToastRequested?.Invoke("待回收区已自动清理",
                $"{expired.Count} 个条目已超过 {RecycleRetentionDays} 天保留期，已永久移除；磁盘文件不受影响。", "info");
        return expired.Count;
    }

    /// <summary>清理「配置里已无记录、但快照文件还在」的孤立文件，保证 7 天后根目录干净。</summary>
    private void SweepOrphanSnapshots()
    {
        try
        {
            var valid = Config.Recycle.Entries.Select(e => e.Id + ".json")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(_store.RecycleDir, "*.json"))
                if (!valid.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch { /* 目录不可读时跳过，不影响主流程 */ }
    }

    /// <summary>还原一条待回收记录：回到原大类 / 原父文件夹 / 原位置；原大类已删则降级到第一个大类。</summary>
    public bool RestoreRecycle(string entryId)
    {
        var e = Config.Recycle.Entries.FirstOrDefault(x => x.Id == entryId);
        if (e == null) return false;

        if (e.Kind == "module" && e.Module != null)
        {
            var m = e.Module;
            m.Tabs ??= new List<ShortcutTab>();
            if (Config.Shortcuts.Modules.Any(x => x.Id == m.Id)) ReassignModuleIds(m);
            if (m.Tabs.Count == 0) m.Tabs.Add(new ShortcutTab { Name = "工作" });
            if (m.Tabs.All(t => t.Id != m.ActiveTabId)) m.ActiveTabId = m.Tabs[0].Id;
            Config.Shortcuts.Modules.Insert(
                Math.Clamp(e.Index, 0, Config.Shortcuts.Modules.Count), m);
            _activeModuleId = m.Id;
        }
        else if (e.Kind == "tab" && e.Tab != null)
        {
            SwitchModuleSilently(e.ModuleId);
            var tab = e.Tab;
            tab.Items ??= new List<ShortcutItem>();
            if (Tabs.Any(t => t.Id == tab.Id)) ReassignTabIds(tab);
            Tabs.Insert(Math.Clamp(e.Index, 0, Tabs.Count), tab);
            ActiveTabId = tab.Id;
        }
        else if (e.Item != null)
        {
            SwitchModuleSilently(e.ModuleId);
            var item = e.Item;
            item.Children ??= new List<ShortcutItem>();
            if (FindItem(item.Id) != null) ReassignItemIds(item);

            if (e.Kind == "child" && !string.IsNullOrWhiteSpace(e.ParentId))
            {
                var parent = Tabs.SelectMany(t => t.Items)
                    .FirstOrDefault(i => i.Id == e.ParentId && i.Type == "folder");
                if (parent != null)
                {
                    parent.Children ??= new List<ShortcutItem>();
                    parent.Children.Insert(Math.Clamp(e.Index, 0, parent.Children.Count), item);
                }
                else AddAsTopLevel(item, e.TabId, e.Index);   // 原父文件夹已不在，降级为顶层条目
            }
            else AddAsTopLevel(item, e.TabId, e.Index);
        }
        else return false;

        Config.Recycle.Entries.Remove(e);
        _store.DeleteRecycleSnapshot(e.Id);
        SaveChanged();
        return true;
    }

    /// <summary>提前彻底删除单条待回收记录（不可还原）。</summary>
    public bool PurgeRecycleEntry(string entryId)
    {
        var e = Config.Recycle.Entries.FirstOrDefault(x => x.Id == entryId);
        if (e == null) return false;
        Config.Recycle.Entries.Remove(e);
        _store.DeleteRecycleSnapshot(e.Id);
        SaveChanged();
        return true;
    }

    /// <summary>清空整个待回收区（不可还原），返回清除数量。</summary>
    public int ClearRecycle()
    {
        var all = Config.Recycle.Entries.ToList();
        if (all.Count == 0) return 0;
        foreach (var e in all) _store.DeleteRecycleSnapshot(e.Id);
        Config.Recycle.Entries.Clear();
        SaveChanged();
        return all.Count;
    }

    private void AddAsTopLevel(ShortcutItem item, string tabId, int index)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == tabId) ?? Tabs[0];
        tab.Items ??= new List<ShortcutItem>();
        tab.Items.Insert(Math.Clamp(index, 0, tab.Items.Count), item);
        ActiveTabId = tab.Id;
    }

    private static void ReassignItemIds(ShortcutItem item)
    {
        item.Id = ShortcutTab.NewId();
        foreach (var c in item.Children) c.Id = ShortcutTab.NewId();
    }

    private static void ReassignModuleIds(ShortcutModule m)
    {
        m.Id = ShortcutModule.NewId();
        foreach (var t in m.Tabs) ReassignTabIds(t);
    }

    private static void ReassignTabIds(ShortcutTab tab)
    {
        tab.Id = ShortcutTab.NewId();
        foreach (var i in tab.Items) ReassignItemIds(i);
    }

    public void SaveNow() => _store.Save();
}
