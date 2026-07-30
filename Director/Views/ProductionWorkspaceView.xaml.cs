using System.Windows.Controls;
using Director.ViewModels;

namespace Director.Views;

public partial class ProductionWorkspaceView : UserControl
{
    public ProductionWorkspaceView()
    {
        InitializeComponent();
    }

    private async void OnWorkspaceTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not TabControl { SelectedIndex: 1 } || DataContext is not ProductionWorkspaceViewModel viewModel)
        {
            return;
        }

        await viewModel.EnsureVideoModelsLoadedAsync();
    }
}
