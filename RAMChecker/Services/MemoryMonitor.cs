using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RAMChecker.Services;

public record MemorySnapshot(ulong Total, ulong Available, ulong? Committed, ulong? CommitLimit, ulong? Cache)
{
    public ulong Used => Total - Available;
    public double Load => Total == 0 ? 0 : Used * 100d / Total;
    public static string Format(ulong bytes) => bytes < 1073741824
        ? $"{bytes / 1048576d:0.#} МБ" : $"{bytes / 1073741824d:0.0} ГБ";
    // Windows и другим программам оставляем не меньше 1 ГБ или 10 % памяти, иначе страницы буфера уйдут в файл подкачки.
    public static long SafeTestBytes(MemorySnapshot s) => (long)Math.Max(0d, s.Available - Math.Max(1073741824d, s.Total * .1));
}

public static partial class MemoryMonitor
{
    // Агрессивная сборка мусора не только освобождает объекты, но и возвращает свободную память Windows.
    public static void ReturnFreeMemory() => GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    public static MemorySnapshot Read()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref m))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var p = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        bool ok = GetPerformanceInfo(ref p, p.Size);
        return new(m.TotalPhys, m.AvailPhys,
            ok ? (ulong)p.CommitTotal * (ulong)p.PageSize : null,
            ok ? (ulong)p.CommitLimit * (ulong)p.PageSize : null,
            ok ? (ulong)p.SystemCache * (ulong)p.PageSize : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, Extended;
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
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInfo info, uint size);
}
