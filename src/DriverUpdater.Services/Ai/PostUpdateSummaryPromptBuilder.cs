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
        sb.AppendLine("The Windows read-back results for driver rows and package version results for software rows are authoritative.");
        sb.AppendLine("Do not claim that AI directly inspected hardware. Explain that the app checked Windows and you are summarizing the result.");
        sb.AppendLine("ManualActionRequired is not a failed installation. It means the app found only an advisory vendor page and could not resolve a safe in-app installer. No external page was opened.");
        sb.AppendLine("A successful installer process is not the same as a verified driver change.");
        sb.AppendLine("For a software package row, VerifiedUpdated means the app re-queried the installed package version and confirmed the target version. Do not describe it as a Windows driver read-back.");
        sb.AppendLine("For a failed software package row, explain that the installer returned but the package version check still showed the old version.");
        sb.AppendLine("When Installer process result is Succeeded but Verified result is NotUpdated, say that the installer ran but Windows did not show a driver change. Do not say that no automatic installation was attempted.");
        sb.AppendLine("Say that no automatic installation was attempted only for ManualActionRequired items.");
        sb.AppendLine("For advisory vendor-page results, do not claim that an update definitely exists and do not present a date-based placeholder as a real driver version.");
        sb.AppendLine("Say that a component may already have been current only when the version Windows now reports is the same as the Expected update version. When it is not, the package was added to the driver store and Windows kept the previous driver; say that instead.");
        sb.AppendLine("Do not recommend another update unless the evidence explicitly says one is still needed.");
        sb.AppendLine("If an installer reported a warning or non-zero exit but Windows now reports a changed driver, describe the verified Windows result and mention the installer warning only briefly.");
        sb.AppendLine("Never describe an Installer process result of Failed as a warning unless the Windows read-back confirms the new driver.");
        sb.AppendLine("Any count you state must match the devices listed below. Do not say how many succeeded or need attention unless that number matches the list.");
        sb.AppendLine("Only say that the previous driver remains active when the Windows read-back version matches the Before version.");
        sb.Append("This check happened ").AppendLine(isAfterRestart ? "after the computer restarted." : "immediately after installation.");
        sb.AppendLine();

        foreach (var item in items)
        {
            sb.Append("Device: ").AppendLine(item.DeviceName);
            sb.Append("Type: ").AppendLine(item.Category.ToString());
            sb.Append("Verified result: ").AppendLine(item.Status.ToString());
            sb.Append("Installer process result: ").AppendLine(item.InstallerStatus.ToString());
            sb.Append("Delivery type: ").AppendLine(item.InstallKind.ToString());
            sb.Append("Verification scope: ").AppendLine(item.IsSoftwarePackage ? "Vendor software package" : "Windows driver");
            sb.Append("Evidence confidence: ").AppendLine(item.Confidence.ToString());
            sb.Append("Before: ").AppendLine(
                Format(item.PreviousVersionLabel ?? item.PreviousVersion?.ToString(), item.PreviousDate));
            sb.Append("Expected update: ").AppendLine(
                Format(item.ExpectedVersionLabel ?? item.ExpectedVersion?.ToString(), item.ExpectedDate));
            sb.Append(item.IsSoftwarePackage ? "Package check now reports: " : "Windows now reports: ")
                .AppendLine(Format(item.CurrentVersionLabel ?? item.CurrentVersion?.ToString(), item.CurrentDate));
            if (!string.IsNullOrWhiteSpace(item.TechnicalDetail))
            {
                sb.Append("Installer detail: ").AppendLine(item.TechnicalDetail);
            }
            if (item.ActionUrl is not null)
            {
                sb.Append("Reference URL, not opened by the app: ").AppendLine(item.ActionUrl.AbsoluteUri);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Format(Version? version, DateOnly? date) =>
        Format(version?.ToString(), date);

    private static string Format(string? version, DateOnly? date) =>
        $"version {version ?? "unknown"}, date {date?.ToString("yyyy-MM-dd") ?? "unknown"}";
}
