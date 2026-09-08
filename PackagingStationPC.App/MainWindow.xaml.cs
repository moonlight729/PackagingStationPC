using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PackagingStationPC.ViewModels;

namespace PackagingStationPC;

public partial class MainWindow : Window
{
    private bool _initialized;
    private readonly DispatcherTimer _scannerInputIdleTimer;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
        _scannerInputIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _scannerInputIdleTimer.Tick += ScannerInputIdleTimer_OnTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ((MainViewModel)DataContext).InitializeAsync();
        FocusScanner();
    }

    private void ScannerInputTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        _scannerInputIdleTimer.Stop();
        SubmitScanIfPossible();
        e.Handled = true;
    }

    private void ScannerInputTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _scannerInputIdleTimer.Stop();
        if (sender is not TextBox textBox || string.IsNullOrWhiteSpace(textBox.Text))
        {
            return;
        }

        if (textBox.Text.Contains('\r') || textBox.Text.Contains('\n'))
        {
            SubmitScanIfPossible();
            return;
        }

        _scannerInputIdleTimer.Start();
    }

    private void ScannerInputIdleTimer_OnTick(object? sender, EventArgs e)
    {
        _scannerInputIdleTimer.Stop();
        SubmitScanIfPossible();
    }

    private void SubmitScanIfPossible()
    {
        var viewModel = (MainViewModel)DataContext;
        if (viewModel.ScanCommand.CanExecute(null))
        {
            viewModel.ScanCommand.Execute(null);
        }

        FocusScanner();
    }

    private void FocusScanner()
    {
        ScannerInputTextBox.Focus();
        Keyboard.Focus(ScannerInputTextBox);
    }
}
