using System.ComponentModel;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly ApplicationBehaviorState _applicationBehavior;
    private readonly NotificationAreaService _notificationArea;
    private readonly ILogger<MainWindow> _logger;
    private Task? _initializationTask;
    private bool _exitRequested;
    private bool _backgroundHintShown;

    public MainWindow(
        MainViewModel viewModel,
        ApplicationBehaviorState applicationBehavior,
        NotificationAreaService notificationArea,
        ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(applicationBehavior);
        ArgumentNullException.ThrowIfNull(notificationArea);
        ArgumentNullException.ThrowIfNull(logger);
        InitializeComponent();
        _viewModel = viewModel;
        _applicationBehavior = applicationBehavior;
        _notificationArea = notificationArea;
        _logger = logger;
        DataContext = viewModel;
        _viewModel.ScrollToRowRequested += OnScrollToRowRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        _notificationArea.OpenRequested += OnNotificationAreaOpenRequested;
        _notificationArea.ExitRequested += OnNotificationAreaExitRequested;

        if (!IsRunningAsAdministrator())
        {
            AdminBadge.Background = System.Windows.Media.Brushes.DarkOrange;
            AdminBadgeText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "App.NotElevated");
            viewModel.StatusText = "Warning: not running as administrator. Most operations will fail.";
        }
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Keep selection while interacting with a row, and long enough for the toolbar's
        // selection command to consume SelectedItems. Every other click clears stale rows,
        // including clicks outside the grid and in its empty area/header.
        var row = FindAncestor<DataGridRow>(source);
        var button = FindAncestor<ButtonBase>(source);
        if (row is null && !ReferenceEquals(button, UpdateSelectedButton))
        {
            DriversGrid.UnselectAll();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OnScrollToRowRequested(object? sender, DriverRowViewModel row)
    {
        // The pipeline raises this each time it moves on to the next install target. We
        // jump the grid to that row so the user always sees the active update without
        // having to scroll through the 250+ rows of unaffected drivers.
        if (DriversGrid.Items.Contains(row))
        {
            DriversGrid.ScrollIntoView(row);
            DriversGrid.SelectedItem = row;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDriverChatVisible)
            && !_viewModel.IsDriverChatVisible)
        {
            ResetDriverChatColumns();
        }
    }

    private void ResetDriverChatColumns()
    {
        DriversColumn.Width = new GridLength(1, GridUnitType.Star);
        DriverChatColumn.Width = GridLength.Auto;
    }

    private void OnDriverSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedButton.Visibility = DriversGrid.SelectedItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await EnsureInitializedAsync().ConfigureAwait(true);
    }

    public async Task<bool> StartInBackgroundAsync()
    {
        ShowInTaskbar = false;
        try
        {
            _notificationArea.Show(showHint: false);
        }
        catch (Exception ex)
        {
            ShowInTaskbar = true;
            _logger.LogError(
                ex,
                "Could not create the Windows notification area icon; opening the main window instead");
            return false;
        }
        _logger.LogInformation("DriverUpdater started hidden in the Windows notification area");
        await EnsureInitializedAsync().ConfigureAwait(true);
        return true;
    }

    public void RequestApplicationExit()
    {
        _exitRequested = true;
    }

    private Task EnsureInitializedAsync() =>
        _initializationTask ??= _viewModel.InitializeAsync();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested
            || _applicationBehavior.CloseBehavior == WindowCloseBehavior.ExitApplication)
        {
            _exitRequested = true;
            _notificationArea.Hide();
            _logger.LogInformation("Main window close requested: exiting DriverUpdater completely");
            return;
        }

        try
        {
            _notificationArea.Show(showHint: !_backgroundHintShown);
        }
        catch (Exception ex)
        {
            _exitRequested = true;
            _logger.LogError(
                ex,
                "Could not create the Windows notification area icon; exiting instead of hiding the app");
            return;
        }

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
        _backgroundHintShown = true;
        _logger.LogInformation(
            "Main window close requested: DriverUpdater remains active in the Windows notification area");
    }

    private void OnNotificationAreaOpenRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _notificationArea.Hide();
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            _logger.LogInformation("DriverUpdater restored from the Windows notification area");
        });
    }

    private void OnNotificationAreaExitRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _exitRequested = true;
            _logger.LogInformation("Exit requested from the Windows notification area");
            Application.Current.Shutdown();
        });
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.ScrollToRowRequested -= OnScrollToRowRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notificationArea.OpenRequested -= OnNotificationAreaOpenRequested;
        _notificationArea.ExitRequested -= OnNotificationAreaExitRequested;
        _notificationArea.Dispose();
        if (_exitRequested && !Application.Current.Dispatcher.HasShutdownStarted)
        {
            Application.Current.Shutdown();
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
