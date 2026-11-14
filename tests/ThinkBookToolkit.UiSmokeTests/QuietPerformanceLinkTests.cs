using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class QuietPerformanceLinkTests
{
    internal static void Run()
    {
        const string key = nameof(ItsMode.QuietPerformance);
        var old = JsonSerializer.Deserialize<PerformanceFanLinkSettings>("""
            {"SwitchFanStrategyWithPerformanceMode":true,"FanControlTargetMode":3,
             "FanStrategiesByMode":{"Performance":{"Mode":2,"ProfileIndex":3}},
             "NoSwitchModes":{"Performance":true}}
            """)!;
        var migrated = PerformanceFanLinkDefaults.Normalize(old);
        var originalStrategy = PerformanceFanLinkDefaults.SelectionFor(migrated, ItsMode.Performance);
        var quietStrategy = PerformanceFanLinkDefaults.SelectionFor(migrated, ItsMode.QuietPerformance);
        Check(quietStrategy.Mode == originalStrategy.Mode && quietStrategy.ProfileIndex == originalStrategy.ProfileIndex &&
            !ReferenceEquals(quietStrategy, originalStrategy) && PerformanceFanLinkDefaults.IsNoSwitchMode(migrated, ItsMode.QuietPerformance),
            "Old base-performance linkage was not independently migrated to quiet performance.");
        migrated.FanControlTargetMode = ItsMode.QuietPerformance;
        migrated.FanStrategiesByMode[key] = new() { Mode = FanControlMode.AdvancedCurve };
        migrated = PerformanceFanLinkDefaults.Normalize(migrated);
        Check(migrated.FanControlTargetMode == ItsMode.QuietPerformance &&
            migrated.FanStrategiesByMode[key].Mode == FanControlMode.AdvancedCurve && migrated.NoSwitchModes[key],
            "Quiet linkage values were discarded during normalization.");

        using var runtime = new ToolkitRuntimeService(new AppSettings { PerformanceFanLink = migrated }, persistSystemSessionState: false);
        var ordinary = ToolkitRuntimeSnapshot.Empty with { ItsMode = ItsMode.Performance, QuietPerformance = new(QuietSwitch.Off, 1) };
        var quiet = ordinary with { QuietPerformance = new(QuietSwitch.On, 3) };
        Check(runtime.TrackFanPerformanceMode(ordinary) is not null &&
            runtime.TrackFanPerformanceMode(quiet) is not null &&
            runtime.TrackFanPerformanceMode(quiet) is null &&
            runtime.TrackFanPerformanceMode(ordinary) is not null, "Same-ITS quiet transitions did not change linkage generation.");
        var before = Field<int>(runtime, "_fanPerformanceModeChangeGeneration");
        Check(runtime.TrackFanPerformanceMode(quiet with { QuietPerformance = new(QuietSwitch.On, 1) }) is null &&
            Field<int>(runtime, "_fanPerformanceModeChangeGeneration") > before, "Unconfirmed quiet cache did not cancel older delayed actions.");
        Check(quiet.QuietPerformance.ConfirmsSelection(quiet.ItsMode, ItsMode.QuietPerformance) &&
            !quiet.QuietPerformance.ConfirmsSelection(quiet.ItsMode, ItsMode.Performance), "Reverse linkage confirmed the base mode instead of quiet performance.");

        runtime.SetReportForTesting(new([
            new(FeatureIds.PerformanceMode, "性能", "性能", true, "fixture"),
            new(FeatureIds.FanControl, "散热", "散热", true, "fixture"),
            new(FeatureIds.TemperatureMonitoring, "监控", "监控", false, "fixture")
        ]));
        runtime.SetSnapshotForTesting(quiet);
        using (var page = new ToolkitPerformancePage(runtime, coolingOnly: true))
        {
            Check(Field<ComboBox>(page, "_itsMode").Items.Count == 0, "Cooling page unexpectedly initialized a hidden mode selector.");
            var rows = Field<List<FrameworkElement>>(page, "_quietPerformanceLinkRows");
            Check(rows.Count == 2 && rows.All(r => r.Visibility == Visibility.Visible), "Quiet strategy/whitelist rows are missing.");
            var target = Field<ComboBox>(page, "_fanControlTargetMode");
            var item = target.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, ItsMode.QuietPerformance));
            Check(item.Content?.ToString() == "静音性能模式" && item.IsEnabled && target.SelectedItem == item,
                "Reverse linkage cannot select quiet performance.");
            var choices = Field<Dictionary<ItsMode, ComboBox>>(page, "_fanStrategyByPerformanceMode");
            choices[ItsMode.QuietPerformance].SelectedIndex = 1;
            Check(runtime.Settings.PerformanceFanLink.FanStrategiesByMode[key].Mode == FanControlMode.FixedRpm &&
                CurveProfileStore.LoadSettings().PerformanceFanLink.FanStrategiesByMode[key].Mode == FanControlMode.FixedRpm,
                "Quiet fan strategy did not persist through the normal UI save path.");
            runtime.SetSnapshotForTesting(ordinary with { QuietPerformance = QuietPerformanceStatus.Unsupported });
            Check(rows.All(r => r.Visibility == Visibility.Collapsed) && !item.IsEnabled &&
                runtime.Settings.PerformanceFanLink.FanControlTargetMode == ItsMode.QuietPerformance,
                "Capability loss discarded the configured target or left quiet controls active.");
            runtime.SetSnapshotForTesting(quiet);
            Check(rows.All(r => r.Visibility == Visibility.Visible) && item.IsEnabled, "Quiet linkage did not recover with capability.");
            for (var i = 0; i < 3; i++) runtime.SetSnapshotForTesting(i % 2 == 0 ? quiet : ordinary);
        }
        // A model with no mode controls must also tolerate quiet-capability data.
        runtime.SetReportForTesting(new([]));
        using (var noControls = new ToolkitPerformancePage(runtime, coolingOnly: false))
        {
            runtime.SetSnapshotForTesting(quiet);
            Check(Field<ComboBox>(noControls, "_itsMode").Items.Count == 0, "Absent mode controls were populated during refresh.");
        }
        Console.WriteLine("Quiet-performance cooling-page construction, two-way linkage, migration and capability recovery passed.");
    }

    private static T Field<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
