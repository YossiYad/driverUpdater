using System.IO;
using Serilog.Core;
using Serilog.Events;

namespace DriverUpdater.App.Logging;

public sealed class InMemoryLogSink : ILogEventSink
{
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _buffer = new();
    private readonly IFormatProvider? _formatProvider;

    public InMemoryLogSink(IFormatProvider? formatProvider = null)
    {
        _formatProvider = formatProvider;
    }

    public event EventHandler<LogEntry>? EntryEmitted;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var entry = new LogEntry(
            Timestamp: logEvent.Timestamp,
            Level: logEvent.Level.ToString(),
            Category: ResolveCategory(logEvent),
            Message: logEvent.RenderMessage(_formatProvider),
            Exception: logEvent.Exception is null ? null : FormatException(logEvent.Exception));

        lock (_gate)
        {
            _buffer.AddLast(entry);
            while (_buffer.Count > Capacity)
            {
                _buffer.RemoveFirst();
            }
        }

        EntryEmitted?.Invoke(this, entry);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    private static string ResolveCategory(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value)
            && value is ScalarValue scalar
            && scalar.Value is string raw
            && !string.IsNullOrWhiteSpace(raw)
                ? ShortenCategory(raw)
                : string.Empty;

    private static string ShortenCategory(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1
            ? fullName[(lastDot + 1)..]
            : fullName;
    }

    private static string FormatException(Exception exception)
    {
        using var writer = new StringWriter();
        writer.Write(exception.GetType().FullName);
        writer.Write(": ");
        writer.WriteLine(exception.Message);
        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            writer.WriteLine(exception.StackTrace);
        }
        return writer.ToString().TrimEnd();
    }
}
