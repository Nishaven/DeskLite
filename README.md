# DeskLite

> 一个轻量、低资源占用、响应迅速的 Windows 桌面收纳工具。
> A lightweight, low-footprint, and responsive desktop organizer for Windows.

---

## 仓库名称 / Repository Name

推荐使用 **`DeskLite`** 作为 GitHub 仓库名（与程序集 `AssemblyName` 一致，便于检索与品牌统一）。

备选（含描述性后缀，便于在搜索结果中体现用途）：

| 用途 | 推荐仓库名 |
| --- | --- |
| 首选 / 主名 | `DeskLite` |
| 描述性增强 | `DeskLite-DesktopOrganizer` |
| 强调收纳 | `DeskLite-TidyDesktop` |

仓库描述（About / 一句话简介）：

- 中文：`Windows 桌面快捷方式与文件轻量收纳工具（WinUI 3）`
- English：`A lightweight Windows desktop organizer for shortcuts & files, built with WinUI 3.`

---

## 中文说明

### 这是什么

DeskLite 是一款面向 Windows 的桌面管理工具，灵感来自 DeskBox 的交互样式。当桌面上的快捷方式、临时文件越来越多、越来越杂乱时，DeskLite 把它们收进一个无边框、可常驻桌面的小窗里，让桌面回归干净。

### 核心功能

- **最近常用（Recent）**：以 N 行 × M 列的网格展示最近打开的条目，支持 LRU / FIFO 策略，上限可配（默认 128），超出自动滚动。
- **快捷方式管理**：支持「大类 + 一层 / 二层」两级结构；默认 3×3 收纳网格；可将一个**纯快捷方式文件夹**整体聚合成一个可展开的入口；图标按名称升序自动取首项。
- **拖放收纳**：通过 Win32 `WM_DROPFILES` 通道支持从桌面 / 资源管理器拖入文件与文件夹；拖入含非快捷方式文件的文件夹会弹窗阻止；同名条目会询问合并或保留。
- **待回收区**：删除的快捷方式先进入「待回收区」，保留 7 天后自动清除，期间可随时还原或提前彻底删除。
- **真实图标**：直接调用快捷方式（`.lnk` / `.url`）自身的系统图标；并可一键**隐藏左下角的快捷方式箭头角标**。
- **无边框常驻窗体**：无边框模式，支持最小化到系统托盘、开机自启、窗口位置记忆、系统 / 浅色 / 深色主题切换、单实例运行。
- **低开销**：JSON Source Generation 去除运行时反射，启动更快；图标缓存上限可配，动画可开关。

### 技术栈

- C# / WinUI 3 / Windows App SDK **2.5.1**
- .NET **10**（目标框架 `net10.0-windows10.0.19041.0`）
- 非打包（Unpackaged）部署，x64 自包含 Windows App SDK 运行时
- 数据持久化于 `%APPDATA%\DeskLite\`（config.json、快捷方式清单、待回收区、日志）

### 构建与运行

```bash
# 还原并发布（非打包、x64）
dotnet publish -c Release -r win-x64

# 直接运行调试
dotnet run -c Debug
```

发布产物位于 `publish/` 目录，双击 `DeskLite.exe` 即可使用。

### 许可证

本项目为一次性发布，按作者意愿 **不附加开源许可证（Unlicensed / 保留所有权利）**，仅供下载试用，不授予再分发或修改的权利。如需开源协议，请与作者联系。

---

## English Description

### What is it

DeskLite is a Windows desktop management tool inspired by the DeskBox interaction style. When your desktop gets cluttered with more and more shortcuts and temp files, DeskLite gathers them into a borderless, always-on-top mini window so your desktop stays clean.

### Key Features

- **Recent Items**: An N×M grid of recently opened entries with LRU / FIFO policy, configurable cap (default 128), and scrollable overflow.
- **Shortcut Management**: A two-level structure of "Tabs + level-1 / level-2" items; a default 3×3收纳 (收纳 = tidy) grid; the ability to aggregate a **folder of pure shortcuts** into a single expandable entry; icons auto-picked by alphabetical first item.
- **Drag & Drop**: Win32 `WM_DROPFILES` channel supports dragging files/folders from the desktop or Explorer; dropping a folder containing non-shortcut files is blocked with a warning; duplicate names prompt to merge or keep.
- **Recycle Area**: Deleted shortcuts go to a "Recycle Area" and are auto-cleared after 7 days, with restore or early permanent delete available in the meantime.
- **Real Icons**: Pulls the target's own system icon for `.lnk` / `.url` shortcuts, with an option to **hide the shortcut arrow overlay** in the bottom-left corner.
- **Borderless & Persistent**: Borderless window, minimize-to-tray, start-with-Windows, window-position memory, system / light / dark theme, and single-instance enforcement.
- **Low Overhead**: JSON Source Generation removes runtime reflection for faster startup; icon cache cap and animation are configurable.

### Tech Stack

- C# / WinUI 3 / Windows App SDK **2.5.1**
- .NET **10** (target framework `net10.0-windows10.0.19041.0`)
- Unpackaged deployment, x64 with self-contained Windows App SDK runtime
- Persistence under `%APPDATA%\DeskLite\` (config.json, shortcut manifest, recycle area, logs)

### Build & Run

```bash
# Restore and publish (unpackaged, x64)
dotnet publish -c Release -r win-x64

# Run for debugging
dotnet run -c Debug
```

The published binary lives in `publish/`; launch `DeskLite.exe` directly.

### License

This project is released as-is as a **one-time publication with no open-source license (Unlicensed / All Rights Reserved)**. It is provided for download and trial only and does not grant rights to redistribute or modify. Contact the author if you need an open-source license.

---

## 截图 / Screenshots

> 将主窗口、设置页、待回收区、隐藏箭头前后的图标对比截图放入 `docs/screenshots/` 并在此处引用。

- `docs/screenshots/main.png` — 主窗口（最近常用 + 快捷方式大类）
- `docs/screenshots/settings.png` — 设置页（主题 / 自启 / 真实图标 / 隐藏箭头）
- `docs/screenshots/recycle.png` — 待回收区
- `docs/screenshots/icon-compare.png` — 箭头隐藏前 / 后对比
