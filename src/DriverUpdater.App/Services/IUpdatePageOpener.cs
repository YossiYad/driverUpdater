using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Services;

public interface IUpdatePageOpener
{
    void Open(UpdateCandidate candidate);
}
