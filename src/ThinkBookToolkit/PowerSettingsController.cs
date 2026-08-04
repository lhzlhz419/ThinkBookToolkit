using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

public sealed record PowerSettingsState(
    int CpuPl1,
    int CpuPl2,
    int CpuTemperatureLimit,
    int CpuTurboTimeLimit,
    int GpuPowerBoost,
    int GpuConfigurableTgp,
    int GpuTemperatureLimit,
    int GpuToCpuDynamicBoost,
    int? Atpp = null);

internal static class PowerSettingsController
{
    private static readonly object IoSync = new();

    private const uint CpuPl1Id = 0x01020000;
    private const uint CpuPl2Id = 0x01010000;
    private const uint CpuTemperatureLimitId = 0x01040000;
    private const uint CpuTurboTimeLimitId = 0x01070000;
    private const uint GpuPowerBoostId = 0x02010000;
    private const uint GpuConfigurableTgpId = 0x02020000;
    private const uint GpuTemperatureLimitId = 0x02030000;
    private const uint AtppId = 0x02040000;
    private const uint GpuToCpuDynamicBoostId = 0x020B0000;

    public static IReadOnlyList<int> TurboTimeLimits { get; } =
        [20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160];

    public static IReadOnlyList<int> LockIntervals { get; } =
        [1, 2, 3, 5, 10];

    public static PowerSettingsState? GetDefaultState(ItsMode mode) =>
        DeviceModelDetector.IsThinkBook16pG5Irx()
            ? mode switch
            {
                ItsMode.Intelligent => new(95, 135, 102, 56, 10, 65, 87, 0, null),
                ItsMode.PowerSaving => new(44, 60, 99, 56, 10, 50, 87, 15, null),
                ItsMode.Performance => new(125, 157, 102, 56, 10, 100, 87, 0, null),
                ItsMode.Geek => new(130, 170, 102, 56, 10, 100, 87, 0, null),
                _ => null
            }
            : mode switch
            {
                ItsMode.Intelligent => new(90, 125, 100, 56, 15, 75, 87, 10, 45),
                ItsMode.PowerSaving => new(35, 60, 93, 56, 10, 50, 87, 10, 25),
                ItsMode.Performance => new(125, 180, 100, 56, 15, 100, 87, 0, 85),
                ItsMode.Geek => new(130, 185, 100, 56, 15, 100, 87, 0, 105),
                _ => null
            };

    public static PowerSettingsState ReadState()
    {
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            return ReadState(other);
        }
    }

    public static void WriteState(PowerSettingsState state)
    {
        Validate(state);
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            WriteState(other, state);
        }
    }

    public static PowerSettingsState WriteAndReadState(PowerSettingsState state)
    {
        Validate(state);
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            WriteState(other, state);
            return ReadState(other);
        }
    }

    public static bool IsSupportedLockInterval(int seconds) =>
        LockIntervals.Contains(seconds);

    public static bool IsValidState(PowerSettingsState? state)
    {
        if (state is null)
            return false;
        try
        {
            Validate(state);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool IsValidLockConfiguration(
        PowerSettingsLockSelection? selection,
        PowerSettingsState? target) =>
        selection is { Any: true } &&
        IsValidState(target) &&
        (!selection.Atpp || target!.Atpp.HasValue);

    public static PowerSettingsState WithSetting(
        PowerSettingsState destination,
        PowerSettingsState source,
        PowerSetting setting) => setting switch
        {
            PowerSetting.CpuPl1 => destination with { CpuPl1 = source.CpuPl1 },
            PowerSetting.CpuPl2 => destination with { CpuPl2 = source.CpuPl2 },
            PowerSetting.CpuTemperatureLimit => destination with { CpuTemperatureLimit = source.CpuTemperatureLimit },
            PowerSetting.CpuTurboTimeLimit => destination with { CpuTurboTimeLimit = source.CpuTurboTimeLimit },
            PowerSetting.GpuPowerBoost => destination with { GpuPowerBoost = source.GpuPowerBoost },
            PowerSetting.GpuConfigurableTgp => destination with { GpuConfigurableTgp = source.GpuConfigurableTgp },
            PowerSetting.GpuTemperatureLimit => destination with { GpuTemperatureLimit = source.GpuTemperatureLimit },
            PowerSetting.GpuToCpuDynamicBoost => destination with { GpuToCpuDynamicBoost = source.GpuToCpuDynamicBoost },
            PowerSetting.Atpp => destination with { Atpp = source.Atpp },
            _ => destination
        };

    public static bool RequiresLockReapply(
        PowerSettingsState current,
        PowerSettingsState target,
        PowerSettingsLockSelection selection) =>
        selection.CpuPl1 && current.CpuPl1 != target.CpuPl1 ||
        selection.CpuPl2 && current.CpuPl2 != target.CpuPl2 ||
        selection.CpuTemperatureLimit && current.CpuTemperatureLimit != target.CpuTemperatureLimit ||
        selection.CpuTurboTimeLimit && current.CpuTurboTimeLimit != target.CpuTurboTimeLimit ||
        selection.GpuPowerBoost && current.GpuPowerBoost != target.GpuPowerBoost ||
        selection.GpuConfigurableTgp && current.GpuConfigurableTgp != target.GpuConfigurableTgp ||
        selection.GpuTemperatureLimit && current.GpuTemperatureLimit != target.GpuTemperatureLimit ||
        selection.GpuToCpuDynamicBoost && current.GpuToCpuDynamicBoost != target.GpuToCpuDynamicBoost ||
        selection.Atpp && current.Atpp.HasValue && current.Atpp != target.Atpp;

    public static void WriteLockedState(
        PowerSettingsState current,
        PowerSettingsState target,
        PowerSettingsLockSelection selection)
    {
        if (!IsValidLockConfiguration(selection, target))
            throw new ArgumentException("The power lock configuration is invalid.");

        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            if (selection.CpuPl1 && current.CpuPl1 != target.CpuPl1)
                SetFeatureValue(other, CpuPl1Id, target.CpuPl1);
            if (selection.CpuPl2 && current.CpuPl2 != target.CpuPl2)
                SetFeatureValue(other, CpuPl2Id, target.CpuPl2);
            if (selection.CpuTemperatureLimit && current.CpuTemperatureLimit != target.CpuTemperatureLimit)
                SetFeatureValue(other, CpuTemperatureLimitId, target.CpuTemperatureLimit - 5);
            if (selection.CpuTurboTimeLimit && current.CpuTurboTimeLimit != target.CpuTurboTimeLimit)
                SetFeatureValue(other, CpuTurboTimeLimitId, target.CpuTurboTimeLimit);
            if (selection.GpuPowerBoost && current.GpuPowerBoost != target.GpuPowerBoost)
                SetFeatureValue(other, GpuPowerBoostId, target.GpuPowerBoost);
            if (selection.GpuConfigurableTgp && current.GpuConfigurableTgp != target.GpuConfigurableTgp)
                SetFeatureValue(other, GpuConfigurableTgpId, target.GpuConfigurableTgp - 50);
            if (selection.GpuTemperatureLimit && current.GpuTemperatureLimit != target.GpuTemperatureLimit)
                SetFeatureValue(other, GpuTemperatureLimitId, target.GpuTemperatureLimit);
            if (selection.GpuToCpuDynamicBoost && current.GpuToCpuDynamicBoost != target.GpuToCpuDynamicBoost)
                SetFeatureValue(other, GpuToCpuDynamicBoostId, target.GpuToCpuDynamicBoost);
            if (selection.Atpp && current.Atpp.HasValue && current.Atpp != target.Atpp)
                SetFeatureValue(other, AtppId, target.Atpp!.Value);
        }
    }

    private static PowerSettingsState ReadState(ManagementObject other)
    {
        int? atpp = TryReadFeatureValue(other, AtppId, out var atppValue) &&
                    atppValue > 0
            ? atppValue
            : null;
        var state = new PowerSettingsState(
            ReadFeatureValue(other, CpuPl1Id),
            ReadFeatureValue(other, CpuPl2Id),
            checked(ReadFeatureValue(other, CpuTemperatureLimitId) + 5),
            ReadFeatureValue(other, CpuTurboTimeLimitId),
            ReadFeatureValue(other, GpuPowerBoostId),
            checked(ReadFeatureValue(other, GpuConfigurableTgpId) + 50),
            ReadFeatureValue(other, GpuTemperatureLimitId),
            ReadFeatureValue(other, GpuToCpuDynamicBoostId),
            atpp);
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
        if (state.Atpp.HasValue)
            SetFeatureValue(other, AtppId, state.Atpp.Value);
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
        if (state.Atpp.HasValue)
            RequirePositive(nameof(state.Atpp), state.Atpp.Value);
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

    private static bool TryReadFeatureValue(
        ManagementObject other,
        uint id,
        out int value)
    {
        try
        {
            value = ReadFeatureValue(other, id);
            return true;
        }
        catch
        {
            value = 0;
            return false;
        }
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
