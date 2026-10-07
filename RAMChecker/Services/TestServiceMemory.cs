using RAMChecker.Models;

namespace RAMChecker.Services
{
    public class TestServiceMemory : IServisMemory
    {
        public List<ModuleMemoryModel> PoluchitModuliPamyati()
        {
            return new List<ModuleMemoryModel>
            {
                new ModuleMemoryModel
                {
                    size_GB = 8,
                    maker = "Kingston",
                    RAM_frequency_MHz = 3200
                },
                new ModuleMemoryModel
                {
                    size_GB = 8,
                    maker = "Kingston",
                    RAM_frequency_MHz = 3200
                }
            };
        }
    }
}