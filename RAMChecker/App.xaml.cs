using System.Windows;

namespace RAMChecker
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                MessageBox.Show(eventArgs.Exception.Message, RAMChecker.Helpers.UiLanguage.Current.Display("Непредвиденная ошибка"), MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}
