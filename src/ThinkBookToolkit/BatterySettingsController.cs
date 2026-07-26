using System;
using System.Buffers.Binary;
using System.Threading;
using Microsoft.Win32;

namespace ThinkBookToolkit;

internal enum BatteryChargeMode
{
    Conservation,
    Normal,
    RapidCharge
}

internal enum AlwaysOnUsbMode
{
    Off,
    OnWhenSleeping,
    OnAlways
}

internal sealed record BatterySettingsState(
    BatteryChargeMode? ChargeMode,
    bool? OvernightCharging,
    AlwaysOnUsbMode? AlwaysOnUsb,
    bool? FlipToStart);

internal static class BatterySettingsController
{
    private const uint IoctlEnergySettings = 0x831020E8;
    private const uint IoctlBatteryChargeMode = 0x831020F8;
    private const uint IoctlBatteryNightCharge = 0x83102150;
    private const string VantagePath =
        @"Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin";

    public static BatterySettingsState ReadState(
        bool refreshFlipToStart = false)
    {
        using var driver = new LenovoEnergyDriver();
        return new(
            TryRead(() => ReadChargeMode(driver)),
            TryRead(() => ReadOvernightCharging(driver)),
            TryRead(() => ReadAlwaysOnUsb(driver)),
            TryRead(() => FlipToStartController.ReadState(
                refreshFlipToStart)));
    }

    public static BatteryChargeMode SetChargeMode(BatteryChargeMode mode)
    {
        using var driver = new LenovoEnergyDriver();
        foreach (var command in mode switch
                 {
                     BatteryChargeMode.Conservation => new uint[] { 0x08, 0x03 },
                     BatteryChargeMode.Normal => [0x05, 0x08],
                     BatteryChargeMode.RapidCharge => [0x05, 0x07],
                     _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
                 })
        {
            _ = driver.Call(IoctlBatteryChargeMode, command);
        }

        var confirmed = WaitFor(mode, () => ReadChargeMode(driver));
        using var key = Registry.CurrentUser.CreateSubKey(VantagePath);
        key?.SetValue(
            "BatteryChargeMode",
            confirmed switch
            {
                BatteryChargeMode.Conservation => "Storage",
                BatteryChargeMode.Normal => "Normal",
                BatteryChargeMode.RapidCharge => "Quick",
                _ => throw new ArgumentOutOfRangeException()
            },
            RegistryValueKind.String);
        return confirmed;
    }

    public static bool SetOvernightCharging(bool enabled)
    {
        using var driver = new LenovoEnergyDriver();
        _ = driver.Call(
            IoctlBatteryNightCharge,
            enabled ? 0x80000012u : 0x12u);
        return WaitFor(enabled, () => ReadOvernightCharging(driver));
    }

    public static AlwaysOnUsbMode SetAlwaysOnUsb(AlwaysOnUsbMode mode)
    {
        using var driver = new LenovoEnergyDriver();
        foreach (var command in mode switch
                 {
                     AlwaysOnUsbMode.Off => new uint[] { 0x0B, 0x12 },
                     AlwaysOnUsbMode.OnWhenSleeping => [0x0A, 0x12],
                     AlwaysOnUsbMode.OnAlways => [0x0A, 0x13],
                     _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
                 })
        {
            _ = driver.Call(IoctlEnergySettings, command);
        }

        return WaitFor(mode, () => ReadAlwaysOnUsb(driver));
    }

    public static bool SetFlipToStart(bool enabled) =>
        FlipToStartController.SetState(enabled);

    private static BatteryChargeMode ReadChargeMode(LenovoEnergyDriver driver)
    {
        var state = driver.Call(IoctlBatteryChargeMode, 0xFF);
        if ((state & 0x20) != 0)
            return BatteryChargeMode.Conservation;
        if ((state & 0x04) != 0)
            return BatteryChargeMode.RapidCharge;
        return BatteryChargeMode.Normal;
    }

    private static bool ReadOvernightCharging(LenovoEnergyDriver driver)
    {
        var state = driver.Call(IoctlBatteryNightCharge, 0x11);
        if ((state & 0x01) == 0)
        {
            throw new InvalidOperationException(
                $"Unknown overnight charging state: 0x{state:X8}.");
        }

        return (state & 0x10) != 0;
    }

    private static AlwaysOnUsbMode ReadAlwaysOnUsb(LenovoEnergyDriver driver)
    {
        var state = BinaryPrimitives.ReverseEndianness(
            driver.Call(IoctlEnergySettings, 0x02));
        if ((state & 0x80000000) == 0)
            return AlwaysOnUsbMode.Off;
        return (state & 0x00800000) != 0
            ? AlwaysOnUsbMode.OnAlways
            : AlwaysOnUsbMode.OnWhenSleeping;
    }

    private static T? TryRead<T>(Func<T> reader) where T : struct
    {
        try
        {
            return reader();
        }
        catch
        {
            return null;
        }
    }

    private static T WaitFor<T>(T expected, Func<T> reader)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var actual = reader();
            if (Equals(actual, expected))
                return actual;
            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            $"The requested state {expected} could not be confirmed.");
    }
}
