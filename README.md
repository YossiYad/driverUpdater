# DriverUpdater

Windows desktop app (WPF .NET 10) that scans the entire machine, lists every driver with its update status, and lets you safely update drivers from Windows Update, the Microsoft Update Catalog, or via OEM tools.

Status: release candidate.

## Highlights

- Full driver inventory via WMI (`Win32_PnPSignedDriver`).
- Update sources: Windows Update (WUApi), Microsoft Update Catalog (opt-in), OEM tool hints.
- Safety first: per-driver backup, System Restore Point before each batch, Dry-run mode, Confirmation dialog, per-device Rollback.
- Optional scheduled scans (Manual / ScanOnly / ScanAndUpdate, daily/weekly/monthly).
- Hebrew and English UI with full RTL support.

## Requirements

- Windows 10 or 11 (x64, ARM64, or x86 where supported by Windows).
- Administrator privileges (the app declares `requireAdministrator` in its manifest).
- No separate .NET installation is required for release builds.

## Local development

```
dotnet restore
dotnet build
```

To run, launch Visual Studio as Administrator and press F5, or use `Launch.cmd` at the repo root.

Run tests with retained diagnostic logs:

```
build\test.ps1
```

TRX results and hang diagnostics are written under `artifacts/test-results/`.

## Release

The release pipeline produces a Velopack-based installer + delta updates under `build/output/`.

```
build\release.cmd 0.1.4 win-x64
```

The script:
1. Restores the solution.
2. Runs a self-contained `dotnet publish` for the requested runtime.
3. Restores or installs the `vpk` global tool.
4. Calls `vpk pack` with the version, runtime, and main-exe metadata.

Supported runtime arguments are `win-x64`, `win-arm64`, and `win-x86`. To build all
three installers, run `build\release-all.cmd 0.1.4`.

Distribute the matching `setup.exe` from `build/output/<runtime>/`. Subsequent versions auto-update through Velopack's manifest if the user has opted in via Settings (UpdaterSettings.CheckOnStartup + FeedUrl).

For a public release, set `VELOPACK_SIGN_PARAMS` to the arguments normally passed
to `signtool.exe` before running the script. Unsigned builds are suitable only for
internal testing because Windows SmartScreen will warn users.

Each runtime directory includes `SHA256SUMS.txt`. See
[docs/RELEASE.md](docs/RELEASE.md) for the publication and hardware smoke-test
checklist.

## Project layout

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the full module layout and design.

## Safety

See [docs/SAFETY.md](docs/SAFETY.md) for backup, restore-point, and rollback behavior.

## License

Private. All rights reserved.
