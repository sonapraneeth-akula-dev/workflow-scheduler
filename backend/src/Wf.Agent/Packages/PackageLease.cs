namespace Wf.Agent.Packages;

internal sealed class PackageLease : IDisposable
{
    internal sealed class Pin
    {
        internal int Count;
        internal bool InUse => Volatile.Read(ref Count) != 0;
    }

    private readonly Pin pin;
    private bool disposed;

    internal PackageLease(Pin pin)
    {
        this.pin = pin;
        Interlocked.Increment(ref pin.Count);
    }

    internal PackageLease Retain()
    {
        lock (this)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new PackageLease(pin);
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Decrement(ref pin.Count);
        }
    }
}
