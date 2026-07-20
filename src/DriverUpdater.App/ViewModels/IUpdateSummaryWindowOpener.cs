using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IUpdateSummaryWindowOpener
{
    void Open(UpdateVerificationReport report, AppLanguage language);
}
