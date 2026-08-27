using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU.Structures;

namespace ThinkBookToolkit;

internal sealed record NvPcfPowerSnapshot(
    int AcTargetTppLimitW,
    int AcDefaultGpuLimitW,
    int AcMinGpuLimitW,
    int AcMaxGpuLimitW,
    int SliderMinimumW,
    int SliderMaximumW,
    bool? DynamicBoostEnabled,
    int? GpuTemperatureLimitC,
    int? GpuTemperatureMinimumC,
    int? GpuTemperatureMaximumC,
    string LayoutName);

internal static class NvPcfPowerPolicy
{
    public static PowerSettingAvailability LegacyGpuMask =>
        PowerSettingsController.Flag(PowerSetting.GpuPowerBoost) |
        PowerSettingsController.Flag(PowerSetting.GpuConfigurableTgp) |
        PowerSettingsController.Flag(PowerSetting.GpuToCpuDynamicBoost) |
        PowerSettingsController.Flag(PowerSetting.Atpp);

    public static PowerSettingAvailability NvPcfMask =>
        PowerSettingsController.Flag(PowerSetting.NvPcfAcTargetTppLimit) |
        PowerSettingsController.Flag(PowerSetting.NvPcfAcDefaultGpuLimit) |
        PowerSettingsController.Flag(PowerSetting.NvPcfAcMinGpuLimit) |
        PowerSettingsController.Flag(PowerSetting.NvPcfAcMaxGpuLimit);

    public static PowerSettingAvailability NvPcfOptionalMask =>
        PowerSettingsController.Flag(PowerSetting.NvPcfDynamicBoost) |
        PowerSettingsController.Flag(PowerSetting.NvApiGpuTemperatureLimit);

    public static PowerSettingsState Merge(
        PowerSettingsState? wmi,
        NvPcfPowerSnapshot nvPcf)
    {
        var result = wmi ?? EmptyState();
        return result with
        {
            AvailableSettings = MergeAvailability(result, nvPcf),
            NvPcfAcTargetTppLimit = nvPcf.AcTargetTppLimitW,
            NvPcfAcDefaultGpuLimit = nvPcf.AcDefaultGpuLimitW,
            NvPcfAcMinGpuLimit = nvPcf.AcMinGpuLimitW,
            NvPcfAcMaxGpuLimit = nvPcf.AcMaxGpuLimitW,
            NvPcfDynamicBoostEnabled = nvPcf.DynamicBoostEnabled,
            NvApiGpuTemperatureLimit = nvPcf.GpuTemperatureLimitC
        };
    }

    private static PowerSettingAvailability MergeAvailability(
        PowerSettingsState state,
        NvPcfPowerSnapshot snapshot)
    {
        var available = (state.AvailableSettings & ~LegacyGpuMask) | NvPcfMask;
        if (snapshot.DynamicBoostEnabled.HasValue)
            available |= PowerSettingsController.Flag(PowerSetting.NvPcfDynamicBoost);
        if (snapshot.GpuTemperatureLimitC.HasValue)
        {
            available &= ~PowerSettingsController.Flag(PowerSetting.GpuTemperatureLimit);
            available |= PowerSettingsController.Flag(PowerSetting.NvApiGpuTemperatureLimit);
        }
        return available;
    }

    public static PowerSettingsState FromLegacy(PowerSettingsState state)
    {
        var defaultGpu = state.GpuConfigurableTgp;
        return state with
        {
            AvailableSettings =
                ((state.AvailableSettings & ~LegacyGpuMask) | NvPcfMask) |
                (state.IsAvailable(PowerSetting.GpuTemperatureLimit)
                    ? PowerSettingsController.Flag(
                        PowerSetting.NvApiGpuTemperatureLimit)
                    : PowerSettingAvailability.None),
            NvPcfAcTargetTppLimit = checked((state.Atpp ?? 0) + defaultGpu),
            NvPcfAcDefaultGpuLimit = defaultGpu,
            NvPcfAcMinGpuLimit = checked(
                defaultGpu - state.GpuToCpuDynamicBoost),
            NvPcfAcMaxGpuLimit = checked(defaultGpu + state.GpuPowerBoost),
            NvApiGpuTemperatureLimit = state.GpuTemperatureLimit
        };
    }

    public static PowerSettingsState ToLegacy(PowerSettingsState state)
    {
        if (!TryValues(state, out var target, out var @default,
                out var minimum, out var maximum))
            return state;
        return state with
        {
            AvailableSettings =
                ((state.AvailableSettings &
                  ~NvPcfMask & ~NvPcfOptionalMask) | LegacyGpuMask),
            GpuConfigurableTgp = @default,
            GpuPowerBoost = checked(maximum - @default),
            GpuToCpuDynamicBoost = checked(@default - minimum),
            Atpp = checked(target - @default),
            GpuTemperatureLimit =
                state.NvApiGpuTemperatureLimit ?? state.GpuTemperatureLimit
        };
    }

    public static bool IsValid(PowerSettingsState state) =>
        TryValues(state, out var target, out var @default,
            out var minimum, out var maximum) &&
        target > 0 && @default > 0 && minimum > 0 && maximum > 0;

    public static bool TryValues(
        PowerSettingsState state,
        out int target,
        out int @default,
        out int minimum,
        out int maximum)
    {
        target = state.NvPcfAcTargetTppLimit ?? 0;
        @default = state.NvPcfAcDefaultGpuLimit ?? 0;
        minimum = state.NvPcfAcMinGpuLimit ?? 0;
        maximum = state.NvPcfAcMaxGpuLimit ?? 0;
        return state.IsAvailable(PowerSetting.NvPcfAcTargetTppLimit) &&
               state.IsAvailable(PowerSetting.NvPcfAcDefaultGpuLimit) &&
               state.IsAvailable(PowerSetting.NvPcfAcMinGpuLimit) &&
               state.IsAvailable(PowerSetting.NvPcfAcMaxGpuLimit);
    }

    private static PowerSettingsState EmptyState() =>
        new(0, 0, 0, 0, 0, 0, 0, 0)
        {
            AvailableSettings = PowerSettingAvailability.None
        };
}

internal static class NvPcfPowerController
{
    private static readonly object Sync = new();
    private static PcfPowerController? _controller;
    private static (int MinimumW, int MaximumW)? _cachedSliderBounds;
    private static bool _dynamicBoostUnsupported;
    private static bool _thermalCapabilityChecked;

    internal static (int MinimumW, int MaximumW)? CachedSliderBounds
    {
        get
        {
            lock (Sync)
                return _cachedSliderBounds;
        }
    }

    internal static (int MinimumC, int MaximumC)? CachedTemperatureBounds
        { get; private set; }

    internal static void SetCachedTemperatureBoundsForTesting(
        (int MinimumC, int MaximumC)? bounds) =>
        CachedTemperatureBounds = bounds;

    internal static void SetCachedSliderBoundsForTesting(
        (int MinimumW, int MaximumW)? bounds)
    {
        lock (Sync)
            _cachedSliderBounds = bounds;
    }

    public static NvPcfPowerSnapshot Read()
    {
        lock (Sync)
        {
            try
            {
                var controller = GetController();
                return Snapshot(
                    controller.GetPowerValues(),
                    controller.Layout,
                    controller.ControllerIndex,
                    TryReadDynamicBoost(controller),
                    TryReadThermalLimit());
            }
            catch
            {
                CloseController();
                throw;
            }
        }
    }

    public static NvPcfPowerSnapshot WriteAndRead(
        PowerSettingsState state,
        PowerSettingsLockSelection? selection = null)
    {
        if (!NvPcfPowerPolicy.IsValid(state))
            throw new ArgumentException(
                "All NVPCF GPU power values must be positive integers.",
                nameof(state));
        lock (Sync)
        {
            try
            {
                var controller = GetController();
                var effectiveSelection = selection ??
                    PowerSettingsLockSelection.AllNvPcf()
                        .With(
                            PowerSetting.NvPcfDynamicBoost,
                            state.IsAvailable(PowerSetting.NvPcfDynamicBoost))
                        .With(
                            PowerSetting.NvApiGpuTemperatureLimit,
                            state.IsAvailable(
                                PowerSetting.NvApiGpuTemperatureLimit));
                var fields = Fields(effectiveSelection);
                if (fields != PcfPowerFields.None)
                    WritePowerFields(controller, fields, state);
                if (effectiveSelection.NvPcfDynamicBoost &&
                    state.NvPcfDynamicBoostEnabled.HasValue)
                {
                    controller.SetDynamicBoostEnabled(
                        state.NvPcfDynamicBoostEnabled.Value);
                }
                if (effectiveSelection.NvApiGpuTemperatureLimit &&
                    state.NvApiGpuTemperatureLimit.HasValue)
                {
                    WriteThermalLimit(state.NvApiGpuTemperatureLimit.Value);
                }
                var confirmed = Snapshot(
                    controller.GetPowerValues(),
                    controller.Layout,
                    controller.ControllerIndex,
                    TryReadDynamicBoost(controller),
                    TryReadThermalLimit());
                ToolkitLog.Info(
                    "NvAPIWrapper PCF power values written and confirmed: " +
                    $"target={confirmed.AcTargetTppLimitW} W; " +
                    $"default={confirmed.AcDefaultGpuLimitW} W; " +
                    $"min={confirmed.AcMinGpuLimitW} W; " +
                    $"max={confirmed.AcMaxGpuLimitW} W.");
                return confirmed;
            }
            catch
            {
                CloseController();
                throw;
            }
        }
    }

    public static void ResetToDefaults()
    {
        lock (Sync)
        {
            try
            {
                GetController().ResetAllOverrides();
                try
                {
                    ResetThermalLimitToDefault();
                }
                catch (Exception ex)
                {
                    ToolkitLog.Warning(
                        "NVAPI GPU thermal limit could not be reset: " +
                        ex.Message);
                }
                ToolkitLog.Info(
                    "NvAPIWrapper reset all PCF power overrides and the " +
                    "supported thermal limit to native defaults.");
            }
            finally
            {
                CloseController();
                _cachedSliderBounds = null;
                CachedTemperatureBounds = null;
                _thermalCapabilityChecked = false;
            }
        }
    }

    public static void ResetAllPowerOverrides()
    {
        lock (Sync)
        {
            try
            {
                GetController().ResetAllOverrides();
                ToolkitLog.Info(
                    "NvAPIWrapper reset all PCF power overrides.");
            }
            finally
            {
                CloseController();
                _cachedSliderBounds = null;
            }
        }
    }

    public static NvPcfPowerSnapshot ReadAfterReset()
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                return Read();
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 10)
                    Thread.Sleep(400);
            }
        }
        throw new InvalidOperationException(
            "The NvAPIWrapper PCF session could not be reopened after reset.",
            lastError);
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            CloseController();
            _dynamicBoostUnsupported = false;
            _thermalCapabilityChecked = false;
        }
    }

    internal static uint WattsToMilliwatts(int watts)
    {
        if (watts <= 0)
            throw new ArgumentOutOfRangeException(nameof(watts));
        return checked((uint)watts * 1000u);
    }

    internal static int MilliwattsToWatts(uint milliwatts) =>
        checked((int)Math.Round(
            milliwatts / 1000d,
            MidpointRounding.AwayFromZero));

    internal static (int MinimumW, int MaximumW) ParseNvidiaSmiPowerBounds(
        string output)
    {
        var line = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        var values = line.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length < 2 ||
            !TryParseWatts(values[0], out var minimum) ||
            !TryParseWatts(values[1], out var maximum) ||
            minimum <= 0 || maximum < minimum)
        {
            throw new InvalidOperationException(
                "nvidia-smi did not return valid Default/Max Power Limit values.");
        }
        return (minimum, maximum);
    }

    internal static (int MinimumW, int MaximumW)? CalculatePowerBoundsFromPcm(
        uint defaultPowerInPcm,
        uint maximumPowerInPcm,
        int maximumPowerInWatts)
    {
        if (defaultPowerInPcm == 0 ||
            maximumPowerInPcm < defaultPowerInPcm ||
            maximumPowerInWatts <= 0)
        {
            return null;
        }
        var defaultWatts = checked((int)Math.Round(
            maximumPowerInWatts * (double)defaultPowerInPcm /
            maximumPowerInPcm,
            MidpointRounding.AwayFromZero));
        return defaultWatts > 0
            ? (defaultWatts, maximumPowerInWatts)
            : null;
    }

    internal static PcfPowerFields Fields(
        PowerSettingsLockSelection selection)
    {
        var result = PcfPowerFields.None;
        if (selection.NvPcfAcTargetTppLimit)
            result |= PcfPowerFields.ACTargetTPPLimit;
        if (selection.NvPcfAcDefaultGpuLimit)
            result |= PcfPowerFields.ACDefaultGPULimit;
        if (selection.NvPcfAcMinGpuLimit)
            result |= PcfPowerFields.ACMinGPULimit;
        if (selection.NvPcfAcMaxGpuLimit)
            result |= PcfPowerFields.ACMaxGPULimit;
        return result;
    }

    private static void WritePowerFields(
        PcfPowerController controller,
        PcfPowerFields fields,
        PowerSettingsState state)
    {
        if (!NvPcfPowerPolicy.TryValues(
                state,
                out var target,
                out var @default,
                out var minimum,
                out var maximum))
            throw new ArgumentException("All NVPCF values are required.", nameof(state));
        var values = new PcfPowerValues(
            WattsToMilliwatts(target),
            WattsToMilliwatts(@default),
            WattsToMilliwatts(minimum),
            WattsToMilliwatts(maximum));
        if (fields == PcfPowerFields.All)
        {
            controller.SetPowerLimits(values);
            return;
        }
        Write(PcfPowerFields.ACTargetTPPLimit,
            values.ACTargetTPPLimitInMilliwatts);
        Write(PcfPowerFields.ACDefaultGPULimit,
            values.ACDefaultGPULimitInMilliwatts);
        Write(PcfPowerFields.ACMinGPULimit,
            values.ACMinGPULimitInMilliwatts);
        Write(PcfPowerFields.ACMaxGPULimit,
            values.ACMaxGPULimitInMilliwatts);
        return;

        void Write(PcfPowerFields field, uint milliwatts)
        {
            if ((fields & field) == 0)
                return;
            controller.SetPowerField(field, milliwatts);
        }
    }

    internal static PcfPowerValues Values(PowerSettingsState state)
    {
        if (!NvPcfPowerPolicy.TryValues(
                state,
                out var target,
                out var @default,
                out var minimum,
                out var maximum))
        {
            throw new ArgumentException(
                "All NVPCF values are required.",
                nameof(state));
        }
        return new(
            WattsToMilliwatts(target),
            WattsToMilliwatts(@default),
            WattsToMilliwatts(minimum),
            WattsToMilliwatts(maximum));
    }

    internal static NvPcfPowerSnapshot SnapshotForTesting(
        PcfPowerValues values,
        PcfPowerLayout layout = PcfPowerLayout.V1,
        uint controllerIndex = 0) =>
        Snapshot(values, layout, controllerIndex, null, null);

    private static NvPcfPowerSnapshot Snapshot(
        PcfPowerValues values,
        PcfPowerLayout layout,
        uint controllerIndex,
        bool? dynamicBoostEnabled,
        ThermalLimitSnapshot? thermal)
    {
        var target = MilliwattsToWatts(values.ACTargetTPPLimitInMilliwatts);
        var @default = MilliwattsToWatts(values.ACDefaultGPULimitInMilliwatts);
        var minimum = MilliwattsToWatts(values.ACMinGPULimitInMilliwatts);
        var maximum = MilliwattsToWatts(values.ACMaxGPULimitInMilliwatts);
        if (_cachedSliderBounds is null)
        {
            _cachedSliderBounds = TryReadNvApiWrapperPowerBounds(values);
        }
        if (_cachedSliderBounds is null)
        {
            try
            {
                _cachedSliderBounds = ReadNvidiaSmiPowerBounds();
            }
            catch (Exception ex)
            {
                ToolkitLog.Warning(
                    "NvAPIWrapper does not expose absolute Default/Max Power " +
                    "Limit watts on this GPU, and nvidia-smi could not be " +
                    "queried; current PCF fields will be used as a fallback: " +
                    ex.Message);
                if (@default > 0 && maximum >= @default)
                    _cachedSliderBounds = (@default, maximum);
            }
        }
        var bounds = _cachedSliderBounds ??
                     (Math.Max(1, @default), Math.Max(@default, maximum));
        return new(
            target,
            @default,
            minimum,
            maximum,
            bounds.MinimumW,
            bounds.MaximumW,
            dynamicBoostEnabled,
            thermal?.TargetC,
            thermal?.MinimumC,
            thermal?.MaximumC,
            $"{layout}; controller {controllerIndex}");
    }

    private sealed record ThermalLimitSnapshot(
        PhysicalGPU Gpu,
        NvAPIWrapper.Native.GPU.ThermalController Controller,
        NvAPIWrapper.Native.GPU.PerformanceStateId PerformanceState,
        int TargetC,
        int DefaultC,
        int MinimumC,
        int MaximumC);

    private static bool? TryReadDynamicBoost(PcfPowerController controller)
    {
        if (_dynamicBoostUnsupported)
            return null;
        try { return controller.GetDynamicBoostEnabled(); }
        catch (Exception ex)
        {
            _dynamicBoostUnsupported = true;
            ToolkitLog.Warning("Dynamic Boost status is unavailable: " + ex.Message);
            return null;
        }
    }

    private static ThermalLimitSnapshot? TryReadThermalLimit()
    {
        if (_thermalCapabilityChecked && !CachedTemperatureBounds.HasValue)
            return null;
        try
        {
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                var infos = gpu.PerformanceControl.ThermalLimitInformation.ToArray();
                var policies = gpu.PerformanceControl.ThermalLimitPolicies.ToArray();
                foreach (var info in infos)
                {
                    var policy = policies.FirstOrDefault(item =>
                        item.Controller == info.Controller);
                    if (policy is null || info.MinimumTemperature <= 0 ||
                        info.MaximumTemperature < info.MinimumTemperature)
                        continue;
                    CachedTemperatureBounds =
                        (info.MinimumTemperature, info.MaximumTemperature);
                    _thermalCapabilityChecked = true;
                    return new ThermalLimitSnapshot(
                        gpu,
                        info.Controller,
                        policy.PerformanceStateId,
                        policy.TargetTemperature,
                        info.DefaultTemperature,
                        info.MinimumTemperature,
                        info.MaximumTemperature);
                }
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning("NVAPI thermal limit is unavailable: " + ex.Message);
        }
        _thermalCapabilityChecked = true;
        return null;
    }

    private static void WriteThermalLimit(int temperatureC)
    {
        var thermal = TryReadThermalLimit() ??
            throw new NotSupportedException(
                "The GPU does not expose an adjustable NVAPI thermal limit.");
        if (temperatureC < thermal.MinimumC || temperatureC > thermal.MaximumC)
            throw new ArgumentOutOfRangeException(nameof(temperatureC));
        GPUApi.SetThermalPoliciesStatus(
            thermal.Gpu.Handle,
            new PrivateThermalPoliciesStatusV2(
            [
                new PrivateThermalPoliciesStatusV2.ThermalPoliciesStatusEntry(
                    thermal.PerformanceState,
                    thermal.Controller,
                    temperatureC)
            ]));
    }

    private static void ResetThermalLimitToDefault()
    {
        var thermal = TryReadThermalLimit();
        if (thermal is not null)
            WriteThermalLimit(thermal.DefaultC);
    }

    private static (int MinimumW, int MaximumW)?
        TryReadNvApiWrapperPowerBounds(PcfPowerValues values)
    {
        try
        {
            var maximumWatts = MilliwattsToWatts(
                values.ACMaxGPULimitInMilliwatts);
            foreach (var gpu in PhysicalGPU.GetPhysicalGPUs())
            {
                foreach (var info in gpu.PerformanceControl
                             .PowerLimitInformation)
                {
                    var bounds = CalculatePowerBoundsFromPcm(
                        info.DefaultPowerInPCM,
                        info.MaximumPowerInPCM,
                        maximumWatts);
                    if (bounds.HasValue)
                    {
                        ToolkitLog.Info(
                            "GPU slider bounds were resolved from " +
                            "NvAPIWrapper PowerLimitInformation: " +
                            $"{bounds.Value.MinimumW}–" +
                            $"{bounds.Value.MaximumW} W.");
                        return bounds;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ToolkitLog.Warning(
                "NvAPIWrapper PowerLimitInformation could not be read: " +
                ex.Message);
        }
        return null;
    }

    private static (int MinimumW, int MaximumW)
        ReadNvidiaSmiPowerBounds()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(
            "--query-gpu=power.default_limit,power.max_limit");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException(
                                "nvidia-smi.exe could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("nvidia-smi power query timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"nvidia-smi power query failed ({process.ExitCode}): " +
                error.GetAwaiter().GetResult().Trim());
        }
        return ParseNvidiaSmiPowerBounds(
            output.GetAwaiter().GetResult());
    }

    private static bool TryParseWatts(string value, out int watts)
    {
        watts = 0;
        var text = value.Trim();
        if (text.EndsWith(" W", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].Trim();
        if (!decimal.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }
        watts = checked((int)decimal.Round(
            parsed,
            0,
            MidpointRounding.AwayFromZero));
        return true;
    }

    private static PcfPowerController GetController()
    {
        if (_controller is not null)
            return _controller;
        _controller = new PcfPowerController();
        ToolkitLog.Info(
            "NvAPIWrapper PCF controller opened: " +
            $"layout={_controller.Layout}; " +
            $"index={_controller.ControllerIndex}; " +
            $"mask=0x{_controller.ControllerMask:X8}.");
        return _controller;
    }

    private static void CloseController()
    {
        try { _controller?.Dispose(); } catch { }
        _controller = null;
    }
}
