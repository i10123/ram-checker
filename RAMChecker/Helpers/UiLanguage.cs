using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace RAMChecker.Helpers;

// Translate presentation values at the binding boundary so saved Russian history remains readable in either language.
public class UiLanguage : INotifyPropertyChanged
{
    public static UiLanguage Current { get; } = new();
    public bool English { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Set(bool english)
    {
        English = english;
        PropertyChanged?.Invoke(this, new(nameof(English)));
    }
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Нет измерений"] = "No samples recorded",
        ["Производитель"] = "Manufacturer",
        ["Модель"] = "Model",
        ["Серийный номер"] = "Serial number",
        ["Тип"] = "Type",
        ["Объём"] = "Capacity",
        ["Слот"] = "Slot",
        ["Настроенная скорость (SMBIOS)"] = "Configured speed (SMBIOS)",
        ["Номинальная частота"] = "Rated speed",
        ["Номинальная частота JEDEC (SPD)"] = "JEDEC rated speed (SPD)",
        ["Тайминги CL-tRCD-tRP-tRAS"] = "Timings CL-tRCD-tRP-tRAS",
        ["Тайминги JEDEC CL-tRCD-tRP-tRAS"] = "JEDEC timings CL-tRCD-tRP-tRAS",
        ["tRC JEDEC"] = "JEDEC tRC",
        ["XMP профиль 1"] = "XMP profile 1",
        ["Напряжение (SMBIOS)"] = "Configured voltage (SMBIOS)",
        ["Нет данных"] = "Unavailable",
        ["Системный кэш"] = "System cache",
        ["Файл подкачки"] = "Paging file",
        ["Доступно для проверки"] = "Available for testing",
        ["Ошибки памяти в журнале Windows"] = "Memory errors in the Windows event log",
        ["Нет"] = "None",
        ["Замер ещё не выполнялся"] = "No benchmark yet",
        ["Идёт замер…"] = "Benchmark running…",
        ["Чтение"] = "Read",
        ["Запись"] = "Write",
        ["Копирование"] = "Copy",
        ["Паспорт SPD DDR4 или DDR5 не найден на шине SMBus."] = "No DDR4 or DDR5 SPD found on the SMBus.",
        ["Планки одинакового объёма: тайминги отнесены к слотам предположительно."] = "Modules have the same capacity. Slot assignments are tentative.",
        ["Для таймингов установите драйвер PawnIO."] = "Install the PawnIO driver to read timings.",
        ["Готово к запуску"] = "Ready to start",
        ["Быстрый"] = "Quick",
        ["Полный"] = "Full",
        ["Циклический"] = "Continuous",
        ["Стресс"] = "Stress",
        ["Обнаружены ошибки"] = "Errors detected",
        ["Ошибок не обнаружено"] = "No errors detected",
        ["Проверка остановлена"] = "Test stopped",
        ["Проверка прервана"] = "Test interrupted",
        ["Проверка не выполнена"] = "Test failed",
        ["Проверка выполняется"] = "Test running",
        ["Сохраните отчёт. Если включён разгон или XMP, верните штатные настройки BIOS и повторите проверку. Если ошибки остаются, проверяйте планки по одной."] = "Save the report. If overclocking or XMP is enabled, restore the default BIOS settings and repeat the test. If errors persist, test each module individually.",
        ["В проверенном объёме ошибок не найдено. При редких сбоях запустите полную или циклическую проверку на большем объёме."] = "No errors were detected in the tested memory. For intermittent failures, run a full or continuous test on a larger buffer.",
        ["Уменьшите объём проверки и повторите запуск."] = "Reduce the test size and try again.",
        ["Проверка не завершена, по ней нельзя судить об исправности памяти."] = "The test did not finish. Memory health could not be determined.",
        ["Не удалось выделить память. Уменьшите объём и повторите проверку."] = "Could not allocate memory. Reduce the test size and try again.",
        ["Буфер не удалось закрепить в оперативной памяти, часть страниц могла уйти в файл подкачки."] = "The buffer could not be locked in RAM. Some pages may have been paged out.",
        ["Приложение завершилось до получения итогового результата."] = "The application closed before the test finished.",
        ["Свой адрес"] = "Own address",
        ["Шахматный шаблон"] = "Checkerboard",
        ["Бегущая единица"] = "Walking ones",
        ["Случайный шаблон"] = "Random pattern",
        ["Заполнение"] = "Fill",
        ["Проверка вперёд"] = "Forward check",
        ["Проверка назад"] = "Reverse check",
        ["найдены ошибки"] = "errors detected",
        ["ещё не проверен"] = "untested",
        ["ждёт текущего прохода"] = "awaiting this pass",
        ["без ошибок"] = "no errors",
        ["Большая нагрузка"] = "High system load",
        ["Недостаточно памяти"] = "Insufficient memory",
        ["Свободной памяти стало меньше, чем нужно для проверки с резервом для Windows. Уменьшите объём."] = "There is no longer enough free memory to keep the Windows reserve. Reduce the test size.",
        ["Не удалось сохранить отчёт"] = "Could not save the report",
        ["Непредвиденная ошибка"] = "Unexpected error",
    };

    public string Display(string value)
    {
        if (!English || value.Length == 0) return value;
        if (Labels.TryGetValue(value, out var label)) return label;
        if (value.Contains(" · "))
            return string.Join(" · ", value.Split(" · ").Select(Display));
        // Legacy history and firmware cards contain formatted Russian units.
        return value.Replace("Модуль ", "Module ").Replace("Блок ", "Block ")
            .Replace("Нет профиля XMP", "No XMP profile").Replace("вероятно активен", "probably active")
            .Replace("Нет данных", "Unavailable").Replace(" шт.", " modules").Replace(" из ", " of ")
            .Replace("не менее ", "at least ").Replace("ГБ/с", "GiB/s").Replace("МБ/с", "MiB/s")
            .Replace(" ГБ", " GiB").Replace(" МБ", " MiB").Replace(" МГц", " MHz").Replace(" В", " V")
            .Replace("Задержка: ", "Latency: ").Replace(" нс", " ns").Replace(", последняя ", ", latest ")
            .Replace("Замер не выполнен: ", "Benchmark failed: ").Replace("Шина SMBus недоступна: ", "SMBus unavailable: ")
            .Replace("шина занята другой программой.", "the bus is busy.")
            .Replace("Паспорт SPD прочитан у ", "SPD read for ").Replace("Паспорт SPD найден у ", "SPD found for ")
            .Replace(" модулей, но его нельзя однозначно отнести к слотам.", " modules; slot assignments are ambiguous.")
            .Replace(" модулей.", " modules.").Replace("На проходе ", "On pass ")
            .Replace(" не удалось снова выделить память, выполнено проходов: ", " reallocation failed. Completed passes: ")
            .Replace("Не удалось прочитать историю: ", "Could not read history: ")
            .Replace("Не удалось сохранить историю: ", "Could not save history: ")
            .Replace("Не удалось получить сведения о модулях: ", "Could not read module information: ")
            .Replace("Мониторинг недоступен: ", "Monitoring unavailable: ")
            .Replace("Пропущено повреждённых записей истории: ", "Damaged history entries skipped: ")
            .Replace("Файл истории повреждён, история начата заново.", "History is damaged. A new history was started.")
            .Replace("Исходный файл сохранён как history.bad.json.", "The original file was saved as history.bad.json.");
    }
}

public class UiTextExtension(string path) : MarkupExtension, IMultiValueConverter
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = this, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(path));
        binding.Bindings.Add(new Binding(nameof(UiLanguage.English)) { Source = UiLanguage.Current });
        return binding.ProvideValue(serviceProvider);
    }
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture) =>
        values[0] is string text ? UiLanguage.Current.Display(text) : "";
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
