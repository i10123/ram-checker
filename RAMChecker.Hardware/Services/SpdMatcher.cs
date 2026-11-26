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
    public static List<(ModuleMemoryModel Module, SpdInfo Info)> Match(List<ModuleMemoryModel> modules, List<SpdInfo> infos, out string matchMethod)
    {
        bool IsSameModule(ModuleMemoryModel module, SpdInfo info) => info.SerialNumber == module.RAM_serialNumber && info.PartNumber == module.RAM_partNumber;
        List<(ModuleMemoryModel, SpdInfo)> pairs = [.. from module in modules from info in infos
            where IsSameModule(module, info) && infos.Count(otherInfo => IsSameModule(module, otherInfo)) == 1 && modules.Count(otherModule => IsSameModule(otherModule, info)) == 1 select (module, info)];
        matchMethod = "совпали серийный номер и модель";
        if (pairs.Count == 0 && infos.Count == modules.Count && modules.Zip(infos).All(pair => pair.First.Size_GB == pair.Second.SizeGB))
        {
            pairs = [.. modules.Zip(infos)];
            matchMethod = modules.Select(module => module.Size_GB).Distinct().Count() < modules.Count ? "отнесён по порядку слотов, предположительно" : "по порядку слотов";
        }
        return pairs;
    }
}
