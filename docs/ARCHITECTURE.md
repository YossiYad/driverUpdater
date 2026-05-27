# Architecture

## Layered structure

```
DriverUpdater.App           WPF shell. Views, ViewModels, DI bootstrap, themes.
        |
        v
DriverUpdater.Services      Business logic. Scan orchestration, update sources,
        |                   backup, scheduling, history.
        v
DriverUpdater.Core          Pure domain. Models, abstractions, Result<T>.
        ^
        |
DriverUpdater.Infrastructure  OS-touching code. WMI, COM (WUApi), pnputil,
                            HTTP client for the Microsoft Update Catalog,
                            PowerShell invoker.
```

Rules:
1. `Core` has zero third-party dependencies. Only BCL types.
2. `Services` depends only on `Core` and on `Infrastructure` abstractions, never on concrete WMI/COM types.
3. `Infrastructure` is the only project allowed to reference `System.Management`, `System.Runtime.InteropServices`, and `Process`.
4. `App` is a thin shell. It composes the DI container and binds Views to ViewModels. No business logic in code-behind.

## Composition root

`App.xaml.cs` uses `Microsoft.Extensions.Hosting` to build the service provider:

1. Configure Serilog file + debug sinks under `%ProgramData%\DriverUpdater\Logs\`.
2. Register settings via `IOptionsMonitor<AppSettings>` bound to `%AppData%\DriverUpdater\settings.json`.
3. Register services from each layer (`AddCore`, `AddServices`, `AddInfrastructure` extension methods, planned).
4. Resolve `MainWindow` and show it.

## Threading

- The UI runs on the WPF dispatcher thread.
- Driver scans return `IAsyncEnumerable<DriverInfo>` and are consumed with `await foreach` on a background thread; row additions marshal back through the dispatcher.
- Update sources publish to a `Channel<UpdateCandidate>` consumed by an aggregator so faster sources (Windows Update) populate the grid before slower ones (Microsoft Update Catalog).
- A single `CancellationTokenSource` per scan flows top to bottom.

## Persistence

- Settings: JSON file at `%AppData%\DriverUpdater\settings.json`.
- History: SQLite database at `%ProgramData%\DriverUpdater\history.db` using `Microsoft.Data.Sqlite` + Dapper.
- Backups: one folder per update under `%ProgramData%\DriverUpdater\Backups\<timestamp>\<device>\`.
- Logs: rolling daily file under `%ProgramData%\DriverUpdater\Logs\` with 14 day retention.

## Localization

- Resource dictionaries `Resources\Strings.en.xaml` and `Resources\Strings.he.xaml` swapped at runtime.
- `FlowDirection=RightToLeft` when the user picks Hebrew.
- Log messages, exceptions, and file names stay English.

## Elevation

- v1: single process, `requireAdministrator` in `app.manifest`.
- Future: split into a non-elevated UI process and an elevated worker process, communicating over a named pipe. Not in v1.
