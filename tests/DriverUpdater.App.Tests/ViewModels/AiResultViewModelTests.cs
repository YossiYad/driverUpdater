using System.Windows;
using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class AiResultViewModelTests
{
    [WpfFact]
    public async Task Translate_changes_explanations_and_original_restores_them()
    {
        var translator = new RecordingTranslator(
            new AiResultTextContent(
                "מומלץ",
                "העדכון מתאים למחשב.",
                "הגרסה חדשה יותר.",
                "הדרייבר הנוכחי תקין.",
                "הדרייבר החדש מתאים.",
                "אפשר להתקין בבטחה."));
        var vm = NewViewModel(translator);
        var originalSummary = vm.Summary;

        await vm.TranslateCommand.ExecuteAsync(null);

        translator.RequestedLanguage.Should().Be(AppLanguage.Hebrew);
        vm.Summary.Should().Be("העדכון מתאים למחשב.");
        vm.CopyText.Should().Contain("אפשר להתקין בבטחה.");
        vm.ContentFlowDirection.Should().Be(FlowDirection.RightToLeft);
        vm.ContentTextAlignment.Should().Be(TextAlignment.Right);
        vm.IsTranslated.Should().BeTrue();

        vm.ShowOriginalCommand.Execute(null);

        vm.Summary.Should().Be(originalSummary);
        vm.ContentFlowDirection.Should().Be(FlowDirection.LeftToRight);
        vm.ContentTextAlignment.Should().Be(TextAlignment.Left);
        vm.IsTranslated.Should().BeFalse();
    }

    [WpfFact]
    public async Task Selecting_English_requests_English_translation()
    {
        var translated = new AiResultTextContent(
            "Recommended",
            "Translated summary",
            "Translated rationale",
            "Installed driver is suitable",
            "Candidate driver is suitable",
            "Installation is safe");
        var translator = new RecordingTranslator(translated);
        var vm = NewViewModel(translator);
        vm.SelectedTranslationLanguage = vm.TranslationLanguages.Single(
            option => option.Language == AppLanguage.English);

        await vm.TranslateCommand.ExecuteAsync(null);

        translator.RequestedLanguage.Should().Be(AppLanguage.English);
        vm.ContentFlowDirection.Should().Be(FlowDirection.LeftToRight);
        vm.TranslationStatus.Should().Be("Translation complete.");
    }

    private static AiResultViewModel NewViewModel(IAiResultTranslator translator)
    {
        var driver = new DriverInfo(
            "device-1",
            "PCI\\VEN_1234",
            "Example graphics adapter",
            DriverCategory.Display,
            "Example provider",
            "Example manufacturer",
            new Version(1, 0),
            new DateOnly(2025, 1, 1),
            "example.inf",
            null,
            true,
            "Display");
        var verdict = new AiVerdict(
            true,
            AiRiskLevel.Safe,
            "Original summary",
            "Original rationale",
            "2.0",
            new DateOnly(2026, 1, 1),
            "https://example.test/driver",
            "Installed suitability",
            "Candidate suitability",
            "2.0",
            "Original advice");

        return new AiResultViewModel(driver, null, verdict, translator);
    }

    private sealed class RecordingTranslator(AiResultTextContent result) : IAiResultTranslator
    {
        public bool IsConfigured => true;
        public AppLanguage? RequestedLanguage { get; private set; }

        public Task<AiResultTextContent?> TranslateAsync(
            AiResultTextContent source,
            AppLanguage targetLanguage,
            CancellationToken cancellationToken = default)
        {
            RequestedLanguage = targetLanguage;
            return Task.FromResult<AiResultTextContent?>(result);
        }
    }
}
