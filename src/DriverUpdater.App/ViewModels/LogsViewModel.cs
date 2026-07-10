using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Logging;
using DriverUpdater.Core.Abstractions;

namespace DriverUpdater.App.ViewModels;

public partial class LogsViewModel : ObservableObject, IDisposable
{
    private readonly InMemoryLogSink _sink;
    private readonly IAiTextCompleter? _aiCompleter;
    private readonly Dispatcher _dispatcher;
    private readonly List<LogEntry> _pending = new();
    private readonly object _pendingGate = new();
    private bool _subscribed;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public IReadOnlyList<string> AvailableLevels { get; } = new[] { "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private string _statusText = "Live - showing latest entries";

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SummarizeWithAiCommand))]
    private bool _isSummarizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiSummary))]
    [NotifyPropertyChangedFor(nameof(IsSummaryShown))]
    [NotifyCanExecuteChangedFor(nameof(CopyAiSummaryCommand))]
    private string? _aiSummary;

    public bool HasAiSummary => !string.IsNullOrWhiteSpace(AiSummary);

    /// <summary>User toggle for the AI log summary panel; the ✕ button sets it false.</summary>
    [ObservableProperty]
    private bool _isSummaryVisible = true;

    /// <summary>The summary panel is shown only when a summary exists AND it has not been closed.</summary>
    public bool IsSummaryShown => HasAiSummary && IsSummaryVisible;

    partial void OnIsSummaryVisibleChanged(bool value) => OnPropertyChanged(nameof(IsSummaryShown));

    [RelayCommand]
    private void CloseSummary() => IsSummaryVisible = false;

    /// <summary>Multi-turn conversation with the AI about the current session logs.</summary>
    public ObservableCollection<LogChatMessage> ChatMessages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    private bool _isChatting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    private string _chatInput = string.Empty;

    /// <summary>True when an AI text completer was supplied, so the chat panel is worth showing.</summary>
    public bool IsAiAvailable => _aiCompleter is not null;

    /// <summary>User toggle for the Ask-AI chat panel (the ✦✦ button). Open by default.</summary>
    [ObservableProperty]
    private bool _isChatPanelVisible = true;

    /// <summary>The chat panel is shown only when AI is available AND the user has it toggled open.</summary>
    public bool IsChatPanelShown => IsAiAvailable && IsChatPanelVisible;

    partial void OnIsChatPanelVisibleChanged(bool value) => OnPropertyChanged(nameof(IsChatPanelShown));

    public bool HasChat => ChatMessages.Count > 0;

    public bool HasNoChat => ChatMessages.Count == 0;

    public LogsViewModel(InMemoryLogSink sink, IAiTextCompleter? aiCompleter = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _aiCompleter = aiCompleter;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        foreach (var existing in sink.Snapshot())
        {
            if (PassesLevelFilter(existing))
            {
                Entries.Add(existing);
            }
        }

        _sink.EntryEmitted += OnEntryEmitted;
        _subscribed = true;
        ChatMessages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChat));
            OnPropertyChanged(nameof(HasNoChat));
        };
        UpdateStatus();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        var snapshot = _sink.Snapshot();
        Entries.Clear();
        foreach (var entry in snapshot)
        {
            if (PassesLevelFilter(entry))
            {
                Entries.Add(entry);
            }
        }
        UpdateStatus();
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!value)
        {
            FlushPending();
        }
        UpdateStatus();
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        lock (_pendingGate)
        {
            _pending.Clear();
            PendingCount = 0;
        }
        UpdateStatus();
    }

    [RelayCommand]
    private void CopyAll()
    {
        if (Entries.Count == 0)
        {
            return;
        }

        var buffer = new StringBuilder();
        foreach (var entry in Entries)
        {
            AppendFormatted(buffer, entry);
        }
        SafeSetClipboard(buffer.ToString());
        StatusText = $"Copied {Entries.Count} entries to clipboard.";
    }

    [RelayCommand]
    private void CopySelection(IList<object>? selected)
    {
        if (selected is null || selected.Count == 0)
        {
            return;
        }

        var buffer = new StringBuilder();
        var count = 0;
        foreach (var item in selected)
        {
            if (item is LogEntry entry)
            {
                AppendFormatted(buffer, entry);
                count++;
            }
        }

        if (count == 0)
        {
            return;
        }

        SafeSetClipboard(buffer.ToString());
        StatusText = $"Copied {count} entries to clipboard.";
    }

    [RelayCommand(CanExecute = nameof(CanSummarize), IncludeCancelCommand = true)]
    private async Task SummarizeWithAiAsync(CancellationToken cancellationToken)
    {
        if (_aiCompleter is null)
        {
            StatusText = "AI summary is not available in this build.";
            return;
        }
        if (!_aiCompleter.IsConfigured)
        {
            StatusText = $"AI is not configured. Open Settings > AI to enable {_aiCompleter.Provider}.";
            return;
        }

        var entries = _sink.Snapshot();
        if (entries.Count == 0)
        {
            StatusText = "No logs to summarize yet.";
            return;
        }

        IsSummarizing = true;
        AiSummary = null;
        StatusText = $"Summarizing {entries.Count} log entries with AI...";
        try
        {
            var prompt = LogSummaryPromptBuilder.Build(entries);
            var summary = await _aiCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(summary))
            {
                StatusText = "AI did not return a summary. Check the AI provider in Settings and try again.";
                return;
            }

            IsSummaryVisible = true; // re-show the panel if it was previously closed
            AiSummary = summary.Trim();
            SafeSetClipboard(AiSummary);
            StatusText = "AI log summary ready and copied to the clipboard. Paste it to share.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI summary cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"AI summary failed: {ex.Message}";
        }
        finally
        {
            IsSummarizing = false;
        }
    }

    private bool CanSummarize() => !IsSummarizing && _aiCompleter is not null;

    [RelayCommand(CanExecute = nameof(HasAiSummary))]
    private void CopyAiSummary()
    {
        if (string.IsNullOrWhiteSpace(AiSummary))
        {
            return;
        }
        SafeSetClipboard(AiSummary);
        StatusText = "AI summary copied to the clipboard.";
    }

    [RelayCommand(CanExecute = nameof(CanSendChat), IncludeCancelCommand = true)]
    private async Task SendChatAsync(CancellationToken cancellationToken)
    {
        var question = ChatInput?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }
        if (_aiCompleter is null)
        {
            StatusText = "AI chat is not available in this build.";
            return;
        }
        if (!_aiCompleter.IsConfigured)
        {
            StatusText = $"AI is not configured. Open Settings > AI to enable {_aiCompleter.Provider}.";
            return;
        }

        var entries = _sink.Snapshot();
        if (entries.Count == 0)
        {
            StatusText = "No logs to ask about yet.";
            return;
        }

        // Snapshot the prior turns before we append the new question so the prompt sees history only.
        var history = ChatMessages.ToArray();
        ChatMessages.Add(new LogChatMessage(IsUser: true, question));
        ChatInput = string.Empty;
        IsChatting = true;
        StatusText = "Asking AI about the logs...";
        try
        {
            var prompt = LogChatPromptBuilder.Build(entries, history, question);
            var answer = await _aiCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(answer))
            {
                ChatMessages.Add(new LogChatMessage(IsUser: false,
                    "(No response from AI. Check the AI provider in Settings and try again.)"));
                StatusText = "AI did not return an answer.";
                return;
            }

            ChatMessages.Add(new LogChatMessage(IsUser: false, answer.Trim()));
            StatusText = "AI answered. Ask a follow-up question or clear the chat.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI chat cancelled.";
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new LogChatMessage(IsUser: false, $"(AI chat failed: {ex.Message})"));
            StatusText = $"AI chat failed: {ex.Message}";
        }
        finally
        {
            IsChatting = false;
        }
    }

    private bool CanSendChat() =>
        !IsChatting && _aiCompleter is not null && !string.IsNullOrWhiteSpace(ChatInput);

    [RelayCommand]
    private void ClearChat()
    {
        ChatMessages.Clear();
        StatusText = "AI chat cleared.";
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriverUpdater",
            "Logs");
        if (!Directory.Exists(logDirectory))
        {
            StatusText = $"Log folder not found: {logDirectory}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = logDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open log folder: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            _sink.EntryEmitted -= OnEntryEmitted;
            _subscribed = false;
        }
    }

    private void OnEntryEmitted(object? sender, LogEntry entry)
    {
        if (IsPaused)
        {
            lock (_pendingGate)
            {
                _pending.Add(entry);
                var pending = _pending.Count;
                _ = _dispatcher.BeginInvoke(() => PendingCount = pending);
            }
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => AddEntry(entry));
            return;
        }

        AddEntry(entry);
    }

    private void AddEntry(LogEntry entry)
    {
        if (!PassesLevelFilter(entry))
        {
            return;
        }

        Entries.Add(entry);
        while (Entries.Count > InMemoryLogSink.Capacity)
        {
            Entries.RemoveAt(0);
        }
    }

    private void FlushPending()
    {
        List<LogEntry> snapshot;
        lock (_pendingGate)
        {
            snapshot = new List<LogEntry>(_pending);
            _pending.Clear();
            PendingCount = 0;
        }

        foreach (var entry in snapshot)
        {
            AddEntry(entry);
        }
    }

    private bool PassesLevelFilter(LogEntry entry) =>
        string.Equals(SelectedLevel, "All", StringComparison.Ordinal)
        || string.Equals(entry.Level, SelectedLevel, StringComparison.OrdinalIgnoreCase);

    private void UpdateStatus()
    {
        if (IsPaused)
        {
            StatusText = $"Paused - {PendingCount} new entries buffered. Resume to see them.";
        }
        else
        {
            StatusText = $"Live - {Entries.Count} entries shown (filter: {SelectedLevel}).";
        }
    }

    partial void OnPendingCountChanged(int value)
    {
        if (IsPaused)
        {
            UpdateStatus();
        }
    }

    private static void AppendFormatted(StringBuilder buffer, LogEntry entry)
    {
        buffer.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        buffer.Append(" [");
        buffer.Append(entry.Level);
        buffer.Append(']');
        if (!string.IsNullOrEmpty(entry.Category))
        {
            buffer.Append(' ');
            buffer.Append(entry.Category);
            buffer.Append(':');
        }
        buffer.Append(' ');
        buffer.Append(entry.Message);
        buffer.AppendLine();
        if (!string.IsNullOrEmpty(entry.Exception))
        {
            buffer.AppendLine(entry.Exception);
        }
    }

    private static void SafeSetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can occasionally fail under contention; swallow so we don't crash the UI.
        }
    }
}
