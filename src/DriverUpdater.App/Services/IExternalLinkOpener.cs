namespace DriverUpdater.App.Services;

public interface IExternalLinkOpener
{
    bool Open(Uri uri);
}
