using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace ThinkBookToolkit;

internal enum QuietSwitch { Unsupported, Unknown, Off, On }

internal sealed record QuietPerformanceStatus(QuietSwitch State, int? DispatcherBits = null, string? Detail = null)
{
    internal static readonly QuietPerformanceStatus Unsupported = new(QuietSwitch.Unsupported);
    internal bool Available => State is (QuietSwitch.On or QuietSwitch.Off) && DispatcherBits is int bits && (bits & 1) != 0;
    internal bool ConfirmsSwitch(bool enable) =>
        State == (enable ? QuietSwitch.On : QuietSwitch.Off) &&
        DispatcherBits is int bits && (bits & 3) == (enable ? 3 : 1);

    // Display follows firmware. A restore can be skipped only when the service
    // agrees too, except for ordinary ITS modes on machines without this feature.
    internal bool ConfirmsSelection(ItsMode raw, ItsMode target) =>
        raw != ItsMode.Performance || State == QuietSwitch.Unsupported ? raw == target :
        target switch
        {
            ItsMode.Performance => ConfirmsSwitch(false),
            ItsMode.QuietPerformance => ConfirmsSwitch(true),
            _ => false
        };
    internal ItsMode Selection(ItsMode raw) => raw != ItsMode.Performance ? raw : State switch
    {
        QuietSwitch.On => ItsMode.QuietPerformance,
        QuietSwitch.Unknown => ItsMode.Unknown,
        _ => ItsMode.Performance
    };
}

internal sealed class QuietPerformanceController
{
    private readonly object _sync = new();
    private QuietPerformanceStatus _last = QuietPerformanceStatus.Unsupported;
    private long _lastProbe;
    private const string ServiceName = "LenovoProcessManagement";

    // Called off the UI thread. The Dispatcher capability bit avoids repeatedly
    // enumerating BIOS settings on machines that do not expose these controls.
    internal QuietPerformanceStatus Read(bool force = false)
    {
        lock (_sync)
        {
            if (!force && _lastProbe != 0 && Stopwatch.GetElapsedTime(_lastProbe) <
                TimeSpan.FromSeconds(_last.State == QuietSwitch.Unsupported ? 30 : 5)) return _last;
            int? bits = null;
            QuietPerformanceStatus next;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(ItsModeDetector.ModernPath, writable: false);
                bits = key?.GetValue("PE_SpecialPowerMode") as int?;
                if ((bits.GetValueOrDefault() & 1) == 0) next = QuietPerformanceStatus.Unsupported;
                else
                {
                    using var service = new ServiceController(ServiceName);
                    next = service.Status == ServiceControllerStatus.Running
                        ? Parse(ReadBiosSettings(), bits!.Value)
                        : new(QuietSwitch.Unknown, bits, "LenovoProcessManagement is not running.");
                }
            }
            catch (Exception ex) { next = new(QuietSwitch.Unknown, bits, ex.GetBaseException().Message); }
            if (next != _last && next.State == QuietSwitch.Unknown)
                ToolkitLog.Warning("Quiet performance state unavailable: " + next.Detail);
            _last = next;
            _lastProbe = Stopwatch.GetTimestamp();
            return next;
        }
    }

    private static IEnumerable<string> ReadBiosSettings()
    {
        using var search = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentSetting FROM Lenovo_BiosSetting");
        search.Options.Timeout = TimeSpan.FromSeconds(2);
        using var items = search.Get();
        foreach (ManagementObject item in items)
            using (item)
                if (item["CurrentSetting"] is string line) yield return line;
    }

    internal static QuietPerformanceStatus Parse(IEnumerable<string> settings, int bits)
    {
        if ((bits & 1) == 0) return QuietPerformanceStatus.Unsupported;
        QuietPerformanceStatus? found = null;
        foreach (var line in settings)
        {
            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
            if (!parts[0].Contains("QuietPerformanceMode", StringComparison.OrdinalIgnoreCase)) continue;
            if (found is not null) return new(QuietSwitch.Unknown, bits, "Multiple QuietPerformanceMode BIOS entries.");
            var value = parts.Length == 2 ? parts[1].Split([';', '['], 2)[0].Trim().ToUpperInvariant() : "";
            var state = value switch { "ENABLE" or "ENABLED" => QuietSwitch.On, "DISABLE" or "DISABLED" => QuietSwitch.Off, _ => QuietSwitch.Unknown };
            found = new(state, bits, line);
        }
        return found ?? new(QuietSwitch.Unknown, bits, "Dispatcher advertises quiet performance but the BIOS setting was not found.");
    }

    internal (bool BaseChanged, QuietPerformanceStatus Verified) Set(bool enable, QuietPerformanceStatus initial) =>
        Transition(enable, initial, ItsModeDetector.ReadModernMode, () => Read(force: true),
            command => { using var service = new ServiceController(ServiceName); service.ExecuteCommand(command); }, Thread.Sleep);

    internal static (bool BaseChanged, QuietPerformanceStatus Verified) Transition(bool enable, QuietPerformanceStatus initial,
        Func<ItsMode> readBase, Func<QuietPerformanceStatus> readSwitch, Action<int> send, Action<int> delay)
    {
        var original = readBase();
        if (!initial.Available || original == ItsMode.Unknown)
            throw new InvalidOperationException("无法确认原性能模式或 BIOS 静音性能状态。 / Cannot verify initial mode: " + initial.Detail);
        ToolkitLog.Info($"Quiet performance request: enable={enable}; base={original}; BIOS={initial.Detail}; cache={initial.DispatcherBits}.");
        if (original != ItsMode.Performance)
        {
            send(165);
            if (!WaitFor(() => readBase() == ItsMode.Performance, delay))
                throw new InvalidOperationException("未确认 ITS 性能档，未发送静音性能开关指令。 / Performance base mode was not confirmed.");
        }
        var wanted = enable ? QuietSwitch.On : QuietSwitch.Off;
        QuietPerformanceStatus? last = null;
        var observedBase = ItsMode.Unknown;
        bool Observe()
        {
            observedBase = ItsMode.Unknown;
            last = readSwitch();
            observedBase = readBase();
            return observedBase == ItsMode.Performance && last.ConfirmsSwitch(enable);
        }
        try
        {
            if (!Observe())
            {
                if (!last!.Available) throw new InvalidOperationException(last.Detail);
                if (observedBase != ItsMode.Performance) throw new InvalidOperationException("ITS mode changed externally.");
                send(enable ? 187 : 188);
                if (!WaitFor(Observe, delay))
                    throw new System.TimeoutException("ITS/BIOS/Dispatcher readback did not confirm the requested combination.");
            }
            return (original != ItsMode.Performance, last!);
        }
        catch (Exception ex)
        {
            if (observedBase == ItsMode.Performance && last?.State == wanted && !last.ConfirmsSwitch(enable))
                throw new InvalidOperationException($"部分成功：性能档和 BIOS 静音性能{(enable ? "开启" : "关闭")}已确认，未确认联想服务缓存同步。 / Performance mode and BIOS target confirmed, but Dispatcher cache synchronization was not confirmed. BIOS={last.Detail}; PE_SpecialPowerMode={last.DispatcherBits}. {ex.Message}", ex);
            throw new InvalidOperationException($"已确认进入性能档，但静音性能{(enable ? "开启" : "关闭")}未确认。 / Quiet performance switch unconfirmed. BIOS={last?.Detail}; cache={last?.DispatcherBits}. {ex.Message}", ex);
        }
    }

    private static bool WaitFor(Func<bool> confirmed, Action<int> delay)
    {
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 15; i++)
        {
            if (confirmed()) return true;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(3) || i == 14) break;
            delay(200);
        }
        return false;
    }
}
