using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Processes;

internal static class ProcessCancellation
{
    internal static async Task WaitForExitAsync(
        Process process,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning(ex, "Could not stop cancelled child process {ProcessId}", process.Id);
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process already exited or was never associated with a native handle.
            }

            throw;
        }
    }
}
