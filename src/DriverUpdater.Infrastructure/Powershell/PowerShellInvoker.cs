using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Processes;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Powershell;

[SupportedOSPlatform("windows")]
public sealed class PowerShellInvoker : IPowerShellInvoker
{
    private readonly ILogger<PowerShellInvoker> _logger;

    public PowerShellInvoker(ILogger<PowerShellInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<ProcessResult> InvokeAsync(string script, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var psi = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        _logger.LogDebug("Running PowerShell: {Script}", script);

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

    private static string ResolvePowerShellPath()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var legacyPath = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(legacyPath) ? legacyPath : "powershell.exe";
    }
}
