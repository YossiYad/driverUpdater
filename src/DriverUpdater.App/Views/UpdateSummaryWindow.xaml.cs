using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class UpdateSummaryWindow : FluentWindow
{
    private readonly UpdateSummaryViewModel _viewModel;

    public UpdateSummaryWindow(UpdateSummaryViewModel viewModel, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        FlowDirection = language == AppLanguage.Hebrew
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.CopyText);
        }
        catch
        {
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
