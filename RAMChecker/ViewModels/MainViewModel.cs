using System.Collections.ObjectModel;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels
{
    // Подготавливает данные, которые окно показывает в таблице.
    public class MainViewModel : NabludaemyObject
    {
        // Коллекция модулей, к которой привязана таблица в главном окне.
        public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; }

        // Пока заполняет таблицу данными тестового сервиса.
        public MainViewModel()
        {
            var servisPamyati = new TestServiceMemory();
            ModulesMemory = new ObservableCollection<ModuleMemoryModel>(
                servisPamyati.GetModulesMemory());
        }
    }
}
