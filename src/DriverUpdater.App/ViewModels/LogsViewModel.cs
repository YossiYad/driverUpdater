using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Logging;

namespace DriverUpdater.App.ViewModels;

public partial class LogsViewModel : ObservableObject, IDisposable
{
    private readonly InMemoryLogSink _sink;
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

    public LogsViewModel(InMemoryLogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
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

    [RelayCommand]
    private void OpenTestResultsFolder()
    {
        var directory = FindTestResultsDirectory();
        if (directory is null)
        {
            StatusText = "Test log folder was not found. Run build\\test.ps1 first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            StatusText = $"Opened test logs: {directory}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open test log folder: {ex.Message}";
        }
    }

    internal static string? FindTestResultsDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"),
            Path.Combine(AppContext.BaseDirectory, "artifacts", "test-results")
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && current is not null; i++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "artifacts", "test-results"));
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(Directory.Exists);
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
