namespace RAMChecker.Services;

// Vermeer SMU power table. Only use firmware layouts with a known MCLK offset.
public sealed class RyzenMemoryClock : IDisposable
{
    private readonly PawnIO driver = new("RyzenSMU.bin");
    private readonly Mutex pci = new(false, @"Global\Access_PCI");
    private bool resolved;
    private bool supported;
    public double? ReadMHz()
    {
        try { if (!pci.WaitOne(100)) return null; }
        catch (AbandonedMutexException) { }
        try
        {
            if (!resolved)
            {
                var layout = new long[2];
                if (!driver.Execute("ioctl_resolve_pm_table", [], layout))
                    return null;
                supported = layout[0] is 0x380005 or 0x380505 or 0x380605 or 0x380705
                    or 0x380804 or 0x380805 or 0x380904 or 0x380905;
                resolved = true;
            }
            if (!supported || !driver.Execute("ioctl_update_pm_table", [], []))
                return null;
            // MCLK at byte 0xCC is the upper float in the last 64-bit driver cell.
            var table = new long[26];
            if (!driver.Execute("ioctl_read_pm_table", [], table))
                return null;
            float mhz = BitConverter.Int32BitsToSingle((int)(table[^1] >> 32));
            return mhz is > 0 and < 10000 ? mhz : null;
        }
        finally { pci.ReleaseMutex(); }
    }
    public void Dispose()
    {
        pci.Dispose();
        driver.Dispose();
    }
}
