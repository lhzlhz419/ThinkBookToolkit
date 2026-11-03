using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal enum AutomationRunPhase { Queued, Running, Completed, Failed, Cancelled }
internal sealed record AutomationRunState(string Id, string Name, AutomationRunPhase Phase, int Step, int Total, string Error = "");

internal sealed class AutomationExecutionQueue : IDisposable
{
    private sealed record Entry(CancellationTokenSource Stop, AutomationRunState State);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AutomationRunState> _history = [];
    private bool _closed;
    internal event EventHandler? Changed;
    internal IReadOnlyList<AutomationRunState> States { get { lock (_sync) return _pending.Values.Select(e => e.State).Concat(_history).ToArray(); } }
    internal bool IsPending(string id) { lock (_sync) return _pending.ContainsKey(id); }
    internal bool HasPending { get { lock (_sync) return _pending.Count > 0; } }
    internal void Progress(string id, int step)
    {
        lock (_sync) if (_pending.TryGetValue(id, out var entry)) _pending[id] = entry with { State = entry.State with { Phase = AutomationRunPhase.Running, Step = step } };
        Changed?.Invoke(this, EventArgs.Empty);
    }
    internal async Task<AutomationRunResult> RunAsync(AutomationDefinition source,
        Func<AutomationDefinition, CancellationToken, Task<AutomationRunResult>> execute, CancellationToken cancellationToken)
    {
        var snapshot = source with { Steps = source.Steps.Select(s => s with { }).ToList(), Triggers = source.Triggers.ToList(),
            PluginTriggers = source.PluginTriggers.ToList(), PluginConditions = source.PluginConditions.ToList() };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            if (_closed || !snapshot.Enabled) return AutomationRunResult.Failure("自动化已停用或程序正在退出。 / Automation disabled or Toolkit exiting.");
            if (_pending.ContainsKey(snapshot.Id)) return AutomationRunResult.Failure("此自动化已在运行或排队。 / Automation already running or queued.");
            if (_pending.Count >= 32) return AutomationRunResult.Failure("自动化队列已满（32 项）。 / Automation queue is full (32).");
            _pending.Add(snapshot.Id, new(stop, new(snapshot.Id, snapshot.Name, AutomationRunPhase.Queued, 0, snapshot.Steps.Count)));
        }
        var result = AutomationRunResult.Failure("自动化未完成。 / Automation incomplete.");
        var acquired = false;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
            await _gate.WaitAsync(stop.Token); acquired = true;
            stop.Token.ThrowIfCancellationRequested();
            Progress(snapshot.Id, 0);
            result = await execute(snapshot, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return result = AutomationRunResult.Failure("自动化已取消。 / Automation cancelled.") with { Cancelled = true };
        }
        catch (Exception ex) { return result = AutomationRunResult.Failure(ex.GetBaseException().Message); }
        finally
        {
            if (acquired) _gate.Release();
            lock (_sync)
            {
                if (_pending.Remove(snapshot.Id, out var entry))
                    _history.Insert(0, entry.State with { Phase = result.Cancelled ? AutomationRunPhase.Cancelled : result.Success ? AutomationRunPhase.Completed : AutomationRunPhase.Failed, Error = result.Error });
                if (_history.Count > 20) _history.RemoveRange(20, _history.Count - 20);
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    internal void Cancel(string? id = null)
    {
        CancellationTokenSource[] stops;
        lock (_sync) stops = _pending.Where(p => id is null || p.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.Stop).ToArray();
        foreach (var stop in stops) try { stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void Dispose() { lock (_sync) _closed = true; Cancel(); }
}
