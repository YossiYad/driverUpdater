using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IPowerShellInvoker
{
    Task<ProcessResult> InvokeAsync(string script, CancellationToken cancellationToken = default);
}
