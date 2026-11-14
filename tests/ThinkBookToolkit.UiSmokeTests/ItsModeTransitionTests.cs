using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ThinkBookToolkit;

namespace ThinkBookToolkit.UiSmokeTests;

internal static class ItsModeTransitionTests
{
    internal static void Run()
    {
        VerifyQuietPerformance();
        var legacy = ItsModeDetector.ResolveControlPath(8191, true);
        var modernReads = 0;
        foreach (var target in new[] { ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Geek })
        {
            Check(ItsModeDetector.ReadModeUsingPath(legacy,
                    () => { modernReads++; return ItsMode.Intelligent; }, () => target) == target,
                "Legacy writes are still being read through the stale modern interface.");
        }
        Check(modernReads == 0 &&
              ItsModeDetector.ReadModeUsingPath(legacy, () => ItsMode.Intelligent, () => ItsMode.Unknown) == ItsMode.Unknown,
            "Missing legacy state must not fall back to a stale modern state.");
        Check(ItsModeDetector.ResolveControlPath(8192, true, ItsModeControlPath.LegacyLitssvc) == legacy &&
              ItsModeDetector.ReadModeUsingPath(ItsModeControlPath.ModernDispatcher,
                  () => ItsMode.Performance, () => throw new Exception("Wrong interface")) == ItsMode.Performance,
            "Successful legacy fallback or the valid modern path was not retained.");

        foreach (var source in new[] { ItsMode.Intelligent, ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Geek })
        {
            var operations = new List<string>();
            var overlay = false;
            var pendingPerformanceWrite = false;
            var current = source;
            ItsModeController.ExecuteLegacyTransition(ItsMode.Geek, () => current,
                command => { operations.Add($"service:{command:X}"); pendingPerformanceWrite = true; },
                enabled => { operations.Add("overlay:" + enabled); overlay = enabled; },
                () =>
                {
                    operations.Add("wait");
                    Check(pendingPerformanceWrite, "Wait was requested without a base-mode write.");
                    current = ItsMode.Performance;
                    pendingPerformanceWrite = false;
                    overlay = false; // A delayed LITSSVC write clears the overlay.
                });
            if (pendingPerformanceWrite) overlay = false;
            Check(overlay, "A delayed performance write overwrote the Geek overlay.");
            Check(source is ItsMode.Performance or ItsMode.Geek
                ? operations.SequenceEqual(new[] { "overlay:True" })
                : operations.SequenceEqual(new[] { "service:86", "service:94", "wait", "overlay:True" }),
                "Legacy Geek transition ordering is incorrect.");
        }
        var sequence = new Queue<ItsMode>([ItsMode.PowerSaving, ItsMode.Performance,
            ItsMode.PowerSaving, ItsMode.Performance, ItsMode.Performance]);
        var delays = 0;
        ItsModeController.WaitForLegacyPerformance(() => sequence.Dequeue(), _ => delays++);
        Check(delays == 4, "Base-mode settling did not require consecutive confirmations.");
        var overlayApplied = false;
        try
        {
            ItsModeController.ExecuteLegacyTransition(ItsMode.Geek, () => ItsMode.Intelligent, _ => { },
                _ => overlayApplied = true,
                () => ItsModeController.WaitForLegacyPerformance(() => ItsMode.Intelligent, _ => { }));
            throw new Exception("Unconfirmed base transition was accepted.");
        }
        catch (TimeoutException) { }
        Check(!overlayApplied, "Geek overlay was enabled before the base mode was confirmed.");
        var normal = new List<string>();
        ItsModeController.ExecuteLegacyTransition(ItsMode.Performance, () => ItsMode.Performance,
            command => normal.Add($"service:{command:X}"), value => normal.Add("overlay:" + value), () => { });
        Check(normal.SequenceEqual(new[] { "overlay:False", "service:86", "service:94" }),
            "Exiting Geek mode did not clear the overlay before applying Performance.");

        using var runtime = new ToolkitRuntimeService(new AppSettings());
        var type = typeof(ToolkitRuntimeService);
        type.GetField("_lastConfirmedItsMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, ItsMode.Geek);
        type.GetField("_itsModeReadGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, 2);
        var resolve = type.GetMethod("ResolveItsModeForRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.Intelligent, 1])! == ItsMode.Geek,
            "An in-flight stale refresh overwrote the newly confirmed mode.");
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.PowerSaving, 2])! == ItsMode.PowerSaving,
            "Normal subsequent observations of external mode changes were suppressed.");
        runtime.SetSnapshotForTesting(runtime.Snapshot with { ItsMode = ItsMode.Intelligent });
        type.GetField("_switchingPerformanceMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, true);
        Check((ItsMode)resolve.Invoke(runtime, [ItsMode.Performance, 2])! == ItsMode.Intelligent,
            "The intermediate Performance base mode leaked into mode/fan-link state during a Geek transition.");
    }

    private static void VerifyQuietPerformance()
    {
        var off = QuietPerformanceController.Parse(["QuietPerformanceMode,DISABLE;[Optional:Enable,Disable]"], 0x81);
        var on = QuietPerformanceController.Parse(["quietperformancemode,Enable;"], 0x83);
        Check(off.State == QuietSwitch.Off && on.State == QuietSwitch.On, "Switch state must come from the current BIOS value, not the cache or options.");
        Check(on.Selection(ItsMode.Performance) == ItsMode.QuietPerformance && on.Selection(ItsMode.Geek) == ItsMode.Geek &&
              on.Selection(ItsMode.Intelligent) == ItsMode.Intelligent, "Quiet flag leaked into non-performance modes.");
        Check(QuietPerformanceController.Parse(["Unrelated,QuietPerformanceMode"], 1).State == QuietSwitch.Unknown &&
              QuietPerformanceController.Parse(["QuietPerformanceMode,Auto"], 1).State == QuietSwitch.Unknown &&
              QuietPerformanceController.Parse(["QuietPerformanceMode,Enable"], 2).State == QuietSwitch.Unsupported,
              "Unsupported or unrecognized capability was accepted.");

        var mode = ItsMode.Geek;
        var firmware = off;
        var sent = new List<int>();
        var pending = 0;
        void Send(int code)
        {
            if (code is 187 or 188) Check(mode == ItsMode.Performance, "Switch command sent before performance base was confirmed.");
            sent.Add(code);
            pending = code;
        }
        void Advance(int _)
        {
            if (pending == 165) mode = ItsMode.Performance;
            else if (pending == 187) firmware = on;
            else if (pending == 188) firmware = off;
            pending = 0;
        }
        QuietPerformanceStatus Set(bool target) => QuietPerformanceController.Transition(target, firmware, () => mode, () => firmware, Send, Advance).Verified;
        Check(Set(true) == on && sent.SequenceEqual([165, 187]), "Quiet mode did not verify both operations.");
        sent.Clear(); Check(Set(true) == on && sent.Count == 0, "Re-selecting quiet mode rewrote BIOS.");
        Check(Set(false) == off && sent.SequenceEqual([188]), "Returning to ordinary performance should only clear the flag.");

        foreach (var enable in new[] { true, false })
        {
            var target = enable ? ItsMode.QuietPerformance : ItsMode.Performance;
            var correct = enable ? on : off;
            var stale = QuietPerformanceController.Parse([correct.Detail!], enable ? 1 : 3);
            Check(stale.Selection(mode) == target && !stale.ConfirmsSelection(mode, target),
                "A stale Dispatcher cache may describe the BIOS selection but must not skip a restore.");
            var code = enable ? 187 : 188;
            sent.Clear();
            var delays = 0;
            var repaired = QuietPerformanceController.Transition(enable, stale, () => mode,
                () => delays < 2 ? stale : correct, sent.Add, _ => delays++);
            Check(repaired.Verified == correct && repaired.Verified.ConfirmsSelection(mode, target) &&
                  delays == 2 && sent.SequenceEqual([code]), "Expected one service command and delayed cache confirmation.");
            sent.Clear();
            try
            {
                QuietPerformanceController.Transition(enable, stale, () => mode, () => stale, sent.Add, _ => { });
                throw new Exception("A permanently stale cache reported success.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("部分成功") &&
                ex.Message.Contains("PE_SpecialPowerMode=" + stale.DispatcherBits)) { }
            Check(sent.SequenceEqual([code]), "A failed cache repair repeated the BIOS switch command.");
        }

        void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Unconfirmed quiet performance transition reported success.");
        }
        sent.Clear(); mode = ItsMode.Intelligent;
        Reject(() => QuietPerformanceController.Transition(true, off, () => mode, () => off, sent.Add, _ => { }));
        Check(sent.SequenceEqual([165]), "Switch write followed an unconfirmed ITS transition.");
        sent.Clear(); mode = ItsMode.Performance;
        Reject(() => QuietPerformanceController.Transition(true, off, () => mode, () => off, sent.Add, _ => { }));
        Check(sent.SequenceEqual([187]), "Stale BIOS readback must fail without retrying the write.");
        sent.Clear();
        Reject(() => QuietPerformanceController.Transition(true, QuietPerformanceStatus.Unsupported, () => mode, () => on, sent.Add, _ => { }));
        Check(sent.Count == 0 && !ItsModeDetector.IsModeSupported(ItsMode.QuietPerformance, ItsModeControlPath.LegacyLitssvc), "Unsupported path allowed a quiet switch write.");
        var selector = new System.Windows.Controls.ComboBox();
        foreach (var raw in PerformanceModeCycle.DefaultOrder) selector.Items.Add(new System.Windows.Controls.ComboBoxItem { Tag = raw });
        var snapshot = ToolkitRuntimeSnapshot.Empty with { ItsMode = ItsMode.Performance, QuietPerformance = on };
        var empty = new System.Windows.Controls.ComboBox();
        ToolkitPageBase.SyncPerformanceChoices(empty, snapshot, ItsModeControlPath.ModernDispatcher, true);
        Check(empty.Items.Count == 0, "An uninitialized selector was populated or threw during quiet-mode discovery.");
        empty.Items.Add(new System.Windows.Controls.ComboBoxItem { Tag = ItsMode.Intelligent });
        ToolkitPageBase.SyncPerformanceChoices(empty, snapshot, ItsModeControlPath.ModernDispatcher, true);
        Check(empty.Items.Count == 1, "A selector lacking the Performance insertion point was corrupted.");
        ToolkitPageBase.SyncPerformanceChoices(selector, snapshot, ItsModeControlPath.ModernDispatcher, true);
        Check(selector.Items.Count == 5 && (selector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "静音性能模式",
            "Verified quiet performance is missing or incorrectly selected.");
        ToolkitPageBase.SyncPerformanceChoices(selector, snapshot with { QuietPerformance = QuietPerformanceStatus.Unsupported }, ItsModeControlPath.ModernDispatcher, true);
        Check(selector.Items.Count == 4 && Equals((selector.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag, ItsMode.Performance),
            "Unsupported machines must keep the original four choices.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
