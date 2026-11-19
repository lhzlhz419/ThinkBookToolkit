using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using ThinkBookToolkit;
using ThinkBookToolkit.Guardian;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class GpuOverclockRangeTests
{
    internal static void Run()
    {
        IPerformanceStates20ClockEntry Entry(PublicClockDomain domain, int minimum, int maximum, bool editable = true)
        {
            object entry = new PerformanceStates20ClockEntryV1(domain, new PerformanceStates20ParameterDelta(0, minimum, maximum));
            typeof(PerformanceStates20ClockEntryV1).GetField("_Flags", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(entry, editable ? 1u : 0u);
            return (IPerformanceStates20ClockEntry)entry;
        }
        var core = Entry(PublicClockDomain.Graphics, -210500, 720999);
        var memory = Entry(PublicClockDomain.Memory, -1400999, 4200000);
        var ranges = GuardianNvidiaOverclockController.RangesFromClocks([memory, core]);
        var states = new Dictionary<PerformanceStateId, IPerformanceStates20ClockEntry[]>
        {
            [(PerformanceStateId)8] = [new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, default,
                new PerformanceStates20ClockEntryV1.PerformanceStates20ClockDependentSingleFrequency(210000))],
            [(PerformanceStateId)2] = [memory],
            [(PerformanceStateId)0] = [new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, default,
                new PerformanceStates20ClockEntryV1.PerformanceStates20ClockDependentFrequencyRange(900000, 2310000, default, 0, 0))]
        };
        var reported = NvidiaPStateControl.SupportedLevels(states);
        Check(reported.SequenceEqual(["P0.Max", "P0.TDP", "P0.Min", "P8.Max", "P8.Min"]) &&
            NvidiaPStateControl.FrequencyFor(states, GpuPStateLevel.Find("P0.Max")!) == 2310000 &&
            NvidiaPStateControl.FrequencyFor(states, GpuPStateLevel.Find("P0.Min")!) == 900000 &&
            NvidiaPStateControl.FrequencyFor(states, GpuPStateLevel.Find("P8.Max")!) == 210000 &&
            NvidiaPStateControl.FrequencyFor(states, GpuPStateLevel.Find("P2.Max")!) == null,
            "P-State choices must follow the reported graphics clock domains, union type and GPUMon order.");
        var calls = new List<string>();
        PrivateClockBoostLockV2 clockWrite = default;
        void Clock(PrivateClockBoostLockV2 value) { calls.Add("clock"); clockWrite = value; }
        void Rated(PrivateRatedTdpControlV1 value) => calls.Add($"tdp:{value.Mode}");
        NvidiaPStateControl.Apply(GpuPStateLevel.Find("P8.Min")!, 210000, Clock, Rated);
        Check(calls.SequenceEqual(["tdp:0", "clock"]) &&
            clockWrite.ClockBoostLocks.Select(c => c.VoltageInMicroV).SequenceEqual(new uint[] {210000, 210000, 8, 8}),
            "P-State locking did not send the driver-reported kHz and target state through the wrapper.");
        calls.Clear();
        NvidiaPStateControl.Apply(GpuPStateLevel.Find("P0.TDP")!, 0, Clock, Rated);
        Check(calls.SequenceEqual(["clock", "tdp:3"]) && clockWrite.ClockBoostLocks.All(c => (uint)c.LockMode == 0),
            "P0.TDP did not clear the clock lock before enabling rated TDP.");
        Check(!GpuOverclockPolicy.Normalize(new GpuOverclockSettings { PStateLockEnabled = true, PStateLockLevel = "bad" }).PStateLockEnabled,
            "Invalid saved P-State must never enable a different lock.");
        Check(ranges.Core == new GpuClockOffsetRange(-210, 720) && ranges.Memory == new GpuClockOffsetRange(-1400, 4200),
            "Driver kHz limits were rounded outward or clock domains were swapped.");
        Check(GuardianNvidiaOverclockController.RangesFromClocks([core]).Memory is null &&
            GuardianNvidiaOverclockController.RangesFromClocks([core, core]).Core is null &&
            GuardianNvidiaOverclockController.RangesFromClocks([Entry(PublicClockDomain.Graphics, -100000, 100000, false)]).Core is null,
            "Missing, ambiguous or read-only driver bounds became editable.");
        Check(GpuClockOffsetRange.FromKhz(1001, 1999, true) is null &&
            GpuClockOffsetRange.FromKhz(2, 1, true) is null &&
            GpuClockOffsetRange.FromKhz(0, 0, true) == new GpuClockOffsetRange(0, 0), "Invalid or fixed driver range handling is incorrect.");
        var settings = new GpuOverclockSettings { CoreFrequencyOffsetMhz = 700, MemoryFrequencyOffsetMhz = 4000 };
        Check(GpuOverclockPolicy.TryValidate(settings, out _) && GpuOverclockPolicy.TryValidateOffsets(settings, ranges, out _),
            "Previously hard-coded bounds still restrict valid driver offsets.");
        Check(!GpuOverclockPolicy.TryValidateOffsets(settings, new(new(-200, 600), ranges.Memory), out _) &&
            !GpuOverclockPolicy.TryValidateOffsets(settings, new(ranges.Core, null), out _), "Narrowed/missing bounds did not reject writes.");
        settings.MemoryFrequencyOffsetEnabled = false;
        Check(GpuOverclockPolicy.TryValidateOffsets(settings, new(ranges.Core, null), out _), "Unchecked offset blocked an independent domain.");
        using var runtime = new ToolkitRuntimeService(new AppSettings { GpuOverclock = settings }, persistSystemSessionState: false);
        var window = new GpuOverclockWindow(null, runtime, new FontFamily("Segoe UI"), 14);
        try
        {
            window.ApplyRanges(GpuWorkerCommandResponse.Ok() with { OverclockRanges = ranges with { PStateLevels = reported } });
            var combo = (ComboBox)typeof(GpuOverclockWindow).GetField("_pStateLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var toggle = (CheckBox)typeof(GpuOverclockWindow).GetField("_pStateLockEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(toggle.IsChecked == false && combo.Items.Cast<GpuPStateLevel>().Select(l => l.Name).SequenceEqual(reported),
                "Lock must default off and the selector must contain only reported levels.");
            toggle.IsChecked = true;
            combo.SelectedIndex = 3;
            window.ApplyRanges(GpuWorkerCommandResponse.Ok() with { OverclockRanges = ranges with { PStateLevels = ["P0.Max"] } });
            Check(combo.Items.Count == 1 && combo.SelectedIndex < 0 && !Collect(),
                "Losing a saved level must not silently select an unsupported or different lock.");
            toggle.IsChecked = false;
            var editor = typeof(GpuOverclockWindow).GetField("_coreOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var slider = (Slider)editor.GetType().GetProperty("Slider")!.GetValue(editor)!;
            var text = (TextBox)editor.GetType().GetProperty("TextBox")!.GetValue(editor)!;
            Check(slider.Minimum == -210 && slider.Maximum == 720 && text.Text == "700", "UI lost the saved offset or driver bounds.");
            bool Collect() => (bool)typeof(GpuOverclockWindow).GetMethod("TryCollect", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object?[] { null, null })!;
            Check(Collect(), "Valid driver offset could not be collected.");
            window.ApplyRanges(GpuWorkerCommandResponse.Ok() with { OverclockRanges = new(new(-100, 600), ranges.Memory) });
            Check(text.Text == "700" && !Collect() && settings.CoreFrequencyOffsetMhz == 700, "Narrower bounds silently clamped a saved overclock.");
            text.Text = "500";
            Check(slider.Value == 500 && Collect(), "Numeric editor retained stale bounds.");
            window.ApplyRanges(GpuWorkerCommandResponse.Failure("driver updating"));
            Check(combo.Items.Count == 0, "A failed driver query retained stale P-State choices.");
            Check(!slider.IsEnabled && !Collect(), "Driver failure retained stale editable limits.");
        }
        finally { window.Close(); }
        Console.WriteLine("Driver overclock range conversion, validation and UI refresh tests passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
