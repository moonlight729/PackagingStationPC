using System.Windows;
using PackagingStationPC.ViewModels;

namespace PackagingStationPC;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ((MainViewModel)DataContext).InitializeAsync();
    }
}
