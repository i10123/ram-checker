using System.ComponentModel;

namespace RAMChecker.Helpers
{
    // Базовый класс ViewModel: сообщает экрану, что данные изменились.
    public abstract class NabludaemyObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Без имени свойства WPF перечитывает все привязки объекта — так проще, чем перечислять каждое.
        protected void Changed(string? property = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            Komanda.Refresh();
        }
    }
}
