namespace ThinkBookToolkit;

internal sealed class ResumePerformanceModePolicy
{
    internal sealed record Ticket(ItsMode Mode, long Generation);
    private readonly object _gate = new();
    private long _generation;
    private Ticket? _pending;
    internal void Capture(ItsMode mode)
    {
        lock (_gate)
        {
            if (_pending is not null) return;
            _generation++;
            if (PerformanceModeCycle.IsSelectableMode(mode)) _pending = new(mode, _generation);
        }
    }
    internal Ticket? Take()
    {
        lock (_gate) { var result = _pending; _pending = null; return result; }
    }
    internal bool IsCurrent(Ticket ticket) { lock (_gate) return ticket.Generation == _generation; }
    internal void Cancel() { lock (_gate) { _pending = null; _generation++; } }
}
