using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using DriverUpdater.App.Ai;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelChatSuggestionTests
{
    [WpfFact]
    public void The_row_starts_with_one_chip_so_the_fades_stay_in_step()
    {
        var vm = NewVm(new StubTextCompleter("ok"));

        vm.ChatSuggestions.Should().ContainSingle();
    }

    [WpfFact]
    public void The_row_grows_one_chip_per_tick_up_to_the_visible_count()
    {
        var vm = NewVm(new StubTextCompleter("ok"));

        vm.AdvanceChatSuggestions();
        vm.ChatSuggestions.Should().HaveCount(2);

        vm.AdvanceChatSuggestions();
        vm.ChatSuggestions.Should().HaveCount(ChatSuggestionCatalog.VisibleCount);

        vm.AdvanceChatSuggestions();
        vm.ChatSuggestions.Should().HaveCount(ChatSuggestionCatalog.VisibleCount);
    }

    [WpfFact]
    public void Once_full_each_tick_replaces_exactly_one_chip_round_robin()
    {
        var vm = NewVm(new StubTextCompleter("ok"));
        vm.AdvanceChatSuggestions();
        vm.AdvanceChatSuggestions();
        var before = vm.ChatSuggestions.ToArray();

        vm.AdvanceChatSuggestions();

        vm.ChatSuggestions[0].Should().NotBe(before[0]);
        vm.ChatSuggestions[1].Should().Be(before[1]);
        vm.ChatSuggestions[2].Should().Be(before[2]);

        vm.AdvanceChatSuggestions();

        vm.ChatSuggestions[1].Should().NotBe(before[1]);
        vm.ChatSuggestions[2].Should().Be(before[2]);
    }

    [WpfFact]
    public void A_swapped_chip_is_removed_and_reinserted_so_its_fade_restarts()
    {
        // An indexer assignment raises Replace, and WPF keeps the same ItemsControl container for
        // it, so the chip would carry over the finished fade-out and never become visible again.
        var vm = NewVm(new StubTextCompleter("ok"));
        vm.AdvanceChatSuggestions();
        vm.AdvanceChatSuggestions();

        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)vm.ChatSuggestions).CollectionChanged += (_, e) => actions.Add(e.Action);

        vm.AdvanceChatSuggestions();

        actions.Should().Equal(NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add);
    }

    [WpfFact]
    public void Chips_on_screen_are_never_duplicated()
    {
        var vm = NewVm(new StubTextCompleter("ok"));

        for (var tick = 0; tick < 30; tick++)
        {
            vm.AdvanceChatSuggestions();
            vm.ChatSuggestions.Should().OnlyHaveUniqueItems();
        }
    }

    [WpfFact]
    public async Task Clicking_a_chip_sends_it_as_the_question()
    {
        var completer = new StubTextCompleter("Here is what I would update.");
        var vm = NewVm(completer);
        var suggestion = vm.ChatSuggestions[0];

        await vm.UseChatSuggestionCommand.ExecuteAsync(suggestion);

        vm.DriverChatMessages[0].IsUser.Should().BeTrue();
        vm.DriverChatMessages[0].Text.Should().Be(suggestion.Text);
        completer.LastPrompt.Should().Contain($"User: {suggestion.Text}");
    }

    [WpfFact]
    public void Clearing_the_chat_starts_the_chip_row_over()
    {
        var vm = NewVm(new StubTextCompleter("ok"));
        vm.AdvanceChatSuggestions();
        vm.AdvanceChatSuggestions();

        vm.ClearDriverChatCommand.Execute(null);

        vm.ChatSuggestions.Should().ContainSingle();
    }

    [WpfFact]
    public void Chips_are_drawn_from_the_Hebrew_pool_when_the_AI_response_language_is_Hebrew()
    {
        var vm = new MainViewModel(
            new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverChatCompleter: new StubTextCompleter("ok"),
            aiSettings: new StubOptionsMonitor<AiSettings>(new AiSettings { ResponseLanguage = AppLanguage.Hebrew }));

        var hebrewPool = ChatSuggestionCatalog.For(AppLanguage.Hebrew);
        vm.ChatSuggestions.Should().OnlyContain(suggestion => hebrewPool.Contains(suggestion));
    }

    [WpfFact]
    public void Chips_are_kept_readable_for_three_full_ticks()
    {
        // The chip fade-out in MainWindow.xaml is timed against this; if the interval moves the
        // animation has to move with it.
        MainViewModel.SuggestionRotationInterval.Should().Be(TimeSpan.FromSeconds(8));
        ChatSuggestionCatalog.VisibleCount.Should().Be(3);
    }

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

    private sealed class StubTextCompleter : IAiTextCompleter
    {
        public StubTextCompleter(string reply) => Reply = reply;

        public string Reply { get; }
        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured => true;
        public string? LastPrompt { get; private set; }

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult<string?>(Reply);
        }
    }

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
