using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RAMChecker.Services;

// ASUS firmware exposes ADC readings in microvolts through the Windows WMI automation API.
[SupportedOSPlatform("windows")]
public sealed class BoardVoltage
{
    private dynamic? device;
    private int index, source;
    private dynamic? amdFirmware;
    private uint voltageQuery;
    public string Source => device != null ? "ASUS WMI" : amdFirmware != null ? "AMD ACPI · VDDIO" : "";

    public BoardVoltage()
    {
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!)!;
        dynamic service = locator.ConnectServer(".", @"root\wmi");
        try { FindAsusSensor(service); }
        catch (COMException) { } // Class absent on other vendors' boards.
        if (device != null) return;
        foreach (dynamic firmware in service.ExecQuery("SELECT * FROM AMD_ACPI"))
        {
            foreach (string directory in new[] { "GetObjectID", "GetObjectID2" })
            {
                try
                {
                    dynamic commands = firmware.ExecMethod_(directory).pack;
                    Array names = (Array)commands.IDstring;
                    Array ids = (Array)commands.ID;
                    for (int i = 0; i < Math.Min(names.Length, ids.Length); i++)
                    {
                        if (!string.Equals((names.GetValue(i) as string)?.Trim(), "Get memory voltages", StringComparison.OrdinalIgnoreCase))
                            continue;
                        voltageQuery = unchecked((uint)Convert.ToInt64(ids.GetValue(i)));
                        amdFirmware = firmware;
                        return;
                    }
                }
                catch (COMException) { } // Firmware can expose only one of the directories.
            }
        }
    }

    private void FindAsusSensor(dynamic service)
    {
        foreach (dynamic board in service.ExecQuery("SELECT * FROM ASUSHW"))
        {
            int count = Convert.ToInt32(board.ExecMethod_("sensor_get_number").Data);
            for (int i = 0; i < Math.Min(count, 256); i++)
            {
                dynamic info = Call(board, "sensor_get_info", "Index", i);
                if (Convert.ToInt32(info.Type) == 0 && string.Equals((string)info.Name, "DRAM Voltage", StringComparison.OrdinalIgnoreCase))
                {
                    device = board;
                    index = i;
                    source = Convert.ToInt32(info.Source);
                    return;
                }
            }
        }
    }
    private static dynamic Call(dynamic board, string method, string parameter, object value)
    {
        dynamic args = board.Methods_.Item(method).InParameters.SpawnInstance_();
        args.Properties_.Item(parameter).Value = value;
        return board.ExecMethod_(method, args);
    }
    public double? ReadVolts()
    {
        if (amdFirmware != null)
        {
            // Only invoke the firmware-advertised read command, with a zero argument.
            var request = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(request, voltageQuery);
            dynamic reply = Call(amdFirmware, "RunCommand", "Inbuf", request);
            if (reply.Outbuf.result is not byte[] data || data.Length < 29) return null;
            int millivolts = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(27));
            return millivolts is > 0 and < 3000 ? millivolts / 1000d : null;
        }
        if (device is null) return null;
        Call(device, "sensor_update_buffer", "Source", source);
        double volts = Convert.ToDouble(Call(device, "sensor_get_value", "Index", index).Data) / 1_000_000;
        return volts is > 0 and < 4 ? volts : null;
    }
}
