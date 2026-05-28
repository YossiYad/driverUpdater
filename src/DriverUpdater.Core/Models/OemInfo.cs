namespace DriverUpdater.Core.Models;

public sealed record OemInfo(
    OemVendor Vendor,
    string Manufacturer,
    string Model,
    string ToolName,
    string? ToolPath,
    Uri FallbackUrl)
{
    public bool ToolInstalled => !string.IsNullOrEmpty(ToolPath) && File.Exists(ToolPath);
}
