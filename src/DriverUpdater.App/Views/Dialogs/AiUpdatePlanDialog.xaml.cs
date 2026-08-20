using System.Windows;
using DriverUpdater.App.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views.Dialogs;

public partial class AiUpdatePlanDialog : FluentWindow
{
    public AiUpdatePlanViewModel ViewModel { get; }

    public AiUpdatePlanDialog(AiUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitializeComponent();
        ViewModel = new AiUpdatePlanViewModel(plan);
        DataContext = ViewModel;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
