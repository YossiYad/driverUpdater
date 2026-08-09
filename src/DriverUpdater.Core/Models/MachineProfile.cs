using System.Text;

namespace DriverUpdater.Core.Models;

/// <summary>
/// What this particular PC is. A driver recommendation is only as good as the machine it is
/// made for: the same GPU wants a different driver on an OEM laptop than on a desktop build,
/// and a vendor's "latest" is often published per model rather than per chip. Every AI prompt
/// that recommends or judges an update carries this so the answer is about the user's machine
/// instead of the device class in general.
/// </summary>
public sealed record MachineProfile(
    string? SystemManufacturer,
    string? SystemModel,
    string? SystemFamily,
    string? SystemSku,
    string? BaseBoardManufacturer,
    string? BaseBoardProduct,
    string? BaseBoardVersion,
    string? BiosManufacturer,
    string? BiosVersion,
    DateOnly? BiosReleaseDate,
    string? ProcessorName,
    int? ProcessorCores,
    int? ProcessorLogicalProcessors,
    IReadOnlyList<string> GraphicsAdapters,
    long? TotalPhysicalMemoryBytes,
    string? OperatingSystemName,
    string? OperatingSystemVersion,
    string? OperatingSystemBuild,
    string? OperatingSystemArchitecture,
    string? SystemType)
{
    public static MachineProfile Empty { get; } = new(
        null, null, null, null,
        null, null, null,
        null, null, null,
        null, null, null,
        Array.Empty<string>(),
        null,
        null, null, null, null, null);

    public bool HasAnyDetail => !string.IsNullOrWhiteSpace(Describe());

    /// <summary>
    /// A compact, prompt-ready block. Only lines whose values were actually read are emitted,
    /// so a machine that hides a field does not teach the model to invent one.
    /// </summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        AppendLine(sb, "System", JoinParts(
            Combine(SystemManufacturer, SystemModel),
            Label("family", SystemFamily),
            Label("SKU", SystemSku),
            Label("type", SystemType)));
        AppendLine(sb, "Motherboard", JoinParts(
            Combine(BaseBoardManufacturer, BaseBoardProduct),
            Label("revision", BaseBoardVersion)));
        AppendLine(sb, "BIOS", JoinParts(
            Combine(BiosManufacturer, BiosVersion),
            Label("released", BiosReleaseDate?.ToString("yyyy-MM-dd"))));
        AppendLine(sb, "CPU", JoinParts(
            ProcessorName,
            ProcessorCores is { } cores ? $"{cores} cores" : null,
            ProcessorLogicalProcessors is { } threads ? $"{threads} threads" : null));
        if (GraphicsAdapters.Count > 0)
        {
            AppendLine(sb, "GPU", string.Join(", ", GraphicsAdapters));
        }
        if (TotalPhysicalMemoryBytes is { } memory and > 0)
        {
            AppendLine(sb, "Memory", $"{Math.Round(memory / 1024d / 1024d / 1024d)} GB");
        }
        AppendLine(sb, "Windows", JoinParts(
            OperatingSystemName,
            Label("version", OperatingSystemVersion),
            Label("build", OperatingSystemBuild),
            OperatingSystemArchitecture));

        return sb.ToString().TrimEnd();
    }

    private static void AppendLine(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("- ").Append(label).Append(": ").AppendLine(value);
        }
    }

    private static string? Combine(string? first, string? second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        // Manufacturers often repeat themselves in the model string ("ASUS ASUS TUF...").
        return right.StartsWith(left, StringComparison.OrdinalIgnoreCase) ? right : $"{left} {right}";
    }

    private static string? Label(string name, string? value) =>
        Normalize(value) is { } text ? $"{name}: {text}" : null;

    private static string? JoinParts(params string?[] parts)
    {
        var kept = parts.Select(Normalize).Where(part => part is not null).ToArray();
        if (kept.Length == 0)
        {
            return null;
        }

        return kept.Length == 1
            ? kept[0]
            : $"{kept[0]} ({string.Join(", ", kept.Skip(1))})";
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        // WMI hands back these placeholders on machines whose OEM never filled the field in.
        return trimmed is "To Be Filled By O.E.M."
            or "To be filled by O.E.M."
            or "System manufacturer"
            or "System Product Name"
            or "Default string"
            or "None"
            or "Not Applicable"
            or "O.E.M."
            ? null
            : trimmed;
    }
}
