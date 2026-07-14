using System.Text;
using DriverUpdater.Core.Models;

namespace DriverUpdater.Services.Ai;

internal static class PostUpdateSummaryPromptBuilder
{
    public static string Build(
        IReadOnlyList<UpdateVerificationItem> items,
        bool isAfterRestart,
        AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(items);
        var languageInstruction = language == AppLanguage.Hebrew
            ? "Write the answer in clear, natural Hebrew."
            : "Write the answer in clear, natural English.";

        var sb = new StringBuilder();
        sb.AppendLine("You explain driver update results to an everyday computer user.");
        sb.AppendLine(languageInstruction);
        sb.AppendLine("Use plain text only, no Markdown, headings, bullets, or technical jargon.");
        sb.AppendLine("Write 2 to 5 short sentences. Start with the overall result, then mention only items that need attention.");
        sb.AppendLine("The Windows read-back results below are authoritative. Do not invent results, causes, or actions.");
        sb.AppendLine("Do not claim that AI directly inspected hardware. Explain that the app checked Windows and you are summarizing the result.");
        sb.Append("This check happened ").AppendLine(isAfterRestart ? "after the computer restarted." : "immediately after installation.");
        sb.AppendLine();

        foreach (var item in items)
        {
            sb.Append("Device: ").AppendLine(item.DeviceName);
            sb.Append("Type: ").AppendLine(item.Category.ToString());
            sb.Append("Verified result: ").AppendLine(item.Status.ToString());
            sb.Append("Before: ").AppendLine(Format(item.PreviousVersion, item.PreviousDate));
            sb.Append("Expected update: ").AppendLine(Format(item.ExpectedVersion, item.ExpectedDate));
            sb.Append("Windows now reports: ").AppendLine(Format(item.CurrentVersion, item.CurrentDate));
            if (!string.IsNullOrWhiteSpace(item.TechnicalDetail))
            {
                sb.Append("Installer detail: ").AppendLine(item.TechnicalDetail);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Format(Version? version, DateOnly? date) =>
        $"version {version?.ToString() ?? "unknown"}, date {date?.ToString("yyyy-MM-dd") ?? "unknown"}";
}
