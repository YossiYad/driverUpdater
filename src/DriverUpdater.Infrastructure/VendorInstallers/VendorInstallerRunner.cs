using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.VendorInstallers;

[SupportedOSPlatform("windows")]
public sealed class VendorInstallerRunner : IVendorInstallerRunner
{
    private readonly ILogger<VendorInstallerRunner> _logger;

    public VendorInstallerRunner(ILogger<VendorInstallerRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _logger.LogDebug("Running vendor installer {FileName} {Arguments}", fileName, arguments);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) { stdOut.AppendLine(e.Data); }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) { stdErr.AppendLine(e.Data); }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await ProcessCancellation.WaitForExitAsync(process, _logger, cancellationToken).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
