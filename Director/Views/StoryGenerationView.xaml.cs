using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Director.ViewModels;

namespace Director.Views;

public partial class StoryGenerationView : UserControl
{
    public StoryGenerationView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private Window? _window;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Closing += OnWindowClosing;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.Closing -= OnWindowClosing;
            _window = null;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not StoryGenerationViewModel { IsBusy: true })
        {
            return;
        }

        var result = MessageBox.Show(
            "Sahne uretimi devam ediyor. Uygulama kapatilirse mevcut paket iptal olacak, ancak daha once kaydedilen paketler korunacaktir. Kapatmak istiyor musunuz?",
            "Sahne Uretimi Devam Ediyor",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        e.Cancel = result != MessageBoxResult.Yes;
    }
}
