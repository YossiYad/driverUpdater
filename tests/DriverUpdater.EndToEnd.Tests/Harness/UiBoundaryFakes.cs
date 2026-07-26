using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;

namespace DriverUpdater.EndToEnd.Tests.Harness;

/// <summary>Stands in for the confirmation dialog: answers with fixed install options.</summary>
public sealed class ScriptedInstallConfirmation : IInstallConfirmation
{
    private readonly InstallOptions? _answer;

    public ScriptedInstallConfirmation(InstallOptions? answer) => _answer = answer;

    public List<(UpdateOperation Operation, bool DryRun)> Prompts { get; } = new();

    public InstallOptions? Confirm(UpdateOperation operation, bool dryRun)
    {
        Prompts.Add((operation, dryRun));
        return _answer;
    }
}

/// <summary>Records which vendor pages the app decided to open in a browser.</summary>
public sealed class RecordingUpdatePageOpener : IUpdatePageOpener
{
    public List<Uri> Opened { get; } = new();

    public void Open(UpdateCandidate candidate) => Opened.Add(candidate.DownloadUrl);
}

public sealed class RecordingWindowOpener : IHistoryWindowOpener, ISettingsWindowOpener, ILogsWindowOpener
{
    public int OpenCount { get; private set; }

    public void Open() => OpenCount++;
}

public sealed class NoOemDetectionService : IOemDetectionService
{
    public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<OemInfo?>(null);
}

/// <summary>Answers the "restart now?" prompt without touching the machine.</summary>
public sealed class ScriptedRebootPrompt : IRebootPrompt
{
    private readonly bool _accept;

    public ScriptedRebootPrompt(bool accept) => _accept = accept;

    public int PromptCount { get; private set; }

    public int LastPromptedDriverCount { get; private set; }

    public int RestartCount { get; private set; }

    public bool ConfirmRestartNow(int rebootRequiredDriverCount)
    {
        PromptCount++;
        LastPromptedDriverCount = rebootRequiredDriverCount;
        return _accept;
    }

    public void RestartNow() => RestartCount++;
}

public sealed class FixedLocalizationService : ILocalizationService
{
    public FixedLocalizationService(AppLanguage language = AppLanguage.English) => CurrentLanguage = language;

    public AppLanguage CurrentLanguage { get; private set; }

    public bool IsRightToLeft => CurrentLanguage == AppLanguage.Hebrew;

    public event EventHandler? LanguageChanged;

    public void ApplyLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Captures the post-update summary instead of opening a window.</summary>
public sealed class RecordingUpdateSummaryWindowOpener : IUpdateSummaryWindowOpener
{
    public List<UpdateVerificationReport> Reports { get; } = new();

    public void Open(UpdateVerificationReport report, AppLanguage language) => Reports.Add(report);
}

/// <summary>Records the post-reboot run-once registration the coordinator asks for.</summary>
public sealed class RecordingPostRebootStartupService : IPostRebootStartupService
{
    public int RegisterCount { get; private set; }

    public int UnregisterCount { get; private set; }

    public Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        RegisterCount++;
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        UnregisterCount++;
        return Task.CompletedTask;
    }
}

public sealed class FixedBootTimeProvider : ISystemBootTimeProvider
{
    public FixedBootTimeProvider(DateTimeOffset bootTimeUtc) => BootTimeUtc = bootTimeUtc;

    public DateTimeOffset BootTimeUtc { get; set; }

    public DateTimeOffset GetBootTimeUtc() => BootTimeUtc;
}

/// <summary>An AI text completer that replays canned answers, or reports itself unconfigured.</summary>
public sealed class ScriptedAiTextCompleter : IAiTextCompleter
{
    private readonly Queue<string?> _answers = new();

    public ScriptedAiTextCompleter(bool isConfigured = true, params string?[] answers)
    {
        IsConfigured = isConfigured;
        foreach (var answer in answers)
        {
            _answers.Enqueue(answer);
        }
    }

    public AiProvider Provider => AiProvider.Gemini;

    public bool IsConfigured { get; }

    public List<string> Prompts { get; } = new();

    public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}
