using System;
using System.Reflection;
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
            window.ApplyRanges(GpuWorkerCommandResponse.Ok() with { OverclockRanges = ranges });
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
