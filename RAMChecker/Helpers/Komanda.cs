using System;
using System.Windows.Input;

namespace RAMChecker.Helpers
{
    public class Komanda : ICommand
    {
        private readonly Action<object?> deistvie;
        private readonly Predicate<object?>? uslovieDostupnosti;

        public event EventHandler? CanExecuteChanged;

        public Komanda(Action<object?> deistvie, Predicate<object?>? uslovieDostupnosti = null)
        {
            this.deistvie = deistvie;
            this.uslovieDostupnosti = uslovieDostupnosti;
        }

        public bool CanExecute(object? parameter)
        {
            return uslovieDostupnosti?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            deistvie(parameter);
        }

        public void SoobshitObIzmeneniiDostupnosti()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}