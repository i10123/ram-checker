using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RAMChecker.Helpers
{
    // Базовый класс сообщает WPF об изменении свойств наследников.
    public class NabludaemyObject : INotifyPropertyChanged
    {
        // Событие, которое слушают привязки интерфейса.
        public event PropertyChangedEventHandler? PropertyChanged;

        // Отправляет WPF имя свойства, которое только что изменилось.
        protected void SoobshitObIzmenenii([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
