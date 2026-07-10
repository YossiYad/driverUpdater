using DriverUpdater.App.Logging;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Logging;

public class LogChatPromptBuilderTests
{
    private static readonly LogEntry[] SampleEntries =
    {
        new(DateTimeOffset.Now, "Information", "Scan", "Scan started", null),
        new(DateTimeOffset.Now, "Error", "Install", "pnputil failed for Realtek", "System.Exception: boom"),
    };

    [Fact]
    public void Build_includes_logs_history_and_new_question()
    {
        var history = new[]
        {
            new LogChatMessage(IsUser: true, "What failed?"),
            new LogChatMessage(IsUser: false, "The Realtek install failed."),
        };

        var prompt = LogChatPromptBuilder.Build(SampleEntries, history, "Why did it fail?");

        prompt.Should().Contain("DriverUpdater");
        prompt.Should().Contain("pnputil failed for Realtek");
        prompt.Should().Contain("Developer: What failed?");
        prompt.Should().Contain("Assistant: The Realtek install failed.");
        prompt.Should().Contain("Developer: Why did it fail?");
        prompt.Should().EndWith("Assistant:");
    }

    [Fact]
    public void Build_works_with_no_prior_history()
    {
        var prompt = LogChatPromptBuilder.Build(SampleEntries, Array.Empty<LogChatMessage>(), "Summarize the errors");

        prompt.Should().Contain("Developer: Summarize the errors");
        prompt.Should().EndWith("Assistant:");
    }

    [Fact]
    public void Build_throws_on_blank_question()
    {
        var act = () => LogChatPromptBuilder.Build(SampleEntries, Array.Empty<LogChatMessage>(), "   ");

        act.Should().Throw<ArgumentException>();
    }
}
