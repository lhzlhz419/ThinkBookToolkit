using System.Collections.Generic;
using System.Management;

namespace ThinkBookToolkit;

internal sealed record LenovoLfcPowerLimitSnapshot(
    int? CpuPl1W,
    int? CpuPl2W);

/// <summary>
/// Read-only CPU power limits exposed by the ThinkBook 16p Gen 4 firmware.
/// </summary>
internal static class LenovoLfcPowerLimitReader
{
    private const string ClassName = "Lfc_thermal_interface";

    public static LenovoLfcPowerLimitSnapshot Read()
    {
        using var instance = LenovoWmi.GetActiveInstance(ClassName);
        return new(
            PowerLimit(ReadValue(instance, "GetPowerLimit1")),
            PowerLimit(ReadValue(instance, "GetPowerLimit2")));
    }

    private static int? ReadValue(ManagementObject instance, string method)
    {
        try
        {
            return LenovoWmi.InvokeInt(
                instance,
                method,
                new Dictionary<string, object>(),
                "Data",
                "Value");
        }
        catch
        {
            return null;
        }
    }

    internal static int? PowerLimit(int? value) =>
        value is > 0 and <= 1000 ? value.Value : null;
}
