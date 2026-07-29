namespace BAOCODETOOL.Core;

/// <summary>
/// Prevents two local hosts from polling the same Telegram bot token and
/// concurrently writing the SQLite database.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\VibeCodeReg-StandaloneHost";
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(false, MutexName);
        var ownsMutex = false;

        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous process crashed. Taking ownership here is the
            // normal recovery path rather than treating it as a live host.
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, ownsMutex);
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            finally
            {
                _ownsMutex = false;
            }
        }

        _mutex.Dispose();
    }
}
