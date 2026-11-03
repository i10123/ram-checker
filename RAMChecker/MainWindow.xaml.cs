using System.ComponentModel;
using System.Windows;
using RAMChecker.ViewModels;

namespace RAMChecker
{
    // Код окна: запуск модели при открытии и корректная остановка проверки при закрытии.
    public partial class MainWindow : Window
    {
        private bool closeAllowed;

        public MainWindow() => InitializeComponent();

        private MainViewModel ViewModel => (MainViewModel)DataContext;

        private async void Window_Loaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();

        // Закрытие откладывается, пока фоновая проверка не завершится и не сохранит результат.
        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (closeAllowed)
                return;
            e.Cancel = true;
            IsEnabled = false;
            await ViewModel.ShutdownAsync();
            closeAllowed = true;
            Close();
        }
    }
}
