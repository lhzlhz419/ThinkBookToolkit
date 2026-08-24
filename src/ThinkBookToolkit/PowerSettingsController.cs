using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace ThinkBookToolkit;

[Flags]
public enum PowerSettingAvailability
{
    None = 0,
    CpuPl1 = 1 << 0,
    CpuPl2 = 1 << 1,
    CpuTemperatureLimit = 1 << 2,
    CpuTurboTimeLimit = 1 << 3,
    GpuPowerBoost = 1 << 4,
    GpuConfigurableTgp = 1 << 5,
    GpuTemperatureLimit = 1 << 6,
    GpuToCpuDynamicBoost = 1 << 7,
    Atpp = 1 << 8,
    All = (1 << 9) - 1
}

public sealed record PowerSettingsState(
    int CpuPl1,
    int CpuPl2,
    int CpuTemperatureLimit,
    int CpuTurboTimeLimit,
    int GpuPowerBoost,
    int GpuConfigurableTgp,
    int GpuTemperatureLimit,
    int GpuToCpuDynamicBoost,
    int? Atpp = null)
{
    public PowerSettingAvailability AvailableSettings { get; init; } =
        PowerSettingAvailability.All;

    public bool IsAvailable(PowerSetting setting) =>
        (AvailableSettings & PowerSettingsController.Flag(setting)) != 0;
}

internal enum PowerDeviceKind
{
    ReadOnly,
    ThinkBook16pG6Iax,
    ThinkBook16pG5Irx,
    ThinkBook16pG5IrxRtx4050,
    ThinkBook14G6PlusImh
}

internal sealed record PowerSettingRule(
    int SliderMinimum,
    int SliderMaximum,
    int? ManualMinimum = null);

internal sealed record PowerDeviceProfile(
    PowerDeviceKind Kind,
    bool Writable,
    bool SupportsDefaults,
    PowerSettingAvailability ExpectedSettings,
    int CpuTemperatureOffset,
    int GpuTgpOffset,
    IReadOnlyDictionary<PowerSetting, PowerSettingRule> Rules)
{
    public bool IsExpected(PowerSetting setting) =>
        (ExpectedSettings & PowerSettingsController.Flag(setting)) != 0;
}

internal static class PowerSettingsController
{
    private static readonly object IoSync = new();
    private static PowerDeviceProfile? _profileForTesting;

    private const uint CpuPl1Id = 0x01020000;
    private const uint CpuPl2Id = 0x01010000;
    private const uint CpuTemperatureLimitId = 0x01040000;
    private const uint CpuTurboTimeLimitId = 0x01070000;
    private const uint GpuPowerBoostId = 0x02010000;
    private const uint GpuConfigurableTgpId = 0x02020000;
    private const uint GpuTemperatureLimitId = 0x02030000;
    private const uint AtppId = 0x02040000;
    private const uint GpuToCpuDynamicBoostId = 0x020B0000;

    private static readonly PowerSetting[] Settings =
        Enum.GetValues<PowerSetting>();

    private static readonly Lazy<PowerDeviceProfile> DetectedProfile = new(() =>
    {
        IReadOnlyList<string> gpus = [];
        try
        {
            gpus = DeviceInformationService.ReadAll().Gpus
                .Select(gpu => gpu.Name)
                .ToArray();
        }
        catch
        {
        }
        return ResolveProfile(DeviceModelDetector.CurrentIdentity.Model, gpus);
    });

    public static IReadOnlyList<int> TurboTimeLimits { get; } =
        [20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160];

    public static IReadOnlyList<int> LockIntervals { get; } = [1, 2, 3, 5, 10];

    public static PowerDeviceProfile CurrentProfile =>
        _profileForTesting ?? DetectedProfile.Value;

    internal static void SetProfileForTesting(PowerDeviceProfile? profile) =>
        _profileForTesting = profile;

    internal static PowerDeviceProfile ResolveProfile(
        string model,
        IReadOnlyList<string>? gpuNames = null)
    {
        var baseRules = G6Rules();
        if (DeviceModelDetector.ModelMatches(model, DeviceModelDetector.ThinkBook16pG6Iax))
            return new(PowerDeviceKind.ThinkBook16pG6Iax, true, true,
                PowerSettingAvailability.All, 5, 50, baseRules);

        if (DeviceModelDetector.ModelMatches(model, DeviceModelDetector.ThinkBook16pG5Irx))
        {
            var is4050 = gpuNames?.Any(name =>
                name.Contains("4050", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)) == true;
            var rules = new Dictionary<PowerSetting, PowerSettingRule>(baseRules)
            {
                [PowerSetting.CpuTemperatureLimit] = new(70, 100),
                [PowerSetting.GpuPowerBoost] = new(0, 25),
                [PowerSetting.GpuConfigurableTgp] = new(55, 115),
                [PowerSetting.Atpp] = new(20, 95, 1)
            };
            return new(
                is4050 ? PowerDeviceKind.ThinkBook16pG5IrxRtx4050 : PowerDeviceKind.ThinkBook16pG5Irx,
                true, true, PowerSettingAvailability.All, 0, 55, rules);
        }

        if (DeviceModelDetector.ModelMatches(model, DeviceModelDetector.ThinkBook14G6PlusImh))
        {
            var available = Flag(PowerSetting.CpuPl1) |
                            Flag(PowerSetting.CpuPl2) |
                            Flag(PowerSetting.CpuTemperatureLimit) |
                            Flag(PowerSetting.GpuPowerBoost) |
                            Flag(PowerSetting.GpuConfigurableTgp) |
                            Flag(PowerSetting.GpuToCpuDynamicBoost);
            var rules = new Dictionary<PowerSetting, PowerSettingRule>
            {
                [PowerSetting.CpuPl1] = new(10, 100, 1),
                [PowerSetting.CpuPl2] = new(20, 120, 1),
                [PowerSetting.CpuTemperatureLimit] = new(70, 100),
                [PowerSetting.GpuPowerBoost] = new(0, 15),
                [PowerSetting.GpuConfigurableTgp] = new(60, 65),
                [PowerSetting.GpuToCpuDynamicBoost] = new(0, 50)
            };
            return new(PowerDeviceKind.ThinkBook14G6PlusImh, true, false,
                available, 0, 60, rules);
        }

        return new(PowerDeviceKind.ReadOnly, false, false,
            PowerSettingAvailability.All, 0, 0,
            new Dictionary<PowerSetting, PowerSettingRule>());
    }

    internal static PowerSettingAvailability RequiredSettingsForFullAvailability(
        PowerDeviceProfile profile) =>
        profile.Kind is PowerDeviceKind.ThinkBook16pG6Iax or
            PowerDeviceKind.ThinkBook16pG5Irx or
            PowerDeviceKind.ThinkBook16pG5IrxRtx4050
            ? profile.ExpectedSettings & ~PowerSettingAvailability.Atpp
            : profile.ExpectedSettings;

    private static IReadOnlyDictionary<PowerSetting, PowerSettingRule> G6Rules() =>
        new Dictionary<PowerSetting, PowerSettingRule>
        {
            [PowerSetting.CpuPl1] = new(30, 150, 1),
            [PowerSetting.CpuPl2] = new(30, 200, 1),
            [PowerSetting.CpuTemperatureLimit] = new(75, 105),
            [PowerSetting.CpuTurboTimeLimit] = new(20, 160),
            [PowerSetting.GpuPowerBoost] = new(0, 15, 0),
            [PowerSetting.GpuConfigurableTgp] = new(50, 100),
            [PowerSetting.GpuTemperatureLimit] = new(75, 87),
            [PowerSetting.GpuToCpuDynamicBoost] = new(0, 50),
            [PowerSetting.Atpp] = new(25, 105, 1)
        };

    public static PowerSettingsState? GetDefaultState(ItsMode mode) =>
        GetDefaultState(mode, CurrentProfile);

    internal static PowerSettingsState? GetDefaultState(
        ItsMode mode,
        PowerDeviceProfile profile)
    {
        PowerSettingsState? state = profile.Kind switch
        {
            PowerDeviceKind.ThinkBook16pG5Irx or
            PowerDeviceKind.ThinkBook16pG5IrxRtx4050 => mode switch
            {
                ItsMode.Intelligent => new(95, 135, 97, 56, 10, 70, 87, 0, 55),
                ItsMode.PowerSaving => new(44, 60, 94, 56, 10, 55, 87, 15, 20),
                ItsMode.Performance when profile.Kind == PowerDeviceKind.ThinkBook16pG5IrxRtx4050 =>
                    new(125, 157, 97, 56, 10, 85, 87, 0, 80),
                ItsMode.Performance => new(125, 157, 97, 56, 10, 105, 87, 0, 75),
                ItsMode.Geek => new(130, 170, 97, 56, 10, 105, 87, 0, 95),
                _ => null
            },
            PowerDeviceKind.ThinkBook16pG6Iax => mode switch
            {
                ItsMode.Intelligent => new(90, 125, 100, 56, 15, 75, 87, 10, 45),
                ItsMode.PowerSaving => new(35, 60, 93, 56, 10, 50, 87, 10, 25),
                ItsMode.Performance => new(125, 180, 100, 56, 15, 100, 87, 0, 85),
                ItsMode.Geek => new(130, 185, 100, 56, 15, 100, 87, 0, 105),
                _ => null
            },
            _ => null
        };
        return state is null
            ? null
            : state with { AvailableSettings = profile.ExpectedSettings };
    }

    public static PowerSettingsState ReadState()
    {
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            return ReadState(other, CurrentProfile);
        }
    }

    public static void WriteState(PowerSettingsState state)
    {
        Validate(state, CurrentProfile);
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            WriteState(other, state, CurrentProfile);
        }
    }

    public static PowerSettingsState WriteAndReadState(PowerSettingsState state)
    {
        Validate(state, CurrentProfile);
        lock (IoSync)
        {
            using var other = GetActiveOtherMethod();
            WriteState(other, state, CurrentProfile);
            return ReadState(other, CurrentProfile);
        }
    }

    public static bool IsSupportedLockInterval(int seconds) => LockIntervals.Contains(seconds);

    public static bool IsValidState(PowerSettingsState? state)
        => IsValidState(state, CurrentProfile);

    internal static bool IsValidState(
        PowerSettingsState? state,
        PowerDeviceProfile profile)
    {
        if (state is null)
            return false;
        try { Validate(state, profile); return true; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static bool IsValidLockConfiguration(
        PowerSettingsLockSelection? selection,
        PowerSettingsState? target) =>
        IsValidLockConfiguration(selection, target, CurrentProfile);

    internal static bool IsValidLockConfiguration(
        PowerSettingsLockSelection? selection,
        PowerSettingsState? target,
        PowerDeviceProfile profile) =>
        selection is { Any: true } && IsValidState(target, profile) &&
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
        Settings.Any(setting => selection.IsLocked(setting) &&
            current.IsAvailable(setting) && target.IsAvailable(setting) &&
            Value(current, setting) is int currentValue &&
            Value(target, setting) is int targetValue &&
            currentValue != targetValue);

    internal static PowerSettingsState ApplyLockedValues(
        PowerSettingsState current,
        PowerSettingsState target,
        PowerSettingsLockSelection selection)
    {
        var result = current;
        foreach (var setting in Settings)
        {
            if (selection.IsLocked(setting) &&
                current.IsAvailable(setting) &&
                target.IsAvailable(setting))
            {
                result = WithSetting(result, target, setting);
            }
        }
        return result;
    }

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
            foreach (var setting in Settings)
            {
                if (selection.IsLocked(setting) && current.IsAvailable(setting) &&
                    target.IsAvailable(setting) &&
                    Value(current, setting) is int currentValue &&
                    Value(target, setting) is int targetValue &&
                    currentValue != targetValue)
                {
                    WriteSetting(other, setting, targetValue, CurrentProfile);
                }
            }
        }
    }

    internal static PowerSettingAvailability Flag(PowerSetting setting) =>
        (PowerSettingAvailability)(1 << (int)setting);

    internal static string DisplayName(PowerSetting setting, bool writable) =>
        setting == PowerSetting.GpuConfigurableTgp
            ? writable ? "GPU TGP" : "GPU Configurable TGP"
            : setting switch
            {
                PowerSetting.CpuPl1 => "CPU PL1",
                PowerSetting.CpuPl2 => "CPU PL2",
                PowerSetting.CpuTemperatureLimit => "CPU temperature limit",
                PowerSetting.CpuTurboTimeLimit => "CPU Turbo Time Limit",
                PowerSetting.GpuPowerBoost => "GPU Power Boost",
                PowerSetting.GpuTemperatureLimit => "GPU temperature limit",
                PowerSetting.GpuToCpuDynamicBoost => "GPU to CPU Dynamic Boost",
                PowerSetting.Atpp => "ATPP",
                _ => setting.ToString()
            };

    private static PowerSettingsState ReadState(ManagementObject other, PowerDeviceProfile profile)
    {
        var raw = new Dictionary<PowerSetting, int>();
        foreach (var setting in Settings.Where(profile.IsExpected))
        {
            if (TryReadFeatureValue(other, Id(setting), out var value))
                raw[setting] = value;
        }
        if (raw.Count == 0)
            throw new InvalidOperationException("No WMI power values could be read.");

        int Read(PowerSetting setting)
        {
            if (!raw.TryGetValue(setting, out var value))
                return 0;
            return setting switch
            {
                PowerSetting.CpuTemperatureLimit => checked(value + profile.CpuTemperatureOffset),
                PowerSetting.GpuConfigurableTgp => checked(value + profile.GpuTgpOffset),
                _ => value
            };
        }
        var available = raw.Keys.Aggregate(PowerSettingAvailability.None,
            (result, setting) => result | Flag(setting));
        return new PowerSettingsState(
            Read(PowerSetting.CpuPl1), Read(PowerSetting.CpuPl2),
            Read(PowerSetting.CpuTemperatureLimit), Read(PowerSetting.CpuTurboTimeLimit),
            Read(PowerSetting.GpuPowerBoost), Read(PowerSetting.GpuConfigurableTgp),
            Read(PowerSetting.GpuTemperatureLimit), Read(PowerSetting.GpuToCpuDynamicBoost),
            raw.ContainsKey(PowerSetting.Atpp) ? Read(PowerSetting.Atpp) : null)
        { AvailableSettings = available };
    }

    private static void WriteState(
        ManagementObject other,
        PowerSettingsState state,
        PowerDeviceProfile profile)
    {
        if (!profile.Writable)
            throw new NotSupportedException("Power settings are read-only on this device.");
        foreach (var setting in Settings)
        {
            if (profile.IsExpected(setting) && state.IsAvailable(setting) &&
                Value(state, setting) is int value)
                WriteSetting(other, setting, value, profile);
        }
    }

    private static void WriteSetting(
        ManagementObject other,
        PowerSetting setting,
        int value,
        PowerDeviceProfile profile)
    {
        var raw = setting switch
        {
            PowerSetting.CpuTemperatureLimit => value - profile.CpuTemperatureOffset,
            PowerSetting.GpuConfigurableTgp => value - profile.GpuTgpOffset,
            _ => value
        };
        SetFeatureValue(other, Id(setting), raw);
    }

    private static void Validate(PowerSettingsState state, PowerDeviceProfile profile)
    {
        if (!profile.Writable)
            throw new NotSupportedException("Power settings are read-only on this device.");
        foreach (var setting in Settings)
        {
            if (!profile.IsExpected(setting) || !state.IsAvailable(setting))
                continue;
            var value = Value(state, setting);
            if (!value.HasValue)
                throw new ArgumentException($"{setting} is required.");
            if (setting == PowerSetting.CpuTurboTimeLimit)
            {
                if (!TurboTimeLimits.Contains(value.Value))
                    throw new ArgumentOutOfRangeException(nameof(state.CpuTurboTimeLimit));
                continue;
            }
            if (!profile.Rules.TryGetValue(setting, out var rule))
                continue;
            if (rule.ManualMinimum.HasValue)
            {
                if (value.Value < rule.ManualMinimum.Value)
                    throw new ArgumentOutOfRangeException(setting.ToString());
            }
            else if (value.Value < rule.SliderMinimum || value.Value > rule.SliderMaximum)
            {
                throw new ArgumentOutOfRangeException(setting.ToString());
            }
        }
    }

    private static int? Value(PowerSettingsState state, PowerSetting setting) => setting switch
    {
        PowerSetting.CpuPl1 => state.CpuPl1,
        PowerSetting.CpuPl2 => state.CpuPl2,
        PowerSetting.CpuTemperatureLimit => state.CpuTemperatureLimit,
        PowerSetting.CpuTurboTimeLimit => state.CpuTurboTimeLimit,
        PowerSetting.GpuPowerBoost => state.GpuPowerBoost,
        PowerSetting.GpuConfigurableTgp => state.GpuConfigurableTgp,
        PowerSetting.GpuTemperatureLimit => state.GpuTemperatureLimit,
        PowerSetting.GpuToCpuDynamicBoost => state.GpuToCpuDynamicBoost,
        PowerSetting.Atpp => state.Atpp,
        _ => null
    };

    private static uint Id(PowerSetting setting) => setting switch
    {
        PowerSetting.CpuPl1 => CpuPl1Id,
        PowerSetting.CpuPl2 => CpuPl2Id,
        PowerSetting.CpuTemperatureLimit => CpuTemperatureLimitId,
        PowerSetting.CpuTurboTimeLimit => CpuTurboTimeLimitId,
        PowerSetting.GpuPowerBoost => GpuPowerBoostId,
        PowerSetting.GpuConfigurableTgp => GpuConfigurableTgpId,
        PowerSetting.GpuTemperatureLimit => GpuTemperatureLimitId,
        PowerSetting.GpuToCpuDynamicBoost => GpuToCpuDynamicBoostId,
        PowerSetting.Atpp => AtppId,
        _ => throw new ArgumentOutOfRangeException(nameof(setting))
    };

    private static ManagementObject GetActiveOtherMethod() =>
        LenovoWmi.GetActiveInstance("LENOVO_OTHER_METHOD");

    private static int ReadFeatureValue(ManagementObject other, uint id)
    {
        var errors = new List<string>();
        try
        {
            var args = new object?[] { id, null };
            other.InvokeMethod("GetFeatureValue", args);
            if (args[1] is not null) return Convert.ToInt32(args[1]);
            errors.Add("positional [id, out value] returned no out value");
        }
        catch (Exception ex) { errors.Add("positional [id, out value]: " + DescribeException(ex)); }
        try
        {
            var args = new object?[] { id };
            var result = other.InvokeMethod("GetFeatureValue", args);
            if (result is not null) return Convert.ToInt32(result);
            errors.Add("positional [id] returned null");
        }
        catch (Exception ex) { errors.Add("positional [id]: " + DescribeException(ex)); }
        try
        {
            using var input = other.GetMethodParameters("GetFeatureValue");
            SetParameter(input, ["IDs", "Data", "Id", "ID", "FeatureId", "AttributeId"], id);
            using var output = other.InvokeMethod("GetFeatureValue", input, null);
            if (TryGetParameter(output, ["value", "Value", "Data"], out var value))
                return Convert.ToInt32(value);
            errors.Add("named parameters returned no value");
        }
        catch (Exception ex) { errors.Add("named parameters: " + DescribeException(ex)); }
        throw new InvalidOperationException($"GetFeatureValue(0x{id:X8}) failed. " + string.Join(" | ", errors));
    }

    private static bool TryReadFeatureValue(ManagementObject other, uint id, out int value)
    {
        try { value = ReadFeatureValue(other, id); return true; }
        catch { value = 0; return false; }
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
        catch (Exception ex) { errors.Add("positional [id, value]: " + DescribeException(ex)); }
        try
        {
            using var input = other.GetMethodParameters("SetFeatureValue");
            SetParameter(input, ["IDs", "Data", "Id", "ID", "FeatureId", "AttributeId"], id);
            SetParameter(input, ["value", "Value"], unsignedValue);
            using var output = other.InvokeMethod("SetFeatureValue", input, null);
            return;
        }
        catch (Exception ex) { errors.Add("named parameters: " + DescribeException(ex)); }
        throw new InvalidOperationException($"SetFeatureValue(0x{id:X8}, {value}) failed. " + string.Join(" | ", errors));
    }

    private static void SetParameter(ManagementBaseObject parameters, string[] names, object value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is null) continue;
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
            if (parameters.Properties[name] is null) continue;
            value = parameters[name];
            return value is not null;
        }
        value = null;
        return false;
    }

    private static string DescribeException(Exception exception) =>
        exception is ManagementException management
            ? $"{exception.GetType().Name}({management.ErrorCode}): {exception.Message}"
            : $"{exception.GetType().Name}: {exception.Message}";
}
