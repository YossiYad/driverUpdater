using DriverUpdater.App.Logging;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Logging;

public class LogSummaryPromptBuilderTests
{
    [Fact]
    public void Build_includes_log_stats_and_entries()
    {
        var entries = new[]
        {
            new LogEntry(DateTimeOffset.Now, "Information", "Scan", "Scan started", null),
            new LogEntry(DateTimeOffset.Now, "Warning", "Catalog", "Slow response", null),
            new LogEntry(DateTimeOffset.Now, "Error", "Install", "pnputil failed", "System.Exception: boom"),
        };

        var prompt = LogSummaryPromptBuilder.Build(entries);

        prompt.Should().Contain("DriverUpdater");
        prompt.Should().Contain("3 entries");
        prompt.Should().Contain("1 error(s)/fatal");
        prompt.Should().Contain("1 warning(s)");
        prompt.Should().Contain("pnputil failed");
        prompt.Should().Contain("System.Exception: boom");
    }

    [Fact]
    public void Build_truncates_very_large_logs_keeping_the_tail()
    {
        var entries = Enumerable.Range(0, 5000)
            .Select(i => new LogEntry(DateTimeOffset.Now, "Information", "Bulk", $"entry number {i} with some padding text", null))
            .ToArray();

        var prompt = LogSummaryPromptBuilder.Build(entries);

        prompt.Should().Contain("older entries omitted");
        // The most recent entry must survive; the very first should be dropped.
        prompt.Should().Contain("entry number 4999");
        prompt.Should().NotContain("entry number 0 with some padding text");
    }
}
