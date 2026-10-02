# Unturned Workshop Download Spinner

**中文** | [English](#english)

为 Unturned 专用服务器控制台提供创意工坊下载进度条：下载创意工坊物品时显示旋转动画、进度条、速度与**预计剩余时间**，只在控制台显示、不写入日志。

<img width="800" height="80" alt="WorkshopDownloadSpinner" src=".github/assets/WorkshopDownloadSpinnerNew.gif" />

*进度条只出现在控制台中，不会被写入日志文件。*

> **Fork 说明（by CatzTimes）：** 本分支是 Gamingtoday093 原模块的**无 Harmony 重写版**，功能与原版保持一致，并包含以下改动：
> - **不依赖 0Harmony**：不再打补丁，由一个专用后台线程以只读反射轮询 `DedicatedUGC.currentDownload` 驱动进度条。
> - **主线程零占用**：控制台渲染在后台线程完成，慢速终端（SSH / 服务 / docker）不再拖慢服务器心跳。
> - **整行原子刷新**：每次进度更新以单次 `Console.Write` 写入整行，并按显示宽度（中文占 2 列）钳制到缓冲区宽度内，避免折行刷屏。
> - **预计剩余时间**：速度旁实时显示 ETA（本地化，如 `剩余时间 1m 05s` / `ETA 1m 05s`）。
> - **本地化**：模块名称、描述与进度文案跟随服务器语言（`-Lang=` 启动参数），自带 `English.dat` 与 `Schinese.dat`。
> - **稳健加固**：控制台光标/键盘操作全部带异常保护（适配重定向控制台）、修复原版速度估算的运算符优先级 bug（改用单调 Stopwatch）、异常路径必恢复控制台状态、Unturned 内部结构变化时优雅降级。

## 安装
1. 在 [Releases](https://github.com/CatzTimes/WorkshopDownloadSpinner/releases/latest) 下载 `WorkshopDownloadSpinnerModule.zip`。
2. 解压到服务器的 `Unturned/Modules/` 目录（可与 Rocket 等其他模块共存，不依赖任何插件框架）。
3. 重启服务器；如需简中文案，在启动参数中加 `-Lang=Schinese`。

## 本地化
模块使用与原生 `/modules` 命令相同的翻译文件机制：
- `English.dat` — 默认/回退语言（模块名称、描述与进度文案）
- `Schinese.dat` — 简体中文

语言由服务器启动参数 `-Lang=` 决定（对应 `Provider.language`），未知语言自动回退英文。

## 构建
任意 .NET SDK 执行 `dotnet build -c Release` 即可。项目通过 NuGet 引用游戏程序集
（`RocketModFix.Unturned.Redist.Server`、`RocketModFix.UnityEngine.Redist`），无需本机安装 Unturned。
构建产物自动组装在 `WorkshopDownloadSpinnerModule/` 目录。

## 致谢
- 原模块：[Gamingtoday093/WorkshopDownloadSpinner](https://github.com/Gamingtoday093/WorkshopDownloadSpinner)
- 无 Harmony 重写与本地化：CatzTimes

<a name="english"></a>
# English

Progress bar, spinner, speed and **estimated time remaining** for Workshop downloads on Unturned dedicated server consoles. Shown in the console only — omitted from the log files.

*The Workshop Download Spinner is omitted from Logs and only appears in the Console.*

> **Fork note (by CatzTimes):** This fork is a **Harmony-free** rework of Gamingtoday093's module.
> All original features are kept, with the following changes:
> - **No 0Harmony dependency.** Instead of Harmony patches, a dedicated background thread polls
>   `DedicatedUGC.currentDownload` (read-only reflection) and renders the progress bar from there.
> - **Zero main-thread impact.** Console rendering runs off the game's main thread, so slow
>   terminals (SSH, services, docker) can no longer stall server ticks while downloading.
> - **Atomic single-line refresh.** Each progress update is one `Console.Write` call, clamped to
>   the console buffer width with display-aware measurements (CJK characters count as two cells),
>   so wide lines can never wrap and flood the console.
> - **Estimated time remaining.** A live ETA is shown next to the speed (localized).
> - **Localization.** Module metadata and the progress line follow the server language
>   (`-Lang=` command line parameter). Ships with `English.dat` and `Schinese.dat`.
> - **Hardening.** All console cursor/keyboard operations are exception-guarded for redirected
>   consoles, the speed estimate uses a monotonic stopwatch (fixing an operator-precedence bug in
>   the original), console state is always restored on exception paths, and the module degrades
>   gracefully if Unturned internals ever change.

## Installation
1. Download `WorkshopDownloadSpinnerModule.zip` from the [Releases](https://github.com/CatzTimes/WorkshopDownloadSpinner/releases/latest) page.
2. Extract it into your server's `Unturned/Modules/` folder (alongside other modules such as Rocket — neither Rocket nor OpenMod is required).
3. Restart the server. Add `-Lang=Schinese` to the launch parameters for 简体中文 output.

## Building
Build with any .NET SDK (`dotnet build -c Release`). The project references the game assemblies
via NuGet — no local Unturned installation is required. The built module is assembled in
`WorkshopDownloadSpinnerModule/`.

## Credits
- Original module: [Gamingtoday093/WorkshopDownloadSpinner](https://github.com/Gamingtoday093/WorkshopDownloadSpinner)
- Harmony-free rework & localization: CatzTimes
