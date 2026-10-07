using System.Collections.ObjectModel;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels
{
    public class MainViewModel : NabludaemyObject
    {
        public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; }

        public MainViewModel()
        {
            var servisPamyati = new TestServiceMemory();
            ModulesMemory = new ObservableCollection<ModuleMemoryModel>(
                servisPamyati.PoluchitModuliPamyati());
        }
    }
}