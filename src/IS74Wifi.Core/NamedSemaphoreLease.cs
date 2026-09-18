namespace IS74Wifi.Core;

/// <summary>
/// Cross-process single-holder gate that is safe to keep across async continuations.
/// Unlike Mutex, Semaphore ownership is not tied to the acquiring thread. The C#
/// runtime uses a distinct object name while the legacy PowerShell mutex may coexist.
/// </summary>
public sealed class NamedSemaphoreLease : IDisposable
{
    private readonly Semaphore semaphore;
    private bool owned;

    private NamedSemaphoreLease(Semaphore semaphore)
    {
        this.semaphore = semaphore;
        owned = true;
    }

    public static NamedSemaphoreLease? TryAcquire(string name)
    {
        var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, name);
        var acquired = false;
        try
        {
            acquired = semaphore.WaitOne(0);
            return acquired ? new NamedSemaphoreLease(semaphore) : null;
        }
        finally
        {
            if (!acquired)
            {
                semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (owned)
        {
            owned = false;
            try { semaphore.Release(); } catch (SemaphoreFullException) { }
        }
        semaphore.Dispose();
    }
}
