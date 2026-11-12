using DeskLite.Core;
using DeskLite.Models;
using DeskLite.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace DeskLite;

/// <summary>
/// 主窗口：DeskBox 风格（云母背景 + 深色半透明圆角卡片 + 紫蓝强调色）。
/// UI 由代码构建，便于按 N×M / 标签页 / 3×3 动态生成，且避免多余 XAML 编译依赖。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppController _c;
    private readonly DispatcherTimer _timer;
    private readonly List<(Border Element, DateTime Expire)> _toasts = new();
    private readonly IntPtr _hwnd;
    private TrayHost? _tray;
    private bool _loadingSettings;
    private bool _hidden;
    private bool _settingSignal;
    private DateTime _nextThemeCheck;
    private DateTime _lastSignalCheck;

    // 主布局
    private Grid _root = null!;
    private Grid _mainLayout = null!;
    private Grid _overlay = null!;
    private Grid _recentGrid = null!;
    private Grid _grid1 = null!;
    private Grid _grid2 = null!;
    private StackPanel _tabsPanel = null!;
    private StackPanel _toastList = null!;
    private Slider _wheelV = null!, _wheelH = null!, _wheel2 = null!;

    // 状态文字
    private TextBlock _statusText = null!, _modeText = null!, _chipPx = null!;
    private TextBlock _recentCount = null!, _gridPill = null!, _t1Count = null!, _t2Path = null!;
    private Button _btnAuto = null!, _btnManual = null!;

    // 浮层
    private Border _confirmBox = null!;
    private FrameworkElement _settingsLayer = null!;
    private TextBlock _confirmOld = null!, _confirmNew = null!, _cdText = null!;
    private ProgressBar _cbar = null!;

    // v1.3 拖放：全窗口统一接收，避免透明网格/覆盖层造成命中失败或重复处理
    private bool _rootDropAttached;
    private Border _dropHint = null!;
    private DispatcherTimer? _selftestTimer;
    private DateTime _lastDropAt;
    private string _lastDropKey = "";

    // v1.4-1/2/3：模态对话框队列（同名合并询问、文件夹阻止提示、删除/退出确认）
    private sealed record DialogSpec(string Title, string Body, string Primary, Action? OnPrimary,
        string? Secondary = null, Action? OnSecondary = null, bool Danger = false);
    private readonly Queue<DialogSpec> _dialogQueue = new();
    private DialogSpec? _currentDialog;
    private bool _dialogOpen;
    private Border _dialogBox = null!, _dialogShade = null!;
    private TextBlock _dialogTitle = null!, _dialogBody = null!;
    private Button _dialogPrimary = null!, _dialogSecondary = null!;

    // v1.4-3：一层 / 二层 / 标签页通用的「修改」编辑器
    private Border _editBox = null!;
    private TextBlock _editCaption = null!;
    private TextBox _editName = null!, _editTarget = null!;
    private StackPanel _editColors = null!, _editTargetRow = null!, _editColorRow = null!;
    private string? _editId;
    private bool _editIsTab;
    private int _editPick = -1;

    // v1.4-4/5：滚轮落点面板、自定义拖动区
    private FrameworkElement _recentPanel = null!, _rightPanel = null!, _titleRow = null!;
    private bool _rootWheelAttached, _syncingWheels;

    // v1.5 改善 1：待回收区域浮层
    private FrameworkElement _recycleLayer = null!;
    private StackPanel _recycleList = null!;
    private TextBlock _recycleSummary = null!, _trashBtnText = null!;
    private int _lastRecycleCount = -1;

    public MainWindow()
    {
        InitializeComponent();
        Title = "桌面助手 · DeskLite v1.6";
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // v1.4-5：无边框窗口（无系统标题栏 / 关闭按钮），设置与托盘右键是唯一退出通道
        ConfigureBorderless();

        // v1.2：先定主题再建 UI，保证所有色值一次性按当前档位生成
        UiTheme.Apply("system");
        try { SystemBackdrop = new MicaBackdrop(); } catch { /* 老版本回退纯色 */ }
        TryResize(1020, 730);

        _c = new AppController();
        // v1.6：图标提取是否隐藏快捷方式左下角箭头角标（设置页可切换）
        ShortcutIconProvider.HideOverlay = _c.Config.Ui.HideShortcutOverlay;
        UiTheme.Apply(_c.Config.Ui.Theme);
        WindowState.Restore(_hwnd, _c.Config.Ui);
        // Q16 建议默认值：开机自启。仅在配置为真时同步当前用户 Run 项，可在设置中关闭。
        if (_c.Config.Ui.StartWithWindows) ApplyStartup(true);
        _c.Changed += RebuildData;
        _c.RecentViewChanged += RefreshRecentGrid;
        _c.FolderViewChanged += RefreshGrid2;
        _c.ToastRequested += AddToast;
        // StateChanged 由统一计时器统一刷新，避免一次 Tick 内重复重绘 UI

        BuildUi();
        RebuildData();
        RefreshDynamic();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            // WM_SETTINGCHANGE 事件优先：系统主题 / 图标间距变化后，本 Tick 立即重采样
            if (_settingSignal)
            {
                _settingSignal = false;
                if ((DateTime.UtcNow - _lastSignalCheck).TotalMilliseconds > 250)
                {
                    _lastSignalCheck = DateTime.UtcNow;
                    _c.RecheckNow();
                    if (UiTheme.RefreshIfNeeded()) RebuildUiForTheme();
                }
            }

            _c.Tick();
            RefreshDynamic();
            ExpireToasts();

            // 跟随系统档位：每 2 秒读一次注册表（一次极轻量读取），仅在变化时才重建 UI
            if (DateTime.UtcNow >= _nextThemeCheck)
            {
                _nextThemeCheck = DateTime.UtcNow.AddSeconds(2);
                if (UiTheme.RefreshIfNeeded()) RebuildUiForTheme();
            }

            // 空闲时降频到 500 ms，倒计时阶段提高到 100 ms，兼顾响应速度与低 CPU
            int next = _c.Phase == SyncPhase.Awaiting ? 100
                     : (_c.Phase == SyncPhase.Bubble || _c.Phase == SyncPhase.Detected) ? 250
                     : 500;
            // 隐藏在托盘时进一步降频到 1 秒，几乎不占 CPU
            if (_hidden) next = Math.Max(next, 1000);
            if ((int)_timer.Interval.TotalMilliseconds != next)
                _timer.Interval = TimeSpan.FromMilliseconds(next);
        };
        _timer.Start();

        Activated += (_, _) =>
        {
            _c.RecheckNow();
            if (UiTheme.RefreshIfNeeded()) RebuildUiForTheme();
        };

        // v1.2：系统托盘（关闭时隐藏而非退出）+ 单实例唤起
        try
        {
            _tray = new TrayHost(_hwnd,
                onShow: ShowFromTray,
                onSettings: () => { ShowFromTray(); OpenSettings(); },
                onExit: ExitFromTray,
                onHide: () => { _hidden = true; SaveWindowPlacement(); },
                onSystemChange: () => _settingSignal = true,
                getStartup: () => _c.Config.Ui.StartWithWindows,
                setStartup: v => { _c.Config.Ui.StartWithWindows = v; ApplyStartup(v); _c.SaveNow(); });
        }
        catch (Exception ex) { AppLog.Write("TrayHost", ex); }

        // v1.3：Win32 原生拖放通道（修复外部文件拖不进窗口）。WndProc 已在 TrayHost 中挂接，
        // 此处登记主窗口并订阅解析结果；与 XAML 的 OLE 通道并存，任一可用即可接收。
        FileDropHost.FilesDropped += payload =>
        {
            try
            {
                AddToast("已收到外部拖放", $"共 {payload.Paths.Count} 项，正在归入快捷方式。", "info");
                OnFilesDropped(payload.Paths, null, new Point(payload.X, payload.Y));
            }
            catch (Exception ex) { AppLog.Write("Win32Drop", ex); }
        };
        FileDropHost.Install(_hwnd);

        // 隐藏诊断入口：--desklite-drop-test="<路径>" 会构造合法 HDROP 走完整链路，用于验证拖放通道
        var selftestArg = Array.Find(Environment.GetCommandLineArgs(),
            a => a.StartsWith("--desklite-drop-test=", StringComparison.OrdinalIgnoreCase));
        if (selftestArg != null)
        {
            var testPath = selftestArg[(selftestArg.IndexOf('=') + 1)..].Trim('"');
            _selftestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            _selftestTimer.Tick += (_, _) =>
            {
                _selftestTimer!.Stop();
                FileDropHost.SelfTest(_hwnd, testPath);
            };
            _selftestTimer.Start();
        }

        Closed += (_, _) =>
        {
            _timer.Stop();
            WindowState.Save(_hwnd, _c.Config.Ui);
            _c.SaveNow();
            _tray?.Dispose();
        };
    }

    private void ShowFromTray()
    {
        TrayHost.BringToFront(_hwnd);
        _hidden = false;
        _c.RecheckNow();
        if (UiTheme.RefreshIfNeeded()) RebuildUiForTheme();
    }

    private void ExitFromTray()
    {
        _tray?.AllowClose();
        Close();
    }

    private void SaveWindowPlacement()
    {
        WindowState.Save(_hwnd, _c.Config.Ui);
        _c.SaveNow();
    }

    /// <summary>切换主题档位：重建整棵控件树，并重置按需刷新缓存以免状态文字不更新。</summary>
    private void ApplyTheme(string mode)
    {
        _c.Config.Ui.Theme = mode;
        UiTheme.Apply(mode);
        _c.SaveNow();
        // ComboBox.SelectionChanged 回调内不能销毁自身所在的控件树，排到下一轮消息再重建
        var q = _root?.DispatcherQueue;
        if (q != null) q.TryEnqueue(RebuildUiForTheme);
        else RebuildUiForTheme();
    }

    private int ThemeIndex() => _c.Config.Ui.Theme switch
    {
        "light" => 1,
        "dark" => 2,
        _ => 0
    };

    private void RebuildUiForTheme()
    {
        bool settingsWasOpen = _settingsLayer != null && _settingsLayer.Visibility == Visibility.Visible;
        bool recycleWasOpen = _recycleLayer != null && _recycleLayer.Visibility == Visibility.Visible;
        _toasts.Clear();
        BuildUi();
        if (settingsWasOpen && _settingsLayer != null) _settingsLayer.Visibility = Visibility.Visible;
        if (recycleWasOpen && _recycleLayer != null) { _recycleLayer.Visibility = Visibility.Visible; RefreshRecycleList(); }

        _lastStatus = _lastMode = _lastChip = _lastCd = _lastOld = _lastNew = "";
        _lastRecycleCount = -1;
        _lastAuto = null;
        _lastAwaiting = false;
        _lastBar = -1;
        RebuildData();
    }

    private void TryResize(int w, int h)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id)?.Resize(new SizeInt32(w, h));
        }
        catch { /* 尺寸失败不影响功能 */ }
    }

    /// <summary>
    /// v1.4 功能完善 5：OverlappedPresenter 去掉系统边框与标题栏（含最小化/关闭按钮），
    /// 同时开启 ExtendsContentIntoTitleBar 以便把自定义标题行注册为拖动区。
    /// </summary>
    private void ConfigureBorderless()
    {
        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            aw?.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
            if (aw?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);
                p.IsResizable = true;
            }
            ExtendsContentIntoTitleBar = true;
        }
        catch (Exception ex) { AppLog.Write("Borderless", ex); }
    }

    /// <summary>无边框后由右下角手柄触发系统标准的 WM_NCLBUTTONDOWN 缩放循环。</summary>
    private static class NativeWindow
    {
        private const uint WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTBOTTOMRIGHT = 17;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public static void Hide(IntPtr hwnd)
        {
            try { ShowWindow(hwnd, 0); } catch { /* SW_HIDE 失败不影响主流程 */ }
        }

        public static void BeginResize(IntPtr hwnd)
        {
            try
            {
                ReleaseCapture();
                PostMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTBOTTOMRIGHT), IntPtr.Zero);
            }
            catch { /* 手柄失效不影响其它功能 */ }
        }
    }

    // ---------------- 控件工具 ----------------

    private static Windows.UI.Color ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
            return Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
        if (hex.Length == 8)
            return Windows.UI.Color.FromArgb(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
        return Windows.UI.Color.FromArgb(255, 255, 255, 255);
    }

    /// <summary>
    /// 统一色值出口：深色为设计基准；浅色下按语义重映射。
    /// 半透明白（面板填充 / 描边）在浅色下改用半透明黑，保证层次依旧可见；
    /// 纯白（彩色图标上的文字、紫色按钮上的文字）保持不变。
    /// </summary>
    private static SolidColorBrush B(string hex, double opacity = 1.0)
    {
        string mapped = UiTheme.Map(hex);
        if (!UiTheme.IsDark && hex == "#FFFFFF" && opacity < 1.0) mapped = "#000000";
        return new SolidColorBrush(ParseColor(mapped)) { Opacity = opacity };
    }

    private static TextBlock T(string text, double size = 12, string hex = "#F3F1F8", bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = B(hex),
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Border IconBox(string label, int colorIndex, int px) => new()
    {
        Width = px,
        Height = px,
        CornerRadius = new CornerRadius(10),
        Background = B(Palette.Of(colorIndex)),
        Child = new TextBlock
        {
            Text = label,
            FontSize = Math.Max(9, px * 0.30),
            Foreground = B("#FFFFFF"),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    /// <summary>v1.5：优先显示快捷方式自身图标；未取到或关闭开关时回退原彩色占位图标。</summary>
    private FrameworkElement IconVisual(string label, int colorIndex, int px, ImageSource? real)
    {
        if (real == null) return IconBox(label, colorIndex, px);
        return new Image
        {
            Source = real,
            Width = px,
            Height = px,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>一层 / 二层：聚合文件夹取字母排序第一项的图标，普通项取自身 Target。</summary>
    private ImageSource? RealIconForItem(ShortcutItem item)
    {
        if (!_c.Config.Ui.UseRealIcons || item == null) return null;
        string path = item.Type == "folder"
            ? (item.Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault()?.Target ?? "")
            : item.Target;
        return string.IsNullOrWhiteSpace(path) ? null : ShortcutIconProvider.GetIcon(path);
    }

    private ImageSource? RealIconForRecent(RecentItem item)
    {
        if (!_c.Config.Ui.UseRealIcons || item == null) return null;
        return string.IsNullOrWhiteSpace(item.Path) ? null : ShortcutIconProvider.GetIcon(item.Path);
    }

    private static Button Bt(string text, RoutedEventHandler onClick, string hex = "#F3F1F8")
    {
        var b = new Button
        {
            Content = T(text, 11.5, hex),
            VerticalAlignment = VerticalAlignment.Center,
            Background = B("#FFFFFF", 0.07),
            BorderBrush = B("#FFFFFF", 0.12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6)
        };
        b.Click += onClick;
        return b;
    }

    private static Border Panel(double radius = 12) => new()
    {
        Background = B("#FFFFFF", 0.05),
        BorderBrush = B("#FFFFFF", 0.08),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius)
    };

    private static Border SectionHeader(string title, string hint, out StackPanel right)
    {
        var b = Panel(10);
        b.Background = B("#000000", 0.13);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var t = T(title, 12.5, bold: true);
        var h = T(hint, 10.5, "#8A829C");
        Grid.SetColumn(t, 0); Grid.SetColumn(h, 1);
        h.Margin = new Thickness(9, 0, 0, 0);
        right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6 };
        Grid.SetColumn(right, 2);
        g.Children.Add(t); g.Children.Add(h); g.Children.Add(right);
        b.Child = g;
        b.Padding = new Thickness(10, 6, 10, 6);
        return b;
    }

    // ---------------- 主布局 ----------------

    private void BuildUi()
    {
        _root = Root;
        // 浅色档位下提高不透明度，避免系统深色 Mica 与浅色面板串色
        _root.Background = B("#1A1622", UiTheme.IsDark ? 0.86 : 0.97);
        _root.RequestedTheme = UiTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        _root.Children.Clear();

        _mainLayout = new Grid { Margin = new Thickness(12) };
        _mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        _mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        _mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _mainLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });

        _titleRow = BuildTitleRow();
        _mainLayout.Children.Add(_titleRow);
        // v1.4-5：自定义标题行即拖动区；其中的按钮仍可正常点击。
        try { SetTitleBar(_titleRow); }
        catch (Exception ex) { AppLog.Write("SetTitleBar", ex); }
        var recent = BuildRecentSection(); Grid.SetRow(recent, 1);
        _mainLayout.Children.Add(recent);
        var shortcuts = BuildShortcutSection(); Grid.SetRow(shortcuts, 2);
        _mainLayout.Children.Add(shortcuts);
        var status = BuildStatusBar(); Grid.SetRow(status, 3);
        _mainLayout.Children.Add(status);

        _root.Children.Add(_mainLayout);
        _overlay = new Grid { IsHitTestVisible = true };
        BuildOverlays();
        _root.Children.Add(_overlay);

        // v1.3：根容器是唯一拖放目标。它有实际背景、覆盖整个客户区，任何位置都能命中；
        // 旧版只挂在透明的一层网格上，空白处无法命中，导致从资源管理器拖放几乎总是无效。
        // _root 在主题切换时会被复用，因此只挂一次事件，避免重复处理。
        if (!_rootDropAttached)
        {
            AttachRootDrop();
            _rootDropAttached = true;
        }

        // v1.4-4：滚轮统一在根容器拦截，按落点分发到最近常用 / 二层收纳，避免子控件吞事件。
        if (!_rootWheelAttached)
        {
            _root.PointerWheelChanged += OnRootWheel;
            _rootWheelAttached = true;
        }

        // v1.4-5：无边框窗口没有可拖的边框，右下角补一个标准缩放手柄。
        var grip = new Border
        {
            Width = 16, Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = B("#FFFFFF", 0.02),
            Child = T("◢", 10, "#8A829C")
        };
        ToolTipService.SetToolTip(grip, "拖动调整窗口大小");
        grip.PointerPressed += (_, e) => { NativeWindow.BeginResize(_hwnd); e.Handled = true; };
        _root.Children.Add(grip);
    }

    private FrameworkElement BuildTitleRow()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // v1.5 待回收区

        var logo = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7),
            Background = B("#6C8CFF"), Margin = new Thickness(0, 0, 8, 0)
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(T("桌面助手", 13, bold: true));
        brand.Children.Add(T("v1.6 · 图标无箭头", 10, "#8A829C"));

        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Padding = new Thickness(2),
            Background = B("#FFFFFF", 0.06), CornerRadius = new CornerRadius(9) };
        _btnAuto = Bt("自动模式", (_, _) => _c.SwitchMode("auto"));
        _btnManual = Bt("手动模式", (_, _) => _c.SwitchMode("manual"));
        seg.Children.Add(_btnAuto); seg.Children.Add(_btnManual);

        _chipPx = T("48 px", 11, "#A9A2B9");
        var chip = new Border { Background = B("#FFFFFF", 0.06), BorderBrush = B("#FFFFFF", 0.12),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 4, 8, 4), Child = _chipPx, VerticalAlignment = VerticalAlignment.Center };

        var trashBtn = Bt("待回收区 (0)", (_, _) => OpenRecycle());
        // Bt 内部用 TextBlock 作 Content，取回来以便按数量刷新
        if (trashBtn.Content is TextBlock tb) _trashBtnText = tb;
        var hideBtn = Bt("隐藏到托盘", (_, _) => HideToTray());
        var setBtn = Bt("设置", (_, _) => OpenSettings());
        setBtn.Margin = new Thickness(6, 0, 0, 0);

        Grid.SetColumn(logo, 0); Grid.SetColumn(brand, 1);
        Grid.SetColumn(seg, 3); Grid.SetColumn(chip, 4);
        Grid.SetColumn(trashBtn, 5); Grid.SetColumn(hideBtn, 6); Grid.SetColumn(setBtn, 7);
        g.Children.Add(logo); g.Children.Add(brand);
        g.Children.Add(seg); g.Children.Add(chip);
        g.Children.Add(trashBtn); g.Children.Add(hideBtn); g.Children.Add(setBtn);
        return g;
    }

    private FrameworkElement BuildRecentSection()
    {
        var outer = Panel();
        outer.Margin = new Thickness(0, 8, 0, 0);
        var stack = new StackPanel();
        var hdr = SectionHeader("最近常用", "按「最近从本软件打开」倒序", out var right);
        _recentCount = T("--", 10, "#A9A2B9");
        _gridPill = T("N×M", 10, "#A9A2B9");
        right.Children.Add(Pill(_recentCount));
        right.Children.Add(Pill(_gridPill));
        stack.Children.Add(hdr);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        _recentGrid = new Grid { Margin = new Thickness(10, 8, 4, 4), Background = B("#000000", 0) };
        Grid.SetColumn(_recentGrid, 0);
        _wheelV = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, Value = 0,
            Width = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 6, 8) };
        _wheelV.ValueChanged += (s, e) => { if (!_loadingSettings && !_syncingWheels) _c.SetRecentPosition(e.NewValue / 100.0); };
        Grid.SetColumn(_wheelV, 1);
        body.Children.Add(_recentGrid); body.Children.Add(_wheelV);
        stack.Children.Add(body);

        _wheelH = new Slider { Orientation = Orientation.Horizontal, Minimum = 0, Maximum = 100, Value = 0,
            Height = 18, Margin = new Thickness(10, 0, 10, 8) };
        _wheelH.ValueChanged += (s, e) => { if (!_loadingSettings && !_syncingWheels) _c.SetRecentPosition(e.NewValue / 100.0); };
        stack.Children.Add(_wheelH);

        outer.Child = stack;
        _recentPanel = outer;   // v1.4-4：滚轮落点判定用
        return outer;
    }

    private static Border Pill(TextBlock text) => new()
    {
        Background = B("#FFFFFF", 0.07), BorderBrush = B("#FFFFFF", 0.10),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(8, 2, 8, 2), Child = text
    };

    private FrameworkElement BuildShortcutSection()
    {
        var outer = Panel();
        outer.Margin = new Thickness(0, 8, 0, 0);
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var hdr = SectionHeader("快捷方式管理", "左侧一层标签页 · 右侧二层收纳", out var right);
        right.Children.Add(Pill(T("二级窗口 3×3", 10, "#A9A2B9")));
        stack.Children.Add(hdr);

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.04, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.96, GridUnitType.Star) });
        Grid.SetRow(split, 1);
        split.Margin = new Thickness(8);

        // 左：一层
        var left = new Border { Background = B("#000000", 0.16), BorderBrush = B("#FFFFFF", 0.06),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
        var lg = new Grid();
        lg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        lg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        lg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var lh = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(9, 6, 9, 6) };
        lh.Children.Add(T("一层快捷方式", 11, bold: true));
        _t1Count = T("--", 10, "#8A829C");
        lh.Children.Add(_t1Count);
        lg.Children.Add(lh);
        _tabsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetRow(_tabsPanel, 1);
        lg.Children.Add(_tabsPanel);
        // v1.3：拖放统一由根容器处理。透明色刷（非 null）让网格参与命中测试，
        // null 背景的 Grid 在 XAML 中不参与命中，是旧版拖放失效的次要原因。
        _grid1 = new Grid { Margin = new Thickness(8), Background = B("#000000", 0),
            VerticalAlignment = VerticalAlignment.Top };
        // v1.4-4：一层条目多时由 ScrollViewer 原生支持鼠标滚轮，不再被裁剪。
        var grid1Scroll = new ScrollViewer
        {
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = _grid1
        };
        Grid.SetRow(grid1Scroll, 2);
        lg.Children.Add(grid1Scroll);
        left.Child = lg;
        Grid.SetColumn(left, 0);
        split.Children.Add(left);

        // 右：二层
        var rightPane = new Border { Background = B("#000000", 0.16), BorderBrush = B("#FFFFFF", 0.06),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 0, 0, 0) };
        var rg = new Grid();
        rg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        rg.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var rh = new Grid();
        rh.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rh.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        rh.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        rh.Margin = new Thickness(9, 6, 9, 6);
        var rt = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        rt.Children.Add(T("二层快捷收纳", 11, bold: true));
        _t2Path = T("· 未打开", 10, "#8A829C");
        rt.Children.Add(_t2Path);
        Grid.SetColumn(rt, 0); rh.Children.Add(rt);
        var more = Bt("修改…", (_, _) =>
        {
            var f = _c.ActiveFolder();
            if (f == null) AddToast("尚未打开二层窗口", "请先点击左侧文件夹聚合图标，或右键任意图标进行修改。", "warn");
            else OpenEditor(f.Id);
        });
        Grid.SetColumn(more, 1); rh.Children.Add(more);
        var close = Bt("×", (_, _) => _c.CloseFolder());
        close.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(close, 2); rh.Children.Add(close);
        rg.Children.Add(rh);

        var t2Body = new Grid();
        t2Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        t2Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        Grid.SetRow(t2Body, 1);
        _grid2 = new Grid { Margin = new Thickness(8), Background = B("#000000", 0) };
        Grid.SetColumn(_grid2, 0);
        _wheel2 = new Slider { Orientation = Orientation.Vertical, Minimum = 0, Maximum = 100, Value = 0,
            Width = 16, Margin = new Thickness(0, 8, 4, 8) };
        _wheel2.ValueChanged += (s, e) => { if (!_loadingSettings && !_syncingWheels) _c.SetFolderPosition(e.NewValue / 100.0); };
        Grid.SetColumn(_wheel2, 1);
        t2Body.Children.Add(_grid2); t2Body.Children.Add(_wheel2);
        rg.Children.Add(t2Body);
        rightPane.Child = rg;
        Grid.SetColumn(rightPane, 1);
        split.Children.Add(rightPane);
        _rightPanel = rightPane;   // v1.4-4：滚轮落点判定用

        stack.Children.Add(split);
        outer.Child = stack;
        return outer;
    }

    private FrameworkElement BuildStatusBar()
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _statusText = T("空闲", 10.5, "#4CD08A");
        _modeText = T("自动（跟随桌面）", 10.5, "#8A829C");
        _modeText.Margin = new Thickness(16, 0, 0, 0);
        var note = T("v1.6 · 真实图标（默认无箭头角标） · 待回收区 7 天 · 右键修改/删除 · 设置中退出", 10, "#8A829C");
        note.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_statusText, 0); Grid.SetColumn(_modeText, 1); Grid.SetColumn(note, 2);
        g.Children.Add(_statusText); g.Children.Add(_modeText); g.Children.Add(note);
        return g;
    }

    // ---------------- 浮层 ----------------

    private void BuildOverlays()
    {
        // 气泡
        _toastList = new StackPanel { Spacing = 8, Width = 290, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 52, 14, 0) };
        _overlay.Children.Add(_toastList);

        // 同步请求
        _confirmBox = new Border
        {
            Width = 356, Padding = new Thickness(16), CornerRadius = new CornerRadius(14),
            Background = B("#2E243C", 0.97), BorderBrush = B("#B45CF0", 0.55), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var cs = new StackPanel();
        cs.Children.Add(T("是否同步桌面图标大小？", 14, bold: true));
        cs.Children.Add(T("检测到系统桌面图标显示尺寸发生变化，确认后本软件内的图标尺寸将同步为新的桌面尺寸。", 11, "#A9A2B9"));
        var cmp = new Grid { Margin = new Thickness(0, 12, 0, 12), Padding = new Thickness(10),
            Background = B("#000000", 0.22), CornerRadius = new CornerRadius(10) };
        cmp.ColumnDefinitions.Add(new ColumnDefinition());
        cmp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        cmp.ColumnDefinitions.Add(new ColumnDefinition());
        var oldV = new StackPanel(); _confirmOld = T("48 px", 19, "#F3F1F8", true);
        oldV.Children.Add(_confirmOld); oldV.Children.Add(T("当前软件内", 10, "#8A829C"));
        var newV = new StackPanel(); _confirmNew = T("64 px", 19, "#B45CF0", true);
        newV.Children.Add(_confirmNew); newV.Children.Add(T("桌面新尺寸", 10, "#8A829C"));
        var arrow = T("→", 16, "#B45CF0"); arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(oldV, 0); Grid.SetColumn(arrow, 1); Grid.SetColumn(newV, 2);
        cmp.Children.Add(oldV); cmp.Children.Add(arrow); cmp.Children.Add(newV);
        cs.Children.Add(cmp);
        _cbar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 100, Height = 4 };
        cs.Children.Add(_cbar);
        _cdText = T("5.0 s 后默认确认", 10.5, "#F2B45A");
        _cdText.Margin = new Thickness(0, 6, 0, 10);
        cs.Children.Add(_cdText);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foot.Children.Add(Bt("暂不同步", (_, _) => _c.DismissSync()));
        var ok = Bt("确认同步", (_, _) => _c.ConfirmSync(), "#FFFFFF");
        ok.Background = B("#B45CF0"); ok.BorderBrush = B("#B45CF0");
        foot.Children.Add(ok);
        cs.Children.Add(foot);
        _confirmBox.Child = cs;
        _overlay.Children.Add(_confirmBox);

        // v1.5 改善 1：待回收区域浮层（同样位于模态遮罩之下）
        _recycleLayer = BuildRecycleLayer();
        _overlay.Children.Add(_recycleLayer);

        // 设置层（位于模态遮罩之下）
        _settingsLayer = BuildSettingsLayer();
        _overlay.Children.Add(_settingsLayer);

        // v1.4-3：条目编辑器
        _editBox = BuildEditBox();
        _overlay.Children.Add(_editBox);

        // v1.4-1/2/3：通用模态对话框（同名合并、阻止提示、删除 / 退出确认）
        _dialogShade = new Border
        {
            Background = B("#000000", 0.45), IsHitTestVisible = true,
            Visibility = Visibility.Collapsed
        };
        _overlay.Children.Add(_dialogShade);
        _dialogBox = BuildDialogBox();
        _overlay.Children.Add(_dialogBox);

        // v1.3 拖放视觉提示：必须 IsHitTestVisible=false，否则会挡住 Drop 的命中判定
        var hintStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 6
        };
        var hintTitle = T("松开以加入快捷方式", 20, "#F3F1F8", true);
        hintTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var hintSub = T("拖到某个标签页可指定大类 · 仅含快捷方式的文件夹会生成二层收纳", 11.5, "#F3F1F8");
        hintSub.HorizontalAlignment = HorizontalAlignment.Center;
        hintStack.Children.Add(hintTitle);
        hintStack.Children.Add(hintSub);
        _dropHint = new Border
        {
            Background = B("#B45CF0", 0.10),
            BorderBrush = B("#B45CF0", 0.85),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = hintStack
        };
        _overlay.Children.Add(_dropHint);

        // 主题切换会重建控件树；若正在等待用户选择，恢复当前对话框。
        if (_currentDialog != null) RenderDialog(_currentDialog);
    }

    private Border BuildDialogBox()
    {
        var box = new Border
        {
            Width = 400, Padding = new Thickness(18), CornerRadius = new CornerRadius(14),
            Background = B("#2E243C", 0.98), BorderBrush = B("#B45CF0", 0.6), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var s = new StackPanel();
        _dialogTitle = T("提示", 15, bold: true);
        _dialogBody = T("", 11.5, "#A9A2B9");
        _dialogBody.TextWrapping = TextWrapping.Wrap;
        _dialogBody.Margin = new Thickness(0, 10, 0, 16);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right };
        _dialogSecondary = Bt("取消", (_, _) => CloseDialog(false));
        _dialogPrimary = Bt("确定", (_, _) => CloseDialog(true), "#FFFFFF");
        foot.Children.Add(_dialogSecondary);
        foot.Children.Add(_dialogPrimary);
        s.Children.Add(_dialogTitle);
        s.Children.Add(_dialogBody);
        s.Children.Add(foot);
        box.Child = s;
        return box;
    }

    private Border BuildEditBox()
    {
        var box = new Border
        {
            Width = 340, Padding = new Thickness(16), CornerRadius = new CornerRadius(14),
            Background = B("#221B2D", 0.98), BorderBrush = B("#B45CF0", 0.55), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var s = new StackPanel();
        _editCaption = T("修改条目", 14, bold: true);
        _editCaption.Margin = new Thickness(0, 0, 0, 10);
        s.Children.Add(_editCaption);

        s.Children.Add(T("显示名称", 10.5, "#A9A2B9"));
        _editName = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
        s.Children.Add(_editName);

        _editTargetRow = new StackPanel();
        _editTargetRow.Children.Add(T("目标路径 / 源文件夹", 10.5, "#A9A2B9"));
        _editTarget = new TextBox { Margin = new Thickness(0, 4, 0, 10) };
        _editTargetRow.Children.Add(_editTarget);
        s.Children.Add(_editTargetRow);

        _editColorRow = new StackPanel();
        _editColorRow.Children.Add(T("图标颜色", 10.5, "#A9A2B9"));
        _editColors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5,
            Margin = new Thickness(0, 6, 0, 4) };
        _editColorRow.Children.Add(_editColors);
        s.Children.Add(_editColorRow);

        s.Children.Add(T("仅修改软件内记录，不会删除或改写磁盘上的文件。", 10, "#8A829C"));

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var del = Bt("移入待回收区", (_, _) => ConfirmDelete(), "#FFFFFF");
        del.Background = B("#C94F6D", 0.9); del.BorderBrush = B("#C94F6D");
        acts.Children.Add(del);
        acts.Children.Add(Bt("取消", (_, _) => _editBox.Visibility = Visibility.Collapsed));
        var save = Bt("保存", (_, _) => SaveEditor(), "#FFFFFF");
        save.Background = B("#B45CF0"); save.BorderBrush = B("#B45CF0");
        acts.Children.Add(save);
        s.Children.Add(acts);
        box.Child = s;
        return box;
    }

    // ---------------- v1.5 改善 1：待回收区域浮层 ----------------

    private FrameworkElement BuildRecycleLayer()
    {
        var layer = new Grid { Background = B("#171320", 0.98), Visibility = Visibility.Collapsed };
        var box = new Border
        {
            Width = 720, Padding = new Thickness(18), CornerRadius = new CornerRadius(14),
            Background = B("#221B2D", 0.98), BorderBrush = B("#B45CF0", 0.45),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var s = new StackPanel();
        s.Children.Add(T("待回收区", 20, bold: true));
        _recycleSummary = T("", 11, "#A9A2B9");
        _recycleSummary.Margin = new Thickness(0, 6, 0, 2);
        s.Children.Add(_recycleSummary);
        var hint = T("删除的快捷方式先到这里，保留 7 天后自动清除；期间可「还原」回原位置，或「彻底删除」。磁盘上的文件始终不受影响。",
            10.5, "#8A829C");
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 0, 0, 10);
        s.Children.Add(hint);

        var scroll = new ScrollViewer { Height = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _recycleList = new StackPanel { Spacing = 6 };
        scroll.Content = _recycleList;
        s.Children.Add(scroll);

        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0)
        };
        var clear = Bt("清空待回收区", (_, _) => ConfirmClearRecycle(), "#FFFFFF");
        clear.Background = B("#C94F6D", 0.85);
        clear.BorderBrush = B("#C94F6D");
        foot.Children.Add(clear);
        foot.Children.Add(Bt("返回主界面", (_, _) => CloseRecycle()));
        s.Children.Add(foot);
        box.Child = s;
        layer.Children.Add(box);
        return layer;
    }

    private void OpenRecycle()
    {
        _settingsLayer.Visibility = Visibility.Collapsed;
        _editBox.Visibility = Visibility.Collapsed;
        _recycleLayer.Visibility = Visibility.Visible;
        RefreshRecycleList();
    }

    private void CloseRecycle() => _recycleLayer.Visibility = Visibility.Collapsed;

    private void RefreshRecycleList()
    {
        if (_recycleList == null) return;
        _recycleList.Children.Clear();
        _recycleSummary.Text = $"共 {_c.RecycleCount} 条 · 保留期 {_c.RecycleRetentionDays} 天 · 快照目录：{_c.RecycleDir}";
        if (_c.RecycleCount == 0)
        {
            _recycleList.Children.Add(T("待回收区为空。删除的快捷方式会先移到这里，7 天内可随时还原。", 11, "#8A829C"));
            return;
        }

        foreach (var e in _c.RecycleEntries.OrderByDescending(x => x.DeletedAt))
        {
            string id = e.Id, name = e.Name;
            string from = string.IsNullOrWhiteSpace(e.ParentName) ? e.TabName : e.TabName + " › " + e.ParentName;
            string days = $"剩余 {_c.RecycleRemainingDays(e):F1} 天自动清除";
            var row = Panel(10);
            row.Padding = new Thickness(10);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(T($"[{e.KindLabel}] {name}", 12, bold: true));
            left.Children.Add(T($"来源：{from} · {days}", 10.5, "#8A829C"));

            var acts = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            acts.Children.Add(Bt("还原", (_, _) =>
            {
                bool ok = _c.RestoreRecycle(id);
                AddToast(ok ? "已还原" : "还原失败",
                    ok ? $"「{name}」已回到原位置。" : "该记录可能已被清除。", ok ? "ok" : "warn");
                RefreshRecycleList();
            }));
            var purge = Bt("彻底删除", (_, _) => EnqueueDialog(new DialogSpec("彻底删除？",
                $"「{name}」将从待回收区永久移除，无法再还原。\n\n磁盘上的文件不受影响。",
                "彻底删除", () =>
                {
                    bool ok = _c.PurgeRecycleEntry(id);
                    AddToast(ok ? "已彻底删除" : "删除失败",
                        ok ? "该记录已从待回收区清除。" : "记录可能已被清除。", ok ? "ok" : "warn");
                    RefreshRecycleList();
                }, "取消", null, true)), "#FFFFFF");
            purge.Background = B("#C94F6D", 0.65);
            purge.BorderBrush = B("#C94F6D");
            acts.Children.Add(purge);

            Grid.SetColumn(left, 0); Grid.SetColumn(acts, 1);
            g.Children.Add(left); g.Children.Add(acts);
            row.Child = g;
            _recycleList.Children.Add(row);
        }
    }

    private void ConfirmClearRecycle()
    {
        if (_c.RecycleCount == 0) return;
        EnqueueDialog(new DialogSpec("清空待回收区？",
            $"共 {_c.RecycleCount} 条记录将被永久移除，无法再还原。\n\n磁盘上的文件不受影响。",
            "清空", () =>
            {
                int n = _c.ClearRecycle();
                AddToast("待回收区已清空", $"已永久移除 {n} 条记录。", "ok");
                RefreshRecycleList();
            }, "取消", null, true));
    }

    private FrameworkElement BuildSettingsLayer()
    {
        var layer = new Grid { Background = B("#171320", 0.98), Visibility = Visibility.Collapsed };
        layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new Border { Background = B("#000000", 0.18), Padding = new Thickness(12) };
        var ns = new StackPanel();
        ns.Children.Add(T("桌面助手 · 设置", 13, bold: true));
        var back = Bt("返回主界面", (_, _) => { _settingsLayer.Visibility = Visibility.Collapsed; _c.SaveNow(); });
        back.Margin = new Thickness(0, 10, 0, 12);
        ns.Children.Add(back);
        foreach (var label in new[] { "常规", "图标大小与同步", "最近常用", "快捷方式", "性能", "关于" })
            ns.Children.Add(T("· " + label, 11.5, "#A9A2B9"));
        nav.Child = ns;
        Grid.SetColumn(nav, 0);
        layer.Children.Add(nav);

        var scroll = new ScrollViewer { Padding = new Thickness(20) };
        var body = new StackPanel();
        body.Children.Add(T("设置", 22, bold: true));
        body.Children.Add(T("样式基准：DeskBox · 卡片式设置行 · 紫色强调色", 11, "#8A829C"));

        body.Children.Add(SettingsTitle("图标大小与同步（需求 0）"));
        body.Children.Add(ModeRow());
        body.Children.Add(SettingRow("PX", "手动图标大小", "自动模式下禁用；手动模式固定使用此值", ManualPxControl()));
        body.Children.Add(SettingRow("3s", "气泡提示时长（秒）", "气泡显示满 3 秒后弹出同步信息请求", NumberControl(_c.Config.Icon.BubbleMs / 1000, 1, 20, v => _c.SetBubbleSeconds(v))));
        body.Children.Add(SettingRow("5s", "未确认超时（秒）", "请求弹出后等待用户操作的时长", NumberControl(_c.Config.Icon.ConfirmMs / 1000, 1, 30, v => _c.SetConfirmSeconds(v))));
        body.Children.Add(SettingRow("OK", "超时默认行为", "5 秒未确认时按此行为处理", ComboControl(new[] { "默认确认并同步", "默认放弃" }, _c.Config.Icon.TimeoutAction == "accept" ? 0 : 1, i => _c.SetTimeoutAction(i == 0 ? "accept" : "dismiss"))));

        body.Children.Add(SettingsTitle("最近常用（需求 1）"));
        body.Children.Add(SettingRow("N", "可见行数 N", "网格可见区域行数", NumberControl(_c.Config.Recent.Rows, 1, 5, v => _c.SetRows(v))));
        body.Children.Add(SettingRow("M", "可见列数 M", "网格可见区域列数；可见条目 = N×M", NumberControl(_c.Config.Recent.Cols, 2, 10, v => _c.SetCols(v))));
        body.Children.Add(SettingRow("≤", "最多显示 N×10 条", "随 N 自动变化的展示上限", ReadOnlyControl("N×10 条")));
        body.Children.Add(SettingRow("DB", "总数据上限", "MRU 列表最多保留的记录数，默认 128", NumberControl(_c.Config.Recent.Cap, 10, 1024, v => _c.SetCap(v))));
        body.Children.Add(SettingRow("LRU", "超限淘汰策略", "达到上限后移除最久未打开的记录", ComboControl(new[] { "最近最少使用（LRU）", "先进先出（FIFO）" }, _c.Config.Recent.Policy == "fifo" ? 1 : 0, i => _c.SetPolicy(i == 0 ? "lru" : "fifo"))));

        body.Children.Add(SettingsTitle("快捷方式（需求 2 / 2.1 / 2.2）"));
        body.Children.Add(SettingRow("3×3", "二层收纳网格", "固定 3 列 × 3 行可见 + 下拉滚动轮", ReadOnlyControl("3 × 3（固定）")));
        body.Children.Add(SettingRow("1st", "文件夹默认图标规则", "仅含快捷方式文件的文件夹 → 生成聚合图标", ComboControl(new[] { "字母排序第一项", "文件系统顺序第一项" }, _c.Config.Shortcuts.IconRule == "alpha-first" ? 0 : 1, i => _c.SetIconRule(i == 0 ? "alpha-first" : "first"))));
        body.Children.Add(SettingRow("→", "首次拖入自动打开二级窗口", "文件夹第一次被拖入时立即展开", SwitchControl(_c.Config.Shortcuts.AutoOpenFirst, v => _c.SetAutoOpenFirst(v))));
        body.Children.Add(SettingRow("≍", "同名项处理", "拖入时若目标大类已有同名项，弹窗询问「合并」或「保留」", ReadOnlyControl("每次询问")));
        body.Children.Add(SettingRow("⛔", "文件夹准入规则", "文件夹须全部由 .lnk / .url 组成；含其它文件或子目录时弹窗阻止", ReadOnlyControl("仅快捷方式")));
        body.Children.Add(SettingRow("✎", "条目编辑", "一层图标、二层子项、标签页大类均支持右键修改与删除", ReadOnlyControl("右键菜单")));
        body.Children.Add(SettingRow("↕", "鼠标滚轮", "一层滚动框原生滚轮；最近常用与二层轮动条按一行翻页", ReadOnlyControl("已适配")));

        body.Children.Add(SettingsTitle("待回收区（改善 1）"));
        body.Children.Add(SettingRow("♻", "待回收区", "删除的快捷方式先暂存到这里，标题栏按钮可打开；期间可还原或彻底删除", ReadOnlyControl($"{_c.RecycleCount} 条")));
        body.Children.Add(SettingRow("7d", "保留天数", "删除后保留多少天；启动时与每 60 秒复查一次，到期自动永久清除", NumberControl(_c.Config.Recycle.RetentionDays, 1, 30, v => _c.SetRecycleRetentionDays(v))));
        body.Children.Add(SettingRow("⌂", "数据根目录", _c.DataRootDir, ReadOnlyControl("config / 清单 / 快照")));
        body.Children.Add(SettingRow("▤", "快捷方式清单", "根目录下的「快捷方式清单.json」保存当前所有大类与路径，每次写盘同步更新", ReadOnlyControl("已启用")));

        body.Children.Add(SettingsTitle("常规与性能"));
        body.Children.Add(SettingRow("TH", "外观主题", "跟随系统时每 2 秒检测 Windows 应用的浅色 / 深色设置",
            ComboControl(new[] { "跟随系统", "浅色", "深色" }, ThemeIndex(),
                i => ApplyTheme(i == 1 ? "light" : i == 2 ? "dark" : "system"))));
        body.Children.Add(SettingRow("↻", "开机自启", "写入当前用户 Run 项，随 Windows 登录启动",
            SwitchControl(_c.Config.Ui.StartWithWindows, v => { _c.Config.Ui.StartWithWindows = v; ApplyStartup(v); _c.SaveNow(); })));
        var exitBtn = Bt("退出 DeskLite", (_, _) => ConfirmExit(), "#FFFFFF");
        exitBtn.Background = B("#C94F6D", 0.9);
        exitBtn.BorderBrush = B("#C94F6D");
        body.Children.Add(SettingRow("◉", "关闭请求拦截", "无边框无关闭按钮；Alt+F4 等关闭请求一律隐藏到托盘，双击托盘图标恢复", ReadOnlyControl("始终启用")));
        body.Children.Add(SettingRow("⏻", "退出程序", "窗口内唯一退出入口；也可右键托盘图标选择「退出」", exitBtn));
        body.Children.Add(SettingRow("1", "单实例启动", "再次启动会把已运行的窗口从托盘唤起", ReadOnlyControl("已启用")));
        body.Children.Add(SettingRow("▭", "窗口位置记忆", "关闭或隐藏时保存位置与尺寸，下次启动恢复", ReadOnlyControl("已启用")));
        body.Children.Add(SettingRow("⚡", "变化检测", "WM_SETTINGCHANGE 事件即时触发，2 秒轮询仅作兜底", ReadOnlyControl("事件 + 2 秒")));
        body.Children.Add(SettingRow("▣", "调用快捷方式自身图标",
            "通过 Shell API 提取 .lnk / .url 的真实图标并按路径缓存；关闭则回到零开销彩色占位图标",
            SwitchControl(_c.Config.Ui.UseRealIcons, v =>
            {
                _c.Config.Ui.UseRealIcons = v;
                ShortcutIconProvider.Clear();
                _c.SaveNow();
                RebuildData();
            })));
        body.Children.Add(SettingRow("↗", "隐藏快捷方式箭头角标",
            "经系统图像列表提取 .lnk / .url 的原图标，与目标程序图标一致且无左下角小箭头；失败自动回退带角标通道",
            SwitchControl(_c.Config.Ui.HideShortcutOverlay, v =>
            {
                _c.Config.Ui.HideShortcutOverlay = v;
                ShortcutIconProvider.HideOverlay = v;
                ShortcutIconProvider.Clear();
                _c.SaveNow();
                RebuildData();
            })));
        body.Children.Add(SettingRow("⇩", "外部文件拖放", "窗口已登记为系统拖放目标；拖到标签页可指定大类，拖入过程见 dragdrop.log",
            ReadOnlyControl("双通道已启用")));
        body.Children.Add(SettingRow("关于", "桌面助手 v1.6",
            "WinUI 3 / Windows App SDK 2.5.1 / .NET 10 · 无边框 · 待回收区 · 真实图标（默认无箭头角标）",
            ReadOnlyControl("增量版本")));

        scroll.Content = body;
        Grid.SetColumn(scroll, 1);
        layer.Children.Add(scroll);
        return layer;
    }

    private FrameworkElement SettingsTitle(string text)
    {
        var t = T(text, 12, "#B45CF0", true);
        t.Margin = new Thickness(0, 18, 0, 8);
        return t;
    }

    private Border SettingRow(string icon, string title, string desc, FrameworkElement control)
    {
        var b = Panel(10);
        b.Margin = new Thickness(0, 0, 0, 6);
        b.Padding = new Thickness(11);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        var ic = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7),
            Background = B("#6C8CFF", 0.65), Child = T(icon, 10, "#FFFFFF", true) };
        Grid.SetColumn(ic, 0);
        var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tx.Children.Add(T(title, 12, bold: true));
        tx.Children.Add(T(desc, 10.5, "#8A829C"));
        Grid.SetColumn(tx, 1);
        Grid.SetColumn(control, 2);
        g.Children.Add(ic); g.Children.Add(tx); g.Children.Add(control);
        b.Child = g;
        return b;
    }

    private FrameworkElement ModeRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var a = Bt("自动（跟随桌面）", (_, _) => _c.SwitchMode("auto"));
        var m = Bt("手动（固定值）", (_, _) => _c.SwitchMode("manual"));
        row.Children.Add(a); row.Children.Add(m);
        return SettingRow("A", "图标大小模式", "自动：跟随系统桌面；手动：忽略系统变化", row);
    }

    private FrameworkElement NumberControl(int value, int min, int max, Action<int> onChange)
    {
        var nb = new NumberBox { Value = value, Minimum = min, Maximum = max, Width = 110,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        nb.ValueChanged += (s, e) => { if (!_loadingSettings && !double.IsNaN(nb.Value)) onChange((int)nb.Value); };
        return nb;
    }

    private FrameworkElement ManualPxControl()
    {
        var s = new Slider { Minimum = 32, Maximum = 96, StepFrequency = 16, Value = _c.Config.Icon.ManualPx, Width = 130 };
        var t = T($"{_c.Config.Icon.ManualPx} px", 11);
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        s.ValueChanged += (_, e) => { t.Text = $"{(int)e.NewValue} px"; if (!_loadingSettings) _c.SetManualPx((int)e.NewValue); };
        p.Children.Add(s); p.Children.Add(t);
        return p;
    }

    private FrameworkElement ComboControl(string[] items, int selected, Action<int> onChange)
    {
        var cb = new ComboBox { Width = 170 };
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedIndex = selected;
        cb.SelectionChanged += (_, _) => { if (!_loadingSettings && cb.SelectedIndex >= 0) onChange(cb.SelectedIndex); };
        return cb;
    }

    private FrameworkElement SwitchControl(bool value, Action<bool> onChange)
    {
        var sw = new ToggleSwitch { IsOn = value };
        sw.Toggled += (_, _) => { if (!_loadingSettings) onChange(sw.IsOn); };
        return sw;
    }

    private static FrameworkElement ReadOnlyControl(string text) => new Border
    {
        Background = B("#FFFFFF", 0.06), BorderBrush = B("#FFFFFF", 0.12),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(9, 5, 9, 5), Child = T(text, 11, "#A9A2B9")
    };

    private static void ApplyStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue("DeskLite", $"\"{exe}\"");
            }
            else key.DeleteValue("DeskLite", false);
        }
        catch { /* 无权限时忽略 */ }
    }

    // ---------------- 数据刷新 ----------------

    private void RebuildData()
    {
        RefreshRecentGrid();
        RefreshTabs();
        RefreshGrid1();
        RefreshGrid2();
        RefreshDynamic();
    }

    private string _lastStatus = "", _lastMode = "", _lastChip = "", _lastCd = "", _lastOld = "", _lastNew = "";
    private bool? _lastAuto;
    private bool _lastAwaiting;
    private double _lastBar = -1;

    /// <summary>只在值真正变化时写 UI，空闲时不触碰任何控件，避免无谓重绘。</summary>
    private void RefreshDynamic()
    {
        if (_statusText == null) return;
        bool auto = _c.Config.Icon.Mode == "auto";
        string status = _c.Status;
        string mode = auto ? "自动（跟随桌面）" : $"手动（固定 {_c.AppPx} px）";
        string chip = $"{_c.AppPx} px";

        if (status != _lastStatus)
        {
            _statusText.Text = status;
            _statusText.Foreground = B(_c.Phase == SyncPhase.Manual ? "#F2B45A" : "#4CD08A");
            _lastStatus = status;
        }
        if (mode != _lastMode) { _modeText.Text = mode; _lastMode = mode; }
        if (chip != _lastChip) { _chipPx.Text = chip; _lastChip = chip; }
        if (_lastAuto != auto)
        {
            _btnAuto.Background = B(auto ? "#B45CF0" : "#FFFFFF", auto ? 1 : 0.07);
            _btnManual.Background = B(auto ? "#FFFFFF" : "#B45CF0", auto ? 0.07 : 1);
            _lastAuto = auto;
        }

        // v1.5：待回收区数量变化时刷新标题按钮；列表刷新排到下一轮消息，避免在按钮事件内销毁自身控件树。
        if (_trashBtnText != null && _lastRecycleCount != _c.RecycleCount)
        {
            _trashBtnText.Text = $"待回收区 ({_c.RecycleCount})";
            _lastRecycleCount = _c.RecycleCount;
            if (_recycleLayer != null && _recycleLayer.Visibility == Visibility.Visible)
                _root.DispatcherQueue.TryEnqueue(RefreshRecycleList);
        }

        bool awaiting = _c.Phase == SyncPhase.Awaiting;
        if (awaiting != _lastAwaiting)
        {
            _confirmBox.Visibility = awaiting ? Visibility.Visible : Visibility.Collapsed;
            _lastAwaiting = awaiting;
        }
        if (!awaiting) return;

        string oldPx = $"{_c.PendingOldPx} px", newPx = $"{_c.PendingNewPx} px";
        if (oldPx != _lastOld) { _confirmOld.Text = oldPx; _lastOld = oldPx; }
        if (newPx != _lastNew) { _confirmNew.Text = newPx; _lastNew = newPx; }
        string cd = $"{(_c.ConfirmRemainingMs / 1000.0):F1} s 后" + (_c.Config.Icon.TimeoutAction == "accept" ? "默认确认" : "默认放弃");
        if (cd != _lastCd) { _cdText.Text = cd; _lastCd = cd; }
        double bar = Math.Clamp(_c.ConfirmRemainingMs / _c.ConfirmTotalMs * 100, 0, 100);
        if (Math.Abs(bar - _lastBar) > 1) { _cbar.Value = bar; _lastBar = bar; }
    }

    private void RefreshRecentGrid()
    {
        _recentGrid.Children.Clear();
        _recentGrid.RowDefinitions.Clear();
        _recentGrid.ColumnDefinitions.Clear();
        int rows = Math.Clamp(_c.Config.Recent.Rows, 1, 5);
        int cols = Math.Clamp(_c.Config.Recent.Cols, 2, 10);
        for (int i = 0; i < rows; i++) _recentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
        for (int j = 0; j < cols; j++) _recentGrid.ColumnDefinitions.Add(new ColumnDefinition());

        int px = Math.Clamp((int)(_c.AppPx * 0.62), 22, 46);
        var window = _c.RecentWindow();
        for (int idx = 0; idx < window.Count; idx++)
        {
            var item = window[idx];
            var cell = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(4, 6, 4, 4) };
            cell.Children.Add(item == null
                ? new Border { Width = px, Height = px, CornerRadius = new CornerRadius(10), Background = B("#FFFFFF", 0.03) }
                : IconVisual(Palette.Initial(item.Title), item.ColorIndex, px, RealIconForRecent(item)));
            var label = T(item?.Title ?? "", 10.5, "#A9A2B9");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MaxWidth = 90;
            cell.Children.Add(label);
            Grid.SetRow(cell, idx / cols);
            Grid.SetColumn(cell, idx % cols);
            if (item != null)
            {
                var captured = item;
                // v1.4-3：改用 Tapped，右键只弹菜单、不触发打开。
                cell.Tapped += (_, _) => _c.OpenRecentFile(captured);
            }
            _recentGrid.Children.Add(cell);
        }
        _recentCount.Text = $"{_c.Recent.EffectiveMax} / {_c.Config.Recent.Cap} 条";
        _gridPill.Text = $"N×M = {rows}×{cols}（{rows * cols} 格）";
        SyncWheels();
    }

    private void RefreshTabs()
    {
        _tabsPanel.Children.Clear();
        foreach (var tab in _c.Config.Shortcuts.Tabs)
        {
            var b = Bt(tab.Name, (_, _) => _c.SetActiveTab(tab.Id));
            b.Background = B(tab.Id == _c.ActiveTabId ? "#B45CF0" : "#FFFFFF", tab.Id == _c.ActiveTabId ? 0.32 : 0.05);
            // v1.3：拖放落点落在该按钮范围内时归入此大类（由根容器统一解析，不再逐按钮挂事件）
            b.Tag = tab.Id;
            // v1.4-3：标签页大类也支持右键重命名 / 删除
            b.ContextFlyout = TabMenu(tab);
            ToolTipService.SetToolTip(b, "左键切换大类 · 右键重命名或删除");
            _tabsPanel.Children.Add(b);
        }
        var add = Bt("＋ 新大类", (_, _) => _c.AddTab());
        add.Background = B("#FFFFFF", 0.03);
        _tabsPanel.Children.Add(add);
    }

    private void RefreshGrid1()
    {
        _grid1.Children.Clear();
        _grid1.RowDefinitions.Clear();
        _grid1.ColumnDefinitions.Clear();
        var tab = _c.ActiveTab();
        int cols = 5;
        int rows = Math.Max(2, (int)Math.Ceiling(tab.Items.Count / (double)cols));
        for (int j = 0; j < cols; j++) _grid1.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows; i++) _grid1.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });

        int px = Math.Clamp((int)(_c.AppPx * 0.55), 24, 38);
        for (int idx = 0; idx < tab.Items.Count; idx++)
        {
            var item = tab.Items[idx];
            var cell = new Grid { Margin = new Thickness(3) };
            var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            int color = item.IconOverride ?? item.ColorIndex;
            string label = item.Type == "folder" ? _c.DefaultIconLabel(item) : Palette.Initial(item.DisplayName);
            stack.Children.Add(IconVisual(label, color, px, RealIconForItem(item)));
            var name = T(item.DisplayName, 10.5, "#A9A2B9");
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = 92;
            stack.Children.Add(name);
            cell.Children.Add(stack);
            if (item.Type == "folder")
            {
                var badge = new Border
                {
                    Background = B("#6C8CFF", 0.9), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(4, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top, Child = T(item.Children.Count.ToString(), 9, "#FFFFFF")
                };
                cell.Children.Add(badge);
            }
            Grid.SetRow(cell, idx / cols); Grid.SetColumn(cell, idx % cols);
            var captured = item;
            cell.Tapped += (_, _) => _c.OpenShortcut(captured);
            cell.ContextFlyout = ItemMenu(captured);
            ToolTipService.SetToolTip(cell, "左键打开 · 右键修改或删除");
            _grid1.Children.Add(cell);
        }
        _t1Count.Text = $"{tab.Items.Count} 项";
    }

    private void RefreshGrid2()
    {
        _grid2.Children.Clear();
        _grid2.RowDefinitions.Clear();
        _grid2.ColumnDefinitions.Clear();
        int rows = Math.Clamp(_c.Config.Shortcuts.Grid2Rows, 1, 5);
        int cols = Math.Clamp(_c.Config.Shortcuts.Grid2Cols, 1, 5);
        for (int i = 0; i < rows; i++) _grid2.RowDefinitions.Add(new RowDefinition());
        for (int j = 0; j < cols; j++) _grid2.ColumnDefinitions.Add(new ColumnDefinition());

        var folder = _c.ActiveFolder();
        _t2Path.Text = folder == null ? "· 未打开" : $"· {_c.ActiveTab().Name} › {folder.DisplayName}";
        var window = _c.FolderWindow();
        for (int idx = 0; idx < window.Count; idx++)
        {
            var item = window[idx];
            var cell = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            if (item != null)
            {
                cell.Children.Add(IconVisual(Palette.Initial(item.DisplayName), item.ColorIndex, 28, RealIconForItem(item)));
                var name = T(item.DisplayName, 10.5, "#A9A2B9");
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.MaxWidth = 100;
                cell.Children.Add(name);
                var captured = item;
                cell.Tapped += (_, _) => _c.OpenShortcut(captured);
                cell.ContextFlyout = ItemMenu(captured);
                ToolTipService.SetToolTip(cell, "左键打开 · 右键修改或删除");
            }
            Grid.SetRow(cell, idx / cols); Grid.SetColumn(cell, idx % cols);
            _grid2.Children.Add(cell);
        }
        SyncWheels();
    }

    // ---------------- 交互 ----------------

    // ---------------- 拖放（v1.3 双通道） ----------------

    /// <summary>
    /// XAML / OLE 通道：只在根容器挂一次。根容器有真实背景且覆盖整个客户区，
    /// 任何位置（空白区、卡片上、标签栏）都能命中；目标标签页由落点统一解析。
    /// </summary>
    private void AttachRootDrop()
    {
        _root.AllowDrop = true;
        _root.DragEnter += (_, e) =>
        {
            if (CanAccept(e)) ShowDropHint();
        };
        _root.DragOver += (_, e) =>
        {
            bool ok = CanAccept(e);
            e.AcceptedOperation = ok ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (e.DragUIOverride != null)
            {
                e.DragUIOverride.Caption = ok ? "放入桌面助手" : "不支持的内容";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = ok;
            }
        };
        _root.DragLeave += (_, _) => HideDropHint();
        _root.Drop += async (_, e) =>
        {
            HideDropHint();
            await HandleDropAsync(e, null);
        };
    }

    private static bool CanAccept(DragEventArgs e) =>
        e.DataView.Contains(StandardDataFormats.StorageItems) ||
        e.DataView.Contains(StandardDataFormats.Text);

    private void ShowDropHint()
    {
        if (_dropHint != null) _dropHint.Visibility = Visibility.Visible;
    }

    private void HideDropHint()
    {
        if (_dropHint != null) _dropHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>按落点找出目标标签页；不在标签栏上则返回 null（归入当前标签页）。</summary>
    private string? ResolveTabId(Point clientPoint)
    {
        try
        {
            foreach (var child in _tabsPanel.Children)
            {
                if (child is not FrameworkElement fe || fe.Tag is not string id) continue;
                var bounds = fe.TransformToVisual(_root)
                    .TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                if (clientPoint.X >= bounds.X && clientPoint.X <= bounds.X + bounds.Width &&
                    clientPoint.Y >= bounds.Y && clientPoint.Y <= bounds.Y + bounds.Height)
                    return id;
            }
        }
        catch (Exception ex) { AppLog.Write("ResolveTabId", ex); }
        return null;
    }

    /// <summary>
    /// Win32 与 XAML 两条通道共用的落库入口。
    /// v1.4 功能完善 2：文件夹先做准入校验，不合格的弹窗阻止且不生成条目；
    /// v1.4 功能完善 1：目标大类内存在同名项时，弹窗询问「合并 / 保留」后再提交。
    /// </summary>
    private void OnFilesDropped(IReadOnlyList<string> paths, string? explicitTabId, Point? clientPoint)
    {
        var valid = paths.Where(p => !string.IsNullOrWhiteSpace(p) &&
                                    (File.Exists(p) || Directory.Exists(p)))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0)
        {
            AddToast("未识别到文件", "请从资源管理器拖入文件、快捷方式或文件夹。", "warn");
            AppLog.Drop("未识别到有效路径：" + string.Join(" | ", paths));
            return;
        }
        string? tabId = explicitTabId ?? (clientPoint.HasValue ? ResolveTabId(clientPoint.Value) : null);
        var tabName = _c.Config.Shortcuts.Tabs
            .FirstOrDefault(t => t.Id == (tabId ?? _c.ActiveTabId))?.Name ?? "当前标签页";

        // Win32 与 XAML 两条通道可能对同一次拖放各投递一次；1.2 秒内的相同路径集只处理一次
        var key = string.Join("|", valid.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var now = DateTime.UtcNow;
        if (key == _lastDropKey && (now - _lastDropAt).TotalMilliseconds < 1200)
        {
            AppLog.Drop("忽略重复投递（双通道去重）");
            return;
        }
        _lastDropKey = key;
        _lastDropAt = now;

        // 功能完善 2：只放行「全部为 .lnk / .url」的文件夹；其余文件夹整项阻止。
        var accepted = new List<string>();
        var blocked = new List<string>();
        foreach (var p in valid)
        {
            if (Directory.Exists(p))
            {
                string? reason = ShortcutService.FolderRejectReason(p);
                if (reason != null)
                {
                    blocked.Add($"· {ShortcutService.DropDisplayName(p)}：{reason}");
                    AppLog.Drop($"已阻止文件夹：{p} · {reason}");
                    continue;
                }
            }
            accepted.Add(p);
        }

        // 功能完善 1：先入队同名询问（决定落库方式），再入队阻止提示。
        var conflicts = _c.FindSameNameConflicts(accepted, tabId);
        if (conflicts.Count > 0)
        {
            string names = string.Join("、", conflicts.Take(5)) +
                           (conflicts.Count > 5 ? $" 等 {conflicts.Count} 项" : "");
            var pendingPaths = accepted;
            var pendingTab = tabId;
            EnqueueDialog(new DialogSpec(
                "检测到同名项，是否合并？",
                $"目标大类「{tabName}」中已存在：{names}。\n\n" +
                "「合并」：同名项并入已有条目（两个文件夹按快捷方式去重），不生成重复图标；\n" +
                "「保留」：两项都保留，各自成为独立条目。",
                "合并", () => CommitDrop(pendingPaths, pendingTab, true),
                "保留", () => CommitDrop(pendingPaths, pendingTab, false)));
        }
        else if (accepted.Count > 0)
        {
            _c.AddDroppedPaths(accepted, tabId, false);
            AppLog.Drop($"已归入「{tabName}」：{accepted.Count} 项");
        }

        if (blocked.Count > 0)
        {
            string list = string.Join("\n", blocked.Take(6)) +
                          (blocked.Count > 6 ? $"\n… 共 {blocked.Count} 个文件夹" : "");
            EnqueueDialog(new DialogSpec(
                "已阻止文件夹加入",
                list + "\n\n仅支持全部由 .lnk / .url 快捷方式组成的文件夹；被阻止的文件夹未生成任何条目。" +
                (accepted.Count > 0 ? "同批其它项目照常处理。" : "本次未加入任何项目。"),
                "知道了", null));
        }
    }

    /// <summary>用户在同名询问中做出选择后，按合并 / 保留策略提交本次拖放。</summary>
    private void CommitDrop(List<string> list, string? tabId, bool merge)
    {
        if (list.Count > 0) _c.AddDroppedPaths(list, tabId, merge);
        AddToast(merge ? "同名项已合并" : "同名项已保留",
            merge ? "同名项已并入已有条目，未生成重复图标。" : "同名项作为独立条目保留，未做合并。",
            merge ? "ok" : "info");
        AppLog.Drop($"同名处理：{(merge ? "合并" : "保留")}，{list.Count} 项");
    }

    private async Task HandleDropAsync(DragEventArgs e, string? tabId)
    {
        var deferral = e.GetDeferral();
        try
        {
            AppLog.Drop("XAML Drop 事件触发");
            var paths = new List<string>();
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths.AddRange(items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            else if (e.DataView.Contains(StandardDataFormats.Text))
            {
                // 兼容部分程序拖出的纯文本路径（一行一个）
                var text = await e.DataView.GetTextAsync();
                paths.AddRange(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim().Trim('"')));
            }
            OnFilesDropped(paths, tabId, e.GetPosition(_root));
        }
        catch (Exception ex)
        {
            AppLog.Write("XamlDrop", ex);
            AddToast("拖入处理失败", "详情见 error.log；Win32 通道仍可用，可重试拖放。", "warn");
        }
        finally { deferral.Complete(); }
    }

    private void OpenSettings()
    {
        _loadingSettings = true;
        if (_recycleLayer != null) _recycleLayer.Visibility = Visibility.Collapsed;
        _settingsLayer.Visibility = Visibility.Visible;
        _loadingSettings = false;
    }

    // ---------------- v1.4：对话框 / 编辑器 / 右键菜单 / 滚轮 ----------------

    private void EnqueueDialog(DialogSpec spec)
    {
        _dialogQueue.Enqueue(spec);
        if (!_dialogOpen) ShowNextDialog();
    }

    private void ShowNextDialog()
    {
        if (_dialogBox == null || _dialogQueue.Count == 0) return;
        _currentDialog = _dialogQueue.Dequeue();
        RenderDialog(_currentDialog);
    }

    private void RenderDialog(DialogSpec spec)
    {
        _dialogTitle.Text = spec.Title;
        _dialogBody.Text = spec.Body;
        _dialogPrimary.Content = T(spec.Primary, 11.5, "#FFFFFF", true);
        _dialogPrimary.Background = B(spec.Danger ? "#C94F6D" : "#B45CF0");
        _dialogPrimary.BorderBrush = B(spec.Danger ? "#C94F6D" : "#B45CF0");
        if (spec.Secondary != null) _dialogSecondary.Content = T(spec.Secondary, 11.5, "#F3F1F8");
        _dialogSecondary.Visibility = spec.Secondary == null ? Visibility.Collapsed : Visibility.Visible;
        _dialogShade.Visibility = Visibility.Visible;
        _dialogBox.Visibility = Visibility.Visible;
        _dialogOpen = true;
    }

    private void CloseDialog(bool primary)
    {
        var spec = _currentDialog;
        _dialogBox.Visibility = Visibility.Collapsed;
        _dialogShade.Visibility = Visibility.Collapsed;
        _currentDialog = null;
        try { if (primary) spec?.OnPrimary?.Invoke(); else spec?.OnSecondary?.Invoke(); }
        catch (Exception ex) { AppLog.Write("DialogAction", ex); }
        // action 里可能继续入队；保持 _dialogOpen=true 直到统一取下一个，避免覆盖。
        _dialogOpen = false;
        ShowNextDialog();
    }

    /// <summary>一层 / 二层条目右键菜单：修改、删除。</summary>
    private MenuFlyout ItemMenu(ShortcutItem item)
    {
        var menu = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "修改名称 / 路径 / 图标…" };
        edit.Click += (_, _) => OpenEditor(item.Id);
        var del = new MenuFlyoutItem { Text = "移入待回收区…" };
        del.Click += (_, _) => ConfirmDeleteItem(item.Id, item.DisplayName);
        menu.Items.Add(edit);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(del);
        return menu;
    }

    /// <summary>标签页大类右键菜单：重命名、删除。</summary>
    private MenuFlyout TabMenu(ShortcutTab tab)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "重命名大类…" };
        rename.Click += (_, _) => OpenTabEditor(tab.Id);
        var del = new MenuFlyoutItem { Text = "移入待回收区…" };
        del.Click += (_, _) => ConfirmDeleteTab(tab.Id, tab.Name);
        menu.Items.Add(rename);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(del);
        return menu;
    }

    private void ConfirmDeleteItem(string id, string name) =>
        EnqueueDialog(new DialogSpec("移入待回收区",
            $"确定把「{name}」移入待回收区吗？\n\n将保留 {_c.RecycleRetentionDays} 天，期间可在标题栏「待回收区」还原；到期自动清除。磁盘文件不受影响。",
            "移入", () =>
            {
                bool ok = _c.DeleteItem(id);
                AddToast(ok ? "已移入待回收区" : "操作失败",
                    ok ? $"「{name}」已暂存，{_c.RecycleRetentionDays} 天内可还原。" : "该条目可能已被移除。",
                    ok ? "ok" : "warn");
            }, "取消", null, true));

    private void ConfirmDeleteTab(string id, string name) =>
        EnqueueDialog(new DialogSpec("移入待回收区",
            $"确定把大类「{name}」及其全部条目移入待回收区吗？\n\n将整体保留 {_c.RecycleRetentionDays} 天，期间可一次性还原；到期自动清除。至少需要保留一个大类。",
            "移入", () =>
            {
                bool ok = _c.DeleteTab(id);
                AddToast(ok ? "已移入待回收区" : "操作失败",
                    ok ? $"大类「{name}」已暂存，可整体还原。" : "至少需要保留一个标签页大类。",
                    ok ? "ok" : "warn");
            }, "取消", null, true));

    private void OpenEditor(string id)
    {
        var item = _c.FindItem(id);
        if (item == null) return;
        _editId = id;
        _editIsTab = false;
        _editCaption.Text = item.Type == "folder" ? "修改文件夹聚合项" : "修改快捷方式";
        _editName.Text = item.DisplayName;
        _editTarget.Text = item.Type == "folder" ? (item.SourceDir ?? "") : item.Target;
        _editTargetRow.Visibility = Visibility.Visible;
        _editColorRow.Visibility = Visibility.Visible;
        int raw = item.IconOverride ?? item.ColorIndex;
        _editPick = ((raw % Palette.Colors.Length) + Palette.Colors.Length) % Palette.Colors.Length;
        BuildColorPicker();
        _editBox.Visibility = Visibility.Visible;
    }

    private void OpenTabEditor(string id)
    {
        var tab = _c.Config.Shortcuts.Tabs.FirstOrDefault(t => t.Id == id);
        if (tab == null) return;
        _editId = id;
        _editIsTab = true;
        _editCaption.Text = "重命名大类";
        _editName.Text = tab.Name;
        _editTargetRow.Visibility = Visibility.Collapsed;
        _editColorRow.Visibility = Visibility.Collapsed;
        _editBox.Visibility = Visibility.Visible;
    }

    private void BuildColorPicker()
    {
        _editColors.Children.Clear();
        for (int i = 0; i < Palette.Colors.Length; i++)
        {
            int idx = i;
            var b = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(6),
                Background = B(Palette.Of(i)),
                BorderBrush = B("#FFFFFF", i == _editPick ? 1 : 0),
                BorderThickness = new Thickness(2),
                Child = T(i.ToString(), 9, "#FFFFFF", true)
            };
            b.PointerPressed += (_, _) =>
            {
                _editPick = idx;
                foreach (var child in _editColors.Children.OfType<Border>())
                    child.BorderBrush = B("#FFFFFF", 0);
                b.BorderBrush = B("#FFFFFF", 1);
            };
            _editColors.Children.Add(b);
        }
    }

    private void SaveEditor()
    {
        if (_editId == null) return;
        bool showTarget = _editTargetRow.Visibility == Visibility.Visible;
        bool showColor = _editColorRow.Visibility == Visibility.Visible;
        bool ok = _editIsTab
            ? _c.RenameTab(_editId, _editName.Text)
            : _c.UpdateItem(_editId, _editName.Text,
                showTarget ? _editTarget.Text : null,
                showColor ? _editPick : (int?)null);
        _editBox.Visibility = Visibility.Collapsed;
        _editId = null;
        AddToast(ok ? "已保存修改" : "修改失败",
            ok ? "仅影响软件内显示与跳转目标，不改写源文件。" : "名称不能为空，或对应条目已被删除。",
            ok ? "ok" : "warn");
    }

    private void ConfirmDelete()
    {
        if (_editId == null) return;
        string id = _editId;
        bool isTab = _editIsTab;
        string name = _editName.Text;
        EnqueueDialog(new DialogSpec("移入待回收区",
            isTab
                ? $"确定把大类「{name}」及其全部条目移入待回收区吗？\n\n保留 {_c.RecycleRetentionDays} 天，期间可整体还原；至少需保留一个大类。"
                : $"确定把「{name}」移入待回收区吗？\n\n保留 {_c.RecycleRetentionDays} 天，期间可还原；到期自动清除。磁盘文件不受影响。",
            "移入", () =>
            {
                bool ok = isTab ? _c.DeleteTab(id) : _c.DeleteItem(id);
                _editBox.Visibility = Visibility.Collapsed;
                _editId = null;
                AddToast(ok ? "已移入待回收区" : "操作失败",
                    ok ? $"已暂存，{_c.RecycleRetentionDays} 天内可还原。" : "至少需保留一个大类，或条目已不存在。",
                    ok ? "ok" : "warn");
            }, "取消", null, true));
    }

    /// <summary>v1.4-4：根容器统一接滚轮，按落点分发；设置层 / 弹窗打开时不抢事件。</summary>
    private void OnRootWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            if (_settingsLayer != null && _settingsLayer.Visibility == Visibility.Visible) return;
            if (_recycleLayer != null && _recycleLayer.Visibility == Visibility.Visible) return;
            if (_dialogOpen || (_editBox != null && _editBox.Visibility == Visibility.Visible)) return;
            var pt = e.GetCurrentPoint(_root);
            int delta = pt.Properties.MouseWheelDelta;
            if (delta == 0) return;
            int dir = delta > 0 ? -1 : 1;
            var pos = pt.Position;

            if (_recentPanel != null && IsInside(_recentPanel, pos))
            {
                _c.ScrollRecentBy(dir * Math.Max(1, _c.Config.Recent.Cols));
                SyncWheels();
                e.Handled = true;
            }
            else if (_rightPanel != null && IsInside(_rightPanel, pos) && _c.ActiveFolder() != null)
            {
                _c.ScrollFolderBy(dir * Math.Max(1, _c.Config.Shortcuts.Grid2Cols));
                SyncWheels();
                e.Handled = true;
            }
            // 一层区域由 ScrollViewer 原生处理滚轮，此处不重复响应。
        }
        catch (Exception ex) { AppLog.Write("Wheel", ex); }
    }

    private bool IsInside(FrameworkElement element, Point p)
    {
        try
        {
            var b = element.TransformToVisual(_root)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return p.X >= b.X && p.X <= b.X + b.Width && p.Y >= b.Y && p.Y <= b.Y + b.Height;
        }
        catch { return false; }
    }

    /// <summary>滚动后把三个轮动条同步到当前偏移，避免滑块与实际窗口脱节。</summary>
    private void SyncWheels()
    {
        if (_wheelV == null || _wheelH == null || _wheel2 == null) return;
        _syncingWheels = true;
        try
        {
            double spanR = Math.Max(1, _c.Recent.EffectiveMax - _c.Recent.VisibleCount);
            double pR = Math.Clamp(_c.RecentOffset / spanR * 100.0, 0, 100);
            _wheelV.Value = pR;
            _wheelH.Value = pR;
            int view = Math.Clamp(_c.Config.Shortcuts.Grid2Rows, 1, 5) *
                       Math.Clamp(_c.Config.Shortcuts.Grid2Cols, 1, 5);
            double spanF = Math.Max(1, _c.FolderChildCount() - view);
            _wheel2.Value = Math.Clamp(_c.T2Offset / spanF * 100.0, 0, 100);
        }
        finally { _syncingWheels = false; }
    }

    /// <summary>v1.4-5：无边框窗口的隐藏入口（不是退出，托盘仍在）。</summary>
    private void HideToTray()
    {
        _hidden = true;
        SaveWindowPlacement();
        NativeWindow.Hide(_hwnd);
    }

    /// <summary>v1.4-5：设置层退出需二次确认；托盘右键「退出」仍可直达。</summary>
    private void ConfirmExit() =>
        EnqueueDialog(new DialogSpec("确认退出桌面助手？",
            "退出后托盘图标会一并移除；需要时可重新运行 DeskLite.exe，或由开机自启拉起。",
            "退出", ExitFromTray, "取消", null, true));

    private void AddToast(string title, string text, string kind)
    {
        var accent = kind == "ok" ? "#4CD08A" : kind == "warn" ? "#F2B45A" : "#B45CF0";
        var b = new Border
        {
            Background = B("#261F32", 0.96), BorderBrush = B(accent, 0.6), BorderThickness = new Thickness(1, 0, 0, 3),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(11), Margin = new Thickness(0, 0, 0, 2)
        };
        var s = new StackPanel();
        s.Children.Add(T(title, 11.5, bold: true));
        var p = T(text, 10.5, "#A9A2B9");
        p.TextWrapping = TextWrapping.Wrap;
        s.Children.Add(p);
        b.Child = s;
        _toastList.Children.Add(b);
        _toasts.Add((b, DateTime.UtcNow.AddSeconds(3.6)));
        while (_toasts.Count > 4)
        {
            var old = _toasts[0];
            _toastList.Children.Remove(old.Element);
            _toasts.RemoveAt(0);
        }
    }

    private void ExpireToasts()
    {
        var now = DateTime.UtcNow;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            if (now < _toasts[i].Expire) continue;
            _toastList.Children.Remove(_toasts[i].Element);
            _toasts.RemoveAt(i);
        }
    }
}
