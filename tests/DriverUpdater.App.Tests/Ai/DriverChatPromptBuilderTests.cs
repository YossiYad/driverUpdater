using DriverUpdater.App.Ai;
using DriverUpdater.App.Logging;
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
    }

    [Fact]
    public void Build_throws_on_blank_question()
    {
        var act = () => DriverChatPromptBuilder.Build(
            Array.Empty<DriverChatContextItem>(), Array.Empty<LogChatMessage>(), "  ");

        act.Should().Throw<ArgumentException>();
    }
}
