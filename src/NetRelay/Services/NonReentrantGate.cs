namespace NetRelay.Services;

public sealed class NonReentrantGate
{
    private int _entered;

    public bool TryEnter()
    {
        return Interlocked.Exchange(ref _entered, 1) == 0;
    }

    public void Exit()
    {
        Volatile.Write(ref _entered, 0);
    }
}
