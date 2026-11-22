using System.ComponentModel;

namespace RAMChecker.Helpers
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void NotifyChanged(params ReadOnlySpan<string> properties)
        {
            if (properties.Length == 0)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            foreach (var property in properties)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            RelayCommand.Refresh();
        }
    }
}
