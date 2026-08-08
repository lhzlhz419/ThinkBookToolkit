using System;
using System.Threading;

namespace ThinkBookToolkit;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\ThinkBookToolkit.Application.v1";
    private const string ActivateEventName = "Local\\ThinkBookToolkit.Activate.v1";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _registeredWait;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    public static bool TryAcquire(out SingleInstanceCoordinator? coordinator)
    {
        var activateEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivateEventName);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (!created)
        {
            mutex.Dispose();
            try
            {
                activateEvent.Set();
            }
            catch
            {
            }
            activateEvent.Dispose();
            coordinator = null;
            return false;
        }

        coordinator = new SingleInstanceCoordinator(mutex, activateEvent);
        return true;
    }

    public void Listen(Action activate)
    {
        if (_activateEvent is null)
            return;
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => activate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
        _activateEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}
