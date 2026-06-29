using System.IO;
using System.Windows;
using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DriverUpdater.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DriverUpdater",
            "Logs");
        Directory.CreateDirectory(logDirectory);

        var inMemoryLogSink = new InMemoryLogSink();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Surface the full AI verification trail (prompt, payload, raw response,
            // per-verdict detail) without turning on Debug for the whole app.
            .MinimumLevel.Override("DriverUpdater.Services.Ai", Serilog.Events.LogEventLevel.Debug)
            .MinimumLevel.Override("DriverUpdater.App.ViewModels.MainViewModel", Serilog.Events.LogEventLevel.Debug)
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "driverupdater-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true)
            .WriteTo.Sink(inMemoryLogSink)
            .Enrich.FromLogContext()
            .CreateLogger();

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DriverUpdater",
            "settings.json");

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(builder =>
            {
                builder.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.Configure<CatalogSettings>(context.Configuration.GetSection(CatalogSettings.SectionName));
                services.Configure<BackupSettings>(context.Configuration.GetSection(BackupSettings.SectionName));
                services.Configure<HistorySettings>(context.Configuration.GetSection(HistorySettings.SectionName));
                services.Configure<ScheduleSettings>(context.Configuration.GetSection(ScheduleSettings.SectionName));
                services.Configure<LanguageSettings>(context.Configuration.GetSection(LanguageSettings.SectionName));
                services.Configure<UpdaterSettings>(context.Configuration.GetSection(UpdaterSettings.SectionName));
                services.Configure<ScraperSettings>(context.Configuration.GetSection(ScraperSettings.SectionName));
                services.Configure<AiSettings>(context.Configuration.GetSection(AiSettings.SectionName));
                services.AddDriverUpdaterApp(inMemoryLogSink);
            })
            .Build();

        await _host.StartAsync();

        try
        {
            await _host.Services.GetRequiredService<IHistoryRepository>().InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "History repository initialization failed; install history will not be recorded");
        }

        var scheduledMode = ScheduledLaunch.FromCommandLine();
        if (scheduledMode != ScheduledLaunchMode.None)
        {
            await RunScheduledAsync(scheduledMode);
            Shutdown();
            return;
        }

        var languageSettings = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LanguageSettings>>().Value;
        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        localization.ApplyLanguage(languageSettings.Language);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        var updater = _host.Services.GetRequiredService<IAppUpdater>();
        _ = updater.CheckAndApplyAsync();
    }

    private async Task RunScheduledAsync(ScheduledLaunchMode mode)
    {
        // Launched by the Windows scheduled task (--scheduled-scan / --scheduled-update):
        // run headless, never show the main window, then exit.
        try
        {
            Log.Information("Starting scheduled run in {Mode} mode", mode);
            var runner = _host!.Services.GetRequiredService<IScheduledScanRunner>();
            await runner.RunAsync(installUpdates: mode == ScheduledLaunchMode.ScanAndUpdate);
            Log.Information("Scheduled run finished");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Scheduled run failed");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
