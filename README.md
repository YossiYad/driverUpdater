# DriverUpdater

Windows desktop app (WPF .NET 10) that scans the entire machine, lists every driver with its update status, and lets you safely update drivers from Windows Update, the Microsoft Update Catalog, or via OEM tools.

## Highlights

- Full driver inventory via WMI (`Win32_PnPSignedDriver`).
- Update sources: Windows Update (WUApi), Microsoft Update Catalog (opt-in), OEM tool hints.
- Safety first: per-driver backup, System Restore Point before each batch, Dry-run mode, Confirmation dialog, per-device Rollback.
- Optional scheduled scans (Manual / ScanOnly / ScanAndUpdate, daily/weekly/monthly).
- Hebrew and English UI with full RTL support.

## Requirements

- Windows 10 or 11 (x64).
- Administrator privileges (the app declares `requireAdministrator` in its manifest).
- No separate .NET install needed: release builds are self-contained and ship the .NET 10 runtime inside the installer.

## Local development

```
dotnet restore
dotnet build
```

To run, launch Visual Studio as Administrator and press F5, or use `Launch.cmd` at the repo root.

## Release

The release pipeline produces a Velopack-based installer + delta updates under `build/output/`.

```
build\release.cmd 0.1.0
```

The script:
1. Restores the solution.
2. Runs `dotnet publish -c Release -r win-x64 --self-contained true` so the .NET runtime ships inside the package.
3. Restores or installs the `vpk` global tool.
4. Calls `vpk pack` with the version, runtime, icon, and main-exe metadata.

Distribute the produced `DriverUpdater-win-Setup.exe` from `build/output/`. Because the build is not code-signed, the first launch shows a "Windows protected your PC" SmartScreen prompt; recipients click "More info" then "Run anyway". Subsequent versions auto-update through Velopack's manifest if the user has opted in via Settings (UpdaterSettings.CheckOnStartup + FeedUrl).

## Project layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full module layout and design.

## Safety

See [docs/SAFETY.md](docs/SAFETY.md) for backup, restore-point, and rollback behavior.

## License

Private. All rights reserved.
