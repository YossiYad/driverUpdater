using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.PnPUtil;

[SupportedOSPlatform("windows")]
public sealed class PnPUtilRunner : IPnPUtilRunner
{
    private readonly ILogger<PnPUtilRunner> _logger;

    public PnPUtilRunner(ILogger<PnPUtilRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);

        var pnputilPath = Path.Combine(WindowsSystemPathResolver.GetNativeSystemDirectory(), "pnputil.exe");

        var psi = new ProcessStartInfo
        {
            FileName = pnputilPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _logger.LogDebug("Running pnputil {Arguments}", arguments);

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

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
