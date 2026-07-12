using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelDriverChatTests
{
    [WpfFact]
    public async Task SendDriverChat_appends_user_and_ai_messages_and_clears_input()
    {
        var completer = new StubTextCompleter(isConfigured: true, reply: "Update the graphics driver; it's a safe minor bump.");
        var vm = NewVm(completer);
        vm.Drivers.Add(new DriverRowViewModel(NewDriver("Intel Iris Xe Graphics")));
        vm.DriverChatInput = "What should I update?";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().HaveCount(2);
        vm.DriverChatMessages[0].IsUser.Should().BeTrue();
        vm.DriverChatMessages[0].Text.Should().Be("What should I update?");
        vm.DriverChatMessages[1].IsUser.Should().BeFalse();
        vm.DriverChatMessages[1].Text.Should().Be("Update the graphics driver; it's a safe minor bump.");
        vm.DriverChatInput.Should().BeEmpty();
        vm.HasDriverChat.Should().BeTrue();
        completer.LastPrompt.Should().Contain("Intel Iris Xe Graphics");
        completer.LastPrompt.Should().Contain("User: What should I update?");
    }

    [WpfFact]
    public async Task SendDriverChat_reports_when_ai_not_configured()
    {
        var completer = new StubTextCompleter(isConfigured: false, reply: "should not be used");
        var vm = NewVm(completer);
        vm.DriverChatInput = "anything";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().ContainSingle();
        vm.DriverChatMessages[0].IsUser.Should().BeFalse();
        vm.DriverChatMessages[0].Text.Should().Contain("not configured");
        completer.WasCalled.Should().BeFalse();
    }

    [WpfFact]
    public void ClearDriverChat_empties_the_conversation()
    {
        var vm = NewVm(new StubTextCompleter(isConfigured: true, reply: "x"));
        vm.DriverChatMessages.Add(new DriverUpdater.App.Logging.LogChatMessage(true, "q"));

        vm.ClearDriverChatCommand.Execute(null);

        vm.DriverChatMessages.Should().BeEmpty();
        vm.HasNoDriverChat.Should().BeTrue();
    }

    [WpfFact]
    public async Task SendDriverChat_appends_a_separate_install_action_message()
    {
        var row = new DriverRowViewModel(NewDriver("Intel Iris Xe Graphics"));
        row.AvailableUpdate = NewCandidate(row.HardwareId);
        var completer = new StubTextCompleter(isConfigured: true,
            reply: $"Update the graphics driver.\nRECOMMEND_UPDATE: {row.HardwareId}");
        var vm = NewVm(completer);
        vm.Drivers.Add(row);
        vm.DriverChatInput = "Update what you recommend";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().HaveCount(3);
        vm.DriverChatMessages[1].Text.Should().Be("Update the graphics driver.");
        vm.DriverChatMessages[1].HasInstallAction.Should().BeFalse();
        var action = vm.DriverChatMessages[2];
        action.IsUser.Should().BeFalse();
        action.HasInstallAction.Should().BeTrue();
        action.RecommendedCount.Should().Be(1);
        action.RecommendedHardwareIds.Should().Equal(row.HardwareId);
    }

    [WpfFact]
    public async Task SendDriverChat_ignores_recommendation_for_unknown_hardware()
    {
        var completer = new StubTextCompleter(isConfigured: true,
            reply: "Do it.\nRECOMMEND_UPDATE: HW\\does-not-exist");
        var vm = NewVm(completer);
        vm.Drivers.Add(new DriverRowViewModel(NewDriver("Realtek Audio")));
        vm.DriverChatInput = "update all";

        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatMessages.Should().HaveCount(2);
        vm.DriverChatMessages[1].Text.Should().Be("Do it.");
        vm.DriverChatMessages.Should().OnlyContain(m => !m.HasInstallAction);
    }

    [WpfFact]
    public async Task Action_messages_are_excluded_from_the_next_prompt_history()
    {
        var row = new DriverRowViewModel(NewDriver("Intel Iris Xe Graphics"));
        row.AvailableUpdate = NewCandidate(row.HardwareId);
        var completer = new StubTextCompleter(isConfigured: true,
            reply: $"Sure.\nRECOMMEND_UPDATE: {row.HardwareId}");
        var vm = NewVm(completer);
        vm.Drivers.Add(row);
        vm.DriverChatInput = "go";
        await vm.SendDriverChatCommand.ExecuteAsync(null);

        vm.DriverChatInput = "thanks";
        await vm.SendDriverChatCommand.ExecuteAsync(null);

        completer.LastPrompt.Should().NotContain("Assistant: \n");
    }

    private static UpdateCandidate NewCandidate(string hardwareId) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: new Version(2, 0, 0, 0),
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "abc",
        SupersededIds: Array.Empty<string>());

    private static MainViewModel NewVm(IAiTextCompleter completer) =>
        new(new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverChatCompleter: completer);

    private static DriverInfo NewDriver(string name) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: $"HW\\{name}",
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "Vendor",
        Manufacturer: "Vendor",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

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
        public bool WasCalled { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastPrompt = prompt;
            return Task.FromResult<string?>(_reply);
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
