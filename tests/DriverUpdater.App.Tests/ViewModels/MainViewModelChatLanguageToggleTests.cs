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
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelChatLanguageToggleTests
{
    [WpfFact]
    public void The_toggle_starts_matching_the_current_AI_response_language()
    {
        var vm = NewVm(new FakeChatSettingsApplier(), new MutableOptionsMonitor<AiSettings>(
            new AiSettings { ResponseLanguage = AppLanguage.Hebrew }));

        vm.IsAiResponseLanguageHebrew.Should().BeTrue();
    }

    [WpfFact]
    public async Task Checking_the_toggle_applies_the_Hebrew_response_language()
    {
        var applier = new FakeChatSettingsApplier();
        var vm = NewVm(applier, new MutableOptionsMonitor<AiSettings>(new AiSettings()));

        vm.IsAiResponseLanguageHebrew = true;
        await applier.ApplyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        applier.Applied.Should().ContainSingle();
        applier.Applied![0].Key.Should().Be("ai.language");
        applier.Applied[0].Value.Should().Be("hebrew");
        vm.StatusText.Should().Contain("Hebrew");
    }

    [WpfFact]
    public async Task Unchecking_the_toggle_applies_the_English_response_language()
    {
        var applier = new FakeChatSettingsApplier();
        var vm = NewVm(applier, new MutableOptionsMonitor<AiSettings>(
            new AiSettings { ResponseLanguage = AppLanguage.Hebrew }));

        vm.IsAiResponseLanguageHebrew = false;
        await applier.ApplyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        applier.Applied.Should().ContainSingle();
        applier.Applied![0].Value.Should().Be("english");
    }

    [WpfFact]
    public async Task A_failed_apply_reverts_the_toggle()
    {
        var applier = new FakeChatSettingsApplier { Failure = "settings.json is locked" };
        var vm = NewVm(applier, new MutableOptionsMonitor<AiSettings>(new AiSettings()));

        vm.IsAiResponseLanguageHebrew = true;
        await applier.ApplyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.IsAiResponseLanguageHebrew.Should().BeFalse();
        vm.StatusText.Should().Contain("settings.json is locked");
    }

    [WpfFact]
    public void An_external_settings_change_updates_the_toggle_without_reapplying()
    {
        var applier = new FakeChatSettingsApplier();
        var monitor = new MutableOptionsMonitor<AiSettings>(new AiSettings());
        var vm = NewVm(applier, monitor);

        monitor.Publish(new AiSettings { ResponseLanguage = AppLanguage.Hebrew });
        PumpDispatcher();

        vm.IsAiResponseLanguageHebrew.Should().BeTrue();
        applier.Applied.Should().BeNull();
    }

    [WpfFact]
    public void Toggling_without_a_settings_applier_does_not_throw()
    {
        var vm = NewVm(applier: null, new MutableOptionsMonitor<AiSettings>(new AiSettings()));

        var act = () => vm.IsAiResponseLanguageHebrew = true;

        act.Should().NotThrow();
        vm.IsAiResponseLanguageHebrew.Should().BeFalse();
    }

    private static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static MainViewModel NewVm(IChatSettingsApplier? applier, IOptionsMonitor<AiSettings> aiSettings) =>
        new(new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverChatCompleter: new StubTextCompleter("ok"),
            aiSettings: aiSettings,
            chatSettingsApplier: applier);

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private Action<T, string?>? _listener;

        public MutableOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; private set; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Publish(T value)
        {
            CurrentValue = value;
            _listener?.Invoke(value, null);
        }
    }

    private sealed class FakeChatSettingsApplier : IChatSettingsApplier
    {
        public IReadOnlyList<ChatSettingChange>? Applied { get; private set; }
        public string? Failure { get; init; }
        public TaskCompletionSource<ChatSettingsApplyResult> ApplyCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppSettings());

        public Task<ChatSettingsApplyResult> ApplyAsync(
            IReadOnlyList<ChatSettingChange> changes,
            CancellationToken cancellationToken = default)
        {
            ChatSettingsApplyResult result;
            if (Failure is not null)
            {
                result = ChatSettingsApplyResult.Failure(Failure);
            }
            else
            {
                Applied = changes;
                result = ChatSettingsApplyResult.Success();
            }

            ApplyCompleted.TrySetResult(result);
            return Task.FromResult(result);
        }
    }

    private sealed class StubTextCompleter : IAiTextCompleter
    {
        public StubTextCompleter(string reply) => Reply = reply;

        public string Reply { get; }
        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured => true;

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Reply);
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
