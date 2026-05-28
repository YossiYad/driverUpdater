using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IPnPUtilRunner
{
    Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default);
}
