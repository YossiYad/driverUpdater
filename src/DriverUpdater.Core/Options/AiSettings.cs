using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class AiSettings
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.Off;

    public string GeminiApiKey { get; set; } = string.Empty;

    public string GeminiModel { get; set; } = "gemini-2.0-flash";

    public bool EnableWebSearch { get; set; } = true;

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string OllamaModel { get; set; } = "llama3.1";
}
