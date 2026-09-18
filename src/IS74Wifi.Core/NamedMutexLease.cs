namespace IS74Wifi.Core;

public sealed class NamedMutexLease : IDisposable
{
    private readonly Mutex mutex;
    private bool owned;

    private NamedMutexLease(Mutex mutex, bool owned)
    {
        this.mutex = mutex;
        this.owned = owned;
    }

    public static NamedMutexLease? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return acquired ? new NamedMutexLease(mutex, owned: true) : null;
        }
        finally
        {
            if (!acquired)
            {
                mutex.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (owned)
        {
            owned = false;
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        mutex.Dispose();
    }
}
