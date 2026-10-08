using System;
using System.Windows.Input;

namespace RAMChecker.Helpers
{
    // Связывает действие из ViewModel с кнопкой или другим элементом WPF.
    public class Komanda : ICommand
    {
        // Действие, которое команда выполняет при запуске.
        private readonly Action<object?> deistvie;

        // Необязательная проверка, разрешено ли сейчас запускать команду.
        private readonly Predicate<object?>? uslovieDostupnosti;

        // WPF подписывается на событие, чтобы узнать об изменении доступности команды.
        public event EventHandler? CanExecuteChanged;

        // Сохраняет действие команды и необязательную проверку доступности.
        public Komanda(Action<object?> deistvie, Predicate<object?>? uslovieDostupnosti = null)
        {
            this.deistvie = deistvie;
            this.uslovieDostupnosti = uslovieDostupnosti;
        }

        // Возвращает true, если команду сейчас можно выполнить.
        public bool CanExecute(object? parameter)
        {
            return uslovieDostupnosti?.Invoke(parameter) ?? true;
        }

        // Выполняет сохранённое действие.
        public void Execute(object? parameter)
        {
            deistvie(parameter);
        }

        // Сообщает WPF, что результат проверки доступности мог измениться.
        public void SoobshitObIzmeneniiDostupnosti()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
