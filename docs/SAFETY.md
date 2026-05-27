# Safety

Driver updates can render a Windows machine unbootable. The app applies multiple layers of mitigation before every installation.

## Layer 1: System Restore Point

Before any update batch the app creates a System Restore Point named `DriverUpdater - before <timestamp>` via PowerShell `Checkpoint-Computer`. If System Restore is disabled on the system drive (default on many Windows 11 installs) the app surfaces a warning banner and offers a one-time prompt to enable it.

## Layer 2: Per-device backup

Before each individual driver replacement the app runs:

```
pnputil /export-driver <oem_inf> <destination>
```

into a per-update folder under `%ProgramData%\DriverUpdater\Backups\<timestamp>\<device>\`. The folder contains the original `.inf`, `.cat`, and payload files so the exact driver can be reinstalled later.

## Layer 3: Confirmation dialog

The Confirmation dialog shown before any install lists:

- Device name and category.
- Current driver version and date.
- New driver version and date.
- Source (Windows Update, Microsoft Update Catalog, or OEM).
- Download size.
- Checkboxes (default checked) for "Create restore point" and "Back up current driver".

For Storage and Display category drivers the dialog adds an extra warning paragraph explaining the boot risk.

## Layer 4: Dry-run mode

Selecting Dry-run shows the exact planned sequence without executing anything:

1. Create restore point named ...
2. Back up current driver to ...
3. Download from ...
4. Install via pnputil add-driver ...

No state changes occur.

## Layer 5: Rollback

The History page lists every update operation with a Rollback button. Rollback uses the backup folder and runs:

```
pnputil /add-driver <backup>\*.inf /install
```

This restores the previous driver immediately. If pnputil fails or the device is missing, the user can fall back to the System Restore Point via `rstrui.exe`.

## What the app explicitly will not do

- It will not install unsigned INFs without an explicit per-install override.
- It will not modify driver staging policies, signature enforcement, or VBS settings.
- It will not auto-update when the user picks Manual mode.
- It will not delete backups before the configured retention period (default 30 days).
