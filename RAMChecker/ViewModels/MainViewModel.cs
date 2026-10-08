using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Windows;
using RAMChecker.Helpers;
using RAMChecker.Models;
using RAMChecker.Services;

namespace RAMChecker.ViewModels
{
    // Подготавливает данные, которые окно показывает в таблице.
    public class MainViewModel : NabludaemyObject
    {
        private string statusMessage = string.Empty;

        // Коллекция модулей, к которой привязана таблица в главном окне.
        public ObservableCollection<ModuleMemoryModel> ModulesMemory { get; }

        // Текст о результате запроса к Windows или возникшей ошибке.
        public string StatusMessage
        {
            get => statusMessage;
            private set
            {
                statusMessage = value;
                SoobshitObIzmenenii();
            }
        }

        // Кнопка будет видна только при ошибке доступа к WMI.
        public bool ShowAdminButton { get; private set; }

        // Команда перезапускает приложение после подтверждения UAC.
        public Komanda RestartAsAdminCommand { get; }

        // Загружает реальные сведения о памяти через WMI.
        public MainViewModel()
        {
            RestartAsAdminCommand = new Komanda(_ => RestartAsAdmin());

            try
            {
                var memoryService = new WmiServiceMemory();
                var modulesMemory = memoryService.GetModulesMemory();
                ModulesMemory = new ObservableCollection<ModuleMemoryModel>(modulesMemory);

                // Объясняет пустую таблицу, если WMI не нашёл планки памяти.
                if (modulesMemory.Count == 0)
                {
                    StatusMessage = "Windows не вернула сведения о модулях памяти.";
                }
            }
            catch (ManagementException error) when (error.ErrorCode == ManagementStatus.AccessDenied)
            {
                // Предлагает повышение прав только при явном отказе в доступе.
                ModulesMemory = new ObservableCollection<ModuleMemoryModel>();
                ShowAdminButton = true;
                StatusMessage = "Windows запретила доступ к WMI. Можно перезапустить приложение от имени администратора.";
            }
            catch (UnauthorizedAccessException)
            {
                // Некоторые ошибки доступа могут прийти как UnauthorizedAccessException.
                ModulesMemory = new ObservableCollection<ModuleMemoryModel>();
                ShowAdminButton = true;
                StatusMessage = "Windows запретила доступ к данным. Можно перезапустить приложение от имени администратора.";
            }
            catch (Exception error)
            {
                // Показывает причину сбоя, который не связан с нехваткой прав.
                ModulesMemory = new ObservableCollection<ModuleMemoryModel>();
                StatusMessage = $"Не удалось получить сведения о памяти: {error.Message}";
            }
        }

        // Повторно запускает это приложение с запросом прав администратора от Windows.
        private void RestartAsAdmin()
        {
            try
            {
                var applicationPath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(applicationPath))
                {
                    StatusMessage = "Не удалось определить путь к приложению для перезапуска.";
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = applicationPath,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                Application.Current.Shutdown();
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                // Пользователь отменил запрос контроля учётных записей Windows.
                StatusMessage = "Перезапуск от имени администратора отменён.";
            }
            catch (Exception error)
            {
                // Сообщает, если Windows не смогла открыть повышенную копию приложения.
                StatusMessage = $"Не удалось перезапустить приложение: {error.Message}";
            }
        }
    }
}
