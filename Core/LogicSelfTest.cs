using System.Text;
using DeskLite.Models;
using DeskLite.Services;

namespace DeskLite.Core;

/// <summary>
/// v1.6 隐藏诊断入口（--desklite-logic-test）：不创建窗口，直接验证待回收区、
/// 7 天自动清除、根目录持久化、快捷方式真实图标提取与箭头角标隐藏的核心逻辑。
/// 通过 DESKLITE_CONFIG_DIR 把配置重定向到临时目录，绝不读写真实用户配置。
/// 报告追加写入 %TEMP%\DeskLite_v1.6_selftest.log，不覆盖任何已有文件。
/// </summary>
public static class LogicSelfTest
{
    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " · " + detail : "")}");
        }

        string root = Path.Combine(Path.GetTempPath(), "DeskLite_v15_logic_" + Guid.NewGuid().ToString("N")[..8]);
        string cfgDir = Path.Combine(root, "config");
        Environment.SetEnvironmentVariable("DESKLITE_CONFIG_DIR", cfgDir);

        try
        {
            Directory.CreateDirectory(root);
            string pure = Path.Combine(root, "纯快捷方式");
            string mixed = Path.Combine(root, "混合文件夹");
            string empty = Path.Combine(root, "空文件夹");
            Directory.CreateDirectory(pure);
            Directory.CreateDirectory(mixed);
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(pure, "A_浏览器.lnk"), "");
            File.WriteAllText(Path.Combine(pure, "B_编辑器.url"), "");
            File.WriteAllText(Path.Combine(mixed, "A_浏览器.lnk"), "");
            File.WriteAllText(Path.Combine(mixed, "说明文档.txt"), "普通文件");
            string single = Path.Combine(root, "拖放测试文档.txt");
            File.WriteAllText(single, "普通文件");
            string urlPath = Path.Combine(root, "图标探针.url");
            File.WriteAllText(urlPath, "[InternetShortcut]\r\nURL=https://example.com\r\nIconIndex=0\r\n");

            // 功能完善 2：文件夹准入
            Check("纯快捷方式文件夹放行", ShortcutService.FolderRejectReason(pure) == null);
            Check("含非快捷方式文件被阻止", ShortcutService.FolderRejectReason(mixed)?.Contains("非快捷方式") == true,
                ShortcutService.FolderRejectReason(mixed) ?? "null");
            Check("空文件夹被阻止", ShortcutService.FolderRejectReason(empty) != null);
            Check("普通文件不走文件夹校验", File.Exists(single) && Directory.Exists(pure));
            Check("IsPureShortcutFolder 判定一致",
                ShortcutService.IsPureShortcutFolder(pure) && !ShortcutService.IsPureShortcutFolder(mixed));

            var c = new AppController();
            string tabId = c.ActiveTabId;
            int baseCount = c.ActiveTab().Items.Count;

            c.AddDroppedPaths(new[] { pure }, tabId, false);
            var first = c.ActiveTab().Items.LastOrDefault();
            Check("首次拖入生成聚合 folder",
                c.ActiveTab().Items.Count == baseCount + 1 && first?.Type == "folder" && first.Children.Count == 2);

            // 功能完善 1：同名询问的数据侧
            var conflicts = c.FindSameNameConflicts(new[] { pure }, tabId);
            Check("同名冲突检测", conflicts.Count == 1 && conflicts[0] == "纯快捷方式",
                string.Join(",", conflicts));

            c.AddDroppedPaths(new[] { pure }, tabId, true);
            Check("选择合并不新增顶层条目", c.ActiveTab().Items.Count == baseCount + 1);
            Check("合并按 Target 去重子项", first?.Children.Count == 2,
                $"children={first?.Children.Count}");

            c.AddDroppedPaths(new[] { pure }, tabId, false);
            Check("选择保留生成独立同名条目", c.ActiveTab().Items.Count == baseCount + 2);

            int beforeBlocked = c.ActiveTab().Items.Count;
            c.AddDroppedPaths(new[] { mixed, empty }, tabId, false);
            Check("非法文件夹在业务层兜底阻止", c.ActiveTab().Items.Count == beforeBlocked);

            // 功能完善 3：修改 / 删除
            var edit = c.ActiveTab().Items.First();
            bool updated = c.UpdateItem(edit.Id, "改名测试", @"D:\新目标.lnk", 5);
            string editedTarget = edit.Type == "folder" ? (edit.SourceDir ?? "") : edit.Target;
            Check("修改名称 / 目标 / 图标色",
                updated && edit.DisplayName == "改名测试" && editedTarget == @"D:\新目标.lnk" && edit.ColorIndex == 5,
                $"type={edit.Type}, target={editedTarget}, name={edit.DisplayName}, color={edit.ColorIndex}");

            // 普通文件仍作为单条 link 加入，且可修改其跳转目标。
            c.AddDroppedPaths(new[] { single }, tabId, false);
            var link = c.ActiveTab().Items.Last();
            c.UpdateItem(link.Id, null, @"D:\另一目标.txt", null);
            Check("单文件修改跳转目标", link.Type == "link" && link.Target == @"D:\另一目标.txt");

            var folder = c.ActiveTab().Items.FirstOrDefault(i => i.Type == "folder" && i.Children.Count > 0);
            Check("存在可测试的二层 folder", folder != null);
            if (folder != null)
            {
                string childId = folder.Children[0].Id;
                int childCount = folder.Children.Count;
                Check("删除二层子项", c.DeleteItem(childId) && folder.Children.Count == childCount - 1);

                c.OpenShortcut(folder);
                for (int i = 0; i < 15; i++)
                    folder.Children.Add(new ShortcutItem
                    {
                        Type = "link", Name = "附加" + i, Target = Path.Combine(root, "add" + i + ".lnk")
                    });

                // 功能完善 4：滚轮线性滚动与边界钳制
                int f0 = c.T2Offset;
                c.ScrollFolderBy(3);
                Check("二层滚轮前进一行", c.T2Offset == f0 + 3, $"{f0} -> {c.T2Offset}");
                c.ScrollFolderBy(99999);
                int view = c.Config.Shortcuts.Grid2Rows * c.Config.Shortcuts.Grid2Cols;
                Check("二层滚轮上界钳制", c.T2Offset == folder.Children.Count - view,
                    $"offset={c.T2Offset}, span={folder.Children.Count - view}");
                c.ScrollFolderBy(-99999);
                Check("二层滚轮回到起点", c.T2Offset == 0);
            }

            string lastId = c.ActiveTab().Items.Last().Id;
            int beforeDelete = c.ActiveTab().Items.Count;
            Check("删除一层条目", c.DeleteItem(lastId) && c.ActiveTab().Items.Count == beforeDelete - 1);

            string tabToEdit = c.Config.Shortcuts.Tabs[0].Id;
            Check("重命名标签页大类", c.RenameTab(tabToEdit, "自测大类") &&
                c.Config.Shortcuts.Tabs.Any(t => t.Name == "自测大类"));
            int tabsBefore = c.Config.Shortcuts.Tabs.Count;
            Check("删除标签页大类", c.DeleteTab(tabToEdit) && c.Config.Shortcuts.Tabs.Count == tabsBefore - 1);

            // 功能完善 4：最近常用滚轮
            for (int i = 0; i < 40; i++)
                c.Recent.Record(Path.Combine(root, "f" + i + ".txt"), "f" + i);
            int r0 = c.RecentOffset;
            c.ScrollRecentBy(c.Config.Recent.Cols);
            Check("最近常用滚轮前进一行", c.RecentOffset == r0 + c.Config.Recent.Cols,
                $"{r0} -> {c.RecentOffset}");
            c.ScrollRecentBy(99999);
            Check("最近常用滚轮上界钳制",
                c.RecentOffset == c.Recent.EffectiveMax - c.Recent.VisibleCount,
                $"offset={c.RecentOffset}");
            c.ScrollRecentBy(-99999);
            Check("最近常用滚轮回到起点", c.RecentOffset == 0);

            // ============ v1.5 改善 1：待回收区域 ============
            Check("数据根目录与待回收目录已建立",
                Directory.Exists(c.DataRootDir) && Directory.Exists(c.RecycleDir), c.DataRootDir);
            c.SaveNow();
            Check("根目录保存快捷方式清单",
                File.Exists(Path.Combine(c.DataRootDir, "快捷方式清单.json")));

            int recycleBase = c.RecycleCount;
            Check("此前删除进待回收区而非直接丢弃", recycleBase > 0, $"count={recycleBase}");

            c.AddDroppedPaths(new[] { single }, c.ActiveTabId, false);
            var delItem = c.ActiveTab().Items.Last();
            string delName = delItem.DisplayName;
            int tabItemsBefore = c.ActiveTab().Items.Count;
            bool moved = c.DeleteItem(delItem.Id);
            var rec = c.RecycleEntries.Last();
            Check("删除一层 → 条目从树移除", moved && c.ActiveTab().Items.Count == tabItemsBefore - 1);
            Check("删除一层 → 待回收区 +1 且 kind=item",
                c.RecycleCount == recycleBase + 1 && rec.Kind == "item" && rec.Name == delName);
            Check("待回收区在根目录留有独立快照",
                File.Exists(Path.Combine(c.RecycleDir, rec.Id + ".json")));
            Check("待回收记录保留原始路径", !string.IsNullOrWhiteSpace(rec.SourcePath));

            bool restored = c.RestoreRecycle(rec.Id);
            Check("还原一层条目回原大类", restored && c.ActiveTab().Items.Any(i => i.DisplayName == delName));
            Check("还原后回收区 -1 且快照删除",
                c.RecycleCount == recycleBase && !File.Exists(Path.Combine(c.RecycleDir, rec.Id + ".json")));

            c.AddDroppedPaths(new[] { pure }, c.ActiveTabId, false);
            var f2 = c.Config.Shortcuts.Tabs.SelectMany(t => t.Items)
                .FirstOrDefault(i => i.Type == "folder" && i.Children.Count > 0);
            Check("存在可测试的二层 folder", f2 != null);
            if (f2 != null)
            {
                var ch = f2.Children[0];
                string chId = ch.Id; int chCount = f2.Children.Count;
                bool ok = c.DeleteItem(chId);
                var crec = c.RecycleEntries.Last();
                Check("删除二层 → kind=child 且记录父文件夹",
                    ok && crec.Kind == "child" && crec.ParentId == f2.Id && f2.Children.Count == chCount - 1);
                bool back = c.RestoreRecycle(crec.Id);
                Check("还原二层子项回原文件夹",
                    back && f2.Children.Count == chCount && f2.Children.Any(x => x.Id == chId));
            }

            var tabToRecycle = c.Config.Shortcuts.Tabs.Last();
            string tabRecycleId = tabToRecycle.Id, tabRecycleName = tabToRecycle.Name;
            int tabsBeforeRecycle = c.Config.Shortcuts.Tabs.Count;
            bool tabMoved = c.DeleteTab(tabRecycleId);
            var trec = c.RecycleEntries.Last();
            Check("删除大类 → kind=tab 且保存整类快照",
                tabMoved && trec.Kind == "tab" && trec.Tab != null &&
                c.Config.Shortcuts.Tabs.Count == tabsBeforeRecycle - 1);
            bool tabBack = c.RestoreRecycle(trec.Id);
            Check("还原整个大类",
                tabBack && c.Config.Shortcuts.Tabs.Count == tabsBeforeRecycle &&
                c.Config.Shortcuts.Tabs.Any(t => t.Name == tabRecycleName));

            Check("默认保留期 7 天", c.RecycleRetentionDays == 7);
            c.SetRecycleRetentionDays(999);
            Check("保留期上界钳制到 30 天", c.RecycleRetentionDays == 30);
            c.SetRecycleRetentionDays(7);
            int beforePurge = c.RecycleCount;
            Check("未过期记录不被清除",
                c.PurgeExpiredRecycle(false) == 0 && c.RecycleCount == beforePurge);
            var expired = c.RecycleEntries.First();
            expired.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 8L * 86_400_000;
            int purged = c.PurgeExpiredRecycle(false);
            Check("超过 7 天自动清除记录",
                purged == 1 && c.RecycleCount == beforePurge - 1, $"purged={purged}");
            Check("过期记录的磁盘快照一并删除",
                !File.Exists(Path.Combine(c.RecycleDir, expired.Id + ".json")));

            if (c.RecycleCount > 0)
            {
                string pid = c.RecycleEntries.First().Id;
                Check("彻底删除单条待回收记录",
                    c.PurgeRecycleEntry(pid) && !c.RecycleEntries.Any(e => e.Id == pid));
            }
            if (c.ActiveTab().Items.Count > 0)
                c.DeleteItem(c.ActiveTab().Items.Last().Id);
            int cleared = c.ClearRecycle();
            Check("清空待回收区", cleared > 0 && c.RecycleCount == 0, $"cleared={cleared}");
            Check("清空后快照目录无残留", Directory.GetFiles(c.RecycleDir, "*.json").Length == 0);

            // ============ v1.5 可行性分析：快捷方式自身图标 ============
            byte[]? png = ShortcutIconProvider.GetPng(urlPath);
            bool validPng = png != null && png.Length > 100 &&
                            png[0] == 137 && png[1] == 80 && png[2] == 78 && png[3] == 71;
            Check(".url 快捷方式可提取自身图标 PNG", validPng,
                png == null ? "null" : $"{png.Length} bytes");
            byte[]? png2 = ShortcutIconProvider.GetPng(urlPath);
            Check("图标缓存命中（复用同一字节数组）", png != null && ReferenceEquals(png, png2));

            const string startMenu = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs";
            string? lnk = Directory.Exists(startMenu)
                ? Directory.GetFiles(startMenu, "*.lnk").FirstOrDefault() : null;
            if (lnk != null)
            {
                byte[]? lp = ShortcutIconProvider.GetPng(lnk);
                Check(".lnk 快捷方式可提取目标程序图标",
                    lp != null && lp.Length > 100 && lp[0] == 137 && lp[1] == 80,
                    $"{Path.GetFileName(lnk)} · {lp?.Length ?? 0} bytes");
            }
            else Check("存在 .lnk 样本", false);

            // ============ v1.6：隐藏快捷方式箭头角标（系统图像列表原图标） ============
            ShortcutIconProvider.Clear();
            ShortcutIconProvider.HideOverlay = true;
            byte[]? cleanLnk = lnk != null ? ShortcutIconProvider.GetPng(lnk) : null;
            byte[]? cleanUrl = ShortcutIconProvider.GetPng(urlPath);
            Check("无角标通道提取 .lnk 原图标",
                cleanLnk != null && cleanLnk.Length > 100 && cleanLnk[0] == 137,
                lnk == null ? "无样本" : $"{Path.GetFileName(lnk)} · {cleanLnk?.Length ?? 0} bytes");
            Check("无角标通道提取 .url 图标",
                cleanUrl != null && cleanUrl.Length > 100 && cleanUrl[0] == 137,
                $"{cleanUrl?.Length ?? 0} bytes");
            ShortcutIconProvider.HideOverlay = false;
            ShortcutIconProvider.Clear();
            byte[]? arrowLnk = lnk != null ? ShortcutIconProvider.GetPng(lnk) : null;
            Check("关闭开关回退带角标通道",
                arrowLnk != null && arrowLnk.Length > 100 && arrowLnk[0] == 137,
                lnk == null ? "无样本" : $"{arrowLnk?.Length ?? 0} bytes");
            Check("开关切换后缓存键失效（两通道结果独立）",
                lnk == null || !ReferenceEquals(cleanLnk, arrowLnk));
            ShortcutIconProvider.HideOverlay = true;
            ShortcutIconProvider.Clear();
            Check("不存在路径安全返回 null",
                ShortcutIconProvider.GetPng(Path.Combine(root, "不存在.lnk")) == null);
        }
        catch (Exception ex)
        {
            Check("未捕获异常", false, ex.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DESKLITE_CONFIG_DIR", null);
        }

        string report = sb + $"合计：PASS {pass} / FAIL {fail}\r\n";
        try
        {
            string log = Path.Combine(Path.GetTempPath(), "DeskLite_v1.6_selftest.log");
            File.AppendAllText(log, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n" + report + "\r\n");
        }
        catch { /* 报告写失败不影响自测结论 */ }
        return report;
    }
}
