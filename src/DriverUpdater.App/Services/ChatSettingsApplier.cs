using DriverUpdater.App.Ai;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class ChatSettingsApplier : IChatSettingsApplier
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISchedulerService _schedulerService;
    private readonly ApplicationBehaviorState? _applicationBehaviorState;
    private readonly IApplicationStartupService? _applicationStartupService;
    private readonly ILogger<ChatSettingsApplier> _logger;

    public ChatSettingsApplier(
        ISettingsStore settingsStore,
        ISchedulerService schedulerService,
        ILogger<ChatSettingsApplier> logger,
        ApplicationBehaviorState? applicationBehaviorState = null,
        IApplicationStartupService? applicationStartupService = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(schedulerService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _schedulerService = schedulerService;
        _logger = logger;
        _applicationBehaviorState = applicationBehaviorState;
        _applicationStartupService = applicationStartupService;
    }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        _settingsStore.LoadAsync(cancellationToken);

    public async Task<ChatSettingsApplyResult> ApplyAsync(
        IReadOnlyList<ChatSettingChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return ChatSettingsApplyResult.Failure("There is nothing to change.");
        }

        AppSettings settings;
        try
        {
            settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat settings change could not read the current settings");
            return ChatSettingsApplyResult.Failure(ex.Message);
        }

        var originalSchedule = CopySchedule(settings.Schedule);

        foreach (var change in changes)
        {
            if (!change.Definition.TryWrite(settings, change.Value))
            {
                _logger.LogWarning(
                    "Chat settings change rejected {Key}={Value} while writing",
                    change.Key,
                    change.Value);
                return ChatSettingsApplyResult.Failure($"{change.Key} could not be set to {change.Value}.");
            }
        }

        // Mirrors the Settings window: minimised-at-startup only means anything while the app
        // keeps running in the background after Windows starts it.
        settings.Application.StartMinimized = settings.Application.StartMinimized
            && settings.Application.StartWithWindows
            && settings.Application.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground;

        try
        {
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat settings change could not be saved");
            return ChatSettingsApplyResult.Failure(ex.Message);
        }

        _logger.LogInformation(
            "Chat applied {Count} setting change(s): {Changes}",
            changes.Count,
            string.Join(", ", changes.Select(change => $"{change.Key}={change.Value}")));

        _applicationBehaviorState?.Apply(settings.Application);

        var warning = await ApplyStartupAsync(settings, cancellationToken).ConfigureAwait(true);
        var scheduleWarning = await ApplyScheduleAsync(settings, cancellationToken).ConfigureAwait(true);
        if (scheduleWarning is not null)
        {
            settings.Schedule = originalSchedule;
            try
            {
                await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chat settings change could not restore the previous saved schedule");
                return ChatSettingsApplyResult.Failure(
                    $"{scheduleWarning} The previous saved schedule could not be restored: {ex.Message}");
            }
        }
        return ChatSettingsApplyResult.Success(warning ?? scheduleWarning);
    }

    private static ScheduleSettings CopySchedule(ScheduleSettings source) => new()
    {
        Mode = source.Mode,
        Cadence = source.Cadence,
        TimeOfDay = source.TimeOfDay,
        DayOfWeek = source.DayOfWeek,
        AutoUpdateScope = source.AutoUpdateScope,
        AiRiskTolerance = source.AiRiskTolerance
    };

    private async Task<string?> ApplyStartupAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (_applicationStartupService is null)
        {
            return null;
        }

        try
        {
            await _applicationStartupService.ApplyAsync(
                settings.Application.StartWithWindows,
                settings.Application.StartMinimized,
                cancellationToken).ConfigureAwait(true);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat settings change could not update the Windows startup entry");
            return "Windows startup could not be updated. See logs for details.";
        }
    }

    private async Task<string?> ApplyScheduleAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _schedulerService.ApplyAsync(
                settings.Schedule.Mode,
                settings.Schedule.Cadence,
                settings.Schedule.TimeOfDay,
                settings.Schedule.DayOfWeek,
                cancellationToken).ConfigureAwait(true);
            return result.IsFailure ? result.Error.Message : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat settings change could not update the scheduled task");
            return "The scheduled task could not be updated. See logs for details.";
        }
    }
}
