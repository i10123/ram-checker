using RAMChecker.Models;

namespace RAMChecker.Services
{
    // Общий набор действий для сервисов, которые возвращают данные о памяти.
    public interface IServisMemory
    {
        // Возвращает список установленных модулей памяти.
        List<ModuleMemoryModel> GetModulesMemory();
    }
}
