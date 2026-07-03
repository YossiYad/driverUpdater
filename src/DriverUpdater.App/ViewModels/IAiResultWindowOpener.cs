using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IAiResultWindowOpener
{
    void Open(DriverInfo driver, UpdateCandidate? candidate, AiVerdict verdict);
}
