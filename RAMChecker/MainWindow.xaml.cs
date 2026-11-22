using System.ComponentModel;
using System.Windows;
using RAMChecker.ViewModels;

namespace RAMChecker
{
    public partial class MainWindow : Window
    {
        private bool closingStarted, closeAllowed;

        public MainWindow() => InitializeComponent();

        private MainViewModel ViewModel => (MainViewModel)DataContext;

        private async void Window_Loaded(object sender, RoutedEventArgs eventArgs) => await ViewModel.InitializeAsync();

        private void Window_Activated(object? sender, EventArgs eventArgs) => ViewModel.Test.WindowActivated();

        private async void Window_Closing(object? sender, CancelEventArgs eventArgs)
        {
            if (closeAllowed)
                return;
            eventArgs.Cancel = true;
            if (closingStarted)
                return;
            closingStarted = true;
            IsEnabled = false;
            try
            {
                await ViewModel.ShutdownAsync();
            }
            catch (Exception) { }
            closeAllowed = true;
            _ = Dispatcher.InvokeAsync(Close);
        }
    }
}
