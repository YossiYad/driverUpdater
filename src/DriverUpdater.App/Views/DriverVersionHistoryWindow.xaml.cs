using System.Windows;
using DriverUpdater.App.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class DriverVersionHistoryWindow : FluentWindow
{
    private readonly DriverVersionHistoryViewModel _viewModel;

    public DriverVersionHistoryWindow(DriverVersionHistoryViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
