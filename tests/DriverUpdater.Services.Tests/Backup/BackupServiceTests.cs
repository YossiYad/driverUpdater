using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Backup;

public class BackupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly BackupSettings _settings;
    private readonly ConstantOptionsMonitor<BackupSettings> _settingsMonitor;
    private readonly TestClock _clock;

    public BackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DriverUpdaterBackupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _settings = new BackupSettings { RootPath = _tempRoot, RetentionDays = 30 };
        _settingsMonitor = new ConstantOptionsMonitor<BackupSettings>(_settings);
        _clock = new TestClock(new DateTimeOffset(2026, 5, 27, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task BackupDriverAsync_returns_failure_when_inf_name_is_missing()
    {
        var fake = new FakePnPUtilRunner();
        var service = NewService(fake);
        var driver = DriverInfo.Empty("ROOT\\X");

        var result = await service.BackupDriverAsync(driver);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BACKUP_NO_INF");
        fake.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task BackupDriverAsync_calls_pnputil_with_export_driver_and_creates_destination()
    {
        var fake = new FakePnPUtilRunner();
        fake.Setup(args => args.Contains("/export-driver"), new ProcessResult(0, "ok", ""), pathInArgs =>
        {
            var sampleInf = Path.Combine(pathInArgs, "oem55.inf");
            File.WriteAllText(sampleInf, "; sample inf");
        });
        var service = NewService(fake);

        var result = await service.BackupDriverAsync(NewDriver("oem55.inf"));

        result.IsSuccess.Should().BeTrue();
        Directory.Exists(result.Value.BackupFolderPath).Should().BeTrue();
        result.Value.DriverInfName.Should().Be("oem55.inf");
        result.Value.SizeBytes.Should().BeGreaterThan(0);
        fake.Invocations.Should().ContainSingle();
        fake.Invocations[0].Should().Contain("/export-driver");
        fake.Invocations[0].Should().Contain("oem55.inf");
    }

    [Fact]
    public async Task BackupDriverAsync_returns_failure_when_pnputil_exits_nonzero()
    {
        var fake = new FakePnPUtilRunner();
        fake.Setup(_ => true, new ProcessResult(1, "", "permission denied"));
        var service = NewService(fake);

        var result = await service.BackupDriverAsync(NewDriver("oem55.inf"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BACKUP_PNPUTIL_FAILED");
        result.Error.Message.Should().Contain("permission denied");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_returns_failure_when_folder_missing()
    {
        var fake = new FakePnPUtilRunner();
        var service = NewService(fake);
        var artifact = new BackupArtifact("oem55.inf", "Foo", Path.Combine(_tempRoot, "does-not-exist"), DateTimeOffset.UtcNow, 100);

        var result = await service.RestoreFromBackupAsync(artifact);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("BACKUP_NOT_FOUND");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_calls_pnputil_add_driver_for_each_inf()
    {
        var fake = new FakePnPUtilRunner();
        fake.Setup(_ => true, new ProcessResult(0, "", ""));
        var backupFolder = Path.Combine(_tempRoot, "test-backup");
        Directory.CreateDirectory(backupFolder);
        File.WriteAllText(Path.Combine(backupFolder, "oem55.inf"), "; one");
        File.WriteAllText(Path.Combine(backupFolder, "oem56.inf"), "; two");
        var artifact = new BackupArtifact("oem55.inf", "Foo", backupFolder, DateTimeOffset.UtcNow, 200);
        var service = NewService(fake);

        var result = await service.RestoreFromBackupAsync(artifact);

        result.IsSuccess.Should().BeTrue();
        fake.Invocations.Should().HaveCount(2);
        fake.Invocations.Should().OnlyContain(a => a.Contains("/add-driver") && a.Contains("/install"));
    }

    [Fact]
    public void PurgeBackupsOlderThan_removes_expired_timestamp_folders()
    {
        var oldFolder = Path.Combine(_tempRoot, "20240101T000000Z");
        var newFolder = Path.Combine(_tempRoot, "20260520T000000Z");
        Directory.CreateDirectory(Path.Combine(oldFolder, "device-a"));
        Directory.CreateDirectory(Path.Combine(newFolder, "device-b"));
        Directory.SetCreationTimeUtc(oldFolder, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetCreationTimeUtc(newFolder, new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc));

        var service = NewService(new FakePnPUtilRunner());
        var removed = service.PurgeBackupsOlderThan(TimeSpan.FromDays(30));

        removed.Should().Be(1);
        Directory.Exists(oldFolder).Should().BeFalse();
        Directory.Exists(newFolder).Should().BeTrue();
    }

    [Theory]
    [InlineData("Intel(R) HD Graphics 5500", "ROOT\\X", "Intel(R)_HD_Graphics_5500")]
    [InlineData("  ", "FALLBACK", "FALLBACK")]
    [InlineData("Plain Name", "F", "Plain_Name")]
    public void SanitizeFolderName_strips_invalid_chars_and_spaces(string device, string fallback, string expected)
    {
        BackupService.SanitizeFolderName(device, fallback).Should().Be(expected);
    }

    private BackupService NewService(IPnPUtilRunner runner) =>
        new(runner, _settingsMonitor, NullLogger<BackupService>.Instance, _clock);

    private static DriverInfo NewDriver(string infName) => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234\\3&1",
        HardwareId: "PCI\\VEN_8086&DEV_1234",
        DeviceName: "Sample Adapter",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel",
        CurrentVersion: new Version(1, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: infName,
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Net");

    private sealed class FakePnPUtilRunner : IPnPUtilRunner
    {
        public List<string> Invocations { get; } = new();
        private Func<string, bool> _matcher = _ => true;
        private ProcessResult _result = new(0, "", "");
        private Action<string>? _onMatch;

        public void Setup(Func<string, bool> matcher, ProcessResult result, Action<string>? onPathFromArgs = null)
        {
            _matcher = matcher;
            _result = result;
            _onMatch = onPathFromArgs;
        }

        public Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments);
            if (_matcher(arguments) && _onMatch is not null)
            {
                var parts = arguments.Split('"');
                if (parts.Length >= 4)
                {
                    _onMatch(parts[3]);
                }
            }
            return Task.FromResult(_result);
        }
    }

    private sealed class ConstantOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public ConstantOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public TestClock(DateTimeOffset utcNow) { _utcNow = utcNow; }
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
