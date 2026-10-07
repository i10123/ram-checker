using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RAMChecker.Helpers
{
    public class NabludaemyObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void SoobshitObIzmenenii([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}