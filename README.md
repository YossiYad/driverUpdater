# DriverUpdater

Windows desktop app (WPF .NET 10) that scans the entire machine, lists every driver with its update status, and lets you safely update drivers from Windows Update, the Microsoft Update Catalog, or via OEM tools.

Status: scaffolding in progress.

## Highlights

- Full driver inventory via WMI (`Win32_PnPSignedDriver`).
- Update sources: Windows Update (WUApi), Microsoft Update Catalog (opt-in), OEM tool hints.
- Safety first: per-driver backup, System Restore Point before each batch, Dry-run mode, Confirmation dialog, per-device Rollback.
- Optional scheduled scans (Manual / ScanOnly / ScanAndUpdate, daily/weekly/monthly).
- Hebrew and English UI with full RTL support.

## Requirements

- Windows 10 or 11 (x64).
- Administrator privileges (the app declares `requireAdministrator` in its manifest).
- .NET 10 Desktop Runtime (bundled in the Velopack installer for release builds).

## Local development

```
dotnet restore
dotnet build
```

To run, launch Visual Studio as Administrator and press F5, or use `Launch.cmd` at the repo root.

## Project layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full module layout and design.

## Safety

See [docs/SAFETY.md](docs/SAFETY.md) for backup, restore-point, and rollback behavior.

## License

Private. All rights reserved.
