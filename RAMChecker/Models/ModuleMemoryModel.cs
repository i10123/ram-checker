namespace RAMChecker.Models
{
    // Хранит отображаемые сведения об одной планке оперативной памяти.
    public class ModuleMemoryModel
    {
        // Объём одной планки в гигабайтах.
        public int size_GB { get; set; }

        // Название производителя, например Kingston.
        public string maker { get; set; } = string.Empty;

        // Частота в мегагерцах или текст «Недоступно», если данных нет.
        public string RAM_frequency_MHz { get; set; } = "Недоступно";
    }
}
