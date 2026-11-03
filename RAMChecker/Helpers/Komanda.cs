using System.Windows.Input;

namespace RAMChecker.Helpers
{
    // Связывает действие из ViewModel с кнопкой WPF.
    public class Komanda : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool>? canExecute;

        public Komanda(Action execute, Func<bool>? canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        // WPF сам перепроверяет доступность кнопок после действий пользователя и вызова Refresh.
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => execute();

        public static void Refresh() => CommandManager.InvalidateRequerySuggested();
    }
}
