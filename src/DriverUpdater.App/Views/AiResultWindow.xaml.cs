using System.Windows;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class AiResultWindow : Window
{
    private readonly AiResultViewModel _viewModel;

    public AiResultWindow(AiResultViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.CopyText);
        }
        catch
        {
            // Clipboard can occasionally fail under contention; swallow so we don't crash the UI.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
