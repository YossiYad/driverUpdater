using DriverUpdater.App.Ai;
using DriverUpdater.App.Logging;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Ai;

public class DriverChatPromptBuilderTests
{
    [Fact]
    public void Build_includes_driver_inventory_history_and_question()
    {
        var drivers = new[]
        {
            new DriverChatContextItem("Intel Iris Xe Graphics", "PCI\\VEN_8086&DEV_A7A0", "Display", "32.0.101.7076", "Outdated", "32.0.101.7085", "MicrosoftCatalog"),
            new DriverChatContextItem("Realtek Audio", "PCI\\VEN_10EC&DEV_0256", "Audio", "6.0.9629.1", "UpToDate", null, null)
        };
        var history = new[]
        {
            new LogChatMessage(IsUser: true, "hi"),
            new LogChatMessage(IsUser: false, "hello")
        };

        var prompt = DriverChatPromptBuilder.Build(drivers, history, "Should I update the graphics driver?");

        prompt.Should().Contain("Intel Iris Xe Graphics");
        prompt.Should().Contain("32.0.101.7085 (MicrosoftCatalog)");
        prompt.Should().Contain("2 total, 1 with an available update");
        prompt.Should().Contain("User: hi");
        prompt.Should().Contain("Assistant: hello");
        prompt.Should().Contain("User: Should I update the graphics driver?");
        prompt.Should().EndWith("Assistant:");
    }

    [Fact]
    public void Build_instructs_the_recommend_update_action_line()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(), Array.Empty<LogChatMessage>(), "What should I update?");

        prompt.Should().Contain("RECOMMEND_UPDATE: <hardwareId>; <hardwareId>");
        prompt.Should().Contain("SCAN_NOW");
        prompt.Should().Contain("zero available updates");
        prompt.Should().Contain("Never output both SCAN_NOW and RECOMMEND_UPDATE");
    }

    [Fact]
    public void Build_instructs_the_ai_to_answer_in_the_selected_language()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "מה כדאי לעדכן?",
            AppLanguage.Hebrew);

        prompt.Should().Contain("clear, natural Hebrew");
    }

    [Fact]
    public void Build_explanation_turn_requests_reasoning_without_a_new_install_action()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "Why this driver?",
            AppLanguage.English,
            allowInstallActions: false);

        prompt.Should().Contain("source reliability");
        prompt.Should().Contain("meaningful risk");
        prompt.Should().Contain("do not output a RECOMMEND_UPDATE or SCAN_NOW line");
        prompt.Should().NotContain("RECOMMEND_UPDATE: <hardwareId>");
    }

    [Fact]
    public void Build_includes_recent_logs_with_instructions_when_provided()
    {
        var logs = new[]
        {
            new LogEntry(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero), "Information", "MainViewModel",
                "Update run: starting Intel Iris Xe Graphics (current version=1.0, target version=2.0, source=MicrosoftCatalog, kind=PnPUtilPackage, url=)", null),
            new LogEntry(new DateTimeOffset(2026, 8, 7, 12, 3, 0, TimeSpan.Zero), "Error", "InstallPipeline",
                "Download failed: timeout", null)
        };

        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "Why is the update taking so long?",
            recentLogs: logs);

        prompt.Should().Contain("RECENT APPLICATION LOGS");
        prompt.Should().Contain("Update run: starting Intel Iris Xe Graphics");
        prompt.Should().Contain("Download failed: timeout");
        prompt.Should().Contain("compare");
        prompt.Should().Contain("the timestamps");
        prompt.Should().NotContain("IN PROGRESS right now");
    }

    [Fact]
    public void Build_flags_a_live_update_run_when_one_is_in_progress()
    {
        var logs = new[]
        {
            new LogEntry(DateTimeOffset.Now, "Information", "MainViewModel", "Update run: starting Realtek Audio", null)
        };

        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "What is happening?",
            recentLogs: logs,
            updateRunInProgress: true);

        prompt.Should().Contain("An update/install run is IN PROGRESS right now");
    }

    [Fact]
    public void Build_omits_the_log_section_when_no_logs_are_provided()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "What should I update?",
            recentLogs: Array.Empty<LogEntry>());

        prompt.Should().NotContain("RECENT APPLICATION LOGS");
    }

    [Fact]
    public void Build_adds_web_search_guidance_and_the_machine_model_when_enabled()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "What do you recommend I update?",
            webSearchEnabled: true,
            machine: MachineProfile.Empty with
            {
                SystemManufacturer = "Dell Inc.",
                SystemModel = "XPS 15 9530",
                ProcessorName = "13th Gen Intel(R) Core(TM) i9-13900H",
                GraphicsAdapters = new[] { "NVIDIA GeForce RTX 4070 Laptop GPU" },
                OperatingSystemName = "Microsoft Windows 11 Pro",
                OperatingSystemBuild = "26200"
            });

        prompt.Should().Contain("You have Google Search available");
        prompt.Should().Contain("known issues or bugs in a specific driver version");
        prompt.Should().Contain("THIS PC (every recommendation is for this machine):");
        prompt.Should().Contain("- System: Dell Inc. XPS 15 9530");
        prompt.Should().Contain("- CPU: 13th Gen Intel(R) Core(TM) i9-13900H");
        prompt.Should().Contain("- GPU: NVIDIA GeForce RTX 4070 Laptop GPU");
        prompt.Should().Contain("build: 26200");
        prompt.Should().Contain("Version history and downgrade");
    }

    [Fact]
    public void Build_omits_web_search_guidance_by_default()
    {
        var prompt = DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(),
            Array.Empty<LogChatMessage>(),
            "What do you recommend I update?");

        prompt.Should().NotContain("Google Search");
        prompt.Should().NotContain("THIS PC");
    }

    [Fact]
    public void Build_throws_on_blank_question()
    {
        var act = () => DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(), Array.Empty<LogChatMessage>(), "  ");

        act.Should().Throw<ArgumentException>();
    }
}
