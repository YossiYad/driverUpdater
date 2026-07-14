using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public sealed partial class AiResultViewModel : ObservableObject
{
    private readonly IAiResultTranslator? _translator;
    private readonly AiResultTextContent _originalContent;
    private readonly Dictionary<AppLanguage, AiResultTextContent> _translationCache = new();

    public AiResultViewModel(
        DriverInfo driver,
        UpdateCandidate? candidate,
        AiVerdict verdict,
        IAiResultTranslator? translator = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(verdict);

        _translator = translator;

        DeviceName = driver.DeviceName;
        HardwareId = driver.HardwareId;
        Provider = string.IsNullOrWhiteSpace(driver.Provider) ? "Unknown" : driver.Provider;
        Manufacturer = string.IsNullOrWhiteSpace(driver.Manufacturer) ? "Unknown" : driver.Manufacturer;
        Category = driver.Category.ToString();
        InstalledVersion = driver.CurrentVersion?.ToString() ?? "Unknown";
        InstalledDate = driver.CurrentDate?.ToString("yyyy-MM-dd") ?? "Unknown";
        CandidateVersion = candidate?.NewVersion.ToString() ?? verdict.LatestKnownVersion ?? "No candidate";
        CandidateDate = candidate?.NewDate.ToString("yyyy-MM-dd")
            ?? verdict.LatestKnownDate?.ToString("yyyy-MM-dd")
            ?? "Unknown";
        Source = candidate?.Source.ToString() ?? "AI latest-driver search";
        InstallKind = candidate?.InstallKind.ToString() ?? "Advisory";
        Risk = verdict.Risk.ToString();
        _summary = EmptyFallback(verdict.Summary);
        _rationale = EmptyFallback(verdict.Rationale);
        LatestKnownVersion = verdict.LatestKnownVersion ?? "Unknown";
        LatestKnownDate = verdict.LatestKnownDate?.ToString("yyyy-MM-dd") ?? "Unknown";
        LatestKnownUrl = verdict.LatestKnownUrl ?? candidate?.DownloadUrl.AbsoluteUri ?? "Unknown";
        _installedSuitability = EmptyFallback(verdict.InstalledSuitability);
        _candidateSuitability = EmptyFallback(verdict.CandidateSuitability);
        RecommendedVersion = verdict.RecommendedVersion ?? "Unknown";
        _advisorNote = EmptyFallback(verdict.AdvisorNote);
        _recommendation = BuildRecommendation(verdict);
        _originalContent = CurrentContent();
        _contentFlowDirection = DetectFlowDirection(_originalContent);
        _contentTextAlignment = AlignmentFor(_contentFlowDirection);
        _translationStatus = translator?.IsConfigured == true
            ? string.Empty
            : "Configure an AI provider in Settings to enable translation.";
    }

    public IReadOnlyList<AiTranslationLanguageOption> TranslationLanguages { get; } =
    [
        new(AppLanguage.Hebrew, "עברית"),
        new(AppLanguage.English, "English")
    ];

    public string DeviceName { get; }
    public string HardwareId { get; }
    public string Provider { get; }
    public string Manufacturer { get; }
    public string Category { get; }
    public string InstalledVersion { get; }
    public string InstalledDate { get; }
    public string CandidateVersion { get; }
    public string CandidateDate { get; }
    public string Source { get; }
    public string InstallKind { get; }
    public string Risk { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _rationale;
    public string LatestKnownVersion { get; }
    public string LatestKnownDate { get; }
    public string LatestKnownUrl { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _installedSuitability;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _candidateSuitability;
    public string RecommendedVersion { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _advisorNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _recommendation;

    [ObservableProperty]
    private AiTranslationLanguageOption _selectedTranslationLanguage =
        new(AppLanguage.Hebrew, "עברית");

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private bool _isTranslated;

    [ObservableProperty]
    private string _translationStatus;

    [ObservableProperty]
    private FlowDirection _contentFlowDirection;

    [ObservableProperty]
    private TextAlignment _contentTextAlignment;

    public string CopyText => string.Join(
        Environment.NewLine,
        new[]
        {
            $"Device: {DeviceName}",
            $"Hardware ID: {HardwareId}",
            $"Provider: {Provider}",
            $"Manufacturer: {Manufacturer}",
            $"Category: {Category}",
            string.Empty,
            "AI recommendation",
            $"Recommendation: {Recommendation}",
            $"Risk: {Risk}",
            $"Summary: {Summary}",
            $"Rationale: {Rationale}",
            string.Empty,
            "Installed driver",
            $"Version: {InstalledVersion}",
            $"Date: {InstalledDate}",
            $"Suitability: {InstalledSuitability}",
            string.Empty,
            "Candidate / latest",
            $"Version: {CandidateVersion}",
            $"Date: {CandidateDate}",
            $"Source: {Source}",
            $"Install kind: {InstallKind}",
            $"Suitability: {CandidateSuitability}",
            string.Empty,
            "Recommended version",
            RecommendedVersion,
            string.Empty,
            "AI advice",
            AdvisorNote,
            string.Empty,
            "Latest known",
            $"Version: {LatestKnownVersion}",
            $"Date: {LatestKnownDate}",
            $"URL: {LatestKnownUrl}"
        });

    [RelayCommand(CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync(CancellationToken cancellationToken)
    {
        if (_translator is null || !_translator.IsConfigured)
        {
            TranslationStatus = "Configure an AI provider in Settings to enable translation.";
            return;
        }

        var language = SelectedTranslationLanguage.Language;
        IsTranslating = true;
        TranslationStatus = language == AppLanguage.Hebrew
            ? "מתרגם..."
            : "Translating...";

        try
        {
            if (!_translationCache.TryGetValue(language, out var translated))
            {
                translated = await _translator.TranslateAsync(
                    _originalContent,
                    language,
                    cancellationToken).ConfigureAwait(true);

                if (translated is not null)
                {
                    _translationCache[language] = translated;
                }
            }

            if (translated is null)
            {
                TranslationStatus = language == AppLanguage.Hebrew
                    ? "לא ניתן היה להשלים את התרגום. נסה שוב."
                    : "The translation could not be completed. Try again.";
                return;
            }

            ApplyContent(translated);
            ContentFlowDirection = language == AppLanguage.Hebrew
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight;
            ContentTextAlignment = AlignmentFor(ContentFlowDirection);
            IsTranslated = true;
            TranslationStatus = language == AppLanguage.Hebrew
                ? "התרגום הושלם."
                : "Translation complete.";
        }
        catch (OperationCanceledException)
        {
            TranslationStatus = language == AppLanguage.Hebrew
                ? "התרגום בוטל."
                : "Translation cancelled.";
        }
        finally
        {
            IsTranslating = false;
        }
    }

    private bool CanTranslate() => !IsTranslating && _translator?.IsConfigured == true;

    [RelayCommand(CanExecute = nameof(CanShowOriginal))]
    private void ShowOriginal()
    {
        ApplyContent(_originalContent);
        ContentFlowDirection = DetectFlowDirection(_originalContent);
        ContentTextAlignment = AlignmentFor(ContentFlowDirection);
        IsTranslated = false;
        TranslationStatus = "Original AI response restored.";
    }

    private bool CanShowOriginal() => IsTranslated && !IsTranslating;

    partial void OnIsTranslatingChanged(bool value)
    {
        TranslateCommand.NotifyCanExecuteChanged();
        ShowOriginalCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTranslatedChanged(bool value) => ShowOriginalCommand.NotifyCanExecuteChanged();

    private AiResultTextContent CurrentContent() => new(
        Recommendation,
        Summary,
        Rationale,
        InstalledSuitability,
        CandidateSuitability,
        AdvisorNote);

    private void ApplyContent(AiResultTextContent content)
    {
        Recommendation = content.Recommendation;
        Summary = content.Summary;
        Rationale = content.Rationale;
        InstalledSuitability = content.InstalledSuitability;
        CandidateSuitability = content.CandidateSuitability;
        AdvisorNote = content.AdvisorNote;
    }

    private static FlowDirection DetectFlowDirection(AiResultTextContent content)
    {
        var text = string.Join(' ',
            content.Recommendation,
            content.Summary,
            content.Rationale,
            content.InstalledSuitability,
            content.CandidateSuitability,
            content.AdvisorNote);

        return text.Any(character => character is >= '\u0590' and <= '\u05FF')
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    private static TextAlignment AlignmentFor(FlowDirection flowDirection) =>
        flowDirection == FlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;

    private static string EmptyFallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "No detail provided." : value;

    private static string BuildRecommendation(AiVerdict verdict)
    {
        if (!verdict.IsGenuinelyNewer)
        {
            return "Do not install";
        }

        return verdict.Risk switch
        {
            AiRiskLevel.Safe => "Recommended",
            AiRiskLevel.Caution => "Use caution",
            AiRiskLevel.HighRisk => "Avoid for now",
            _ => "Not enough evidence"
        };
    }
}

public sealed record AiTranslationLanguageOption(AppLanguage Language, string Label);
