using System.Security.Cryptography;
using System.Text;

namespace KompasMcp.Domain.Journaling;

/// <summary>Named cross-process mutex keyed to a FILE PATH; serialises journal WRITES only.</summary>
/// <remarks>MEASURED: <c>FileMode.Append</c> is NOT atomic with two writers; under this lock it is.
/// INVARIANT: writes are serialised, reads stay free — a lost record would let a replay re-apply the
/// mutation. LIMIT: a named mutex, not a file lock — a file lock would also block readers.
/// History: docs/decisions/journaling.md#append-atomicity</remarks>
public sealed class NamedFileLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _held;

    private NamedFileLock(Mutex mutex) => _mutex = mutex;

    /// <summary>Lock for <paramref name="filePath"/>. The name comes from the FULL path (case-insensitive on
    /// Windows), so one file yields one lock and different files yield different locks.</summary>
    public static NamedFileLock For(string filePath, string purpose)
    {
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return new NamedFileLock(new Mutex(initiallyOwned: false, name: $"Local\\kompas-mcp-{purpose}-{hash}"));
    }

    /// <summary>Acquire the lock. INVARIANT: an abandoned mutex (owner killed) counts as acquired — otherwise
    /// one crashed process would block journal writes forever.</summary>
    public bool Enter(TimeSpan timeout)
    {
        try
        {
            _held = _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            _held = true;
        }

        return _held;
    }

    /// <summary>Release only if we hold it. LIMIT: a named mutex is owned by a THREAD, so a mutex taken on
    /// another thread is not released here; the refusal is swallowed, not read as success.</summary>
    public void Exit()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Owned by another thread — must not and need not be released.
        }
    }

    public void Dispose()
    {
        Exit();
        _mutex.Dispose();
    }
}
