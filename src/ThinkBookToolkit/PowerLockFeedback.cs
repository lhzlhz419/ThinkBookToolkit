using System;

namespace ThinkBookToolkit;

internal sealed class PowerLockFeedback
{
    private DateTimeOffset _retryAt, _notifyAt;
    private int _failures;
    internal string LastProblem { get; private set; } = "";
    internal bool CanAttempt(DateTimeOffset now) => now >= _retryAt;
    internal void Failed(DateTimeOffset now)
    {
        _failures = Math.Min(_failures + 1, 4);
        _retryAt = now.AddSeconds(Math.Min(30, 5 * (1 << (_failures - 1))));
    }
    internal void ReadSucceeded() { _failures = 0; _retryAt = default; }
    internal bool ShouldNotify(string problem, DateTimeOffset now)
    {
        var changed = problem != LastProblem;
        LastProblem = problem;
        if (string.IsNullOrEmpty(problem) || !changed || now < _notifyAt) return false;
        _notifyAt = now.AddMinutes(1);
        return true;
    }
    internal void Reset() { _retryAt = _notifyAt = default; _failures = 0; LastProblem = ""; }
}
