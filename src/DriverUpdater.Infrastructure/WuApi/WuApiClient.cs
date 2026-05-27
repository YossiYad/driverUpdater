using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
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
