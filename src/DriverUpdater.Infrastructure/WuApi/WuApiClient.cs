using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.WuApi;

[SupportedOSPlatform("windows")]
public sealed class WuApiClient : IWuApiClient
{
    private const string SessionProgId = "Microsoft.Update.Session";
    private const string SearchCriteria = "Type='Driver' AND IsInstalled=0";
    private const int ServerSelectionWindowsUpdate = 2;

    private readonly ILogger<WuApiClient> _logger;

    public WuApiClient(ILogger<WuApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var records = await Task.Run(() => Search(cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    public async Task<Result<WuInstallResult>> DownloadAndInstallAsync(
        string updateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updateId);

        return await Task.Run(() => DownloadAndInstall(updateId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private Result<WuInstallResult> DownloadAndInstall(string updateId, CancellationToken cancellationToken)
    {
        var sessionType = Type.GetTypeFromProgID(SessionProgId)
            ?? throw new InvalidOperationException($"COM type '{SessionProgId}' is not registered.");

        var collectionType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")
            ?? throw new InvalidOperationException("COM type 'Microsoft.Update.UpdateColl' is not registered.");

        var tracked = new Stack<object>();
        try
        {
            dynamic? session = Activator.CreateInstance(sessionType);
            if (session is null)
            {
                return ResultError.From("WU_SESSION_FAILED", "Could not create Microsoft.Update.Session.");
            }
            Track(tracked, session);
            session.ClientApplicationID = "DriverUpdater";

            dynamic searcher = session.CreateUpdateSearcher();
            Track(tracked, searcher);
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            var criteria = $"UpdateID='{updateId.Replace("'", "''", StringComparison.Ordinal)}' AND Type='Driver'";
            _logger.LogInformation("Locating WU driver update {UpdateId}", updateId);
            dynamic searchResult = searcher.Search(criteria);
            Track(tracked, searchResult);

            dynamic foundUpdates = searchResult.Updates;
            Track(tracked, foundUpdates);
            int foundCount = (int)foundUpdates.Count;
            if (foundCount == 0)
            {
                return ResultError.From("WU_NOT_FOUND", $"WU update {updateId} not found or already installed.");
            }

            dynamic update = foundUpdates.Item(0);
            Track(tracked, update);
            string title = (string)update.Title;

            dynamic? collection = Activator.CreateInstance(collectionType);
            if (collection is null)
            {
                return ResultError.From("WU_COLLECTION_FAILED", "Could not create UpdateColl.");
            }
            Track(tracked, collection);
            collection.Add(update);

            cancellationToken.ThrowIfCancellationRequested();

            dynamic downloader = session.CreateUpdateDownloader();
            Track(tracked, downloader);
            downloader.Updates = collection;
            _logger.LogInformation("Downloading WU update '{Title}'", title);
            dynamic dlResult = downloader.Download();
            Track(tracked, dlResult);

            int dlResultCode = (int)dlResult.ResultCode;
            if (dlResultCode != 2 && dlResultCode != 3)
            {
                return ResultError.From("WU_DOWNLOAD_FAILED", $"Download result code {dlResultCode} for '{title}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            dynamic installer = session.CreateUpdateInstaller();
            Track(tracked, installer);
            installer.Updates = collection;
            _logger.LogInformation("Installing WU update '{Title}'", title);
            dynamic instResult = installer.Install();
            Track(tracked, instResult);

            int instResultCode = (int)instResult.ResultCode;
            int hResult = (int)instResult.HResult;
            bool rebootRequired = (bool)instResult.RebootRequired;

            if (instResultCode != 2 && instResultCode != 3)
            {
                return ResultError.From("WU_INSTALL_FAILED",
                    $"Install result code {instResultCode}, HRESULT 0x{hResult:X8} for '{title}'.");
            }

            return new WuInstallResult(hResult, rebootRequired, title);
        }
        finally
        {
            while (tracked.Count > 0)
            {
                var obj = tracked.Pop();
                try
                {
                    Marshal.FinalReleaseComObject(obj);
                }
                catch
                {
                }
            }
        }
    }

    private IReadOnlyList<WuDriverUpdateRecord> Search(CancellationToken cancellationToken)
    {
        var sessionType = Type.GetTypeFromProgID(SessionProgId)
            ?? throw new InvalidOperationException($"COM type '{SessionProgId}' is not registered.");

        var trackedComObjects = new Stack<object>();
        try
        {
            dynamic? session = Activator.CreateInstance(sessionType);
            if (session is null)
            {
                throw new InvalidOperationException("Failed to create Microsoft.Update.Session.");
            }
            Track(trackedComObjects, session);
            session.ClientApplicationID = "DriverUpdater";

            dynamic searcher = session.CreateUpdateSearcher();
            Track(trackedComObjects, searcher);
            searcher.ServerSelection = ServerSelectionWindowsUpdate;

            _logger.LogInformation("Querying Windows Update for driver updates");
            dynamic searchResult = searcher.Search(SearchCriteria);
            Track(trackedComObjects, searchResult);

            int resultCode = (int)searchResult.ResultCode;
            if (resultCode != 2 && resultCode != 3)
            {
                _logger.LogWarning("Windows Update search returned non-success result code {ResultCode}", resultCode);
            }

            dynamic updates = searchResult.Updates;
            Track(trackedComObjects, updates);

            int count = (int)updates.Count;
            _logger.LogInformation("Windows Update returned {Count} driver updates", count);

            var records = new List<WuDriverUpdateRecord>(count);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic update = updates.Item(i);
                Track(trackedComObjects, update);
                records.Add(MapToRecord(update, trackedComObjects));
            }

            return records;
        }
        finally
        {
            while (trackedComObjects.Count > 0)
            {
                var obj = trackedComObjects.Pop();
                try
                {
                    Marshal.FinalReleaseComObject(obj);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FinalReleaseComObject threw for {Type}", obj.GetType().Name);
                }
            }
        }
    }

    private static WuDriverUpdateRecord MapToRecord(dynamic update, Stack<object> tracked)
    {
        dynamic identity = update.Identity;
        Track(tracked, identity);

        string updateId = (string)identity.UpdateID;
        int revision = (int)identity.RevisionNumber;
        string title = (string)update.Title;

        string? driverHardwareId = TryGetString(update, "DriverHardwareID");
        string? driverModel = TryGetString(update, "DriverModel");
        string? driverManufacturer = TryGetString(update, "DriverManufacturer");
        string? driverProvider = TryGetString(update, "DriverProvider");
        DateOnly? driverVerDate = TryGetDateOnly(update, "DriverVerDate");
        long maxSize = TryGetLong(update, "MaxDownloadSize");

        string? downloadUrl = null;
        try
        {
            dynamic contents = update.DownloadContents;
            Track(tracked, contents);
            int contentCount = (int)contents.Count;
            if (contentCount > 0)
            {
                dynamic firstContent = contents.Item(0);
                Track(tracked, firstContent);
                downloadUrl = TryGetString(firstContent, "DownloadUrl");
            }
        }
        catch
        {
        }

        var kbList = new List<string>();
        try
        {
            dynamic kbCollection = update.KBArticleIDs;
            Track(tracked, kbCollection);
            int kbCount = (int)kbCollection.Count;
            for (int j = 0; j < kbCount; j++)
            {
                kbList.Add((string)kbCollection.Item(j));
            }
        }
        catch
        {
        }

        return new WuDriverUpdateRecord(
            UpdateId: updateId,
            RevisionNumber: revision,
            Title: title,
            DriverHardwareId: driverHardwareId,
            DriverModel: driverModel,
            DriverManufacturer: driverManufacturer,
            DriverProvider: driverProvider,
            DriverVerDate: driverVerDate,
            MaxDownloadSize: maxSize,
            DownloadUrl: downloadUrl,
            KbArticleIds: kbList);
    }

    private static void Track(Stack<object> tracked, object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            tracked.Push(value);
        }
    }

    private static string? TryGetString(dynamic obj, string memberName)
    {
        try
        {
            object? value = ((dynamic)obj).GetType().InvokeMember(
                memberName,
                System.Reflection.BindingFlags.GetProperty,
                null,
                obj,
                null);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static long TryGetLong(dynamic obj, string memberName)
    {
        try
        {
            object? value = ((dynamic)obj).GetType().InvokeMember(
                memberName,
                System.Reflection.BindingFlags.GetProperty,
                null,
                obj,
                null);
            return value is null ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0L;
        }
    }

    private static DateOnly? TryGetDateOnly(dynamic obj, string memberName)
    {
        try
        {
            object? value = ((dynamic)obj).GetType().InvokeMember(
                memberName,
                System.Reflection.BindingFlags.GetProperty,
                null,
                obj,
                null);
            return value is DateTime dt ? DateOnly.FromDateTime(dt) : null;
        }
        catch
        {
            return null;
        }
    }
}
