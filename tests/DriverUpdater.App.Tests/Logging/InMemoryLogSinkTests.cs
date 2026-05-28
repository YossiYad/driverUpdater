using DriverUpdater.App.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace DriverUpdater.App.Tests.Logging;

public class InMemoryLogSinkTests
{
    [Fact]
    public void Emit_appends_entry_to_buffer_and_raises_event()
    {
        var sink = new InMemoryLogSink();
        LogEntry? captured = null;
        sink.EntryEmitted += (_, entry) => captured = entry;

        sink.Emit(MakeEvent(LogEventLevel.Information, "hello world"));

        captured.Should().NotBeNull();
        captured!.Message.Should().Be("hello world");
        captured.Level.Should().Be("Information");
        sink.Snapshot().Should().ContainSingle();
    }

    [Fact]
    public void Snapshot_caps_buffer_at_capacity()
    {
        var sink = new InMemoryLogSink();

        for (var i = 0; i < InMemoryLogSink.Capacity + 50; i++)
        {
            sink.Emit(MakeEvent(LogEventLevel.Debug, $"entry {i}"));
        }

        var snapshot = sink.Snapshot();
        snapshot.Should().HaveCount(InMemoryLogSink.Capacity);
        snapshot[0].Message.Should().Be($"entry 50");
        snapshot[^1].Message.Should().Be($"entry {InMemoryLogSink.Capacity + 49}");
    }

    [Fact]
    public void Category_is_shortened_from_source_context()
    {
        var sink = new InMemoryLogSink();
        var logEvent = MakeEvent(
            LogEventLevel.Warning,
            "categorized",
            new LogEventProperty("SourceContext", new ScalarValue("DriverUpdater.Services.Sources.AmdGraphicsSource")));

        sink.Emit(logEvent);

        sink.Snapshot()[0].Category.Should().Be("AmdGraphicsSource");
    }

    [Fact]
    public void Exception_is_captured_with_type_and_message()
    {
        var sink = new InMemoryLogSink();
        var ex = new InvalidOperationException("boom");

        sink.Emit(MakeEvent(LogEventLevel.Error, "scrape failed", exception: ex));

        var entry = sink.Snapshot()[0];
        entry.Exception.Should().NotBeNull();
        entry.Exception!.Should().Contain("InvalidOperationException");
        entry.Exception.Should().Contain("boom");
    }

    private static LogEvent MakeEvent(
        LogEventLevel level,
        string message,
        LogEventProperty? property = null,
        Exception? exception = null)
    {
        var template = new MessageTemplateParser().Parse(message);
        var props = property is null ? Array.Empty<LogEventProperty>() : new[] { property };
        return new LogEvent(DateTimeOffset.UtcNow, level, exception, template, props);
    }
}
