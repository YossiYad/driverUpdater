# Safety

Driver updates can render a Windows machine unbootable. The app applies multiple layers of mitigation before every installation.

## Layer 1: System Restore Point

Before any update batch the app creates a System Restore Point named `DriverUpdater - before <timestamp>` via PowerShell `Checkpoint-Computer`. If System Restore is disabled on the system drive (default on many Windows 11 installs) the app surfaces a warning banner and offers a one-time prompt to enable it.

Creating the checkpoint needs two machine-wide settings temporarily out of the way:

- `SystemRestorePointCreationFrequency` is set to 0 so Windows does not silently skip a checkpoint created within 1440 minutes of a previous one. The previous value is captured first and put back once the checkpoint attempt finishes, including when it fails.
- System Protection is enabled on the system drive when it is off, because `Checkpoint-Computer` cannot run without it. This one is **not** reverted: turning protection off again deletes the checkpoint that was just created. The app logs a warning when it had to make that change.

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

## Layer 6: Scope of an unattended run

Settings > Schedule offers one list of schedule types, and the installing ones run without anybody watching:

- **Off**: nothing runs on its own.
- **Scan only**: scans on the schedule and installs nothing.
- **General schedule**: every confirmed update the run found is installed.
- **Custom schedule**: only the devices on the user's list, edited through Settings > Schedule > "Choose the drivers...".
- **AI schedule**: the run scans, sends every update it found to the configured AI provider, and installs only what the AI endorses. The risk tolerance decides whether "Caution" counts as an endorsement; "High risk", "Unknown", and any update the AI did not answer for are never installed. The verdict is stored on the cached candidate, so the next interactive session shows why an update was left alone.

Every one of these gates fails closed. An unreadable selection file, a missing AI provider, an unreachable AI provider, or a failed AI review all mean "install nothing this run" - never "install everything". Restore point and per-device backup still run for each unattended install.

## Layer 7: One-click "Update with AI"

The toolbar button runs an AI scan and then installs only the updates the AI endorsed, without asking the user to judge each one. It uses the same rules as the AI schedule and the same risk tolerance from Settings > Schedule, so the two never disagree about the same verdict:

- An update without a verdict is never installed. "No answer" is not an endorsement.
- An update the AI does not consider a genuine upgrade is never installed.
- An update rated above the configured tolerance is never installed.
- Excluded devices stay excluded, and the confirmation dialog, restore point, and per-device backup all still run for the batch.

If the scan is cancelled or fails, nothing is installed at all.

## What the app explicitly will not do

- It will not install unsigned INFs without an explicit per-install override.
- It will not modify driver staging policies, signature enforcement, or VBS settings.
- It will not auto-update when the user picks Manual mode.
- It will not delete backups before the configured retention period (default 30 days).
