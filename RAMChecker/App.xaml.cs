using System.Globalization;
using System.Windows;

namespace RAMChecker
{
    // Класс запуска приложения, связанный с настройками в App.xaml.
    public partial class App : Application
    {
        public App()
        {
            // Интерфейс только русский: даты и дробные числа выглядят одинаково при любом языке Windows.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = new("ru-RU");
            // Непредвиденная ошибка в интерфейсе показывается сообщением и не закрывает программу посреди проверки.
            DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                MessageBox.Show(e.Exception.Message, "Непредвиденная ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}
