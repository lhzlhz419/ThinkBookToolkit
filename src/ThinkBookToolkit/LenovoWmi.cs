using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

internal static class LenovoWmi
{
    private const string NamespacePath = @"root\WMI";
    private static readonly ConcurrentDictionary<string, string> ActiveInstancePaths =
        new(StringComparer.OrdinalIgnoreCase);

    public static ManagementObject GetActiveInstance(string className)
    {
        if (ActiveInstancePaths.TryGetValue(className, out var cachedPath))
            return new ManagementObject(cachedPath);

        using var searcher = new ManagementObjectSearcher(
            NamespacePath,
            $"SELECT * FROM {className}");
        foreach (ManagementObject item in searcher.Get())
        {
            if (item.Properties["Active"] is null ||
                Convert.ToBoolean(item["Active"]))
            {
                ActiveInstancePaths[className] = item.Path.Path;
                return item;
            }

            item.Dispose();
        }

        throw new InvalidOperationException(
            $"No active {className} instance found.");
    }

    public static int InvokeInt(
        ManagementObject instance,
        string method,
        IReadOnlyDictionary<string, object>? inputs,
        params string[] outputNames)
    {
        using var output = Invoke(instance, method, inputs) ??
                           throw new InvalidOperationException(
                               $"{method} returned no result.");
        foreach (var name in outputNames)
        {
            var property = FindProperty(output, name);
            if (property?.Value is not null)
                return Convert.ToInt32(property.Value);
        }

        var available = string.Join(
            ", ",
            output.Properties.Cast<PropertyData>().Select(property => property.Name));
        throw new InvalidOperationException(
            $"{method} returned none of [{string.Join(", ", outputNames)}]. Available: {available}");
    }

    public static string InvokeString(
        ManagementObject instance,
        string method,
        IReadOnlyDictionary<string, object>? inputs,
        params string[] outputNames)
    {
        using var output = Invoke(instance, method, inputs) ??
                           throw new InvalidOperationException(
                               $"{method} returned no result.");
        foreach (var name in outputNames)
        {
            var property = FindProperty(output, name);
            if (property?.Value is not null)
                return Convert.ToString(property.Value) ?? string.Empty;
        }

        return string.Empty;
    }

    public static void InvokeVoid(
        ManagementObject instance,
        string method,
        IReadOnlyDictionary<string, object>? inputs = null)
    {
        using var output = Invoke(instance, method, inputs);
    }

    public static int GetFeatureValue(uint id)
    {
        using var instance = GetActiveInstance("LENOVO_OTHER_METHOD");
        return InvokeInt(
            instance,
            "GetFeatureValue",
            new Dictionary<string, object> { ["IDs"] = id },
            "Value",
            "Data");
    }

    public static void SetFeatureValue(uint id, int value)
    {
        using var instance = GetActiveInstance("LENOVO_OTHER_METHOD");
        InvokeVoid(
            instance,
            "SetFeatureValue",
            new Dictionary<string, object>
            {
                ["IDs"] = id,
                ["value"] = value
            });
    }

    private static ManagementBaseObject? Invoke(
        ManagementObject instance,
        string method,
        IReadOnlyDictionary<string, object>? inputs)
    {
        using var input = instance.GetMethodParameters(method);
        if (inputs is not null)
        {
            foreach (var (name, value) in inputs)
            {
                var property = FindProperty(input, name) ??
                               throw new InvalidOperationException(
                                   $"{method} has no parameter named {name}.");
                input[property.Name] = ConvertValue(value, property.Type);
            }
        }

        return instance.InvokeMethod(method, input, null);
    }

    private static PropertyData? FindProperty(
        ManagementBaseObject instance,
        string name) =>
        instance.Properties
            .Cast<PropertyData>()
            .FirstOrDefault(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static object ConvertValue(object value, CimType type) =>
        type switch
        {
            CimType.UInt8 => Convert.ToByte(value),
            CimType.SInt8 => Convert.ToSByte(value),
            CimType.UInt16 => Convert.ToUInt16(value),
            CimType.SInt16 => Convert.ToInt16(value),
            CimType.UInt32 => Convert.ToUInt32(value),
            CimType.SInt32 => Convert.ToInt32(value),
            CimType.UInt64 => Convert.ToUInt64(value),
            CimType.SInt64 => Convert.ToInt64(value),
            CimType.Boolean => Convert.ToBoolean(value),
            CimType.String => Convert.ToString(value) ?? string.Empty,
            _ => value
        };
}
