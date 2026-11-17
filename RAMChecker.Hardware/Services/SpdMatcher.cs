using RAMChecker.Models;

namespace RAMChecker.Services;

// Сопоставляет паспорта SPD с модулями из SMBIOS: порядок адресов на шине и порядок слотов не обязан совпадать.
public static class SpdMatcher
{
    // Пары «модуль из SMBIOS — паспорт SPD». Надёжный признак планки — серийный номер и модель из второй страницы SPD:
    // пара принимается, если они совпали ровно у одного модуля и одного паспорта. Распаянная на плате память
    // микросхемы SPD не имеет, такой модуль остаётся без пары. Если по номеру не нашлось ни одной пары (номер не
    // прочитан или BIOS записал его в SMBIOS иначе), паспорта сопоставляются по порядку (адреса SPD 0x50, 0x51… и список
    // SMBIOS обычно идут от первого слота), но только если число модулей и объём каждого совпадают. Если объёмы всех
    // планок разные, совпадение объёма подтверждает пару; если есть одинаковые, их можно перепутать.
    public static List<(ModuleMemoryModel Module, SpdInfo Info)> Match(List<ModuleMemoryModel> modules, List<SpdInfo> infos, out string how)
    {
        bool Same(ModuleMemoryModel m, SpdInfo i) => i.Serial == m.RAM_serialNumber && i.Part == m.RAM_partNumber;
        List<(ModuleMemoryModel, SpdInfo)> pairs = [.. from m in modules from i in infos
            where Same(m, i) && infos.Count(j => Same(m, j)) == 1 && modules.Count(n => Same(n, i)) == 1 select (m, i)];
        how = "совпали серийный номер и модель";
        if (pairs.Count == 0 && infos.Count == modules.Count && modules.Zip(infos).All(p => p.First.size_GB == p.Second.SizeGB))
        {
            pairs = [.. modules.Zip(infos)];
            how = modules.Select(m => m.size_GB).Distinct().Count() < modules.Count ? "отнесён по порядку слотов, предположительно" : "по порядку слотов";
        }
        return pairs;
    }
}
