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
                MessageBox.Show(eventArgs.Exception.Message, "Непредвиденная ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }
    }
}
