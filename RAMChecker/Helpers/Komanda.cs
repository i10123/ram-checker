using System.Windows.Input;

namespace RAMChecker.Helpers
{
    // Связывает действие из ViewModel с кнопкой WPF. Параметры основного конструктора (C# 12) доступны во всём классе.
    public class Komanda(Action execute, Func<bool>? canExecute = null) : ICommand
    {
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
