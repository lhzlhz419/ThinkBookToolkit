using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;
using ThinkBookToolkit.FanBackend;

namespace ThinkBookToolkit.FanBackend.Wmi;

public sealed class WmiFanBackend : IFanBackend, IFanBackendCapabilityProbe
{
    private const string NamespacePath = @"root\wmi";
    private const uint FullSpeedId = 0x04020000;
    private const uint Fan1Id = 0x04030001;
    private const uint Fan2Id = 0x04030002;
    private const uint XiaoXinFan2Id = 0x04030004;
    private readonly bool _qlcn = IsXiaoXinQlcn();
    private IReadOnlyDictionary<string, FanBackendRange>? _cachedLimits;
    private bool? _fan2Available;

    public WmiFanBackend()
    {
        if (FanBackendRuntimeContext.DeclaredFanCount == 1)
            _fan2Available = false;
    }

    // Both the application and the recovery worker construct this backend.
    private static bool IsXiaoXinQlcn()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var version = bios?.GetValue("BIOSVersion") as string;
        var product = bios?.GetValue("SystemProductName") as string;
        var family = bios?.GetValue("SystemFamily") as string;
        return MatchesQlcn(version, product, family);
    }

    internal static bool MatchesQlcn(string? version, string? product, string? family)
    {
        static bool Model(string? value) =>
            value?.Contains("XiaoXinPro 14 IAH10", StringComparison.OrdinalIgnoreCase) == true ||
            value?.Contains("XiaoXinPro 14 IAX10", StringComparison.OrdinalIgnoreCase) == true;
        return version?.StartsWith("QLCN", StringComparison.OrdinalIgnoreCase) == true &&
            (product?.StartsWith("83JK", StringComparison.OrdinalIgnoreCase) == true ||
             Model(family) || Model(product));
    }

    public Version ApiVersion => FanBackendContract.CurrentVersion;

    public string Name => "Lenovo WMI fan backend";

    public string Transport => "WMI";

    public FanBackendStartupNotice? StartupNotice => null;

    public bool SupportsDisableControlOnSleep => false;

    public TimeSpan MinimumReadInterval => TimeSpan.FromSeconds(0.5);

    public TimeSpan MinimumWriteInterval => TimeSpan.FromSeconds(6);

    public FanBackendControlSemantics ControlSemantics => new(
        FanTargetZeroBehavior.ReleaseFanToFirmwareControl,
        _qlcn ? FanAutomaticControlRestoreMechanism.DedicatedBackendOperation : FanAutomaticControlRestoreMechanism.WriteZeroToBothTargets,
        _qlcn ? "QLCN: write 0 once to FAN1 to release both fans" : "Lenovo WMI SetFeatureValue: write 0 to FAN1 and FAN2",
        new(
            FanFullSpeedControlMechanism.FeatureToggle,
            "Lenovo WMI SetFeatureValue(0x04020000, 1)",
            "Lenovo WMI SetFeatureValue(0x04020000, 0)"));

    public FanBackendSnapshot ReadSnapshot()
    {
        using var other = GetActiveOtherMethod();
        var fan1 = ReadFeatureValue(other, Fan1Id);
        var limits = _cachedLimits ??= ReadFanLimits();
        _fan2Available ??= limits.ContainsKey("fan2");
        var fan2 = fan1;
        if (_fan2Available == true)
        {
            try
            {
                fan2 = ReadFeatureValue(other, _qlcn ? XiaoXinFan2Id : Fan2Id);
            }
            catch when (!_qlcn)
            {
                _fan2Available = false;
            }
        }
        return new FanBackendSnapshot(DateTimeOffset.Now, fan1, fan2, limits);
    }

    public void Apply(int fan1Rpm, int fan2Rpm)
    {
        using var other = GetActiveOtherMethod();
        if (_qlcn)
        {
            ApplyQlcnTargets(fan1Rpm, fan2Rpm, (id, value) => SetFeatureValue(other, id, value));
            return;
        }
        SetFeatureValue(other, Fan1Id, fan1Rpm);
        if (_fan2Available is null)
        {
            try
            {
                var limits = _cachedLimits ??= ReadFanLimits();
                _fan2Available = limits.ContainsKey("fan2");
            }
            catch
            {
                // Fall back to probing FAN2 once when range data is absent.
            }
        }
        if (_fan2Available != false)
        {
            try
            {
                SetFeatureValue(other, Fan2Id, fan2Rpm);
                _fan2Available = true;
            }
            catch when (_fan2Available is null)
            {
                _fan2Available = false;
            }
        }
    }

    internal static void ApplyQlcnTargets(int fan1Rpm, int fan2Rpm, Action<uint, int> write)
    {
        // WMAE divides nonzero RPM by 100 into an 8-bit EC argument.
        if (fan1Rpm is < 0 or > 25599 || fan2Rpm is < 0 or > 25599 ||
            fan1Rpm is > 0 and < 100 || fan2Rpm is > 0 and < 100)
            throw new ArgumentOutOfRangeException("RPM");
        try
        {
            // Both zero IDs issue the same global release (0x84). Release
            // first so a zero FAN2 cannot cancel a new manual FAN1 target.
            if (fan1Rpm == 0 || fan2Rpm == 0) write(Fan1Id, 0);
            if (fan1Rpm > 0) write(Fan1Id, fan1Rpm);
            if (fan2Rpm > 0) write(XiaoXinFan2Id, fan2Rpm);
        }
        catch
        {
            write(Fan1Id, 0);
            throw;
        }
    }

    public void RestoreAuto() => Apply(0, 0);

    public void SetFullSpeed(bool enabled)
    {
        using var other = GetActiveOtherMethod();
        SetFeatureValue(other, FullSpeedId, enabled ? 1 : 0);
    }

    public bool TryProbeFullSpeedControl(out string detail)
    {
        try
        {
            using var other = GetActiveOtherMethod();
            _ = ReadFeatureValue(other, FullSpeedId);
            detail = _qlcn ? "QLCN: 0x04020000 writes FFON; its getter always returns zero."
                : "Lenovo WMI full-speed feature 0x04020000 is readable.";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Lenovo WMI full-speed feature 0x04020000 is unavailable: " +
                     DescribeException(ex);
            return false;
        }
    }

    private static ManagementObject GetActiveOtherMethod()
    {
        using var searcher = new ManagementObjectSearcher(
            NamespacePath,
            "SELECT * FROM LENOVO_OTHER_METHOD");
        foreach (ManagementObject item in searcher.Get())
        {
            if (IsActive(item))
                return item;
            item.Dispose();
        }

        throw new InvalidOperationException(
            "No active LENOVO_OTHER_METHOD instance found.");
    }

    private IReadOnlyDictionary<string, FanBackendRange> ReadFanLimits()
    {
        using var searcher = new ManagementObjectSearcher(
            NamespacePath,
            "SELECT * FROM LENOVO_FAN_TEST_DATA");
        foreach (ManagementObject item in searcher.Get())
        {
            using (item)
            {
                if (!IsActive(item))
                    continue;

                var ids = ToIntArray(item["FanId"]);
                var mins = ToIntArray(item["FanMinSpeed"]);
                var maxes = ToIntArray(item["FanMaxSpeed"]);
                if (_qlcn && (ids.Length < 2 || ids[0] != 1 || ids[1] != 4 ||
                    mins.Length < 2 || maxes.Length < 2))
                    throw new InvalidOperationException("Unexpected QLCN fan range table (expected fan IDs 1 and 4).");
                if (ids.Length < 1 || mins.Length < 1 || maxes.Length < 1)
                    break;

                var result = new Dictionary<string, FanBackendRange>
                {
                    ["fan1"] = new("fan1", Fan1Id, mins[0], maxes[0])
                };
                if (FanBackendRuntimeContext.DeclaredFanCount > 1 &&
                    ids.Length >= 2 && mins.Length >= 2 && maxes.Length >= 2)
                {
                    result["fan2"] = new(
                        "fan2",
                        _qlcn ? XiaoXinFan2Id : Fan2Id,
                        mins[1],
                        maxes[1]);
                }
                return result;
            }
        }

        throw new InvalidOperationException(
            "No active LENOVO_FAN_TEST_DATA instance found.");
    }

    private static int ReadFeatureValue(ManagementObject other, uint id)
    {
        var errors = new List<string>();

        try
        {
            var args = new object?[] { id, null };
            other.InvokeMethod("GetFeatureValue", args);
            if (args.Length > 1 && args[1] is not null)
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
            SetParameter(
                inParams,
                ["Data", "Id", "ID", "FeatureId", "AttributeId"],
                id);
            using var outParams = other.InvokeMethod(
                "GetFeatureValue",
                inParams,
                null);
            if (TryGetParameter(outParams, ["value", "Value", "Data"], out var value))
                return Convert.ToInt32(value);
            errors.Add("named parameters returned no value");
        }
        catch (Exception ex)
        {
            errors.Add("named parameters: " + DescribeException(ex));
        }

        throw new InvalidOperationException(
            $"GetFeatureValue(0x{id:X8}) failed. " + string.Join(" | ", errors));
    }

    private static void SetFeatureValue(ManagementObject other, uint id, int value)
    {
        var errors = new List<string>();

        try
        {
            var args = new object?[] { id, unchecked((uint)value) };
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
            SetParameter(
                inParams,
                ["IDs", "Data", "Id", "ID", "FeatureId", "AttributeId"],
                id);
            SetParameter(inParams, ["value", "Value"], unchecked((uint)value));
            using var outParams = other.InvokeMethod(
                "SetFeatureValue",
                inParams,
                null);
            return;
        }
        catch (Exception ex)
        {
            errors.Add("named parameters: " + DescribeException(ex));
        }

        throw new InvalidOperationException(
            $"SetFeatureValue(0x{id:X8}, {value}) failed. " +
            string.Join(" | ", errors));
    }

    private static void SetParameter(
        ManagementBaseObject parameters,
        string[] names,
        object value)
    {
        foreach (var name in names)
        {
            if (parameters.Properties[name] is null)
                continue;
            parameters[name] = value;
            return;
        }

        var available = string.Join(
            ", ",
            parameters.Properties.Cast<PropertyData>().Select(property => property.Name));
        throw new InvalidOperationException(
            $"None of the WMI parameters [{string.Join(", ", names)}] exist. " +
            $"Available: {available}");
    }

    private static bool TryGetParameter(
        ManagementBaseObject parameters,
        string[] names,
        out object? value)
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

    private static string DescribeException(Exception exception) =>
        exception is ManagementException managementException
            ? $"{exception.GetType().Name}({managementException.ErrorCode}): " +
              exception.Message
            : $"{exception.GetType().Name}: {exception.Message}";

    private static bool IsActive(ManagementBaseObject item) =>
        item.Properties["Active"] is null || Convert.ToBoolean(item["Active"]);

    private static int[] ToIntArray(object? value) =>
        value is Array array
            ? array.Cast<object>().Select(Convert.ToInt32).ToArray()
            : [];
}
