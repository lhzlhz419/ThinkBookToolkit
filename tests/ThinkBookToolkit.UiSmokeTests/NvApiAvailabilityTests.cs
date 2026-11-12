using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class NvApiAvailabilityTests
{
    internal static async Task RunAsync()
    {
        Check(NvPcfDriverDetector.Evaluate([]) == NvPcfDriverStatus.Missing &&
            NvPcfDriverDetector.Evaluate([(false, 45u)]) == NvPcfDriverStatus.Missing &&
            NvPcfDriverDetector.Evaluate([(true, 22u)]) == NvPcfDriverStatus.Disabled &&
            NvPcfDriverDetector.Evaluate([(true, 31u)]) == NvPcfDriverStatus.Unavailable &&
            NvPcfDriverDetector.Evaluate([(true, 0u)]) == NvPcfDriverStatus.Available,
            "NVPCF device status classification is incorrect.");
        var active = new TemperatureSnapshot(null, 50, null, null, 20, "", "GPU", "")
            { GpuName = "NVIDIA GeForce RTX 4060", DiscreteGpuState = DiscreteGpuActivityState.Active };
        Task<PowerSettingsState> Switch(bool enabled) => Task.FromResult(NvPcfPowerPolicy.EmptyState());
        void AllowProbe(ToolkitRuntimeService runtime) => typeof(ToolkitRuntimeService)
            .GetField("_nextNvApiProbe", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runtime, DateTimeOffset.MinValue);
        ToolkitRuntimeService Runtime(bool available = true)
        {
            var runtime = new ToolkitRuntimeService(new AppSettings { UseNvApiGpuPower = true }, persistSystemSessionState: false);
            runtime.SetReportForTesting(new([new(FeatureIds.NvApiGpuPower, "性能", "NVAPI", available, "fixture")]));
            runtime.SetSnapshotForTesting(ToolkitRuntimeSnapshot.Empty with { Temperatures = active });
            return runtime;
        }
        foreach (var status in new[] { NvPcfDriverStatus.Missing, NvPcfDriverStatus.Disabled, NvPcfDriverStatus.Unavailable })
        {
            using var runtime = Runtime();
            using var page = new ToolkitSettingsPage(runtime);
            var row = Field<Border>(page, "_nvApiGpuPowerRow");
            var toggle = Field<CheckBox>(page, "_useNvApiGpuPower");
            Check(row.Visibility == Visibility.Visible && toggle.IsChecked == true, "Test did not start with an enabled visible switch.");
            var saves = 0; var probes = 0;
            Task Probe() { probes++; return Task.CompletedTask; }
            void Save() { Check(runtime.Settings.UseNvApiGpuPower, "Driver failure erased the saved enable preference."); saves++; }
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, status, Probe, Save);
            Check(runtime.Settings.UseNvApiGpuPower && !runtime.NvApiGpuPowerEnabled && !runtime.NvApiGpuPowerVisible &&
                runtime.Report?.IsAvailable(FeatureIds.NvApiGpuPower) == false && saves == 1 && probes == 0,
                "Missing/disabled NVPCF did not suspend the backend while retaining user intent.");
            Check(row.Visibility == Visibility.Collapsed && toggle.IsChecked == false && !toggle.IsEnabled,
                "An already-open settings page retained the Beta row.");
            Check(await runtime.SetNvApiGpuPowerEnabledAsync(true) is not null, "Unavailable NVPCF can still be enabled.");
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, status, Probe, Save);
            Check(saves == 1 && probes == 0, "Unchanged driver absence repeated writes/probes.");
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, Probe, Save, Switch);
            Check(probes == 1 && row.Visibility == Visibility.Visible && toggle.IsChecked == true && toggle.IsEnabled &&
                runtime.Settings.UseNvApiGpuPower && runtime.NvApiGpuPowerEnabled, "Driver recovery did not restore the previously enabled Beta switch.");
        }
        using (var runtime = Runtime())
        {
            var probes = 0;
            Task Failure() { probes++; throw new InvalidOperationException("No PCF controller"); }
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, Failure, () => { });
            Check(!runtime.NvApiGpuPowerVisible && !runtime.NvApiGpuPowerEnabled && runtime.Settings.UseNvApiGpuPower,
                "A failed NVPCF capability probe left the Beta switch on or visible.");
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, Failure, () => { });
            Check(probes == 1, "Failed probes ignore their retry cooldown while the switch is hidden.");
            AllowProbe(runtime);
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { }, Switch);
            Check(runtime.NvApiGpuPowerEnabled, "Probe recovery did not restore the saved preference.");
        }
        using (var runtime = Runtime())
        {
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Disabled, () => Task.CompletedTask,
                () => throw new IOException("simulated save failure"));
            Check(!runtime.NvApiGpuPowerEnabled && runtime.Settings.UseNvApiGpuPower && !runtime.NvApiGpuPowerVisible,
                "Save failure rolled an unavailable backend back to enabled.");
            var saved = false;
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Disabled, () => Task.CompletedTask, () => saved = true);
            Check(saved, "Failed persistence was not retried.");
        }
        using (var runtime = Runtime())
        {
            typeof(ToolkitRuntimeService).GetField("_nvApiReadFailed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runtime, 1);
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available,
                () => throw new InvalidOperationException("Must not probe yet"), () => { });
            Check(!runtime.NvApiGpuPowerEnabled && runtime.Settings.UseNvApiGpuPower, "Read failure erased intent or kept the backend active.");
            AllowProbe(runtime);
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { }, Switch);
            Check(runtime.NvApiGpuPowerEnabled, "Read failure recovery did not reactivate Beta.");
        }
        using (var runtime = Runtime())
        {
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Missing, () => Task.CompletedTask,
                () => CurveProfileStore.SaveSettings(runtime.Settings));
            Check(CurveProfileStore.LoadSettings().UseNvApiGpuPower, "Restart would lose the enable preference during an outage.");
            using var restarted = new ToolkitRuntimeService(CurveProfileStore.LoadSettings(), persistSystemSessionState: false);
            restarted.SetReportForTesting(new([new(FeatureIds.NvApiGpuPower, "性能", "NVAPI", false, "fixture")]));
            await restarted.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { }, Switch);
            Check(restarted.NvApiGpuPowerEnabled, "A restarted Toolkit did not restore Beta after driver recovery.");
            Check(await runtime.SetNvApiGpuPowerEnabledAsync(false) is null && !runtime.Settings.UseNvApiGpuPower,
                "An explicit disable could not cancel recovery while the driver was absent.");
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { },
                _ => throw new InvalidOperationException("Manual off must not reactivate hardware."));
            Check(!runtime.NvApiGpuPowerEnabled && runtime.NvApiGpuPowerVisible, "Manual off was overridden by recovery.");
        }
        using (var runtime = Runtime())
        {
            runtime.Settings.UseNvApiGpuPower = false;
            runtime.SetSnapshotForTesting(ToolkitRuntimeSnapshot.Empty with { Temperatures = active });
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Disabled, () => Task.CompletedTask, () => { });
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { },
                _ => throw new InvalidOperationException("Originally disabled Beta must stay disabled."));
            Check(!runtime.NvApiGpuPowerEnabled && !runtime.Settings.UseNvApiGpuPower, "Originally off preference was lost.");
        }
        using (var runtime = Runtime())
        {
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Missing, () => Task.CompletedTask, () => { });
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active with { DiscreteGpuState = DiscreteGpuActivityState.Inactive },
                NvPcfDriverStatus.Available, () => throw new InvalidOperationException("Idle GPU must not be probed."), () => { }, Switch);
            Check(!runtime.NvApiGpuPowerEnabled && runtime.Settings.UseNvApiGpuPower, "Idle GPU recovery lost the preference.");
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { },
                _ => throw new InvalidOperationException("Simulated enable failure"));
            Check(!runtime.NvApiGpuPowerEnabled && runtime.Settings.UseNvApiGpuPower, "Failed activation erased future recovery.");
            AllowProbe(runtime);
            await runtime.UpdateNvApiAvailabilityForTestingAsync(active, NvPcfDriverStatus.Available, () => Task.CompletedTask, () => { }, Switch);
            Check(runtime.NvApiGpuPowerEnabled, "Activation failure did not allow a later retry.");
        }
        using (var runtime = Runtime(available: false))
        {
            using var page = new ToolkitSettingsPage(runtime);
            Check(Field<Border>(page, "_nvApiGpuPowerRow").Visibility == Visibility.Collapsed, "Unprobed/unsupported NVPCF is initially visible.");
        }
        Console.WriteLine("NVPCF absence/disable/recovery and live Beta switch tests passed.");
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
