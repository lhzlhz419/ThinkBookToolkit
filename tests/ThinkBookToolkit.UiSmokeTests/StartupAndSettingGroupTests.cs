using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinkBookToolkit;
using ThinkBookToolkit.PluginApi;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class StartupAndSettingGroupTests
{
    internal static void Run()
    {
        TimerDoesNotStarve();
        WarrantyDates();
        Backlight().GetAwaiter().GetResult();
        BacklightPreferences();
        SettingGroups();
        Console.WriteLine("Power timer scheduling, warranty dates, startup backlight restore and plugin setting groups passed.");
    }

    private static void TimerDoesNotStarve()
    {
        var timer = new DispatcherTimer();
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        var ticks = 0;
        timer.Tick += (_, _) => ticks++;
        var interval = TimeSpan.FromMilliseconds(150);
        refresh.Tick += (_, _) => ToolkitRuntimeService.SyncPowerSettingsTimer(timer, true, interval);
        try
        {
            ToolkitRuntimeService.SyncPowerSettingsTimer(timer, true, interval);
            refresh.Start();
            Pump(650);
            Check(ticks >= 2, "Frequent status refreshes starved the lock timer.");
            refresh.Stop();
            ToolkitRuntimeService.SyncPowerSettingsTimer(timer, false, interval);
            var stopped = ticks;
            Pump(200);
            Check(!timer.IsEnabled && ticks == stopped, "Disabled lock timer still fires.");
            ToolkitRuntimeService.SyncPowerSettingsTimer(timer, true, TimeSpan.FromMilliseconds(50));
            Pump(180);
            Check(ticks > stopped && timer.Interval.TotalMilliseconds == 50, "Timer did not restart with the changed interval.");
        }
        finally { timer.Stop(); refresh.Stop(); }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var end = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        end.Tick += (_, _) => frame.Continue = false;
        end.Start();
        try { Dispatcher.PushFrame(frame); } finally { end.Stop(); }
    }

    private static void WarrantyDates()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var future = WarrantySnapshot.FromDates(today.AddDays(30), today.AddDays(400), isStale: true);
        var current = WarrantySnapshot.FromDates(today.AddDays(-300), today.AddDays(400));
        Check(future.State == WarrantyState.InWarranty && future.RemainingDays == 400 &&
            future.RemainingDays == current.RemainingDays && future.IsStale,
            "A future extension still hides remaining warranty time.");
        Check(WarrantySnapshot.FromDates(today.AddDays(-50), today).State == WarrantyState.InWarranty &&
            WarrantySnapshot.FromDates(today.AddDays(-50), today.AddDays(-1)).State == WarrantyState.Expired &&
            WarrantySnapshot.FromDates(today.AddDays(-50), today.AddDays(-1)).RemainingDays == 0,
            "Warranty expiry boundary changed unexpectedly.");
    }

    private static async Task Backlight()
    {
        var settings = new AppSettings { LastKeyboardBacklightLevel = KeyboardBacklightLevel.High };
        var reads = 0; var writes = 0;
        KeyboardBacklightState State(KeyboardBacklightLevel? level) => new(level, 0, false, null, 0, 0);
        KeyboardBacklightState Read() { reads++; return State(KeyboardBacklightLevel.Low); }
        KeyboardBacklightState Write(KeyboardBacklightLevel level) { writes++; return State(level); }
        Check(settings.RestoreKeyboardBacklightOnStartup, "Startup restore must default to enabled.");
        await new KeyboardBacklightStartupRestore().ApplyOnceAsync(new AppSettings(), true, Read, Write);
        Check(reads == 0 && writes == 0, "First run without a saved brightness must leave hardware alone.");
        var startup = new KeyboardBacklightStartupRestore();
        await Task.WhenAll(startup.ApplyOnceAsync(settings, true, Read, Write), startup.ApplyOnceAsync(settings, true, Read, Write));
        await startup.ApplyOnceAsync(settings, true, Read, Write);
        Check(reads == 1 && writes == 1 && settings.LastKeyboardBacklightLevel == KeyboardBacklightLevel.High,
            "Startup backlight lock ran more than once or drifted its target.");
        await new KeyboardBacklightStartupRestore().ApplyOnceAsync(settings, true, () => State(KeyboardBacklightLevel.High), Write);
        Check(writes == 1, "Matching startup brightness was unnecessarily rewritten.");
        await new KeyboardBacklightStartupRestore().ApplyOnceAsync(settings, false, Read, Write);
        settings.RestoreKeyboardBacklightOnStartup = false;
        await new KeyboardBacklightStartupRestore().ApplyOnceAsync(settings, true, Read, Write);
        Check(reads == 1 && writes == 1, "Disabled or unsupported backlight lock accessed hardware.");
        settings.RestoreKeyboardBacklightOnStartup = true;
        var failed = new KeyboardBacklightStartupRestore();
        try { await failed.ApplyOnceAsync(settings, true, Read, _ => State(KeyboardBacklightLevel.Low)); throw new Exception("Unconfirmed brightness was accepted."); }
        catch (InvalidOperationException) { }
        await failed.ApplyOnceAsync(settings, true, Read, Write);
        Check(reads == 2 && writes == 1 && settings.LastKeyboardBacklightLevel == KeyboardBacklightLevel.High,
            "Startup failure caused repeated enforcement or rewrote the saved target.");
    }

    private static void BacklightPreferences()
    {
        using var runtime = new ToolkitRuntimeService(new AppSettings(), persistSystemSessionState: false);
        Check(runtime.TryRememberKeyboardBacklightLevel(KeyboardBacklightLevel.Off, out _), "Could not save configured brightness.");
        var loaded = CurveProfileStore.LoadSettings();
        Check(loaded.RestoreKeyboardBacklightOnStartup && loaded.LastKeyboardBacklightLevel == KeyboardBacklightLevel.Off,
            "Startup backlight lock was lost on reload.");
        Check(!runtime.TryRememberKeyboardBacklightLevel((KeyboardBacklightLevel)99, out _) &&
            runtime.Settings.LastKeyboardBacklightLevel == KeyboardBacklightLevel.Off, "Invalid backlight target was saved.");
        Check(runtime.TrySetKeyboardBacklightRestoreOnStartup(false, out _) && !CurveProfileStore.LoadSettings().RestoreKeyboardBacklightOnStartup,
            "Startup lock could not be disabled without a current reading.");
        Check(runtime.TryRememberKeyboardBacklightLevel(KeyboardBacklightLevel.Low, out _) &&
            CurveProfileStore.LoadSettings().LastKeyboardBacklightLevel == KeyboardBacklightLevel.Low &&
            !runtime.Settings.RestoreKeyboardBacklightOnStartup, "Brightness changes must be remembered even with restore disabled.");
        runtime.Settings.RestoreKeyboardBacklightOnStartup = true;
        using var page = new ToolkitInputPage(runtime);
        Check(Descendants(page).OfType<TextBlock>().Any(t => t.Text == "恢复上次设置的键盘背光亮度"), "Separate startup-restore row is missing.");
        File.WriteAllText(CurveProfileStore.SettingsPath, "{\"ConfigurationVersion\":\"1.0\"}");
        var legacy = CurveProfileStore.LoadSettings();
        Check(legacy.RestoreKeyboardBacklightOnStartup && legacy.LastKeyboardBacklightLevel is null,
            "Old configurations must default to enabled without inventing a saved brightness.");
    }

    private static void SettingGroups()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, ".tmp", "setting-groups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "fixture.dll"), []);
        var setting = new PluginSetting("enabled", "battery", new("测试设置", "Test setting"), "boolean", JsonSerializer.SerializeToElement(false))
            { GroupId = "test.groups.power", Description = new("这是插件增加的设置", "A setting contributed by a plugin"), Glyph = "\uE7F4" };
        var manifest = new PluginManifest("test.groups", "Groups", "1", 1, "fixture.dll", "Fixture", [], [], [setting], [])
        {
            SettingGroups = [new("test.groups.power", "battery", new("扩展供电", "Additional power settings"), new("插件设置分组", "Plugin setting group"), "\uE8B7")]
        };
        ToolkitPluginManager.ValidateManifest(manifest, directory);
        var legacy = manifest with { SettingGroups = [], Settings = [setting with { GroupId = null }] };
        ToolkitPluginManager.ValidateManifest(legacy, directory);
        Reject(manifest with { Settings = [setting with { GroupId = "missing" }] });
        Reject(manifest with { Settings = [setting with { PageId = "input" }] });
        Reject(manifest with { SettingGroups = [manifest.SettingGroups[0], manifest.SettingGroups[0]] });
        Reject(manifest with { SettingGroups = [manifest.SettingGroups[0] with { Id = "another.plugin.group" }] });
        var roundtrip = JsonSerializer.Deserialize<PluginManifest>(JsonSerializer.Serialize(manifest))!;
        Check(roundtrip.SettingGroups.Length == 1 && roundtrip.Settings[0].GroupId == setting.GroupId, "Group metadata did not round-trip.");
        foreach (var dark in new[] { true, false })
        {
            ModernTheme.Apply(Application.Current, dark);
            using var runtime = new ToolkitRuntimeService(new AppSettings { Theme = dark ? "dark" : "light", Language = dark ? "zh-CN" : "en-US",
                BackgroundBaseColorEnabled = true }, persistSystemSessionState: false);
            var plugin = new PluginInstallation(directory, manifest, "fixture") { Enabled = true };
            plugin.Settings[setting.Id] = setting.DefaultValue;
            ((List<PluginInstallation>)runtime.Plugins.Installations).Add(plugin);
            using var page = new TestPage(runtime, plugin);
            foreach (var width in new[] { 940d, 480d })
            {
                page.Width = width;
                page.Measure(new Size(width, double.PositiveInfinity));
                page.Arrange(new Rect(0, 0, width, page.DesiredSize.Height)); page.UpdateLayout();
                var borders = Descendants(page).OfType<Border>().ToArray();
                Check(borders[0].Background is SolidColorBrush brush && brush.Color == (Color)ColorConverter.ConvertFromString(
                    ToolkitPalette.For(runtime.IsDark, runtime.HasCustomBackground).Surface), "Group transparency differs from built-in cards.");
                var toggle = Descendants(page).OfType<CheckBox>().Single();
                var bounds = toggle.TransformToAncestor(page).TransformBounds(new Rect(toggle.RenderSize));
                Check(toggle.Content is null && bounds.Right <= width + 1 && bounds.Left >= 0, "Grouped setting duplicates its label or overflows.");
                var labels = Descendants(page).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Check(labels.Contains(dark ? "扩展供电" : "Additional power settings") && labels.Contains(dark ? "测试设置" : "Test setting"), "Group/row headings are missing.");
                var bitmap = new RenderTargetBitmap((int)width, (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(page);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(directory, $"{dark}-{width}.png")); encoder.Save(output);
            }
        }
        Console.WriteLine("Setting group previews: " + directory);
        void Reject(PluginManifest invalid)
        {
            try { ToolkitPluginManager.ValidateManifest(invalid, directory); }
            catch (InvalidDataException) { return; }
            throw new Exception("Invalid setting group was accepted.");
        }
    }

    private sealed class TestPage : ToolkitPageBase
    {
        internal TestPage(ToolkitRuntimeService runtime, PluginInstallation plugin) : base(runtime)
        {
            var root = new StackPanel();
            foreach (var item in BuildPluginSettings(plugin, plugin.Manifest.Settings)) root.Children.Add(item);
            Content = root;
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Descendants(child)) yield return item;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
