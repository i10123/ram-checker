using System.ComponentModel;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RAMChecker.Services;

public record MemorySnapshot(ulong Total, ulong Available, ulong? Committed, ulong? CommitLimit, ulong? Cache)
{
    public ulong? RemainingCommit => Committed is ulong used && CommitLimit is > 0
        ? CommitLimit.Value - Math.Min(used, CommitLimit.Value) : null;
    public ulong Used => Total - Available;
    public double LoadPercent => Total == 0 ? 0 : Used * 100d / Total;
    public static string Format(ulong bytes) => bytes < 1073741824
        ? $"{bytes / 1048576d:0.#} МБ" : $"{bytes / 1073741824d:0.0} ГБ";
    // Windows и другим программам оставляем не меньше 1 ГБ или 10 % памяти, иначе страницы буфера уйдут в файл подкачки.
    public static long SafeTestBytes(MemorySnapshot snapshot)
    {
        ulong available = Math.Min(snapshot.Available, snapshot.RemainingCommit ?? snapshot.Available);
        ulong reserve = Math.Max(1UL << 30, snapshot.Total / 10);
        return (long)Math.Min((ulong)long.MaxValue, available - Math.Min(available, reserve));
    }
}

public static partial class MemoryMonitor
{
    // Агрессивная сборка мусора не только освобождает объекты, но и возвращает свободную память Windows.
    public static void ReturnFreeMemory() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    // Во время проверки Windows не уходит в сон: ES_CONTINUOUS | ES_SYSTEM_REQUIRED, после проверки — только ES_CONTINUOUS.
    // Флаг действует для вызвавшего потока, поэтому включается и выключается из потока интерфейса.
    public static void KeepAwake(bool enabled) => SetThreadExecutionState(enabled ? 0x80000001u : 0x80000000u);

    public static MemorySnapshot Read()
    {
        var memoryStatus = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memoryStatus))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var performanceInfo = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        bool performanceInfoRead = GetPerformanceInfo(ref performanceInfo, performanceInfo.Size);
        return new(memoryStatus.TotalPhysical, memoryStatus.AvailablePhysical,
            performanceInfoRead ? (ulong)performanceInfo.CommitTotal * (ulong)performanceInfo.PageSize : null,
            performanceInfoRead ? (ulong)performanceInfo.CommitLimit * (ulong)performanceInfo.PageSize : null,
            performanceInfoRead ? (ulong)performanceInfo.SystemCache * (ulong)performanceInfo.PageSize : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFileBytes, AvailablePageFileBytes, TotalVirtualBytes, AvailableVirtualBytes, AvailableExtendedVirtualBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInfo
    {
        public uint Size;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    // LibraryImport (.NET 7+) генерирует код вызова Win32 при компиляции, а не во время работы программы.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus memoryStatusBuffer);
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInfo performanceInformation, uint structureSize);
    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint executionStateFlags);
}
