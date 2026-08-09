using System;
using System.Threading;

namespace ThinkBookToolkit;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\ThinkBookToolkit.Application.v1";
    private const string ActivateEventName = "Local\\ThinkBookToolkit.Activate.v1";
    private const string ExitForUpdateEventName =
        "Local\\ThinkBookToolkit.ExitForUpdate.v1";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private readonly EventWaitHandle? _exitForUpdateEvent;
    private RegisteredWaitHandle? _activateWait;
    private RegisteredWaitHandle? _exitForUpdateWait;

    private SingleInstanceCoordinator(
        Mutex mutex,
        EventWaitHandle activateEvent,
        EventWaitHandle exitForUpdateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _exitForUpdateEvent = exitForUpdateEvent;
    }

    public static bool TryAcquire(out SingleInstanceCoordinator? coordinator)
        => TryAcquire(
            MutexName,
            ActivateEventName,
            ExitForUpdateEventName,
            out coordinator);

    internal static bool TryAcquireForTesting(
        string instanceId,
        out SingleInstanceCoordinator? coordinator) => TryAcquire(
            $"Local\\ThinkBookToolkit.Test.{instanceId}.Application",
            $"Local\\ThinkBookToolkit.Test.{instanceId}.Activate",
            $"Local\\ThinkBookToolkit.Test.{instanceId}.ExitForUpdate",
            out coordinator);

    private static bool TryAcquire(
        string mutexName,
        string activateEventName,
        string exitForUpdateEventName,
        out SingleInstanceCoordinator? coordinator)
    {
        var activateEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            activateEventName);
        var exitForUpdateEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            exitForUpdateEventName);
        var mutex = new Mutex(
            initiallyOwned: true,
            mutexName,
            out var created);
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
            exitForUpdateEvent.Dispose();
            coordinator = null;
            return false;
        }

        coordinator = new SingleInstanceCoordinator(
            mutex,
            activateEvent,
            exitForUpdateEvent);
        return true;
    }

    public static bool TrySignalExitForUpdate()
        => TrySignalExitForUpdate(MutexName, ExitForUpdateEventName);

    internal static bool TrySignalExitForUpdateForTesting(
        string instanceId) => TrySignalExitForUpdate(
            $"Local\\ThinkBookToolkit.Test.{instanceId}.Application",
            $"Local\\ThinkBookToolkit.Test.{instanceId}.ExitForUpdate");

    private static bool TrySignalExitForUpdate(
        string mutexName,
        string exitForUpdateEventName)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            using var exitEvent = EventWaitHandle.OpenExisting(
                exitForUpdateEventName);
            return exitEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Listen(Action activate, Action exitForUpdate)
    {
        if (_activateEvent is null || _exitForUpdateEvent is null)
            return;
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => activate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
        _exitForUpdateWait = ThreadPool.RegisterWaitForSingleObject(
            _exitForUpdateEvent,
            (_, _) => exitForUpdate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _activateWait?.Unregister(null);
        _exitForUpdateWait?.Unregister(null);
        _activateEvent?.Dispose();
        _exitForUpdateEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
    }
}
