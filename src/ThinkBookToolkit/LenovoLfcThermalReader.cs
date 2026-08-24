using System;
using System.Collections.Generic;
using System.Management;
using System.Threading;

namespace ThinkBookToolkit;

internal sealed record LenovoLfcThermalSnapshot(
    double? CpuTemperatureC,
    double? GpuTemperatureC,
    double? RamTemperatureC,
    int? Fan1Rpm,
    int? Fan2Rpm)
{
    public static LenovoLfcThermalSnapshot Empty { get; } =
        new(null, null, null, null, null);
}

/// <summary>
/// Read-only telemetry exposed by the ThinkBook 16p Gen 4 ACPI-WMI firmware.
/// No setter on Lfc_thermal_interface is used here.
/// </summary>
internal static class LenovoLfcThermalReader
{
    private const string ClassName = "Lfc_thermal_interface";
    private static int _unavailableLogged;

    public static LenovoLfcThermalSnapshot Read()
    {
        try
        {
            using var instance = LenovoWmi.GetActiveInstance(ClassName);
            return new(
                Temperature(ReadValue(instance, "GetCPUTemperature")),
                Temperature(ReadValue(instance, "GetGPUTemperature")),
                Temperature(ReadValue(instance, "GetRAMTemperature")),
                FanSpeed(ReadValue(instance, "GetFan1Speed")),
                FanSpeed(ReadValue(instance, "GetFan2Speed")));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _unavailableLogged, 1) == 0)
            {
                ToolkitLog.Warning(
                    $"Lenovo LFC thermal telemetry is unavailable: {ex.Message}");
            }
            return LenovoLfcThermalSnapshot.Empty;
        }
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

    internal static double? Temperature(int? value) =>
        value is > 0 and < 130 ? value.Value : null;

    internal static int? FanSpeed(int? value) =>
        value is >= 0 and <= 10000 ? value.Value : null;
}
