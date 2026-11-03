using System.ComponentModel;

namespace RAMChecker.Helpers
{
    // Базовый класс ViewModel: сообщает экрану, что данные изменились.
    public abstract class NabludaemyObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        // Без имён свойств WPF перечитывает все привязки объекта. Для частых обновлений
        // (раз в секунду) перечисляем только изменившиеся свойства, чтобы не перерисовывать весь экран.
        // params ReadOnlySpan (C# 13) передаёт имена через стек, без создания массива при каждом вызове.
        protected void Changed(params ReadOnlySpan<string> properties)
        {
            if (properties.Length == 0)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            foreach (var property in properties)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            Komanda.Refresh();
        }
    }
}
