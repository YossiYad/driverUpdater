namespace DriverUpdater.EndToEnd.Tests.Harness;

/// <summary>
/// An isolated on-disk directory for one end-to-end test. Every store under test writes its
/// real files here, so assertions can inspect what the app actually persisted.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "DriverUpdater.E2E",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts) =>
        System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Root))
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
        }
        catch (System.IO.IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
