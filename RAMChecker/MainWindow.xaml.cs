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

        private void Window_Activated(object? sender, EventArgs e) => ViewModel.Test.WindowActivated();

        // Закрытие откладывается, пока фоновая проверка не завершится и не сохранит результат.
        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (closeAllowed)
                return;
            e.Cancel = true;
            IsEnabled = false;
            try
            {
                await ViewModel.ShutdownAsync();
            }
            catch (Exception) { } // Сбой при завершении не должен оставить окно открытым и отключённым.
            closeAllowed = true;
            // Если ждать было нечего, мы всё ещё внутри обработчика Closing, а вызывать Close в нём WPF запрещает.
            // InvokeAsync выполнит закрытие сразу после выхода из обработчика; ждать его здесь не нужно.
            _ = Dispatcher.InvokeAsync(Close);
        }
    }
}
