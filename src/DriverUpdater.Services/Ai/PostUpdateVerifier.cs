using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Ai;

public sealed class PostUpdateVerifier : IPostUpdateVerifier
{
    private readonly IInstalledDriverProbe _installedDriverProbe;
    private readonly IAiTextCompleter _aiTextCompleter;
    private readonly ILogger<PostUpdateVerifier> _logger;

    public PostUpdateVerifier(
        IInstalledDriverProbe installedDriverProbe,
        IAiTextCompleter aiTextCompleter,
        ILogger<PostUpdateVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(installedDriverProbe);
        ArgumentNullException.ThrowIfNull(aiTextCompleter);
        ArgumentNullException.ThrowIfNull(logger);
        _installedDriverProbe = installedDriverProbe;
        _aiTextCompleter = aiTextCompleter;
        _logger = logger;
    }

    public async Task<UpdateVerificationReport> VerifyAsync(
        PendingUpdateVerificationBatch batch,
        bool isAfterRestart,
        AppLanguage language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var items = new List<UpdateVerificationItem>(batch.Operations.Count);
        foreach (var operation in batch.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await VerifyOperationAsync(operation, isAfterRestart, cancellationToken).ConfigureAwait(false));
        }

        string? aiSummary = null;
        var aiWasUsed = false;
        if (_aiTextCompleter.IsConfigured)
        {
            try
            {
                var prompt = PostUpdateSummaryPromptBuilder.Build(items, isAfterRestart, language);
                aiSummary = await _aiTextCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
                aiWasUsed = !string.IsNullOrWhiteSpace(aiSummary);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AI could not summarize the post-update verification results");
            }
        }

        return new UpdateVerificationReport(
            BatchId: batch.BatchId,
            CreatedAt: batch.CreatedAt,
            IsAfterRestart: isAfterRestart,
            Items: items,
            AiSummary: aiSummary,
            AiWasUsed: aiWasUsed);
    }

    private async Task<UpdateVerificationItem> VerifyOperationAsync(
        UpdateOperation operation,
        bool isAfterRestart,
        CancellationToken cancellationToken)
    {
        var before = operation.TargetSnapshot;
        var baseStatus = ClassifyTerminalStatus(operation);
        if (baseStatus is not null)
        {
            return CreateItem(operation, current: null, baseStatus.Value);
        }

        var requiresRestart = operation.ErrorMessage?.Contains("reboot", StringComparison.OrdinalIgnoreCase) == true;
        if (requiresRestart && !isAfterRestart)
        {
            return CreateItem(operation, current: null, UpdateVerificationStatus.PendingRestart);
        }

        InstalledDriverState? current;
        try
        {
            current = await _installedDriverProbe.GetCurrentAsync(before.DeviceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify installed driver for {Device}", before.DeviceName);
            current = null;
        }

        if (current is null || (current.Version is null && current.Date is null))
        {
            return CreateItem(operation, current, UpdateVerificationStatus.Inconclusive);
        }

        var changed = !Equals(current.Version, before.CurrentVersion)
            || current.Date != before.CurrentDate;
        return CreateItem(
            operation,
            current,
            changed ? UpdateVerificationStatus.VerifiedUpdated : UpdateVerificationStatus.NotUpdated);
    }

    private static UpdateVerificationStatus? ClassifyTerminalStatus(UpdateOperation operation) =>
        operation.Status switch
        {
            UpdateStatus.Failed => UpdateVerificationStatus.Failed,
            UpdateStatus.Cancelled => UpdateVerificationStatus.Skipped,
            UpdateStatus.RolledBack => UpdateVerificationStatus.NotUpdated,
            UpdateStatus.Skipped when operation.ErrorMessage?.Contains("kept the existing", StringComparison.OrdinalIgnoreCase) == true
                || operation.ErrorMessage?.Contains("version unchanged", StringComparison.OrdinalIgnoreCase) == true
                => UpdateVerificationStatus.NotUpdated,
            UpdateStatus.Skipped => UpdateVerificationStatus.Skipped,
            UpdateStatus.Succeeded => null,
            _ => UpdateVerificationStatus.Inconclusive
        };

    private static UpdateVerificationItem CreateItem(
        UpdateOperation operation,
        InstalledDriverState? current,
        UpdateVerificationStatus status) =>
        new(
            OperationId: operation.OperationId,
            DeviceName: string.IsNullOrWhiteSpace(operation.TargetSnapshot.DeviceName)
                ? operation.TargetSnapshot.HardwareId
                : operation.TargetSnapshot.DeviceName,
            Category: operation.TargetSnapshot.Category,
            PreviousVersion: operation.TargetSnapshot.CurrentVersion,
            PreviousDate: operation.TargetSnapshot.CurrentDate,
            ExpectedVersion: operation.Candidate.NewVersion,
            ExpectedDate: operation.Candidate.NewDate,
            CurrentVersion: current?.Version,
            CurrentDate: current?.Date,
            Status: status,
            TechnicalDetail: operation.ErrorMessage);
}
