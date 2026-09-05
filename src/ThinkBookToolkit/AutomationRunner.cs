using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record AutomationRunResult(bool Success, string Error)
{
    public static AutomationRunResult Ok() => new(true, string.Empty);
    public static AutomationRunResult Failure(string error) => new(false, error);
}

internal sealed class AutomationRunner
{
    private readonly ToolkitRuntimeService _runtime;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public AutomationRunner(ToolkitRuntimeService runtime)
    {
        _runtime = runtime;
    }

    public async Task<AutomationRunResult> RunAsync(
        string automationId,
        CancellationToken cancellationToken = default)
    {
        var automation = _runtime.Settings.Automations.FirstOrDefault(item =>
            item.Id.Equals(automationId, StringComparison.OrdinalIgnoreCase));
        if (automation is null)
        {
            ToolkitLog.Warning(
                $"Automation request rejected because '{automationId}' " +
                "was not found.");
            return AutomationRunResult.Failure(
                _runtime.L("自动化不存在。", "The automation does not exist."));
        }

        if (_executionGate.CurrentCount == 0)
        {
            ToolkitLog.Info(
                $"Automation queued: {automation.Name} ({automation.Id}).");
        }
        try
        {
            await _executionGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ToolkitLog.Warning(
                $"Automation cancelled while queued: {automation.Name} " +
                $"({automation.Id}).");
            return AutomationRunResult.Failure(
                _runtime.L("自动化已取消。", "The automation was cancelled."));
        }
        var runTimer = Stopwatch.StartNew();
        try
        {
            ToolkitLog.Info(
                $"Automation started: {automation.Name} ({automation.Id}), " +
                $"{automation.Steps.Count} step(s).");
            for (var index = 0; index < automation.Steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = automation.Steps[index];
                var stepTimer = Stopwatch.StartNew();
                ToolkitLog.Info(
                    $"Automation step started: automation={automation.Id}; " +
                    $"step={index + 1}/{automation.Steps.Count}; " +
                    $"{StepLogDescription(step, _runtime)}.");
                var error = await ExecuteStepAsync(
                    step,
                    cancellationToken,
                    $"automation:{automation.Id}:step:{index + 1}");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    var message = _runtime.L(
                        $"步骤 {index + 1} 执行失败：{error}",
                        $"Step {index + 1} failed: {error}");
                    ToolkitLog.Warning(
                        $"Automation {automation.Id} stopped at step " +
                        $"{index + 1} ({step.Kind}) after " +
                        $"{stepTimer.Elapsed.TotalMilliseconds:0} ms: {error}");
                    return AutomationRunResult.Failure(message);
                }
                ToolkitLog.Info(
                    $"Automation step completed: automation={automation.Id}; " +
                    $"step={index + 1}/{automation.Steps.Count}; " +
                    $"kind={step.Kind}; " +
                    $"elapsed={stepTimer.Elapsed.TotalMilliseconds:0} ms.");
            }
            ToolkitLog.Info(
                $"Automation completed: {automation.Name} ({automation.Id}); " +
                $"elapsed={runTimer.Elapsed.TotalMilliseconds:0} ms.");
            _runtime.NotifyControlStateChanged();
            return AutomationRunResult.Ok();
        }
        catch (OperationCanceledException)
        {
            ToolkitLog.Warning(
                $"Automation cancelled: {automation.Name} " +
                $"({automation.Id}); " +
                $"elapsed={runTimer.Elapsed.TotalMilliseconds:0} ms.");
            return AutomationRunResult.Failure(
                _runtime.L("自动化已取消。", "The automation was cancelled."));
        }
        catch (Exception ex)
        {
            ToolkitLog.Error(
                $"Automation {automation.Id} failed unexpectedly.",
                ex);
            return AutomationRunResult.Failure(ex.GetBaseException().Message);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<string?> ExecuteStepAsync(
        AutomationStep step,
        CancellationToken cancellationToken,
        string executionSource)
    {
        switch (step.Kind)
        {
            case AutomationStepKind.PerformanceMode:
                return ParseEnum<ItsMode>(step.Value, out var itsMode)
                    ? await _runtime.SetItsModeAsync(itsMode)
                    : InvalidValue(step);
            case AutomationStepKind.GpuMode:
                return ParseEnum<GpuWorkingMode>(step.Value, out var gpuMode)
                    ? await _runtime.SetGpuModeAsync(gpuMode)
                    : InvalidValue(step);
            case AutomationStepKind.GpuOverclockEnabled:
                return await ApplyBooleanAsync(step, value =>
                    _runtime.SetGpuOverclockEnabledAsync(value));
            case AutomationStepKind.KillGpuApplications:
            {
                var result = await _runtime.KillDiscreteGpuApplicationsAsync();
                return result.Success ? null : result.Error;
            }
            case AutomationStepKind.FanFullSpeed:
                return await ApplyBooleanAsync(step, value =>
                    _runtime.SetFullSpeedAsync(value));
            case AutomationStepKind.FanStrategy:
                return await SetFanStrategyAsync(step.Value);
            case AutomationStepKind.FixedRpmGameMode:
                return ParseBoolean(step.Value, out var gameMode)
                    ? await _runtime.SetFixedRpmGameModeAsync(gameMode)
                    : InvalidValue(step);
            case AutomationStepKind.BatteryChargeMode:
                return ParseEnum<BatteryChargeMode>(step.Value, out var chargeMode)
                    ? await RunAndRefreshAsync(() =>
                        BatterySettingsController.SetChargeMode(chargeMode))
                    : InvalidValue(step);
            case AutomationStepKind.OvernightCharging:
                return await ApplyBooleanAsync(step, value =>
                    RunAndRefreshAsync(() =>
                        BatterySettingsController.SetOvernightCharging(value)));
            case AutomationStepKind.AlwaysOnUsb:
                return ParseEnum<AlwaysOnUsbMode>(step.Value, out var usbMode)
                    ? await RunAndRefreshAsync(() =>
                        BatterySettingsController.SetAlwaysOnUsb(usbMode))
                    : InvalidValue(step);
            case AutomationStepKind.FlipToStart:
                return await ApplyBooleanAsync(step, value =>
                    RunAndRefreshAsync(() =>
                        BatterySettingsController.SetFlipToStart(value)));
            case AutomationStepKind.RefreshRate:
                return await SetRefreshRateAsync(step.Value);
            case AutomationStepKind.VantageEyeCare:
                return await ApplyBooleanAsync(step, SetVantageEyeCareAsync);
            case AutomationStepKind.PcManagerEyeCare:
                return await ApplyBooleanAsync(step, value =>
                    RunAndRefreshAsync(() =>
                        PcManagerEyeCareController.SetEnabled(
                            value,
                            PcManagerDefaults())));
            case AutomationStepKind.ColorManagement:
                return ParseEnum<ColorManagementMode>(step.Value, out var colorMode)
                    ? await RunAndRefreshAsync(() =>
                        DisplaySettingsController.SetColorManagementMode(
                            colorMode))
                    : InvalidValue(step);
            case AutomationStepKind.OsdEnabled:
                return await ApplyBooleanAsync(step, value =>
                    Task.FromResult(_runtime.TrySetOsdEnabled(
                        value,
                        out var error)
                            ? null
                            : error));
            case AutomationStepKind.OsdLockPosition:
                return await ApplyBooleanAsync(step, value =>
                {
                    var settings = CurveProfileStore.NormalizeOsdSettings(
                        _runtime.Settings.Osd);
                    settings.FixedPosition = value;
                    return Task.FromResult(_runtime.TrySetOsdSettings(
                        settings,
                        out var error)
                            ? null
                            : error);
                });
            case AutomationStepKind.SensorRecordingEnabled:
                return await ApplyBooleanAsync(step, value =>
                    Task.FromResult(_runtime.TrySetSensorRecordingEnabled(
                        value,
                        out var error)
                            ? null
                            : error));
            case AutomationStepKind.DolbyEnabled:
                return await ApplyBooleanAsync(step, SetDolbyEnabledAsync);
            case AutomationStepKind.DolbyProfile:
                return ParseEnum<DolbyProfile>(step.Value, out var dolbyProfile)
                    ? await SetDolbyProfileAsync(dolbyProfile)
                    : InvalidValue(step);
            case AutomationStepKind.SpeakerNoiseCancellation:
                return await ApplyBooleanAsync(step, value =>
                    RunAndRefreshAsync(() =>
                        SoundSettingsController.SetSpeakerNoiseEnabled(
                            value)));
            case AutomationStepKind.MicrophoneNoiseMode:
                return ParseEnum<MicrophoneNoiseMode>(step.Value, out var micMode)
                    ? await RunAndRefreshAsync(() =>
                        SoundSettingsController.SetMicrophoneNoiseMode(micMode))
                    : InvalidValue(step);
            case AutomationStepKind.KeyboardBacklight:
                return ParseEnum<KeyboardBacklightLevel>(step.Value, out var light)
                    ? await RunAndRefreshAsync(() =>
                        KeyboardBacklightController.SetBrightness(light))
                    : InvalidValue(step);
            case AutomationStepKind.KeyboardBacklightAutoOff:
                return await ApplyBooleanAsync(step, value =>
                    RunAndRefreshAsync(() =>
                        KeyboardBacklightController.SetAutoOff(value)));
            case AutomationStepKind.FunctionLock:
                return await SetInputAsync(step, InputSettingKind.FunctionLock);
            case AutomationStepKind.CapsLockOsd:
                return await SetInputAsync(step, InputSettingKind.CapsLockOsd);
            case AutomationStepKind.NumLockOsd:
                return await SetInputAsync(step, InputSettingKind.NumLockOsd);
            case AutomationStepKind.FnCtrlSwap:
                return await SetInputAsync(step, InputSettingKind.FnCtrlSwap);
            case AutomationStepKind.Touchpad:
                return await SetInputAsync(step, InputSettingKind.Touchpad);
            case AutomationStepKind.ShowToolkitWindow:
                _runtime.ShowMainWindow();
                return null;
            case AutomationStepKind.MinimizeToolkitWindow:
                _runtime.MinimizeMainWindow();
                return null;
            case AutomationStepKind.ToggleToolkitWindow:
                _runtime.ToggleMainWindow();
                return null;
            case AutomationStepKind.OpenApplication:
                return OpenApplication(step);
            case AutomationStepKind.RunMacro:
                return await _runtime.RunMacroAsync(
                    step.Value,
                    cancellationToken,
                    executionSource);
            case AutomationStepKind.Delay:
                if (!double.TryParse(
                        step.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds) ||
                    seconds is < 0 or > 86400)
                {
                    return InvalidValue(step);
                }
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
                return null;
            default:
                return InvalidValue(step);
        }
    }

    private async Task<string?> SetFanStrategyAsync(string value)
    {
        if (value.Equals("FirmwareAutomatic", StringComparison.OrdinalIgnoreCase))
            return await _runtime.SetFanModeAsync(FanControlMode.FirmwareAutomatic);
        if (value.Equals("FixedRpm", StringComparison.OrdinalIgnoreCase))
            return await _runtime.SetFanModeAsync(FanControlMode.FixedRpm);
        if (value.Equals("AdvancedCurve", StringComparison.OrdinalIgnoreCase))
            return await _runtime.SetFanModeAsync(FanControlMode.AdvancedCurve);
        if (!value.StartsWith("FanCurve:", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(value[9..], out var profileIndex))
            return "Invalid fan strategy.";
        var error = await _runtime.SelectFanProfileAsync(profileIndex);
        return string.IsNullOrWhiteSpace(error)
            ? await _runtime.SetFanModeAsync(FanControlMode.FanCurve)
            : error;
    }

    private async Task<string?> SetRefreshRateAsync(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || !uint.TryParse(parts[1], out var frequency))
            return "Invalid refresh rate.";
        var mode = new DisplayRefreshRateMode(
            frequency,
            parts[0].Equals("dynamic", StringComparison.OrdinalIgnoreCase));
        var result = await Task.Run(() =>
        {
            var success = RefreshRateController.TrySetRefreshRate(
                mode,
                out var error);
            return (success, error);
        });
        if (!result.success)
            return result.error;
        _runtime.NotifyControlStateChanged();
        return null;
    }

    private async Task<string?> SetVantageEyeCareAsync(bool enabled)
    {
        return await RunAndRefreshAsync(() =>
        {
            var state = DisplaySettingsController.ReadState(
                PcManagerDefaults()).EyeCare;
            if (!state.Available)
                throw new NotSupportedException(state.Error);
            return DisplaySettingsController.SetEyeCareEnabled(enabled, state);
        });
    }

    private async Task<string?> SetDolbyEnabledAsync(bool enabled)
    {
        return await RunAndRefreshAsync(() =>
        {
            var state = SoundSettingsController.ReadState().Dolby;
            if (!state.Available)
                throw new NotSupportedException(state.Error);
            return SoundSettingsController.SetDolbyState(enabled, state.Profile);
        });
    }

    private async Task<string?> SetDolbyProfileAsync(DolbyProfile profile)
    {
        return await RunAndRefreshAsync(() =>
        {
            var state = SoundSettingsController.ReadState().Dolby;
            if (!state.Available)
                throw new NotSupportedException(state.Error);
            return SoundSettingsController.SetDolbyState(state.Enabled, profile);
        });
    }

    private async Task<string?> SetInputAsync(
        AutomationStep step,
        InputSettingKind kind) =>
        await ApplyBooleanAsync(step, value =>
            _runtime.SetInputSettingAsync(kind, value));

    private async Task<string?> ApplyBooleanAsync(
        AutomationStep step,
        Func<bool, Task<string?>> apply)
    {
        if (ParseBoolean(step.Value, out var explicitValue))
            return await apply(explicitValue);
        if (!step.Value.Equals("toggle", StringComparison.OrdinalIgnoreCase))
            return InvalidValue(step);
        var current = await ReadBooleanStateAsync(step.Kind);
        return current.HasValue
            ? await apply(!current.Value)
            : _runtime.L("无法读取当前开关状态。", "The current switch state is unavailable.");
    }

    private async Task<bool?> ReadBooleanStateAsync(AutomationStepKind kind)
    {
        switch (kind)
        {
            case AutomationStepKind.GpuOverclockEnabled:
                return _runtime.Settings.GpuOverclock.Enabled;
            case AutomationStepKind.FanFullSpeed:
                return _runtime.Snapshot.FullSpeed;
            case AutomationStepKind.OvernightCharging:
                return (await Task.Run(() =>
                    BatterySettingsController.ReadState())).OvernightCharging;
            case AutomationStepKind.FlipToStart:
                return (await Task.Run(() =>
                    BatterySettingsController.ReadState(true))).FlipToStart;
            case AutomationStepKind.VantageEyeCare:
                return (await Task.Run(() =>
                    DisplaySettingsController.ReadState(PcManagerDefaults())))
                    .EyeCare.Enabled;
            case AutomationStepKind.PcManagerEyeCare:
                return (await Task.Run(() =>
                    PcManagerEyeCareController.ReadState(PcManagerDefaults())))
                    .Enabled;
            case AutomationStepKind.DolbyEnabled:
                return (await Task.Run(SoundSettingsController.ReadState))
                    .Dolby.Enabled;
            case AutomationStepKind.SpeakerNoiseCancellation:
                return (await Task.Run(SoundSettingsController.ReadState))
                    .SpeakerNoise.Enabled;
            case AutomationStepKind.OsdEnabled:
                return _runtime.Settings.OsdEnabled;
            case AutomationStepKind.OsdLockPosition:
                return _runtime.Settings.Osd.FixedPosition;
            case AutomationStepKind.SensorRecordingEnabled:
                return _runtime.Settings.SensorRecordingEnabled;
            case AutomationStepKind.KeyboardBacklightAutoOff:
                return (await Task.Run(KeyboardBacklightController.ReadState))
                    .AutoOffEnabled;
            case AutomationStepKind.FunctionLock:
            case AutomationStepKind.CapsLockOsd:
            case AutomationStepKind.NumLockOsd:
            case AutomationStepKind.FnCtrlSwap:
            case AutomationStepKind.Touchpad:
            {
                var inputKind = kind switch
                {
                    AutomationStepKind.FunctionLock => InputSettingKind.FunctionLock,
                    AutomationStepKind.CapsLockOsd => InputSettingKind.CapsLockOsd,
                    AutomationStepKind.NumLockOsd => InputSettingKind.NumLockOsd,
                    AutomationStepKind.FnCtrlSwap => InputSettingKind.FnCtrlSwap,
                    _ => InputSettingKind.Touchpad
                };
                if (_runtime.Settings.TakeOverFnKeys &&
                    (inputKind is InputSettingKind.CapsLockOsd or
                        InputSettingKind.NumLockOsd))
                    return inputKind == InputSettingKind.CapsLockOsd
                        ? _runtime.Settings.ShowCapsLockOsd
                        : _runtime.Settings.ShowNumLockOsd;
                return (await Task.Run(() =>
                    InputSettingsController.ReadState())).Get(inputKind).Enabled;
            }
            default:
                return null;
        }
    }

    private async Task<string?> RunAndRefreshAsync<T>(Func<T> action)
    {
        try
        {
            _ = await Task.Run(action);
            await _runtime.RefreshAsync(force: true);
            _runtime.NotifyControlStateChanged();
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    private string OpenApplication(AutomationStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Value) ||
            !File.Exists(step.Value))
        {
            return _runtime.L(
                "应用路径不存在。",
                "The application path does not exist.");
        }
        Process.Start(new ProcessStartInfo
        {
            FileName = step.Value,
            Arguments = step.SecondaryValue ?? string.Empty,
            UseShellExecute = true
        });
        return string.Empty;
    }

    private PcManagerEyeCareDefaults PcManagerDefaults() => new(
        _runtime.Settings.PcManagerNormalDefaultTemperature,
        _runtime.Settings.PcManagerEyeCareDefaultTemperature);

    private static bool ParseBoolean(string value, out bool result) =>
        bool.TryParse(value, out result);

    private static bool ParseEnum<T>(string value, out T result)
        where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result) &&
        Enum.IsDefined(result);

    private static string InvalidValue(AutomationStep step) =>
        $"Invalid value for {step.Kind}.";

    internal static string StepLogDescription(
        AutomationStep step,
        ToolkitRuntimeService runtime)
    {
        if (step.Kind == AutomationStepKind.OpenApplication)
            return "kind=OpenApplication; value=<redacted>";
        if (step.Kind == AutomationStepKind.RunMacro)
        {
            var macro = runtime.Settings.Macros.FirstOrDefault(item =>
                item.Id.Equals(
                    step.Value,
                    StringComparison.OrdinalIgnoreCase));
            return macro is null
                ? $"kind=RunMacro; macro={step.Value}"
                : $"kind=RunMacro; macro={macro.Name} ({macro.Id})";
        }
        return string.IsNullOrWhiteSpace(step.Value)
            ? $"kind={step.Kind}"
            : $"kind={step.Kind}; value={step.Value}";
    }
}
