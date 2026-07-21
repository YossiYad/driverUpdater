using DriverUpdater.App.Ai;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Ai;

public class DriverChatActionParserTests
{
    [Fact]
    public void Parse_extracts_ids_and_strips_action_line()
    {
        var answer = "Update the graphics and audio drivers.\nRECOMMEND_UPDATE: PCI\\VEN_8086&DEV_A7A0; PCI\\VEN_10EC&DEV_0256";

        var (text, ids, requestsScan) = DriverChatActionParser.Parse(answer);

        text.Should().Be("Update the graphics and audio drivers.");
        ids.Should().Equal("PCI\\VEN_8086&DEV_A7A0", "PCI\\VEN_10EC&DEV_0256");
        requestsScan.Should().BeFalse();
    }

    [Fact]
    public void Parse_returns_no_ids_when_marker_absent()
    {
        var (text, ids, requestsScan) = DriverChatActionParser.Parse("Nothing worth updating right now.");

        text.Should().Be("Nothing worth updating right now.");
        ids.Should().BeEmpty();
        requestsScan.Should().BeFalse();
    }

    [Fact]
    public void Parse_is_case_insensitive_and_dedupes()
    {
        var answer = "Do it.\nrecommend_update: ID-1, id-1, ID-2";

        var (_, ids, _) = DriverChatActionParser.Parse(answer);

        ids.Should().Equal("ID-1", "ID-2");
    }

    [Fact]
    public void Parse_handles_crlf_and_marker_only_answer()
    {
        var (text, ids, _) = DriverChatActionParser.Parse("RECOMMEND_UPDATE: X\r\n");

        text.Should().BeEmpty();
        ids.Should().Equal("X");
    }

    [Fact]
    public void Parse_merges_multiple_action_lines()
    {
        var answer = "First.\nRECOMMEND_UPDATE: A\nMore prose.\nRECOMMEND_UPDATE: B";

        var (text, ids, _) = DriverChatActionParser.Parse(answer);

        text.Should().Be("First.\nMore prose.");
        ids.Should().Equal("A", "B");
    }

    [Fact]
    public void Parse_extracts_scan_request_and_strips_action_line()
    {
        var answer = "I do not see available updates in this scan.\nSCAN_NOW";

        var (text, ids, requestsScan) = DriverChatActionParser.Parse(answer);

        text.Should().Be("I do not see available updates in this scan.");
        ids.Should().BeEmpty();
        requestsScan.Should().BeTrue();
    }

    [Fact]
    public void Parse_recognizes_scan_request_case_insensitively()
    {
        var (text, _, requestsScan) = DriverChatActionParser.Parse("scan_now\r\n");

        text.Should().BeEmpty();
        requestsScan.Should().BeTrue();
    }
}
