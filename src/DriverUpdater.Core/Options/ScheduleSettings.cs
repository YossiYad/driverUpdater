using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class ScheduleSettings
{
    public const string SectionName = "Schedule";

    public ScheduleMode Mode { get; set; } = ScheduleMode.Manual;

    public ScheduleCadence Cadence { get; set; } = ScheduleCadence.Weekly;

    public TimeOnly TimeOfDay { get; set; } = new(9, 0);

    public DayOfWeek DayOfWeek { get; set; } = System.DayOfWeek.Monday;

    // Which drivers an unattended ScanAndUpdate run is allowed to install. The device list
    // itself lives in the auto-update selection store, not here.
    public AutoUpdateScope AutoUpdateScope { get; set; } = AutoUpdateScope.AllDrivers;

    // Only read for AutoUpdateScope.AiRecommended: the highest risk rating the AI may hand
    // back for an update to still be installed without anybody watching.
    public AiAutoUpdateRiskTolerance AiRiskTolerance { get; set; } = AiAutoUpdateRiskTolerance.SafeOnly;
}
