using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class AiSettings
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.Off;

    public AppLanguage ResponseLanguage { get; set; } = AppLanguage.English;

    public string GeminiApiKey { get; set; } = string.Empty;

    public List<string> GeminiApiKeys { get; set; } = new();

    public string GeminiModel { get; set; } = "gemini-2.5-flash";

    public bool EnableWebSearch { get; set; } = true;

    public int GeminiDailyRequestLimit { get; set; }

    public bool ShowAiScanUsageWarning { get; set; } = true;

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "llama3.1";

    public IReadOnlyList<string> GetGeminiApiKeys()
    {
        var keys = new List<string>();

        AddIfUnique(GeminiApiKey);
        foreach (var key in GeminiApiKeys ?? new List<string>())
        {
            AddIfUnique(key);
        }

        return keys;

        void AddIfUnique(string? key)
        {
            var normalized = key?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized)
                && !keys.Contains(normalized, StringComparer.Ordinal))
            {
                keys.Add(normalized);
            }
        }
    }
}
