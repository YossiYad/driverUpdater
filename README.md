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
- Administrator privileges (the regular app launch requests UAC elevation after Velopack hooks finish).
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
build\release.cmd
```

The script reads the version from `Directory.Build.props`, runs the tests and text lint,
creates a clean self-contained publish, verifies the assembly version, packages the app
with Velopack, and wraps the Setup in an elevated repair launcher. Passing a version is
optional, but if supplied it must match the project version.

Distribute the produced `DriverUpdater-win-Setup.exe` from `build/output/`. Because the build is not code-signed, the first launch shows a "Windows protected your PC" SmartScreen prompt; recipients click "More info" then "Run anyway". Subsequent versions auto-update from the configured GitHub repository or web feed if the user has opted in via Settings.

### Upgrading an older installation

Use the `DriverUpdater-win-Setup.exe` asset, not the portable ZIP. The Setup requests
administrator privileges, closes any running DriverUpdater instance, and then runs the
original Velopack installer. It can therefore replace or repair version 0.1.32 or older
even when the old elevated application is still running.

Version 0.1.33 and later let Velopack run its install/update hooks normally and request
administrator privileges only when the regular app starts. Settings remain under
`%AppData%\DriverUpdater`; history, logs, cache, and backups remain under
`%ProgramData%\DriverUpdater`, so installing a newer Setup over the old version does not
erase them. If an older Setup reported error 740 after copying files, running the latest
Setup again repairs the installation.

## Project layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full module layout and design.

## Safety

See [docs/SAFETY.md](docs/SAFETY.md) for backup, restore-point, and rollback behavior.

## License

Private. All rights reserved.
