using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriverUpdater.App.Logging;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class LogsWindow : Window
{
    private readonly LogsViewModel _viewModel;

    public LogsWindow(LogsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Entries.CollectionChanged += OnEntriesCollectionChanged;
        _viewModel.ChatMessages.CollectionChanged += OnChatCollectionChanged;
        ScrollToEnd();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Entries.CollectionChanged -= OnEntriesCollectionChanged;
        _viewModel.ChatMessages.CollectionChanged -= OnChatCollectionChanged;
        _viewModel.Dispose();
    }

    private void OnChatCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ChatScroll.ScrollToEnd();
            }
            catch
            {
                // Auto-scroll is a nicety; never let it crash the UI.
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel.AutoScroll)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (LogsGrid.Items.Count == 0)
        {
            return;
        }

        var lastItem = LogsGrid.Items[LogsGrid.Items.Count - 1];
        if (lastItem is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(LogsGrid);
                scrollViewer?.ScrollToEnd();
            }
            catch
            {
                // Auto-scroll is a nicety; don't crash the UI if it fails during virtualization churn.
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
