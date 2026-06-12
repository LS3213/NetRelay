using System.Threading;

namespace NetRelay.Services;

public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _listenerTask;
    private bool _ownsMutex;

    public SingleInstanceService(string scopeName)
    {
        _mutex = new Mutex(false, $@"Local\{scopeName}-Mutex");
        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            $@"Local\{scopeName}-Activate");
    }

    public bool TryAcquire()
    {
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        return _ownsMutex;
    }

    public void SignalPrimaryInstance()
    {
        _activationEvent.Set();
    }

    public void StartListening(Action activationAction)
    {
        _listenerTask = Task.Run(() =>
        {
            var waitHandles = new WaitHandle[]
            {
                _activationEvent,
                _cancellationTokenSource.Token.WaitHandle
            };

            while (WaitHandle.WaitAny(waitHandles) == 0)
            {
                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                activationAction();
            }
        });
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _activationEvent.Set();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Shutdown must continue even if the listener is already exiting.
        }

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _activationEvent.Dispose();
        _mutex.Dispose();
        _cancellationTokenSource.Dispose();
    }
}
