using System.IO;
using System.Reflection;
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
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // The XAML resource is a build-time placeholder; the running assembly's version is
        // always accurate, so override it here instead of hand-editing the string per release.
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            Resources["App.Version"] = $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        var logDirectory = LogCleanupService.DefaultLogDirectory();
        Directory.CreateDirectory(logDirectory);

        var inMemoryLogSink = new InMemoryLogSink();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // All DriverUpdater namespaces log at Debug so skip/miss decisions in every
            // source and pipeline step land in the file; framework noise stays at
            // Information+ (HttpClient is pushed further down to Warning).
            .MinimumLevel.Override("DriverUpdater", Serilog.Events.LogEventLevel.Debug)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.Debug()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "driverupdater-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
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
                services.Configure<LogCleanupSettings>(context.Configuration.GetSection(LogCleanupSettings.SectionName));
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
        _host.Services.GetRequiredService<AiQuotaNotificationService>().Start();

        var savedSettings = await _host.Services
            .GetRequiredService<ISettingsStore>()
            .LoadAsync()
            .ConfigureAwait(true);
        var applicationBehavior = _host.Services.GetRequiredService<ApplicationBehaviorState>();
        applicationBehavior.Apply(savedSettings.Application);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        var backgroundLaunch = e.Args.Any(
            argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        if (backgroundLaunch && applicationBehavior.ShouldStartHidden)
        {
            var startedInBackground = await mainWindow.StartInBackgroundAsync();
            if (!startedInBackground)
            {
                mainWindow.Show();
                await ShowWelcomeIfNeededAsync(mainWindow, languageSettings.Language);
            }
        }
        else
        {
            mainWindow.Show();
            await ShowWelcomeIfNeededAsync(mainWindow, languageSettings.Language);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestApplicationExit();
        }
        base.OnSessionEnding(e);
    }

    private async Task ShowWelcomeIfNeededAsync(Window owner, DriverUpdater.Core.Models.AppLanguage language)
    {
        try
        {
            var settingsStore = _host!.Services.GetRequiredService<ISettingsStore>();
            var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
            if (!WelcomeExperience.ShouldShow(settings))
            {
                return;
            }

            var welcomeWindow = new WelcomeWindow(language, settings.Onboarding.ShowOnStartup)
            {
                Owner = owner
            };
            welcomeWindow.OpenAiSettingsRequested += (_, _) =>
            {
                var settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
                settingsWindow.SelectAiTab();
                settingsWindow.Owner = welcomeWindow;
                settingsWindow.ShowDialog();
            };
            welcomeWindow.OpenAutomaticUpdateSettingsRequested += (_, _) =>
            {
                var settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
                settingsWindow.SelectAboutTab();
                settingsWindow.Owner = welcomeWindow;
                settingsWindow.ShowDialog();
            };
            welcomeWindow.ShowDialog();

            // A settings window can be opened from the guide. Reload before writing the
            // onboarding marker so a save made in that window is not overwritten here.
            var latestSettings = await settingsStore.LoadAsync().ConfigureAwait(true);
            WelcomeExperience.RecordChoice(latestSettings, welcomeWindow.ShowOnStartup);
            await settingsStore.SaveAsync(latestSettings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The welcome guide could not be shown or saved");
        }
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
