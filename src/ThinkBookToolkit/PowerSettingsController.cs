using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

internal sealed record PowerSettingsState(
    int CpuPl1,
    int CpuPl2,
    int CpuTemperatureLimit,
    int CpuTurboTimeLimit,
    int GpuPowerBoost,
    int GpuConfigurableTgp,
    int GpuTemperatureLimit,
    int GpuToCpuDynamicBoost);

internal static class PowerSettingsController
{
    private const uint CpuPl1Id = 0x01020000;
    private const uint CpuPl2Id = 0x01010000;
    private const uint CpuTemperatureLimitId = 0x01040000;
    private const uint CpuTurboTimeLimitId = 0x01070000;
    private const uint GpuPowerBoostId = 0x02010000;
    private const uint GpuConfigurableTgpId = 0x02020000;
    private const uint GpuTemperatureLimitId = 0x02030000;
    private const uint GpuToCpuDynamicBoostId = 0x020B0000;

    public static IReadOnlyList<int> TurboTimeLimits { get; } =
        [20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160];

    public static PowerSettingsState? GetDefaultState(ItsMode mode) =>
        mode switch
        {
            ItsMode.Intelligent => new(90, 125, 100, 56, 15, 75, 87, 10),
            ItsMode.PowerSaving => new(35, 60, 93, 56, 10, 50, 87, 10),
            ItsMode.Performance => new(125, 180, 100, 56, 15, 100, 87, 0),
            ItsMode.Geek => new(130, 185, 100, 56, 15, 100, 87, 0),
            _ => null
        };

    public static PowerSettingsState ReadState()
    {
        using var other = GetActiveOtherMethod();
        return ReadState(other);
    }

    public static void WriteState(PowerSettingsState state)
    {
        Validate(state);
        using var other = GetActiveOtherMethod();
        WriteState(other, state);
    }

    public static PowerSettingsState WriteAndReadState(PowerSettingsState state)
    {
        Validate(state);
        using var other = GetActiveOtherMethod();
        WriteState(other, state);
        return ReadState(other);
    }

    private static PowerSettingsState ReadState(ManagementObject other)
    {
        var state = new PowerSettingsState(
            ReadFeatureValue(other, CpuPl1Id),
            ReadFeatureValue(other, CpuPl2Id),
            checked(ReadFeatureValue(other, CpuTemperatureLimitId) + 5),
            ReadFeatureValue(other, CpuTurboTimeLimitId),
            ReadFeatureValue(other, GpuPowerBoostId),
            checked(ReadFeatureValue(other, GpuConfigurableTgpId) + 50),
            ReadFeatureValue(other, GpuTemperatureLimitId),
            ReadFeatureValue(other, GpuToCpuDynamicBoostId));
        Validate(state);
        return state;
    }

    private static void WriteState(ManagementObject other, PowerSettingsState state)
    {
        SetFeatureValue(other, CpuPl1Id, state.CpuPl1);
        SetFeatureValue(other, CpuPl2Id, state.CpuPl2);
        SetFeatureValue(other, CpuTemperatureLimitId, state.CpuTemperatureLimit - 5);
        SetFeatureValue(other, CpuTurboTimeLimitId, state.CpuTurboTimeLimit);
        SetFeatureValue(other, GpuPowerBoostId, state.GpuPowerBoost);
        SetFeatureValue(other, GpuConfigurableTgpId, state.GpuConfigurableTgp - 50);
        SetFeatureValue(other, GpuTemperatureLimitId, state.GpuTemperatureLimit);
        SetFeatureValue(other, GpuToCpuDynamicBoostId, state.GpuToCpuDynamicBoost);
    }

    private static void Validate(PowerSettingsState state)
    {
        RequirePositive(nameof(state.CpuPl1), state.CpuPl1);
        RequirePositive(nameof(state.CpuPl2), state.CpuPl2);
        RequireRange(nameof(state.CpuTemperatureLimit), state.CpuTemperatureLimit, 75, 105);
        if (!TurboTimeLimits.Contains(state.CpuTurboTimeLimit))
            throw new ArgumentOutOfRangeException(nameof(state.CpuTurboTimeLimit), state.CpuTurboTimeLimit, "Unsupported CPU turbo time limit.");
        RequireRange(nameof(state.GpuPowerBoost), state.GpuPowerBoost, 0, 15);
        RequireRange(nameof(state.GpuConfigurableTgp), state.GpuConfigurableTgp, 50, 100);
        RequireRange(nameof(state.GpuTemperatureLimit), state.GpuTemperatureLimit, 75, 87);
        RequireRange(nameof(state.GpuToCpuDynamicBoost), state.GpuToCpuDynamicBoost, 0, 50);
    }

    private static void RequireRange(string name, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"Value must be between {minimum} and {maximum}.");
    }

    private static void RequirePositive(string name, int value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(
                name,
                value,
                "Value must be a positive integer.");
    }

    private static ManagementObject GetActiveOtherMethod()
        => LenovoWmi.GetActiveInstance("LENOVO_OTHER_METHOD");

    private static int ReadFeatureValue(ManagementObject other, uint id)
    {
        var errors = new List<string>();

        try
        {
            var args = new object?[] { id, null };
            other.InvokeMethod("GetFeatureValue", args);
            if (args[1] is not null)
                return Convert.ToInt32(args[1]);
            errors.Add("positional [id, out value] returned no out value");
        }
        catch (Exception ex)
        {
            errors.Add("positional [id, out value]: " + DescribeException(ex));
        }

        try
        {
            var args = new object?[] { id };
            var result = other.InvokeMethod("GetFeatureValue", args);
            if (result is not null)
                return Convert.ToInt32(result);
            errors.Add("positional [id] returned null");
        }
        catch (Exception ex)
        {
            errors.Add("positional [id]: " + DescribeException(ex));
        }

        try
        {
            using var inParams = other.GetMethodParameters("GetFeatureValue");
            SetParameter(inParams, ["IDs", "Data", "Id", "ID", "FeatureId", "AttributeId"], id);
            using var outParams = other.InvokeMethod("GetFeatureValue", inParams, null);
            if (TryGetParameter(outParams, ["value", "Value", "Data"], out var value))
                return Convert.ToInt32(value);
            errors.Add("named parameters returned no value");
        }
        catch (Exception ex)
        {
            errors.Add("named parameters: " + DescribeException(ex));
        }

        throw new InvalidOperationException($"GetFeatureValue(0x{id:X8}) failed. " + string.Join(" | ", errors));
    }

    private static void SetFeatureValue(ManagementObject other, uint id, int value)
    {
        var errors = new List<string>();
        var unsignedValue = checked((uint)value);

        try
        {
            var args = new object?[] { id, unsignedValue };
            _ = other.InvokeMethod("SetFeatureValue", args);
            return;
        }
        catch (Exception ex)
        {
            errors.Add("positional [id, value]: " + DescribeException(ex));
        }

        try
        {
            using var inParams = other.GetMethodParameters("SetFeatureValue");
            SetParameter(inParams, ["IDs", "Data", "Id", "ID", "FeatureId", "AttributeId"], id);
            SetParameter(inParams, ["value", "Value"], unsignedValue);
            using var outParams = other.InvokeMethod("SetFeatureValue", inParams, null);
            return;
        }
        catch (Exception ex)
        {
            errors.Add("named parameters: " + DescribeException(ex));
        }

        throw new InvalidOperationException($"SetFeatureValue(0x{id:X8}, {value}) failed. " + string.Join(" | ", errors));
    }

    private static void SetParameter(ManagementBaseObject parameters, string[] names, object value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is null)
                continue;

            parameters[name] = value;
            return;
        }

        var available = string.Join(", ", parameters.Properties.Cast<PropertyData>().Select(property => property.Name));
        throw new InvalidOperationException($"None of the WMI parameters [{string.Join(", ", names)}] exist. Available: {available}");
    }

    private static bool TryGetParameter(ManagementBaseObject parameters, string[] names, out object? value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is null)
                continue;

            value = parameters[name];
            return value is not null;
        }

        value = null;
        return false;
    }

    private static string DescribeException(Exception exception)
    {
        return exception is ManagementException managementException
            ? $"{exception.GetType().Name}({managementException.ErrorCode}): {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";
    }
}
