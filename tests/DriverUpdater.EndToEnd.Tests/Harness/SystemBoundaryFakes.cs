using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.EndToEnd.Tests.Harness;

/// <summary>Records the restore points the pipeline asks Windows to create.</summary>
public sealed class FakeRestorePointService : IRestorePointService
{
    private int _sequence;

    public List<string> Descriptions { get; } = new();

    public bool FailAllRequests { get; set; }

    public Task<bool> IsSystemRestoreEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(!FailAllRequests);

    public Task<Result<RestorePointInfo>> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
    {
        Descriptions.Add(description);
        if (FailAllRequests)
        {
            return Task.FromResult(Result<RestorePointInfo>.Failure("RP_DISABLED", "System Protection is turned off."));
        }

        var info = new RestorePointInfo(
            SequenceNumber: (++_sequence).ToString(),
            Description: description,
            CreatedAt: DateTimeOffset.UtcNow);
        return Task.FromResult(Result<RestorePointInfo>.Success(info));
    }
}

/// <summary>Records every pnputil invocation and replays a scripted exit code.</summary>
public sealed class FakePnPUtilRunner : IPnPUtilRunner
{
    private readonly Func<string, ProcessResult> _respond;

    public FakePnPUtilRunner(Func<string, ProcessResult>? respond = null)
    {
        _respond = respond ?? (_ => new ProcessResult(0, "Driver package added successfully.", string.Empty));
    }

    public List<string> Invocations { get; } = new();

    public Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
    {
        Invocations.Add(arguments);
        return Task.FromResult(_respond(arguments));
    }
}

/// <summary>Records every vendor installer invocation and replays a scripted exit code.</summary>
public sealed class FakeVendorInstallerRunner : IVendorInstallerRunner
{
    private readonly Func<string, string, ProcessResult> _respond;

    public FakeVendorInstallerRunner(Func<string, string, ProcessResult>? respond = null)
    {
        _respond = respond ?? ((_, _) => new ProcessResult(0, string.Empty, string.Empty));
    }

    public List<(string FileName, string Arguments)> Invocations { get; } = new();

    public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        Invocations.Add((fileName, arguments));
        return Task.FromResult(_respond(fileName, arguments));
    }
}

/// <summary>Returns the driver Windows reports as active for a device, per read-back call.</summary>
public sealed class ScriptedInstalledDriverProbe : IInstalledDriverProbe
{
    private readonly Dictionary<string, Queue<InstalledDriverState?>> _readbacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstalledDriverState?> _defaults =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> ProbedDeviceIds { get; } = new();

    public ScriptedInstalledDriverProbe Always(string deviceId, InstalledDriverState? state)
    {
        _defaults[deviceId] = state;
        return this;
    }

    public ScriptedInstalledDriverProbe Then(string deviceId, InstalledDriverState? state)
    {
        if (!_readbacks.TryGetValue(deviceId, out var queue))
        {
            queue = new Queue<InstalledDriverState?>();
            _readbacks[deviceId] = queue;
        }
        queue.Enqueue(state);
        return this;
    }

    public Task<InstalledDriverState?> GetCurrentAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ProbedDeviceIds.Add(deviceId);
        if (_readbacks.TryGetValue(deviceId, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue());
        }
        return Task.FromResult(_defaults.TryGetValue(deviceId, out var fallback) ? fallback : null);
    }
}

/// <summary>An update source that yields a fixed candidate list, like a real source would.</summary>
public sealed class StubUpdateSource : IUpdateSource
{
    private readonly IReadOnlyList<UpdateCandidate> _candidates;

    public StubUpdateSource(UpdateSource kind, string displayName, params UpdateCandidate[] candidates)
    {
        Kind = kind;
        DisplayName = displayName;
        _candidates = candidates;
    }

    public UpdateSource Kind { get; }

    public string DisplayName { get; }

    public IReadOnlyCollection<DriverInfo>? LastRequestedDrivers { get; private set; }

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastRequestedDrivers = drivers;
        foreach (var candidate in _candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return candidate;
        }
    }
}

/// <summary>Replays a canned Windows Update inventory and records install requests.</summary>
public sealed class FakeWuApiClient : IWuApiClient
{
    private readonly IReadOnlyList<WuDriverUpdateRecord> _records;
    private readonly Func<string, Result<WuInstallResult>> _install;

    public FakeWuApiClient(
        IReadOnlyList<WuDriverUpdateRecord>? records = null,
        Func<string, Result<WuInstallResult>>? install = null)
    {
        _records = records ?? Array.Empty<WuDriverUpdateRecord>();
        _install = install ?? (_ => Result<WuInstallResult>.Success(
            new WuInstallResult(HResult: 0, RebootRequired: false, Message: "Installed.")));
    }

    public List<string> InstalledUpdateIds { get; } = new();

    public async IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in _records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return record;
        }
    }

    public Task<Result<WuInstallResult>> DownloadAndInstallAsync(string updateId, CancellationToken cancellationToken = default)
    {
        InstalledUpdateIds.Add(updateId);
        return Task.FromResult(_install(updateId));
    }
}
