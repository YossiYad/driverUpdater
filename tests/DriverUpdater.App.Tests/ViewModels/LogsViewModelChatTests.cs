using DriverUpdater.App.Logging;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace DriverUpdater.App.Tests.ViewModels;

public class LogsViewModelChatTests
{
    [WpfFact]
    public async Task SendChat_appends_user_and_ai_messages_and_clears_input()
    {
        var sink = new InMemoryLogSink();
        EmitInfo(sink, "Scan started");
        var completer = new StubTextCompleter(isConfigured: true, reply: "The Realtek install failed at 12:00.");
        var vm = new LogsViewModel(sink, completer)
        {
            ChatInput = "What failed?"
        };

        await vm.SendChatCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().HaveCount(2);
        vm.ChatMessages[0].IsUser.Should().BeTrue();
        vm.ChatMessages[0].Text.Should().Be("What failed?");
        vm.ChatMessages[1].IsUser.Should().BeFalse();
        vm.ChatMessages[1].Text.Should().Be("The Realtek install failed at 12:00.");
        vm.ChatInput.Should().BeEmpty();
        vm.HasChat.Should().BeTrue();
        completer.LastPrompt.Should().Contain("Scan started");
        completer.LastPrompt.Should().Contain("Developer: What failed?");
    }

    [WpfFact]
    public void ChatPanel_is_shown_by_default_and_toggles_with_the_ai_button()
    {
        var sink = new InMemoryLogSink();
        var completer = new StubTextCompleter(isConfigured: true, reply: "ok");
        var vm = new LogsViewModel(sink, completer);

        vm.IsAiAvailable.Should().BeTrue();
        vm.IsChatPanelVisible.Should().BeTrue();
        vm.IsChatPanelShown.Should().BeTrue();

        vm.IsChatPanelVisible = false;
        vm.IsChatPanelShown.Should().BeFalse("the ✦✦ toggle hides the panel");

        vm.IsChatPanelVisible = true;
        vm.IsChatPanelShown.Should().BeTrue();
    }

    [WpfFact]
    public void SummaryPanel_shows_only_with_a_summary_and_hides_on_close()
    {
        var sink = new InMemoryLogSink();
        var vm = new LogsViewModel(sink, new StubTextCompleter(isConfigured: true, reply: "x"));

        vm.IsSummaryShown.Should().BeFalse("no summary generated yet");

        vm.AiSummary = "session went fine";
        vm.IsSummaryShown.Should().BeTrue();

        vm.CloseSummaryCommand.Execute(null);
        vm.IsSummaryVisible.Should().BeFalse();
        vm.IsSummaryShown.Should().BeFalse("closed with the ✕ button");
    }

    [WpfFact]
    public void ChatPanel_is_never_shown_when_ai_is_unavailable()
    {
        var sink = new InMemoryLogSink();
        var vm = new LogsViewModel(sink, aiCompleter: null);

        vm.IsAiAvailable.Should().BeFalse();
        vm.IsChatPanelShown.Should().BeFalse();

        vm.IsChatPanelVisible = true;
        vm.IsChatPanelShown.Should().BeFalse("no AI completer means no chat panel regardless of the toggle");
    }

    [WpfFact]
    public async Task SendChat_sends_prior_turns_as_history()
    {
        var sink = new InMemoryLogSink();
        EmitInfo(sink, "Scan started");
        var completer = new StubTextCompleter(isConfigured: true, reply: "answer");
        var vm = new LogsViewModel(sink, completer) { ChatInput = "first" };
        await vm.SendChatCommand.ExecuteAsync(null);

        vm.ChatInput = "second";
        await vm.SendChatCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().HaveCount(4);
        completer.LastPrompt.Should().Contain("Developer: first");
        completer.LastPrompt.Should().Contain("Developer: second");
    }

    [WpfFact]
    public async Task SendChat_reports_when_ai_not_configured()
    {
        var sink = new InMemoryLogSink();
        EmitInfo(sink, "Scan started");
        var completer = new StubTextCompleter(isConfigured: false, reply: "unused");
        var vm = new LogsViewModel(sink, completer) { ChatInput = "hello" };

        await vm.SendChatCommand.ExecuteAsync(null);

        vm.ChatMessages.Should().BeEmpty();
        vm.StatusText.Should().Contain("not configured");
    }

    private static void EmitInfo(InMemoryLogSink sink, string message)
    {
        var template = new MessageTemplateParser().Parse(message);
        sink.Emit(new LogEvent(
            DateTimeOffset.Now,
            LogEventLevel.Information,
            exception: null,
            template,
            Array.Empty<LogEventProperty>()));
    }

    private sealed class StubTextCompleter : IAiTextCompleter
    {
        private readonly string _reply;

        public StubTextCompleter(bool isConfigured, string reply)
        {
            IsConfigured = isConfigured;
            _reply = reply;
        }

        public AiProvider Provider => AiProvider.Gemini;

        public bool IsConfigured { get; }

        public string? LastPrompt { get; private set; }

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult<string?>(_reply);
        }
    }
}
