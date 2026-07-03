using DriverUpdater.Core.Models;
using DriverUpdater.Services.Ai;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Ai;

public class AiVerificationProtocolTests
{
    [Fact]
    public void BuildPrompt_lists_each_candidate_with_its_correlation_id()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(new[]
        {
            NewRequest("corr-1", "AMD Radeon RX 7700 XT", "1.0.0.0", "2.0.0.0"),
            NewRequest("corr-2", "Realtek Audio", "6.0.9927.1", "6.0.9927.1")
        });

        prompt.Should().Contain("id=corr-1");
        prompt.Should().Contain("AMD Radeon RX 7700 XT");
        prompt.Should().Contain("id=corr-2");
        prompt.Should().Contain("isGenuinelyNewer");
        prompt.Should().Contain("risk");
        prompt.Should().Contain("installedSuitability");
        prompt.Should().Contain("recommendedVersion");
    }

    [Fact]
    public void ParseVerdicts_reads_clean_json_object()
    {
        const string raw = """
            {"verdicts":[
              {"id":"corr-1","isGenuinelyNewer":true,"risk":"Caution","summary":"New driver","rationale":"Newer build","latestKnownVersion":"2.0.0.0"},
              {"id":"corr-2","isGenuinelyNewer":false,"risk":"Safe","summary":"Same version","rationale":"Already installed","latestKnownVersion":null}
            ]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts.Should().HaveCount(2);
        verdicts["corr-1"].IsGenuinelyNewer.Should().BeTrue();
        verdicts["corr-1"].Risk.Should().Be(AiRiskLevel.Caution);
        verdicts["corr-1"].LatestKnownVersion.Should().Be("2.0.0.0");
        verdicts["corr-2"].IsGenuinelyNewer.Should().BeFalse();
        verdicts["corr-2"].Risk.Should().Be(AiRiskLevel.Safe);
        verdicts["corr-2"].LatestKnownVersion.Should().BeNull();
    }

    [Fact]
    public void ParseVerdicts_extracts_json_wrapped_in_prose_and_markdown_fences()
    {
        const string raw = """
            Sure, here is my assessment based on the latest information: {not the JSON payload}

            ```json
            {"verdicts":[{"id":"corr-1","isGenuinelyNewer":true,"risk":"HighRisk","summary":"Known black screen bug","rationale":"Multiple reports.","latestKnownVersion":"2.1.0.0"}]}
            ```

            Let me know if you need anything else. {also not JSON}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts.Should().ContainKey("corr-1");
        verdicts["corr-1"].Risk.Should().Be(AiRiskLevel.HighRisk);
        verdicts["corr-1"].Summary.Should().Be("Known black screen bug");
    }

    [Fact]
    public void ParseVerdicts_ignores_quotes_in_prose_before_json()
    {
        const string raw = """
            The useful payload is "below", after this explanation:
            {"verdicts":[{"id":"corr-1","isGenuinelyNewer":true,"risk":"Safe","summary":"Newer driver","rationale":"Version is newer.","latestKnownVersion":"2.0.0.0"}]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts.Should().ContainKey("corr-1");
        verdicts["corr-1"].Risk.Should().Be(AiRiskLevel.Safe);
    }

    [Fact]
    public void ParseVerdicts_preserves_braces_inside_json_strings()
    {
        const string raw = """
            {"verdicts":[{"id":"corr-1","isGenuinelyNewer":true,"risk":"Caution","summary":"Check release notes","rationale":"The vendor page mentions {optional} firmware tooling.","latestKnownVersion":null}]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts.Should().ContainKey("corr-1");
        verdicts["corr-1"].Rationale.Should().Be("The vendor page mentions {optional} firmware tooling.");
    }

    [Fact]
    public void ParseVerdicts_reads_driver_advisor_feedback_fields()
    {
        const string raw = """
            {"verdicts":[{
              "id":"corr-1",
              "isGenuinelyNewer":true,
              "risk":"Safe",
              "summary":"Recommended",
              "rationale":"The version matches the hardware family.",
              "latestKnownVersion":"2.0.0.0",
              "latestKnownDate":"2026-02-03",
              "latestKnownUrl":"https://example.com/driver",
              "installedSuitability":"The installed driver is compatible but old.",
              "candidateSuitability":"The candidate is suitable for this adapter and Windows generation.",
              "recommendedVersion":"2.0.0.0",
              "advisorNote":"Install if you want the latest fixes; keep current if everything is stable."
            }]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts["corr-1"].LatestKnownDate.Should().Be(new DateOnly(2026, 2, 3));
        verdicts["corr-1"].LatestKnownUrl.Should().Be("https://example.com/driver");
        verdicts["corr-1"].InstalledSuitability.Should().Be("The installed driver is compatible but old.");
        verdicts["corr-1"].CandidateSuitability.Should().Be("The candidate is suitable for this adapter and Windows generation.");
        verdicts["corr-1"].RecommendedVersion.Should().Be("2.0.0.0");
        verdicts["corr-1"].AdvisorNote.Should().Contain("Install");
    }

    [Theory]
    [InlineData("safe", AiRiskLevel.Safe)]
    [InlineData("Caution", AiRiskLevel.Caution)]
    [InlineData("HighRisk", AiRiskLevel.HighRisk)]
    [InlineData("high risk", AiRiskLevel.HighRisk)]
    [InlineData("something-weird", AiRiskLevel.Unknown)]
    public void ParseVerdicts_maps_risk_strings_tolerantly(string risk, AiRiskLevel expected)
    {
        var raw = $$"""{"verdicts":[{"id":"x","isGenuinelyNewer":true,"risk":"{{risk}}","summary":"","rationale":""}]}""";

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts["x"].Risk.Should().Be(expected);
    }

    [Fact]
    public void ParseVerdicts_returns_empty_for_garbage_or_null()
    {
        AiVerificationProtocol.ParseVerdicts(null).Should().BeEmpty();
        AiVerificationProtocol.ParseVerdicts("").Should().BeEmpty();
        AiVerificationProtocol.ParseVerdicts("no json here at all").Should().BeEmpty();
        AiVerificationProtocol.ParseVerdicts("{not valid json}").Should().BeEmpty();
    }

    private static AiVerificationRequest NewRequest(
        string correlationId, string deviceName, string installedVersion, string candidateVersion) => new(
        CorrelationId: correlationId,
        DeviceName: deviceName,
        HardwareId: "PCI\\VEN_1002&DEV_747E",
        InstalledVersion: installedVersion,
        InstalledDate: new DateOnly(2024, 1, 1),
        CandidateVersion: candidateVersion,
        CandidateDate: new DateOnly(2026, 1, 1),
        Source: UpdateSource.Oem,
        DownloadUrl: "https://example.com/driver.exe");
}
