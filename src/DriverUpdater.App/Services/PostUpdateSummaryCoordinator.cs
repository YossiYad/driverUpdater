using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class PostUpdateSummaryCoordinator : IPostUpdateSummaryCoordinator
{
    private readonly IPostUpdateVerifier _verifier;
    private readonly IUpdateSummaryWindowOpener _windowOpener;
    private readonly ILocalizationService _localization;
    private readonly ILogger<PostUpdateSummaryCoordinator> _logger;

    public PostUpdateSummaryCoordinator(
        IPostUpdateVerifier verifier,
        IUpdateSummaryWindowOpener windowOpener,
        ILocalizationService localization,
        ILogger<PostUpdateSummaryCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(windowOpener);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(logger);
        _verifier = verifier;
        _windowOpener = windowOpener;
        _localization = localization;
        _logger = logger;
    }

    public async Task CompleteRunAsync(
        IReadOnlyCollection<UpdateOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            return;
        }

        try
        {
            var batch = new PendingUpdateVerificationBatch(
                BatchId: Guid.NewGuid(),
                CreatedAt: DateTimeOffset.UtcNow,
                Operations: operations.ToArray());
            var report = await _verifier.VerifyAsync(
                batch,
                isAfterRestart: false,
                _localization.CurrentLanguage,
                cancellationToken).ConfigureAwait(true);

            _windowOpener.Open(report, _localization.CurrentLanguage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not complete post-update verification and summary");
        }
    }

}
