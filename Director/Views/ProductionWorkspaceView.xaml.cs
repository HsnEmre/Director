using System.Windows;
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
        if (e.Source is not TabControl tabControl || DataContext is not ProductionWorkspaceViewModel viewModel)
        {
            return;
        }

        if (tabControl.SelectedIndex == 1)
        {
            await viewModel.EnsureVideoModelsLoadedAsync();
        }
        else
        {
            VideoPreviewElement.Stop();
        }
    }

    private void OnVideoMediaOpened(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProductionWorkspaceViewModel viewModel)
        {
            var duration = VideoPreviewElement.NaturalDuration.HasTimeSpan
                ? VideoPreviewElement.NaturalDuration.TimeSpan.TotalSeconds
                : (double?)null;
            viewModel.NotifyVideoPreviewOpened(duration);
        }

        VideoPreviewElement.Play();
    }

    private void OnVideoMediaEnded(object sender, RoutedEventArgs e)
    {
        VideoPreviewElement.Stop();
    }

    private void OnVideoMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (DataContext is ProductionWorkspaceViewModel viewModel)
        {
            viewModel.NotifyVideoPreviewFailed(e.ErrorException?.Message ?? "MediaElement video dosyasini acamadi.");
        }
    }
}
