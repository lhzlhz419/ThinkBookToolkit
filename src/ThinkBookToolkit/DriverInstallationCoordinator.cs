using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed class DriverInstallationCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<IReadOnlyCollection<DriverUpdateItem>, Task<DriverUpdateInstallResult>> _install;
    internal DriverInstallationCoordinator(Func<IReadOnlyCollection<DriverUpdateItem>, Task<DriverUpdateInstallResult>>? install = null)
        => _install = install ?? (updates => DriverUpdateController.InstallAsync(updates));
    internal bool IsBusy { get { lock (_sync) return _pending.Count > 0; } }
    internal bool IsQueued(string id) { lock (_sync) return _pending.Contains(id); }
    internal DriverUpdateInstallResult? LastResult { get; private set; }
    internal event EventHandler? Changed;

    internal async Task<DriverUpdateInstallResult> InstallAsync(IReadOnlyCollection<DriverUpdateItem> updates)
    {
        var batch = updates.DistinctBy(u => u.PackageId, StringComparer.OrdinalIgnoreCase).ToArray();
        if (batch.Length == 0) throw new ArgumentException("No updates selected.");
        lock (_sync)
        {
            if (batch.Any(u => _pending.Contains(u.PackageId)))
                throw new InvalidOperationException("此更新已在安装队列中。 / This update is already queued.");
            foreach (var item in batch) _pending.Add(item.PackageId);
        }
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
            await _gate.WaitAsync();
            try { return LastResult = await _install(batch); }
            finally { _gate.Release(); }
        }
        catch
        {
            LastResult = new("Failure", false, batch.Select(u => u.PackageId).ToArray());
            throw;
        }
        finally
        {
            lock (_sync) foreach (var item in batch) _pending.Remove(item.PackageId);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
