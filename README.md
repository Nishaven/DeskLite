# DeskLite

> 一个轻量、低资源占用、响应迅速的 Windows 桌面收纳工具。
> A lightweight, low-footprint, and responsive desktop organizer for Windows.

**当前版本 / Current version: v1.7.0**

- 中文：`Windows 桌面快捷方式与文件轻量收纳工具（WinUI 3）`
- English: `A lightweight Windows desktop organizer for shortcuts & files, built with WinUI 3.`

---

## 下载 / Download

前往 [Releases](https://github.com/Nishaven/DeskLite/releases) 下载最新版本：

| 文件 / File | 说明 / Description |
| --- | --- |
| `v1.7.0_DeskLite.exe` | 单文件可执行程序（self-contained single-file build）。无需安装 .NET 或 Windows App SDK，双击即用。 |
| `v1.7.0_DeskLite-win-x64-selfcontained.zip` | 完整自包含发布包（解压后运行 `DeskLite.exe`）。同样无需安装运行时，适合需要查看/替换随附文件的场景。 |

> 环境要求 / Requirements: Windows 10 19041 (2004) 或更高版本，x64。
> 首次运行时程序会在 `%APPDATA%\DeskLite\` 创建配置目录，不会改写磁盘上的快捷方式源文件。

---

## 中文说明

### 这是什么

DeskLite 是一款面向 Windows 的桌面管理工具，灵感来自 DeskBox 的交互样式。当桌面上的快捷方式、临时文件越来越多、越来越杂乱时，DeskLite 把它们收进一个无边框、可常驻桌面的小窗里，让桌面回归干净。

### v1.7 新增：模块化的「快捷方式管理」

「快捷方式管理」不再是单一面板，而是一个**可向下扩展的同构模块**：

- 点击「＋ 新增模块」即可在主窗口内**垂直堆叠复制 N 个功能完全相同、可独立命名的模块**；弹窗中可选择「空白模块」或「复制当前模块内容」。
- 每个模块拥有独立的一层大类、二层 3×3 收纳、活动大类、打开的文件夹与滚轮偏移，**彼此完全隔离**。
- 模块标题栏提供 **改名 / 复制 / 上移 / 下移 / 移入待回收区** 五项管理操作；删除整个模块可在 7 天内整体还原，且至少保留一个模块。
- 拖放落点、滚轮事件、右键编辑、待回收区还原都按模块归属精确分发。
- 主窗口带整体滚动条，模块多时可上下浏览；旧版 `config.json` 在启动时**自动迁移**成第一个模块，原有大类与快捷方式完整保留。

### v1.6.1 修复

- 修复部分快捷方式图标**上下颠倒 180°** 的问题（`GetDIBits` 行序双重翻转），改为确定性正确的「正高度请求 + 单次翻转」。

### 核心功能

- **最近常用（Recent）**：N 行 × M 列网格，LRU / FIFO 策略，上限可配（默认 128），支持轮动条与鼠标滚轮。
- **两层快捷收纳**：大类 + 一层 / 二层结构，默认 3×3 网格；纯快捷方式文件夹可整体聚合为一个可展开入口；图标按名称升序自动取首项。
- **拖放收纳**：Win32 `WM_DROPFILES` + XAML 双通道，支持从桌面 / 资源管理器拖入；含非快捷方式文件的文件夹弹窗阻止；同名条目询问合并或保留。
- **待回收区**：删除的条目先进入待回收区，保留 7 天后自动清除，期间可还原或提前彻底删除；磁盘文件不受影响。
- **真实图标**：调用 `.lnk` / `.url` 自身的系统图标，并可隐藏左下角的**快捷方式箭头角标**（`SHGFI_SYSICONINDEX` + `ImageList_GetIcon`）。
- **无边框常驻窗体**：最小化到系统托盘、开机自启、窗口位置记忆、系统 / 浅色 / 深色主题、单实例运行、设置页退出。
- **低开销**：JSON Source Generation 去除运行时反射；图标缓存上限可配；动画可开关。
- **逻辑自测**：`DeskLite.exe --desklite-logic-test` 可离线运行 73 项核心逻辑断言，不创建窗口、不读写真实配置。

### 技术栈

- C# / WinUI 3 / Windows App SDK **2.5.1**
- .NET **10**（`net10.0-windows10.0.19041.0`）
- 非打包（Unpackaged）部署，x64 自包含运行时
- 数据持久化于 `%APPDATA%\DeskLite\`（`config.json`、快捷方式清单、待回收区、日志）

### 构建 / Build

```bash
dotnet publish -c Release -r win-x64

# 自包含（免装 .NET / WinAppSDK）
dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:WindowsPackageType=None

# 单文件 EXE
dotnet publish -c Release -r win-x64 -p:SelfContained=true -p:WindowsPackageType=None `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

发布产物位于 `publish/`，双击 `DeskLite.exe` 即可使用。

### 许可证

本项目为一次性发布，按作者意愿 **不附加开源许可证（Unlicensed / 保留所有权利）**，仅供下载试用，不授予再分发或修改的权利。详见 [LICENSE](LICENSE)。

---

## English Description

### What is it

DeskLite is a Windows desktop management tool inspired by the DeskBox interaction style. When your desktop gets cluttered with shortcuts and temp files, DeskLite gathers them into a borderless, always-available mini window so your desktop stays clean.

### New in v1.7: modular Shortcut Management

"Shortcut Management" is no longer a single panel — it is an **extensible, replicable module**:

- Use **＋ Add Module** to stack **N functionally identical, independently named modules** vertically inside the main window; a dialog lets you choose a blank module or a full copy of the current one.
- Each module owns its own tabs, level-1 / level-2 3×3 grid, active tab, open folder and scroll offsets — **fully isolated** from the others.
- Each module header offers **Rename / Duplicate / Move Up / Move Down / Move to Recycle Area**; a deleted module can be restored as a whole within 7 days, and at least one module is always kept.
- Drop targets, wheel events, context editing and recycle restores are all dispatched by module.
- The main window scrolls as a whole; legacy `config.json` files are **auto-migrated** into the first module at startup with all tabs and shortcuts preserved.

### Fixed in v1.6.1

- Some shortcut icons were rendered **upside down (180° flip)** due to a double row-flip in `GetDIBits` handling. Replaced with a deterministic "positive-height request + single flip".

### Key Features

- **Recent Items**: N×M grid, LRU / FIFO, configurable cap (default 128), wheel + scrollbar support.
- **Two-level Shortcut Grid**: tabs + level-1 / level-2, default 3×3 grid; a folder of pure shortcuts aggregates into one expandable entry; icons auto-picked alphabetically.
- **Drag & Drop**: Win32 `WM_DROPFILES` + XAML dual channel; folders with non-shortcut files are blocked with a prompt; duplicate names prompt to merge or keep.
- **Recycle Area**: deleted entries stay 7 days (auto-cleared afterwards), restorable or permanently removable at any time; disk files are never touched.
- **Real Icons**: the target's own system icon for `.lnk` / `.url`, with an option to **hide the shortcut arrow overlay** (`SHGFI_SYSICONINDEX` + `ImageList_GetIcon`).
- **Borderless & Persistent**: minimize-to-tray, start-with-Windows, window-position memory, system / light / dark themes, single-instance, exit from Settings.
- **Low Overhead**: JSON Source Generation removes runtime reflection; configurable icon cache cap and animations.
- **Logic Self-Test**: `DeskLite.exe --desklite-logic-test` runs 73 offline assertions without creating a window or touching real user config.

### Tech Stack

- C# / WinUI 3 / Windows App SDK **2.5.1**
- .NET **10** (`net10.0-windows10.0.19041.0`)
- Unpackaged deployment, self-contained x64 runtime
- Persistence under `%APPDATA%\DeskLite\` (config.json, shortcut manifest, recycle area, logs)

### Build & Run

```bash
dotnet publish -c Release -r win-x64
dotnet run -c Debug
```

The published binary lives in `publish/`; launch `DeskLite.exe` directly.

### License

One-time publication with **no open-source license (Unlicensed / All Rights Reserved)**. Provided for download and trial only; redistribution or modification is not granted. See [LICENSE](LICENSE).

---

## 截图 / Screenshots

| 截图 | 内容 |
| --- | --- |
| `docs/screenshots/v1.7_三模块界面验证.png` | 三个模块垂直堆叠，每个模块含一层大类与二层收纳 |
| `docs/screenshots/v1.7_新增模块弹窗验证.png` | 新增模块时询问「空白模块 / 复制当前模块」 |
| `docs/screenshots/v1.7_新增模块结果验证.png` | 新增后模块数从 3 变 4、编号与提示气泡 |
| `docs/screenshots/v1.7_改名结果验证.png` | 模块改名为「常用工具箱」后的即时更新 |
| `docs/screenshots/v1.7_真实配置迁移验证.png` | 旧配置自动迁移为第一个模块，原有大类与图标保留 |
