using System.IO;
using System.Windows;
using System.Windows.Threading;
using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure;
using DriverUpdater.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DriverUpdater.App;

public partial class App : Application
{
    private IHost? _host;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await InitializeApplicationAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            MessageBox.Show(
                "DriverUpdater could not finish starting. See the log under ProgramData\\DriverUpdater\\Logs for details.",
                "DriverUpdater",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task InitializeApplicationAsync()
    {
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
                services.AddDriverUpdaterInfrastructure();
                services.AddDriverUpdaterServices();
                services.AddSingleton<IInstallConfirmation, DialogInstallConfirmation>();
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IUpdatePageOpener, UpdatePageOpener>();
                services.AddSingleton<IAppUpdater, VelopackAppUpdater>();
                services.AddSingleton<IHistoryWindowOpener, HistoryWindowOpener>();
                services.AddSingleton<ISettingsWindowOpener, SettingsWindowOpener>();
                services.AddSingleton<ILogsWindowOpener, LogsWindowOpener>();
                services.AddSingleton(inMemoryLogSink);
                services.AddSingleton<MainViewModel>();
                services.AddTransient<HistoryViewModel>();
                services.AddTransient<HistoryWindow>();
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<SettingsWindow>();
                services.AddTransient<LogsViewModel>();
                services.AddTransient<LogsWindow>();
                services.AddSingleton<MainWindow>();
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

        var languageSettings = _host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LanguageSettings>>().Value;
        var localization = _host.Services.GetRequiredService<ILocalizationService>();
        localization.ApplyLanguage(languageSettings.Language);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        var updater = _host.Services.GetRequiredService<IAppUpdater>();
        _ = updater.CheckAndApplyAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Application host shutdown failed");
        }
        finally
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        MessageBox.Show(
            "DriverUpdater encountered an unexpected error and must close. Details were written to the log.",
            "DriverUpdater",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-1);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled application-domain exception; terminating={IsTerminating}", e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved background task exception");
        e.SetObserved();
    }
}
