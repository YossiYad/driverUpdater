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
    public void BuildPrompt_puts_the_machine_in_front_of_the_candidates()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Intel(R) Iris(R) Xe Graphics", "32.0.101.7076", "32.0.101.7088") },
            machine: Machine(),
            webSearchEnabled: true);

        prompt.Should().Contain("THIS PC (the machine every verdict is for):");
        prompt.Should().Contain("- System: ASUSTeK COMPUTER INC. Vivobook X1502VA");
        prompt.Should().Contain("- CPU: 13th Gen Intel(R) Core(TM) i5-13500H");
        prompt.Should().Contain("- GPU: Intel(R) Iris(R) Xe Graphics");
        prompt.Should().Contain("- Windows: Microsoft Windows 11 Home");
        prompt.Should().Contain("When you search, put these details in the query");
        prompt.IndexOf("THIS PC", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Candidates:", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildPrompt_says_nothing_about_a_machine_it_could_not_read()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: MachineProfile.Empty);

        prompt.Should().NotContain("THIS PC");
    }

    [Fact]
    public void BuildPrompt_only_tells_the_model_to_search_when_it_can()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: Machine(),
            webSearchEnabled: true);

        prompt.Should().Contain("search the web for the latest official driver");
        prompt.Should().Contain("Use the web to check for reported problems");
        prompt.Should().NotContain("You have NO web access");
    }

    [Fact]
    public void BuildPrompt_requires_vendor_and_user_report_research_before_recommending()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "AMD Radeon RX 7700 XT", "1.0.0.0", "2.0.0.0") },
            machine: Machine(),
            webSearchEnabled: true);

        prompt.Should().Contain("RESEARCH BEFORE YOU ANSWER");
        prompt.Should().Contain("download and support pages for this exact device");
        prompt.Should().Contain("ties a driver branch or version to a hardware generation");
        prompt.Should().Contain("community forum");
        prompt.Should().Contain("release notes and known-issues list");
        prompt.Should().Contain("sources");
    }

    [Fact]
    public void BuildPrompt_does_not_ask_for_research_it_cannot_do()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: Machine(),
            webSearchEnabled: false);

        prompt.Should().NotContain("RESEARCH BEFORE YOU ANSWER");
        prompt.Should().Contain("leave this as an empty array");
    }

    [Fact]
    public void BuildPrompt_forbids_invented_lookups_when_there_is_no_web_access()
    {
        // Ollama has no search tool. Telling it to "search the web" only invites a made-up
        // "I checked the release notes" answer.
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: Machine(),
            webSearchEnabled: false);

        prompt.Should().Contain("You have NO web access");
        prompt.Should().NotContain("search the web for the latest official driver");
        prompt.Should().NotContain("Use the web to check for reported problems");
        prompt.Should().NotContain("When you search, put these details in the query");
    }

    [Fact]
    public void BuildPrompt_raises_the_bar_for_an_unattended_scheduled_run()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: Machine(),
            webSearchEnabled: true,
            unattendedRun: true);

        prompt.Should().Contain("UNATTENDED scheduled run");
        prompt.Should().Contain("prefer Caution or Unknown over an optimistic Safe");
    }

    [Fact]
    public void BuildPrompt_does_not_mention_an_unattended_run_for_an_interactive_scan()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "Realtek Audio", "1.0", "2.0") },
            machine: Machine());

        prompt.Should().NotContain("UNATTENDED");
    }

    private static MachineProfile Machine() => MachineProfile.Empty with
    {
        SystemManufacturer = "ASUSTeK COMPUTER INC.",
        SystemModel = "Vivobook X1502VA",
        ProcessorName = "13th Gen Intel(R) Core(TM) i5-13500H",
        GraphicsAdapters = new[] { "Intel(R) Iris(R) Xe Graphics" },
        OperatingSystemName = "Microsoft Windows 11 Home",
        OperatingSystemBuild = "26200"
    };

    [Fact]
    public void BuildPrompt_instructs_the_ai_to_write_user_facing_fields_in_the_selected_language()
    {
        var prompt = AiVerificationProtocol.BuildPrompt(
            new[] { NewRequest("corr-1", "AMD Radeon", "1.0.0.0", "2.0.0.0") },
            AppLanguage.Hebrew);

        prompt.Should().Contain("clear, natural Hebrew");
        prompt.Should().Contain("JSON property names and enum values in English");
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

    [Fact]
    public void ParseVerdicts_reads_the_pages_the_model_says_it_checked()
    {
        const string raw = """
            {"verdicts":[{
              "id":"corr-1",
              "isGenuinelyNewer":true,
              "risk":"Caution",
              "summary":"Use caution",
              "rationale":"The vendor pins this hardware generation to an older branch.",
              "latestKnownVersion":"2.0.0.0",
              "sources":["https://vendor.example/support/driver","  ","https://VENDOR.example/support/driver","https://forum.example/thread/1"]
            }]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts["corr-1"].Sources.Should().Equal(
            "https://vendor.example/support/driver",
            "https://forum.example/thread/1");
    }

    [Fact]
    public void ParseVerdicts_leaves_sources_null_when_the_model_listed_none()
    {
        const string raw = """
            {"verdicts":[{"id":"corr-1","isGenuinelyNewer":true,"risk":"Safe","summary":"ok","rationale":"ok","sources":[]}]}
            """;

        var verdicts = AiVerificationProtocol.ParseVerdicts(raw);

        verdicts["corr-1"].Sources.Should().BeNull();
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
