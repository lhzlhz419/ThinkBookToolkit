using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ThinkBookToolkit;

internal sealed partial class AutomationRunner
{
    private async Task<string?> SetPanelLayoutAsync(AutomationStep step, CancellationToken token)
    {
        if (_runtime.AutomationSessionLocked || _runtime.IsSystemSessionEnding || _runtime.ExitRequested)
            return _runtime.L("显示调整需要已解锁的交互桌面。", "Display changes require an unlocked interactive desktop.");
        var layout = await Task.Run(RefreshRateController.ReadLaptopLayout, token);
        token.ThrowIfCancellationRequested();
        using var change = await Task.Run(() =>
        {
            if (step.Kind == AutomationStepKind.DpiScale)
                return RefreshRateController.BeginLaptopScale(layout.DeviceName, int.Parse(step.Value, CultureInfo.InvariantCulture));
            var dimensions = step.Value.Split('x');
            return RefreshRateController.BeginLaptopResolution(layout.DeviceName,
                new PanelResolution(uint.Parse(dimensions[0]), uint.Parse(dimensions[1])));
        }, token);
        token.ThrowIfCancellationRequested();
        var confirmation = new DisplayChangeConfirmationWindow(_runtime) { Owner = Application.Current?.MainWindow };
        using var cancel = token.Register(() =>
        {
            if (!confirmation.Dispatcher.HasShutdownStarted)
                confirmation.Dispatcher.BeginInvoke(new Action(() => { if (confirmation.IsVisible) confirmation.Close(); }));
        });
        var keep = confirmation.ShowDialog() == true && !token.IsCancellationRequested;
        await Task.Run(() => change.Finish(keep));
        _runtime.NotifyControlStateChanged();
        return keep ? null : _runtime.L("显示修改未确认，已还原并停止后续步骤。", "Display change was not confirmed; reverted and stopped subsequent steps.");
    }

    private async Task<string?> SetPowerParameterAsync(AutomationStep step, CancellationToken token)
    {
        var setting = Enum.Parse<PowerSetting>(step.Value, true);
        var lockEnabled = step.Kind == AutomationStepKind.PowerLock && (step.SecondaryValue.Equals("toggle", StringComparison.OrdinalIgnoreCase)
            ? !_runtime.CurrentPowerSettingsLocks.IsLocked(setting) : bool.Parse(step.SecondaryValue));
        if (step.Kind == AutomationStepKind.PowerLock && !lockEnabled)
            return _runtime.TrySetPowerSettingLock(setting, false, null, out var unlockError) ? null : unlockError;
        if (!_runtime.CanWritePowerSetting(setting))
            return _runtime.L("当前功耗接口不支持此参数，请检查设备和 Beta 开关。", "This parameter is unavailable; check the device and power Beta settings.");
        var current = await _runtime.ReadPowerSettingsAsync();
        token.ThrowIfCancellationRequested();
        if (!current.IsAvailable(setting)) return _runtime.L("未读到此功耗参数，已停止。", "The power parameter could not be read.");
        if (step.Kind == AutomationStepKind.PowerLock)
        {
            return _runtime.TrySetPowerSettingLock(setting, lockEnabled, current, out var error) ? null : error;
        }
        var value = int.Parse(step.SecondaryValue, CultureInfo.InvariantCulture);
        var desired = WithPowerValue(current, setting, value);
        if (!PowerSettingsController.IsValidState(desired))
            return _runtime.L("功耗值不符合当前接口的写入范围。", "Power value is outside the active interface's supported input range.");
        await _runtime.ApplyPowerSettingsAsync(desired);
        await _runtime.RefreshAsync(force: true);
        _runtime.NotifyControlStateChanged();
        return null;
    }

    internal static PowerSettingsState WithPowerValue(PowerSettingsState state, PowerSetting setting, int value) => setting switch
    {
        PowerSetting.CpuPl1 => state with { CpuPl1 = value },
        PowerSetting.CpuPl2 => state with { CpuPl2 = value },
        PowerSetting.CpuTemperatureLimit => state with { CpuTemperatureLimit = value },
        PowerSetting.CpuTurboTimeLimit => state with { CpuTurboTimeLimit = value },
        PowerSetting.GpuPowerBoost => state with { GpuPowerBoost = value },
        PowerSetting.GpuConfigurableTgp => state with { GpuConfigurableTgp = value },
        PowerSetting.GpuTemperatureLimit => state with { GpuTemperatureLimit = value },
        PowerSetting.GpuToCpuDynamicBoost => state with { GpuToCpuDynamicBoost = value },
        PowerSetting.Atpp => state with { Atpp = value },
        PowerSetting.NvPcfAcTargetTppLimit => state with { NvPcfAcTargetTppLimit = value },
        PowerSetting.NvPcfAcDefaultGpuLimit => state with { NvPcfAcDefaultGpuLimit = value },
        PowerSetting.NvPcfAcMinGpuLimit => state with { NvPcfAcMinGpuLimit = value },
        PowerSetting.NvPcfAcMaxGpuLimit => state with { NvPcfAcMaxGpuLimit = value },
        PowerSetting.NvApiGpuTemperatureLimit => state with { NvApiGpuTemperatureLimit = value },
        _ => throw new ArgumentOutOfRangeException(nameof(setting))
    };
}
