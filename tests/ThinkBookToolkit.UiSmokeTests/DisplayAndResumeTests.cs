using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class DisplayAndResumeTests
{
    internal static void Run()
    {
        var policy = new ResumePerformanceModePolicy();
        policy.Capture(ItsMode.Performance);
        policy.Capture(ItsMode.Intelligent);
        var ticket = policy.Take()!;
        Check(ticket.Mode == ItsMode.Performance && policy.Take() is null && policy.IsCurrent(ticket), "Duplicate suspend/resume lost the captured mode.");
        policy.Cancel();
        Check(!policy.IsCurrent(ticket), "User mode change did not cancel a stale wake restore.");
        policy.Capture(ItsMode.Geek);
        var next = policy.Take()!;
        Check(policy.IsCurrent(next) && !policy.IsCurrent(ticket), "New suspend cycle reused an obsolete restore request.");
        policy.Capture(ItsMode.Unknown);
        Check(policy.Take() is null && !policy.IsCurrent(next), "Unknown mode should never be restored.");

        Check(RefreshRateController.SelectResolutionFrequency([60, 120, 165], 120) == 120 &&
            RefreshRateController.SelectResolutionFrequency([60, 144], 165) == 144, "Resolution selection unnecessarily changed refresh rate.");
        Check(CustomDisplayScaling.ToLogPixels(100) == 96 && CustomDisplayScaling.ToLogPixels(125) == 120 &&
            CustomDisplayScaling.ToLogPixels(137) == 132 && CustomDisplayScaling.ToLogPixels(500) == 480,
            "Custom DPI conversion is wrong.");
        foreach (var percent in new[] { -1, 99, 501 })
        {
            try { CustomDisplayScaling.ToLogPixels(percent); throw new Exception("Invalid scale accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
        var reverted = 0; var committed = 0;
        using (var change = new ReversibleDisplayChange(keep => { if (keep) committed++; else reverted++; })) { }
        using (var change = new ReversibleDisplayChange(keep => { if (keep) committed++; else reverted++; })) change.Finish(true);
        Check(reverted == 1 && committed == 1, "Display changes did not roll back or committed twice.");
        DiagnosePowerReadback();
        var directory = Path.Combine(Environment.CurrentDirectory, ".tmp", "display-layout-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var dark in new[] { true, false })
        {
            using var runtime = new ToolkitRuntimeService(new AppSettings { Theme = dark ? "dark" : "light", Language = dark ? "zh-CN" : "en-US" }, persistSystemSessionState: false);
            ModernTheme.Apply(Application.Current, dark);
            using var page = new ToolkitDisplayPage(runtime);
            var layout = new LaptopDisplayLayout("Fixture panel", new(2560, 1600), [new(2560, 1600), new(1920, 1200)], 150, [100, 125, 150, 175, 200], null);
            Check(ToolkitDisplayPage.NormalPanelStatus(layout with { DeviceName = @"\\.\DISPLAY36" }) == string.Empty &&
                ToolkitDisplayPage.NormalPanelStatus(layout with { ScaleError = "fixture read failure" }) == "fixture read failure",
                "Normal status leaked a device identifier or hid a genuine error.");
            var status = (TextBlock)typeof(ToolkitDisplayPage).GetField("_panelStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            var setStatus = typeof(ToolkitDisplayPage).GetMethod("SetPanelStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setStatus.Invoke(page, ["fixture error"]);
            Check(status.Visibility == Visibility.Visible, "Panel error was hidden.");
            setStatus.Invoke(page, [string.Empty]);
            Check(status.Visibility == Visibility.Collapsed, "Normal panel status still reserves a line.");
            typeof(ToolkitDisplayPage).GetField("_panelLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, layout);
            var resolutions = (ComboBox)typeof(ToolkitDisplayPage).GetField("_panelResolution", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            resolutions.ItemsSource = layout.Resolutions; resolutions.SelectedItem = layout.Current;
            var scales = (ComboBox)typeof(ToolkitDisplayPage).GetField("_panelScale", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            foreach (var value in layout.Scales) scales.Items.Add(new ComboBoxItem { Content = $"{value}%", Tag = value });
            scales.SelectedIndex = 2;
            var refresh = (ComboBox)typeof(ToolkitDisplayPage).GetField("_refreshRate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            typeof(ToolkitDisplayPage).GetField("_syncing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
            refresh.Items.Add(new ComboBoxItem { Content = "165 Hz", Tag = new DisplayRefreshRateMode(165) });
            refresh.SelectedIndex = 0;
            typeof(ToolkitDisplayPage).GetField("_syncing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, false);
            typeof(ToolkitDisplayPage).GetMethod("SetPanelEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            foreach (var width in new[] { 980d, 520d })
            {
                var section = (FrameworkElement)((StackPanel)page.Content).Children[0];
                Check(IsWithin(section, refresh) && IsWithin(section, resolutions) && IsWithin(section, scales),
                    "Refresh rate, resolution and DPI must belong to the same display card.");
                for (var pass = 0; pass < 3; pass++)
                {
                    section.InvalidateMeasure(); section.Measure(new Size(width, double.PositiveInfinity));
                    section.Arrange(new Rect(0, 0, width, section.DesiredSize.Height)); section.UpdateLayout();
                }
                var bitmap = new RenderTargetBitmap((int)width, (int)Math.Ceiling(section.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(section);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{dark}-{width}.png")); encoder.Save(file);
            }
            var watch = Stopwatch.StartNew();
            var confirmation = new DisplayChangeConfirmationWindow(runtime, TimeSpan.FromMilliseconds(80));
            Check(confirmation.ShowDialog() == false && watch.Elapsed < TimeSpan.FromSeconds(3), "Unconfirmed display settings were not rejected automatically.");
        }
        Console.WriteLine("Resume policy, reversible display UI, custom scaling validation and power readback diagnosis passed: " + directory);
    }

    private static void DiagnosePowerReadback()
    {
        var previous = PowerSettingsController.CurrentProfile;
        PowerSettingsController.SetProfileForTesting(PowerSettingsController.ResolveProfile("ThinkBook 16p G6 IAX"));
        try
        {
            var target = new PowerSettingsState(80, 120, 95, 56, 10, 80, 85, 10, 25);
            var locks = new PowerSettingsLockSelection { CpuTemperatureLimit = true };
            var profile = new PowerModeLockSettings { Target = target, Locks = locks };
            Check(PowerLockAcceptancePolicy.Accept(profile, target with { CpuTemperatureLimit = 70 }, locks),
                "Hardware feedback outside the manual slider range must be accepted.");
            Check(!PowerLockAcceptancePolicy.Accept(profile, target with { AvailableSettings = PowerSettingAvailability.None }, locks),
                "Diagnostic assumption changed: missing locked readings are no longer rejected.");
        }
        finally { PowerSettingsController.SetProfileForTesting(previous); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static bool IsWithin(DependencyObject root, DependencyObject child)
    {
        for (DependencyObject? current = child; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, root)) return true;
        return false;
    }
}
