using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DriverUpdater.App.Tests.Services;

public class AiResultTranslatorTests
{
    [Fact]
    public async Task TranslateAsync_returns_all_translated_fields_from_json_response()
    {
        var completer = new Mock<IAiTextCompleter>();
        completer.SetupGet(item => item.IsConfigured).Returns(true);
        completer
            .Setup(item => item.CompleteAsync(
                It.Is<string>(prompt =>
                    prompt.Contains("Hebrew", StringComparison.Ordinal)
                    && prompt.Contains("Keep this summary", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                """
                ```json
                {
                  "Recommendation": "מומלץ",
                  "Summary": "סיכום מתורגם",
                  "Rationale": "הסבר מתורגם",
                  "InstalledSuitability": "הדרייבר המותקן מתאים",
                  "CandidateSuitability": "הדרייבר החדש מתאים",
                  "AdvisorNote": "אפשר להתקין"
                }
                ```
                """);
        var translator = new AiResultTranslator(
            completer.Object,
            NullLogger<AiResultTranslator>.Instance);

        var result = await translator.TranslateAsync(SourceContent(), AppLanguage.Hebrew);

        result.Should().NotBeNull();
        result!.Recommendation.Should().Be("מומלץ");
        result.Summary.Should().Be("סיכום מתורגם");
        result.AdvisorNote.Should().Be("אפשר להתקין");
    }

    [Fact]
    public async Task TranslateAsync_rejects_non_json_ai_response()
    {
        var completer = new Mock<IAiTextCompleter>();
        completer.SetupGet(item => item.IsConfigured).Returns(true);
        completer
            .Setup(item => item.CompleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Here is the translation without the requested structure.");
        var translator = new AiResultTranslator(
            completer.Object,
            NullLogger<AiResultTranslator>.Instance);

        var result = await translator.TranslateAsync(SourceContent(), AppLanguage.Hebrew);

        result.Should().BeNull();
    }

    private static AiResultTextContent SourceContent() => new(
        "Recommended",
        "Keep this summary",
        "Original rationale",
        "Installed driver is suitable",
        "Candidate driver is suitable",
        "Installation is optional");
}
