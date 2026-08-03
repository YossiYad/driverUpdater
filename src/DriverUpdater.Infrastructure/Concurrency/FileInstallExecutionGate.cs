using DriverUpdater.Core.Abstractions;

namespace DriverUpdater.Infrastructure.Concurrency;

public sealed class FileInstallExecutionGate : IInstallExecutionGate
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly string _lockPath;

    public FileInstallExecutionGate()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriverUpdater",
            "install-operation.lock"))
    {
    }

    internal FileInstallExecutionGate(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        _lockPath = lockPath;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
