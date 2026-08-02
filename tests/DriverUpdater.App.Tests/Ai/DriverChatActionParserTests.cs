using DriverUpdater.App.Ai;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Ai;

public class DriverChatActionParserTests
{
    [Fact]
    public void Parse_extracts_ids_and_strips_action_line()
    {
        var answer = "Update the graphics and audio drivers.\nRECOMMEND_UPDATE: PCI\\VEN_8086&DEV_A7A0; PCI\\VEN_10EC&DEV_0256";

        var (text, ids, requestsScan, settings) = DriverChatActionParser.Parse(answer);

        text.Should().Be("Update the graphics and audio drivers.");
        ids.Should().Equal("PCI\\VEN_8086&DEV_A7A0", "PCI\\VEN_10EC&DEV_0256");
        requestsScan.Should().BeFalse();
        settings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_returns_no_ids_when_marker_absent()
    {
        var (text, ids, requestsScan, settings) = DriverChatActionParser.Parse("Nothing worth updating right now.");

        text.Should().Be("Nothing worth updating right now.");
        ids.Should().BeEmpty();
        requestsScan.Should().BeFalse();
        settings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_is_case_insensitive_and_dedupes()
    {
        var answer = "Do it.\nrecommend_update: ID-1, id-1, ID-2";

        var (_, ids, _, _) = DriverChatActionParser.Parse(answer);

        ids.Should().Equal("ID-1", "ID-2");
    }

    [Fact]
    public void Parse_handles_crlf_and_marker_only_answer()
    {
        var (text, ids, _, _) = DriverChatActionParser.Parse("RECOMMEND_UPDATE: X\r\n");

        text.Should().BeEmpty();
        ids.Should().Equal("X");
    }

    [Fact]
    public void Parse_merges_multiple_action_lines()
    {
        var answer = "First.\nRECOMMEND_UPDATE: A\nMore prose.\nRECOMMEND_UPDATE: B";

        var (text, ids, _, _) = DriverChatActionParser.Parse(answer);

        text.Should().Be("First.\nMore prose.");
        ids.Should().Equal("A", "B");
    }

    [Fact]
    public void Parse_extracts_scan_request_and_strips_action_line()
    {
        var answer = "I do not see available updates in this scan.\nSCAN_NOW";

        var (text, ids, requestsScan, _) = DriverChatActionParser.Parse(answer);

        text.Should().Be("I do not see available updates in this scan.");
        ids.Should().BeEmpty();
        requestsScan.Should().BeTrue();
    }

    [Fact]
    public void Parse_recognizes_scan_request_case_insensitively()
    {
        var (text, _, requestsScan, _) = DriverChatActionParser.Parse("scan_now\r\n");

        text.Should().BeEmpty();
        requestsScan.Should().BeTrue();
    }

    [Fact]
    public void Parse_extracts_a_settings_change_and_strips_the_action_line()
    {
        var answer = "I can turn on a scheduled scan for you.\nSET_OPTION: schedule=scan-only";

        var (text, _, _, settings) = DriverChatActionParser.Parse(answer);

        text.Should().Be("I can turn on a scheduled scan for you.");
        settings.Should().ContainSingle();
        settings[0].Key.Should().Be("schedule");
        settings[0].Value.Should().Be("scan-only");
    }

    [Fact]
    public void Parse_extracts_several_settings_from_one_line()
    {
        var answer = "Here is the setup.\nset_option: schedule=update-ai; schedule.cadence=weekly; schedule.time=22:00";

        var (_, _, _, settings) = DriverChatActionParser.Parse(answer);

        settings.Select(change => $"{change.Key}={change.Value}")
            .Should().Equal("schedule=update-ai", "schedule.cadence=weekly", "schedule.time=22:00");
    }

    [Fact]
    public void Parse_drops_unknown_keys_and_values_without_leaking_the_line()
    {
        var answer = "Sure.\nSET_OPTION: delete-everything=on; schedule=explode; close-button=background";

        var (text, _, _, settings) = DriverChatActionParser.Parse(answer);

        text.Should().Be("Sure.");
        settings.Should().ContainSingle();
        settings[0].Key.Should().Be("close-button");
    }

    [Fact]
    public void Parse_keeps_only_the_last_value_for_a_repeated_key()
    {
        var answer = "SET_OPTION: schedule=scan-only\nSET_OPTION: schedule=off";

        var (_, _, _, settings) = DriverChatActionParser.Parse(answer);

        settings.Should().ContainSingle();
        settings[0].Value.Should().Be("off");
    }
}
