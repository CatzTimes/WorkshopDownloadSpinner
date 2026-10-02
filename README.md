# Unturned Server Workshop Download Spinner
Spinner and Information when Downloading Workshop Items for Unturned.

<img width="800" height="80" alt="WorkshopDownloadSpinner" src=".github/assets/WorkshopDownloadSpinnerNew.gif" />

*The Workshop Download Spinner is omitted from Logs and only appears in the Console.*

> **Fork note (by CatzTimes):** This fork is a **Harmony-free** rework of Gamingtoday093's module.
> All original features are kept 1:1, with the following changes:
> - **No 0Harmony dependency.** Instead of Harmony patches, a dedicated background thread polls
>   `DedicatedUGC.currentDownload` (read-only reflection) and renders the progress bar from there.
> - **Zero main-thread impact.** Console rendering runs off the game's main thread, so slow
>   terminals (SSH, services, docker) can no longer stall server ticks while downloading.
> - **Single-line atomic refresh.** Each progress update is written with one `Console.Write` call
>   to minimize interleaving with the game's own console output.
> - **Localization.** Module metadata and the progress line follow the server language
>   (`-Lang=` command line parameter). Ships with `English.dat` and `Schinese.dat`.
> - **Hardening.** All console cursor/keyboard operations are exception-guarded for redirected
>   consoles, the speed estimate uses a monotonic stopwatch, and the input-discard loop no longer
>   busy-spins a CPU core.

## Installation
Simply download the [Latest Release](https://github.com/CatzTimes/WorkshopDownloadSpinner/releases/latest) and extract it in your Server's `Modules` folder along side other Modules such as for example **Rocket.Unturned** and/or **OpenMod.Unturned**. Neither **Rocket.Unturned** or **OpenMod.Unturned** are required for the Download Spinner to work but it will work with them loaded.

## Localization
The module reads the same translation files as the vanilla `/modules` command:
- `English.dat` — default/fallback (module name, description and progress texts)
- `Schinese.dat` — 简体中文

The language is selected by the server's `-Lang=` command line parameter (e.g. `-Lang=Schinese`),
matching `Provider.language`. Unknown languages automatically fall back to English.

## Building
Build with any .NET SDK (`dotnet build -c Release`). The project references the game assemblies
via NuGet (`RocketModFix.Unturned.Redist.Server`, `RocketModFix.UnityEngine.Redist`) — no local
Unturned installation is required. The built module is assembled in `WorkshopDownloadSpinnerModule/`.

## Credits
- Original module: [Gamingtoday093/WorkshopDownloadSpinner](https://github.com/Gamingtoday093/WorkshopDownloadSpinner)
- Harmony-free rework & localization: CatzTimes
