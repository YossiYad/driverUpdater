using System.Windows;
using DriverUpdater.App.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class ExcludedDriverSelectionWindow : FluentWindow
{
    private readonly ExcludedDriverSelectionViewModel _viewModel;

    public ExcludedDriverSelectionWindow(ExcludedDriverSelectionViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.SaveCompleted += OnSaveCompleted;
        Closed += (_, _) => _viewModel.SaveCompleted -= OnSaveCompleted;
    }

    private void OnSaveCompleted(object? sender, EventArgs e) => Close();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
