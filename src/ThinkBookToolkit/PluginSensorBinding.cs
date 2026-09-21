using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace ThinkBookToolkit;

internal sealed class PluginSensorBinding(ToolkitPluginManager plugins, string property) : IMultiValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["Fan1Speed"] = "fan1Rpm", ["Fan2Speed"] = "fan2Rpm",
        ["CpuUtilization"] = "cpuUtilization", ["CpuTemperature"] = "cpuTemperatureC", ["CpuPower"] = "cpuPowerW",
        ["CpuAverageFrequency"] = "cpuAverageMhz", ["CpuMaximumFrequency"] = "cpuMaximumMhz",
        ["CpuPerformanceCoreAverageFrequency"] = "cpuPerformanceCoreAverageMhz", ["CpuEfficiencyCoreAverageFrequency"] = "cpuEfficiencyCoreAverageMhz",
        ["GpuUtilization"] = "gpuUtilization", ["GpuMemoryUtilization"] = "vramUtilization",
        ["GpuCoreFrequency"] = "gpuCoreMhz", ["GpuMemoryFrequency"] = "vramMhz",
        ["GpuCoreTemperature"] = "gpuTemperatureC", ["GpuHotSpotTemperature"] = "gpuHotSpotTemperatureC",
        ["GpuMemoryTemperature"] = "vramTemperatureC", ["GpuPower"] = "gpuPowerW",
        ["MemorySlot1Temperature"] = "memorySlot1TemperatureC", ["MemorySlot2Temperature"] = "memorySlot2TemperatureC",
        ["BatteryPower"] = "batteryPowerW", ["BatteryCapacity"] = "batteryCapacityWh"
    };
    internal static MultiBinding Create(ToolkitPluginManager plugins, string property)
    {
        var result = new MultiBinding { Converter = new PluginSensorBinding(plugins, property) };
        result.Bindings.Add(new Binding(property)); result.Bindings.Add(new Binding(nameof(ToolkitPluginManager.Revision)) { Source = plugins });
        return result;
    }
    internal static string Format(ToolkitPluginManager plugins, string property, string original) =>
        Keys.TryGetValue(property, out var key) ? plugins.OverrideText("toolkit.sensor." + key, original) : original;
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture)
    {
        var fallback = values[0]?.ToString() ?? "--";
        return Format(plugins, property, fallback);
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
