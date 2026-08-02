using System.Runtime.CompilerServices;
using DriverUpdater.App.Ai;
using DriverUpdater.App.Services;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelChatSettingsTests
{
    [WpfFact]
    public async Task A_proposed_setting_becomes_a_confirmation_card_and_changes_nothing_yet()
    {
        var applier = new FakeChatSettingsApplier();
        var vm = NewVm(new StubTextCompleter("I can keep the app in the tray.\nSET_OPTION: close-button=background"), applier);
        vm.DriverChatInput = "What should the X button do?";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().HaveCount(3);
        vm.DriverChatMessages[1].Text.Should().Be("I can keep the app in the tray.");
        var card = vm.DriverChatMessages[2];
        card.HasSettingAction.Should().BeTrue();
        card.SettingProposal!.Descriptions.Should().ContainSingle();
        card.SettingProposal.IsPending.Should().BeTrue();
        applier.Applied.Should().BeNull();
        vm.StatusText.Should().Contain("Confirm it in the chat");
    }

    [WpfFact]
    public async Task Applying_the_card_writes_the_change_and_resolves_it()
    {
        var applier = new FakeChatSettingsApplier();
        var vm = NewVm(new StubTextCompleter("Sure.\nSET_OPTION: schedule=scan-only"), applier);
        vm.DriverChatInput = "Set up a scheduled scan";
        await vm.SendDriverChatCommand.ExecuteAsync(null);
        var proposal = vm.DriverChatMessages[^1].SettingProposal!;

        await vm.ApplyChatSettingsCommand.ExecuteAsync(proposal);

        applier.Applied.Should().ContainSingle();
        applier.Applied![0].Key.Should().Be("schedule");
        applier.Applied[0].Value.Should().Be("scan-only");
        proposal.IsResolved.Should().BeTrue();
        proposal.ResultText.Should().Contain("Settings updated");
        vm.ApplyChatSettingsCommand.CanExecute(proposal).Should().BeFalse();
    }

    [WpfFact]
    public async Task Declining_the_card_leaves_the_settings_alone()
    {
        var applier = new FakeChatSettingsApplier();
        var vm = NewVm(new StubTextCompleter("Sure.\nSET_OPTION: schedule=off"), applier);
        vm.DriverChatInput = "Turn scheduling off";
        await vm.SendDriverChatCommand.ExecuteAsync(null);
        var proposal = vm.DriverChatMessages[^1].SettingProposal!;

        vm.DeclineChatSettingsCommand.Execute(proposal);

        applier.Applied.Should().BeNull();
        proposal.IsResolved.Should().BeTrue();
        proposal.ResultText.Should().Contain("Nothing was changed");
    }

    [WpfFact]
    public async Task A_failed_apply_is_reported_on_the_card()
    {
        var applier = new FakeChatSettingsApplier { Failure = "settings.json is locked" };
        var vm = NewVm(new StubTextCompleter("Sure.\nSET_OPTION: schedule=off"), applier);
        vm.DriverChatInput = "Turn scheduling off";
        await vm.SendDriverChatCommand.ExecuteAsync(null);
        var proposal = vm.DriverChatMessages[^1].SettingProposal!;

        await vm.ApplyChatSettingsCommand.ExecuteAsync(proposal);

        proposal.ResultText.Should().Contain("settings.json is locked");
        proposal.IsResolved.Should().BeTrue();
    }

    [WpfFact]
    public async Task Turning_on_unattended_installing_warns_on_the_card()
    {
        var vm = NewVm(
            new StubTextCompleter("I will update everything weekly.\nSET_OPTION: schedule=update-all"),
            new FakeChatSettingsApplier());
        vm.DriverChatInput = "Update my drivers automatically every week";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        var proposal = vm.DriverChatMessages[^1].SettingProposal!;
        proposal.WarnsAboutUnattendedInstalls.Should().BeTrue();
        proposal.UnattendedInstallWarning.Should().Contain("without asking");
    }

    [WpfFact]
    public async Task A_harmless_change_carries_no_unattended_warning()
    {
        var vm = NewVm(
            new StubTextCompleter("Done.\nSET_OPTION: schedule=scan-only"),
            new FakeChatSettingsApplier());
        vm.DriverChatInput = "Just scan on a schedule";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages[^1].SettingProposal!.WarnsAboutUnattendedInstalls.Should().BeFalse();
    }

    [WpfFact]
    public async Task A_settings_only_reply_never_leaks_the_protocol_line()
    {
        var vm = NewVm(new StubTextCompleter("SET_OPTION: schedule=off"), new FakeChatSettingsApplier());
        vm.DriverChatInput = "Turn scheduling off";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().NotContain(message => message.Text.Contains("SET_OPTION"));
        vm.DriverChatMessages[^1].HasSettingAction.Should().BeTrue();
    }

    [WpfFact]
    public async Task The_prompt_carries_the_current_settings_and_the_key_catalogue()
    {
        var applier = new FakeChatSettingsApplier();
        applier.Current.Schedule.Mode = ScheduleMode.ScanOnly;
        var completer = new StubTextCompleter("Your schedule only scans.");
        var vm = NewVm(completer, applier);
        vm.DriverChatInput = "Is scheduling on?";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        completer.LastPrompt.Should().Contain("schedule = scan-only");
        completer.LastPrompt.Should().Contain("SET_OPTION: <key>=<value>");
        completer.LastPrompt.Should().Contain("close-button: exit | background");
    }

    [WpfFact]
    public async Task Settings_are_left_out_of_the_prompt_when_the_chat_cannot_change_them()
    {
        var completer = new StubTextCompleter("Nothing to do.");
        var vm = NewVm(completer, applier: null);
        vm.DriverChatInput = "Is scheduling on?";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        completer.LastPrompt.Should().NotContain("SET_OPTION");
    }

    [WpfFact]
    public async Task A_proposed_setting_is_ignored_when_the_chat_cannot_change_settings()
    {
        var vm = NewVm(new StubTextCompleter("Sure.\nSET_OPTION: schedule=off"), applier: null);
        vm.DriverChatInput = "Turn scheduling off";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().NotContain(message => message.HasSettingAction);
        vm.DriverChatMessages.Should().NotContain(message => message.Text.Contains("SET_OPTION"));
    }

    private static MainViewModel NewVm(IAiTextCompleter completer, IChatSettingsApplier? applier) =>
        new(new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverChatCompleter: completer,
            chatSettingsApplier: applier);

    private sealed class FakeChatSettingsApplier : IChatSettingsApplier
    {
        public AppSettings Current { get; } = new();
        public IReadOnlyList<ChatSettingChange>? Applied { get; private set; }
        public string? Failure { get; init; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task<ChatSettingsApplyResult> ApplyAsync(
            IReadOnlyList<ChatSettingChange> changes,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                return Task.FromResult(ChatSettingsApplyResult.Failure(Failure));
            }

            Applied = changes;
            return Task.FromResult(ChatSettingsApplyResult.Success());
        }
    }

    private sealed class StubTextCompleter : IAiTextCompleter
    {
        public StubTextCompleter(string reply) => Reply = reply;

        public string Reply { get; set; }
        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured => true;
        public string? LastPrompt { get; private set; }

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult<string?>(Reply);
        }
    }

    private sealed class FakeScanService : IDriverScanService
    {
        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
