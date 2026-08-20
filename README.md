<div align="center">

<img src="src/DriverUpdater.App/Assets/app.ico" width="96" alt="DriverUpdater icon" />

# DriverUpdater

**Scan every driver on your PC, see exactly what is outdated, and update safely - with an AI assistant that explains, recommends, and watches over every install.**

[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D6?logo=windows&logoColor=white)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](#local-development)
[![UI](https://img.shields.io/badge/WPF-Fluent%20UI-68217A)](#features)
[![Languages](https://img.shields.io/badge/UI-English%20%7C%20%D7%A2%D7%91%D7%A8%D7%99%D7%AA%20(RTL)-2ea44f)](#features)
[![License](https://img.shields.io/badge/License-Private-lightgrey)](#license)

</div>

---

## Features

| | Feature | What it does |
|---|---|---|
| 🔍 | **Full driver inventory** | Scans the whole machine via WMI (`Win32_PnPSignedDriver`) and lists every device with its installed driver version and status |
| 🌐 | **Multiple update sources** | Windows Update (WUApi), Microsoft Update Catalog (opt-in), OEM tool hints, and official vendor downloads: NVIDIA, AMD graphics and chipset, Intel, and motherboard vendors |
| 🏷️ | **Honest version reporting** | Versions are shown the way the vendor publishes them (for example the NVIDIA release number), and after an install the app reports the version Windows actually bound |
| 🛡️ | **Safety first** | Per-driver backup, System Restore Point before each batch, dry-run mode, confirmation dialog, and per-device rollback |
| ⏰ | **Scheduled runs** | Daily, weekly, or monthly: scan only, update everything, update only your own list, or let the AI decide which updates are worth installing |
| 🌍 | **Bilingual UI** | Hebrew and English with full RTL support |

## AI assistant

DriverUpdater ships with an optional AI layer that turns the raw scan into decisions you can trust:

- **Driver chat in the main window.** Ask anything about the scan in plain language: what is worth updating, what is risky, why a driver is flagged. The assistant answers grounded in your actual driver list and can offer a one-click install for the drivers it recommends.
- **Live update awareness.** The chat reads the session logs, so during an update run you can ask "why is this taking so long?" or "did anything fail?" and get an answer based on what is actually happening, step by step and timestamp by timestamp.
- **Update verification.** After installs, the AI reviews what really changed and flags updates that did not stick.
- **Update with AI.** One button in the toolbar: the app scans, the AI researches the current drivers for your exact hardware on the web, and only the updates it endorses at your configured risk tolerance are installed. Anything it cannot rate is left alone.
- **AI-decided scheduled updates.** In scheduled mode the AI reviews every found update with the configured provider and installs only what it recommends, up to the risk level you allow.
- **Settings by conversation.** The assistant can propose app settings changes; nothing is applied without your confirmation.

## Getting started

1. Download `DriverUpdater-win-Setup.exe` from the latest [GitHub release](https://github.com/YossiYad/driverUpdater/releases).
2. Run it. The build is not code-signed, so SmartScreen shows "Windows protected your PC" on first launch: click **More info**, then **Run anyway**.
3. The app requests administrator privileges (UAC) on launch and auto-updates from GitHub if you opt in via Settings.

## Requirements

- Windows 10 or 11 (x64)
- Administrator privileges (the regular app launch requests UAC elevation after Velopack hooks finish)
- No separate .NET install needed: release builds are self-contained and ship the .NET 10 runtime inside the installer

## Local development

```
dotnet restore
dotnet build
```

To run, launch Visual Studio as Administrator and press F5, or use `Launch.cmd` at the repo root.

## Release

The release pipeline produces a Velopack-based installer plus delta updates under `build/output/`.

```
build\release.cmd
```

The script reads the version from `Directory.Build.props`, runs the tests and text lint, creates a clean self-contained publish, verifies the assembly version, packages the app with Velopack, and wraps the Setup in an elevated repair launcher. Passing a version is optional, but if supplied it must match the project version.

Publish the GitHub release after the build with a Markdown release-notes file:

```
build\publish-release.ps1 -NotesFile path\to\release-notes.md
```

The publish script uploads only the installer and Velopack update assets needed by the app: Setup, full package, delta package when produced, `RELEASES`, and `releases.win.json`. It intentionally does not upload the portable ZIP or `assets.win.json`. GitHub still shows Source code archives because it adds them automatically for tag releases.

### Upgrading an older installation

Use the `DriverUpdater-win-Setup.exe` asset, not the portable ZIP. The Setup requests administrator privileges, closes any running DriverUpdater instance, and then runs the original Velopack installer. It can therefore replace or repair version 0.1.32 or older even when the old elevated application is still running.

Version 0.1.33 and later let Velopack run its install/update hooks normally and request administrator privileges only when the regular app starts. Settings remain under `%AppData%\DriverUpdater`; history, logs, cache, and backups remain under `%ProgramData%\DriverUpdater`, so installing a newer Setup over the old version does not erase them. If an older Setup reported error 740 after copying files, running the latest Setup again repairs the installation.

## Documentation

| Document | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Full module layout and design |
| [docs/SAFETY.md](docs/SAFETY.md) | Backup, restore-point, and rollback behavior |

## License

Private. All rights reserved.
