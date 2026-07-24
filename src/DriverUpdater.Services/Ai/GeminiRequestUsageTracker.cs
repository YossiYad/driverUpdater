using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Ai;

public sealed class GeminiRequestUsageTracker
{
    private const string UsageFileName = "gemini-request-usage.json";
    private static readonly TimeZoneInfo PacificTimeZone = FindPacificTimeZone();
    private readonly object _sync = new();
    private readonly ILogger<GeminiRequestUsageTracker> _logger;
    private readonly string _usagePath;
    private readonly TimeProvider _clock;
    private UsageState _state;

    public GeminiRequestUsageTracker(ILogger<GeminiRequestUsageTracker> logger)
        : this(
            logger,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DriverUpdater",
                UsageFileName),
            TimeProvider.System)
    {
    }

    internal GeminiRequestUsageTracker(
        ILogger<GeminiRequestUsageTracker> logger,
        string usagePath,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(usagePath);
        ArgumentNullException.ThrowIfNull(clock);
        _logger = logger;
        _usagePath = usagePath;
        _clock = clock;
        _state = LoadState();
    }

    public int GetRequestsToday(string model)
    {
        lock (_sync)
        {
            ResetForNewDayIfNeeded();
            return _state.RequestsByModel.TryGetValue(NormalizeModel(model), out var count)
                ? count
                : 0;
        }
    }

    public void RecordRequest(string model)
    {
        lock (_sync)
        {
            ResetForNewDayIfNeeded();
            var normalizedModel = NormalizeModel(model);
            _state.RequestsByModel[normalizedModel] = GetRequestsTodayUnsafe(normalizedModel) + 1;
            SaveState();
        }
    }

    private int GetRequestsTodayUnsafe(string normalizedModel) =>
        _state.RequestsByModel.TryGetValue(normalizedModel, out var count) ? count : 0;

    private void ResetForNewDayIfNeeded()
    {
        var today = GetPacificDate(_clock.GetUtcNow());
        if (_state.PacificDate == today)
        {
            return;
        }

        _state = new UsageState
        {
            PacificDate = today,
            RequestsByModel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        };
        SaveState();
    }

    private UsageState LoadState()
    {
        try
        {
            if (File.Exists(_usagePath))
            {
                var json = File.ReadAllText(_usagePath);
                var state = JsonSerializer.Deserialize<UsageState>(json);
                if (state is not null)
                {
                    state.RequestsByModel = new Dictionary<string, int>(
                        state.RequestsByModel ?? new Dictionary<string, int>(),
                        StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load local Gemini request usage from {Path}", _usagePath);
        }

        return new UsageState
        {
            PacificDate = GetPacificDate(_clock.GetUtcNow()),
            RequestsByModel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private void SaveState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_usagePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _usagePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, _usagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save local Gemini request usage to {Path}", _usagePath);
        }
    }

    internal static DateOnly GetPacificDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, PacificTimeZone).DateTime);

    private static string NormalizeModel(string model) =>
        string.IsNullOrWhiteSpace(model) ? "unknown" : model.Trim();

    private static TimeZoneInfo FindPacificTimeZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private sealed class UsageState
    {
        public DateOnly PacificDate { get; set; }
        public Dictionary<string, int> RequestsByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
