using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class AutomationCompletionTests
{
    internal static void Run()
    {
        Wait(QueueTests());
        Check((int)AutomationStepKind.SensorRecordingEnabled == 34 && (int)AutomationStepKind.Resolution == 35 &&
            (int)AutomationTriggerKind.GameStopped == 3 && (int)AutomationTriggerKind.ToolkitStarted == 4,
            "Existing persisted enum values changed.");
        foreach (var kind in Enum.GetValues<AutomationStepKind>()) Check(AutomationStepCatalog.Metadata(kind).Kind == kind, "Missing editor metadata.");
        foreach (var vendor in new[] { "GenuineIntel", "AuthenticAMD", "Unknown" })
        {
            Check(AutomationStepCatalog.IsAvailableForCpu(AutomationStepKind.IntelCpuPowerBeta, vendor) == (vendor == "GenuineIntel") &&
                AutomationStepCatalog.IsAvailableForCpu(AutomationStepKind.AmdCpuPowerBeta, vendor) == (vendor == "AuthenticAMD") &&
                AutomationStepCatalog.IsAvailableForCpu(AutomationStepKind.NvApiGpuPowerBeta, vendor), "CPU Beta filtering or always-visible NVAPI is incorrect.");
        }
        foreach (var step in new[]
        {
            new AutomationStep { Kind = AutomationStepKind.Delay, Value = "NaN" },
            new AutomationStep { Kind = AutomationStepKind.Delay, Value = "Infinity" },
            new AutomationStep { Kind = AutomationStepKind.Resolution, Value = "0x0" },
            new AutomationStep { Kind = AutomationStepKind.DpiScale, Value = "99" },
            new AutomationStep { Kind = AutomationStepKind.PowerValue, Value = "CpuPl1", SecondaryValue = "-1" },
            new AutomationStep { Kind = AutomationStepKind.PowerLock, Value = "CpuPl1", SecondaryValue = "maybe" },
            new AutomationStep { Kind = AutomationStepKind.RefreshRate, Value = "unknown:165" },
            new AutomationStep { Kind = AutomationStepKind.PerformanceMode, Value = "Unknown" }
        }) Check(AutomationStepCatalog.Validate(step) is not null, "Invalid step passed preflight: " + step.Kind);
        Check(AutomationStepCatalog.Validate(new() { Kind = AutomationStepKind.Delay, Value = "1.25" }) is null &&
            AutomationStepCatalog.Validate(new() { Kind = AutomationStepKind.Resolution, Value = "2560x1600" }) is null &&
            AutomationStepCatalog.Validate(new() { Kind = AutomationStepKind.PowerLock, Value = "CpuPl1", SecondaryValue = "toggle" }) is null,
            "Valid new actions or fractional delays rejected.");
        var legacy = JsonSerializer.Deserialize<AutomationDefinition>("{\"Name\":\"Legacy\"}")!;
        Check(legacy.Enabled && !AutomationSettingsDefaults.Normalize([legacy with { Enabled = false }])[0].Enabled,
            "Definition enabled state broke compatibility.");
        var initial = new PowerSettingsState(80, 120, 95, 56, 10, 80, 85, 10, 25);
        var changed = AutomationRunner.WithPowerValue(initial, PowerSetting.CpuPl1, 65);
        Check(changed.CpuPl1 == 65 && changed.CpuPl2 == initial.CpuPl2 && initial.CpuPl1 == 80, "Power draft changed unrelated fields.");

        using var runtime = new ToolkitRuntimeService(new AppSettings { AutomationEnabled = true }, persistSystemSessionState: false);
        var workflow = new AutomationDefinition { Name = "Session fixture", Triggers = [AutomationTriggerKind.SessionLocked, AutomationTriggerKind.SessionUnlocked],
            Steps = [new() { Kind = AutomationStepKind.Delay, Value = "0" }] };
        runtime.Settings.Automations.Add(workflow);
        var handler = typeof(ToolkitRuntimeService).GetMethod("OnAutomationSessionSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var reason in new[] { SessionSwitchReason.SessionLock, SessionSwitchReason.SessionLock, SessionSwitchReason.SessionUnlock, SessionSwitchReason.SessionUnlock })
            handler.Invoke(runtime, [runtime, new SessionSwitchEventArgs(reason)]);
        Pump(120);
        Check(runtime.AutomationExecution.States.Count == 2 && runtime.AutomationExecution.States.All(s => s.Phase == AutomationRunPhase.Completed),
            "Session trigger deduplication failed.");
        workflow.Enabled = false;
        handler.Invoke(runtime, [runtime, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)]);
        Pump(80); Check(runtime.AutomationExecution.States.Count == 2, "Disabled definition ran on event.");
        runtime.Settings.FnKeyAutomationBindings[FnAutomationKeyIds.FnQ] = workflow.Id;
        Check(!runtime.HasFnAutomationBinding(FnAutomationKeyIds.FnQ), "Disabled definition intercepts Fn keys.");
        workflow.Enabled = true;
        workflow.Steps = [new() { Kind = AutomationStepKind.Delay, Value = "60" }];
        var run = runtime.RunAutomationAsync(workflow.Id);
        Check(runtime.AutomationExecution.HasPending, "Runtime execution state missing.");
        Check(runtime.TrySetAutomationEnabled(false, out _), "Could not disable master.");
        Wait(run); Check(run.Result.Cancelled, "Disabling master did not cancel an active delay.");
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        using var page = new ToolkitAutomationPage(runtime);
        var history = (Expander)typeof(ToolkitAutomationPage).GetField("_executionDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var details = (StackPanel)typeof(ToolkitAutomationPage).GetField("_automationDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var cancelAll = (Button)typeof(ToolkitAutomationPage).GetField("_cancelRuns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(LogicalTreeHelper.GetParent(history) == details && !history.IsExpanded && details.Visibility == Visibility.Collapsed &&
            cancelAll.Visibility == Visibility.Collapsed && ((StackPanel)page.Content).Children.OfType<Border>().Count() == 2,
            "Run history should be folded inside automation, with no idle cancel action or standalone status card.");
        var available = ((IEnumerable<AutomationStepMetadata>)typeof(ToolkitAutomationPage).GetMethod("AvailableCatalogItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!).Select(m => m.Kind).ToArray();
        Check(available.Contains(AutomationStepKind.IntelCpuPowerBeta) == CpuVendorDetector.IsIntel &&
            available.Contains(AutomationStepKind.AmdCpuPowerBeta) == CpuVendorDetector.IsAmd && available.Contains(AutomationStepKind.NvApiGpuPowerBeta),
            "The editor does not use CPU vendor filtering.");
        var incompatible = new AutomationDefinition { Name = "Other CPU fixture", Steps = [new() {
            Kind = CpuVendorDetector.IsIntel ? AutomationStepKind.AmdCpuPowerBeta : AutomationStepKind.IntelCpuPowerBeta, Value = "false" }] };
        runtime.Settings.Automations.Add(incompatible);
        Check(AutomationSettingsDefaults.Normalize([incompatible])[0].Steps.Count == 1, "Filtering deleted an existing incompatible step.");
        var incompatibleRun = runtime.RunAutomationAsync(incompatible.Id);
        Wait(incompatibleRun);
        Check(!incompatibleRun.Result.Success && incompatibleRun.Result.Error.Contains("当前处理器"), "An incompatible saved CPU action was executed.");
        var activeRun = runtime.RunAutomationAsync(workflow.Id);
        Check(cancelAll.Visibility == Visibility.Visible && cancelAll.IsEnabled, "Cancel-all is missing while work is active.");
        runtime.CancelAutomation(); Wait(activeRun);
        Check(cancelAll.Visibility == Visibility.Collapsed && !history.IsExpanded, "Completing a run unfolded history or left an idle cancel button visible.");
        var toggles = (Dictionary<AutomationTriggerKind, CheckBox>)typeof(ToolkitAutomationPage)
            .GetField("_triggerToggles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Check(toggles.Count == 8 && toggles.ContainsKey(AutomationTriggerKind.ToolkitStarted) && toggles.ContainsKey(AutomationTriggerKind.SystemResumed),
            "New trigger options missing.");
        RenderEditor(page, runtime, workflow);
        Console.WriteLine("Automation queue, cancellation, snapshots, session events, enabled rules and new actions passed.");
    }

    private static async Task QueueTests()
    {
        using var queue = new AutomationExecutionQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new AutomationDefinition { Name = "First", Steps = [new() { Kind = AutomationStepKind.Delay, Value = "1" }] };
        var second = first with { Id = Guid.NewGuid().ToString(), Name = "Second", Steps = [new() { Kind = AutomationStepKind.Delay, Value = "2" }] };
        var a = queue.RunAsync(first, async (_, token) => { await release.Task.WaitAsync(token); return AutomationRunResult.Ok(); }, default);
        var observed = "";
        var b = queue.RunAsync(second, (snapshot, _) => { observed = snapshot.Steps.Single().Value; return Task.FromResult(AutomationRunResult.Ok()); }, default);
        second.Steps[0].Value = "999";
        Check(!(await queue.RunAsync(second, (_, _) => throw new Exception("Duplicate ran"), default)).Success, "Duplicate run accepted.");
        var third = first with { Id = Guid.NewGuid().ToString(), Name = "Third" };
        var c = queue.RunAsync(third, (_, _) => throw new Exception("Cancelled queued task ran"), default);
        queue.Cancel(third.Id); Check((await c).Cancelled, "Queued task not cancelled.");
        release.SetResult(); await Task.WhenAll(a, b);
        Check(observed == "2" && !queue.HasPending, "Queued workflow did not use a snapshot.");
        var active = queue.RunAsync(first, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return AutomationRunResult.Ok(); }, default);
        queue.Cancel(); Check((await active).Cancelled, "Running delay not cancelled.");
        var failure = await queue.RunAsync(first, (_, _) => throw new IOException("fixture"), default);
        Check(!failure.Success && !queue.HasPending, "Failure stranded queue.");
        Check(!(await queue.RunAsync(first with { Enabled = false }, (_, _) => throw new Exception(), default)).Success, "Disabled job ran.");
        for (var i = 0; i < 24; i++) await queue.RunAsync(first, (_, _) => Task.FromResult(AutomationRunResult.Ok()), default);
        Check(queue.States.Count == 20, "History is unbounded.");
        var capacityGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new List<Task<AutomationRunResult>>();
        for (var i = 0; i < 32; i++) queued.Add(queue.RunAsync(first with { Id = Guid.NewGuid().ToString() },
            async (_, token) => { await capacityGate.Task.WaitAsync(token); return AutomationRunResult.Ok(); }, default));
        Check(!(await queue.RunAsync(first with { Id = Guid.NewGuid().ToString() }, (_, _) => throw new Exception(), default)).Success,
            "Queue exceeded its capacity.");
        queue.Cancel(); await Task.WhenAll(queued);
        Check(queued.All(t => t.Result.Cancelled) && !queue.HasPending, "Cancel-all left active or queued work behind.");
        queue.Dispose(); Check(!(await queue.RunAsync(first, (_, _) => throw new Exception(), default)).Success, "Disposed queue accepted work.");
    }

    private static void RenderEditor(ToolkitAutomationPage page, ToolkitRuntimeService runtime, AutomationDefinition workflow)
    {
        ModernTheme.Apply(Application.Current, runtime.IsDark);
        typeof(ToolkitAutomationPage).GetMethod("BeginEdit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [workflow]);
        var action = (ComboBox)typeof(ToolkitAutomationPage).GetField("_action", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        action.SelectedItem = action.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, AutomationStepKind.PowerLock));
        var output = Path.Combine(Environment.CurrentDirectory, ".tmp", "automation-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        foreach (var width in new[] { 1080d, 620d, 1080d })
        {
            var collapsed = width == 1080 && File.Exists(Path.Combine(output, "automation-1080.png"));
            if (collapsed) typeof(ToolkitAutomationPage).GetMethod("SetAutomationExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [false]);
            var root = (FrameworkElement)page;
            root.Width = width;
            for (var pass = 0; pass < 5; pass++)
            {
                root.InvalidateMeasure(); root.Measure(new Size(width, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height)); root.UpdateLayout();
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, collapsed ? "automation-collapsed.png" : $"automation-{width}.png")); encoder.Save(file);
        }
        Console.WriteLine("Automation editor previews: " + output);
    }
    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump(10);
        Check(task.IsCompleted, "Automation test timed out."); task.GetAwaiter().GetResult();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
