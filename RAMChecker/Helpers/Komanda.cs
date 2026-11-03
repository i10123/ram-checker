using System.Windows.Input;

namespace RAMChecker.Helpers
{
    // Связывает действие из ViewModel с кнопкой или другим элементом WPF.
    public class Komanda : ICommand
    {
        private readonly Action execute;

        private readonly Func<bool>? canExecute;

        public event EventHandler? CanExecuteChanged;

        public Komanda(Action execute, Func<bool>? canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            return canExecute?.Invoke() ?? true;
        }

        public void Execute(object? parameter)
        {
            execute();
        }

        public void Refresh()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
