using Microsoft.Win32;

namespace DeskLite.Services;

/// <summary>
/// 主题中心：以 DeskBox 深色版为设计基准，浅色版按「语义」重映射颜色，
/// 避免在 940 余行界面代码里散落两套色值。
/// 支持 system / light / dark 三档；system 读取系统 AppsUseLightTheme。
/// </summary>
public static class UiTheme
{
    private static string _mode = "system";

    /// <summary>当前是否深色。默认深色，与 v1.1 视觉一致。</summary>
    public static bool IsDark { get; private set; } = true;

    public static string Mode => _mode;

    /// <summary>应用主题档位并立即计算深浅。</summary>
    public static void Apply(string mode)
    {
        _mode = mode is "light" or "dark" ? mode : "system";
        IsDark = _mode switch
        {
            "light" => false,
            "dark" => true,
            _ => !SystemUsesLightTheme()
        };
    }

    /// <summary>系统档位下周期性检查；返回 true 表示深浅发生变化、需要重建 UI。</summary>
    public static bool RefreshIfNeeded()
    {
        if (_mode != "system") return false;
        bool dark = !SystemUsesLightTheme();
        if (dark == IsDark) return false;
        IsDark = dark;
        return true;
    }

    /// <summary>Windows 10/11：AppsUseLightTheme = 1 表示应用使用浅色。</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>深色基准色 → 当前主题实际色值。强调色 / 彩色图标块保持不变。</summary>
    public static string Map(string hex)
    {
        if (IsDark) return hex;
        return hex.ToUpperInvariant() switch
        {
            "#1A1622" => "#F3F0F8",   // 窗口底色
            "#F3F1F8" => "#211C2B",   // 主文字
            "#A9A2B9" => "#5D5570",   // 次文字
            "#8A829C" => "#7C7489",   // 弱文字
            "#2E243C" => "#FFFFFF",   // 同步请求卡片
            "#221B2D" => "#FFFFFF",   // 文件夹设置卡片
            "#171320" => "#FAF8FD",   // 设置层背景
            "#261F32" => "#FFFFFF",   // 标签 / 二级窗口
            "#4CD08A" => "#1B8A55",   // 状态绿
            "#F2B45A" => "#A96A17",   // 警告橙
            _ => hex
        };
    }
}
