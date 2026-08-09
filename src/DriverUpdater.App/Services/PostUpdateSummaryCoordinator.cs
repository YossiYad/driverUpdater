using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class PostUpdateSummaryCoordinator : IPostUpdateSummaryCoordinator
{
    private readonly IPostUpdateVerifier _verifier;
    private readonly IPendingUpdateVerificationStore _store;
    private readonly IPostRebootStartupService _startupService;
    private readonly ISystemBootTimeProvider _bootTimeProvider;
    private readonly IUpdateSummaryWindowOpener _windowOpener;
    private readonly ILocalizationService _localization;
    private readonly IIneffectiveUpdateStore _ineffectiveUpdateStore;
    private readonly ILogger<PostUpdateSummaryCoordinator> _logger;

    public PostUpdateSummaryCoordinator(
        IPostUpdateVerifier verifier,
        IPendingUpdateVerificationStore store,
        IPostRebootStartupService startupService,
        ISystemBootTimeProvider bootTimeProvider,
        IUpdateSummaryWindowOpener windowOpener,
        ILocalizationService localization,
        IIneffectiveUpdateStore ineffectiveUpdateStore,
        ILogger<PostUpdateSummaryCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(startupService);
        ArgumentNullException.ThrowIfNull(bootTimeProvider);
        ArgumentNullException.ThrowIfNull(windowOpener);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(ineffectiveUpdateStore);
        ArgumentNullException.ThrowIfNull(logger);
        _verifier = verifier;
        _store = store;
        _startupService = startupService;
        _bootTimeProvider = bootTimeProvider;
        _windowOpener = windowOpener;
        _localization = localization;
        _ineffectiveUpdateStore = ineffectiveUpdateStore;
        _logger = logger;
    }

    public async Task<UpdateVerificationReport?> CompleteRunAsync(
        IReadOnlyCollection<UpdateOperation> operations,
        Action<UpdateVerificationReport>? beforeSummaryOpen = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return null;
        }

        try
        {
            var batch = new PendingUpdateVerificationBatch(
                BatchId: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow,
                Operations: operations.ToArray());

            var restartRequiredCount = batch.Operations.Count(operation => operation.RequiresRestart);
            if (restartRequiredCount > 0)
            {
                var pendingOperations = batch.Operations.ToList();
                var existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
                if (existing is not null)
                {
                    pendingOperations = existing.Operations
                        .Concat(pendingOperations)
                        .DistinctBy(operation => operation.OperationId)
                        .ToList();
                }

                var pendingBatch = new PendingUpdateVerificationBatch(
                    BatchId: existing?.BatchId ?? batch.BatchId,
                    CreatedAt: batch.CreatedAt,
                    Operations: pendingOperations);
                await _store.SaveAsync(pendingBatch, cancellationToken).ConfigureAwait(true);
                await _startupService.RegisterAsync(cancellationToken).ConfigureAwait(true);
                _logger.LogInformation(
                    "Deferred the complete summary for {OperationCount} update result(s) because {RestartCount} require a restart",
                    pendingOperations.Count,
                    restartRequiredCount);
                return null;
            }

            var report = await _verifier.VerifyAsync(
                batch,
                isAfterRestart: false,
                _localization.CurrentLanguage,
                cancellationToken).ConfigureAwait(true);

            beforeSummaryOpen?.Invoke(report);
            _windowOpener.Open(report, _localization.CurrentLanguage);
            return report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not complete post-update verification and summary");
            return null;
        }
    }

    public async Task ResumeAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        PendingUpdateVerificationBatch? batch;
        try
        {
            batch = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load pending post-restart verification");
            return;
        }

        if (batch is null)
        {
            return;
        }

        var bootTime = _bootTimeProvider.GetBootTimeUtc();
        if (bootTime <= batch.CreatedAt)
        {
            _logger.LogInformation(
                "Post-update verification is still waiting for a restart. Current boot time is {BootTime}, batch was created at {CreatedAt}",
                bootTime,
                batch.CreatedAt);
            return;
        }

        try
        {
            var report = await _verifier.VerifyAsync(
                batch,
                isAfterRestart: true,
                _localization.CurrentLanguage,
                cancellationToken).ConfigureAwait(true);
            await RecordProvenNoOpsAsync(batch, report, cancellationToken).ConfigureAwait(true);
            _windowOpener.Open(report, _localization.CurrentLanguage);
            await _store.ClearAsync(cancellationToken).ConfigureAwait(true);
            await _startupService.UnregisterAsync(cancellationToken).ConfigureAwait(true);
            _logger.LogInformation("Post-restart driver verification completed for batch {BatchId}", batch.BatchId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-restart driver verification failed; it will be retried on the next launch");
        }
    }

    /// <summary>
    /// An install that reported success but needed a restart is only proven after that restart.
    /// When the driver is still unchanged once the machine is back up, the package went into the
    /// driver store and Windows refused to bind it. Nothing recorded that until now, so the same
    /// package was downloaded, installed and prompted for a restart again on every later scan.
    /// </summary>
    private async Task RecordProvenNoOpsAsync(
        PendingUpdateVerificationBatch batch,
        UpdateVerificationReport report,
        CancellationToken cancellationToken)
    {
        var operationsById = batch.Operations.ToDictionary(operation => operation.OperationId);
        foreach (var item in report.Items)
        {
            if (item.Status != UpdateVerificationStatus.NotUpdated
                || item.InstallerStatus != UpdateStatus.Succeeded
                || !operationsById.TryGetValue(item.OperationId, out var operation)
                || operation.Candidate.NewVersion is null
                || string.IsNullOrWhiteSpace(operation.TargetSnapshot.DeviceId))
            {
                continue;
            }

            try
            {
                await _ineffectiveUpdateStore.RecordAsync(
                    operation.TargetSnapshot.DeviceId,
                    operation.Candidate.NewVersion.ToString(),
                    operation.TargetSnapshot.CurrentVersion?.ToString(),
                    cancellationToken).ConfigureAwait(true);
                _logger.LogInformation(
                    "Recorded {Target} as a proven no-op for {Device}: the installer reported success, but after the "
                    + "restart the active driver is still {Installed}. It will not be offered again until the "
                    + "installed driver changes.",
                    operation.Candidate.NewVersion,
                    item.DeviceName,
                    operation.TargetSnapshot.CurrentVersion?.ToString() ?? "the existing driver");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not record the ineffective update for {Device}", item.DeviceName);
            }
        }
    }
}
