using System.Diagnostics;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Holds the test that deliberately starves the shared thread pool. It owns every pool worker
/// while it runs, so it must not share the pool with any other test collection.</summary>
/// <remarks>History: docs/decisions/tests.md#file-locked-guard</remarks>
[CollectionDefinition("file-lock-owner-lookup-pool-starvation", DisableParallelization = true)]
public sealed class PoolStarvationCollection
{
}

/// <summary>The holder lookup of the FILE_LOCKED refusal runs under its OWN budget: a slow lookup must be
/// abandoned and the refusal must go out with the holder named unread and the reason stated, never wait
/// for a process name. The decision is a pure function of a substituted probe, so it is tested without the
/// Restart Manager and without a locked file.</summary>
/// <remarks>History: docs/decisions/tests.md#file-locked-guard</remarks>
public class FileLockOwnerLookupTests
{
    // WHY A WIDE BUDGET HERE: the lookup returns as soon as the probe ends, so a budget far above any
    // plausible probe duration costs nothing and takes the scheduler out of the assertion. A SHORT budget
    // is kept only where the budget itself is what is under test.
    private static readonly TimeSpan WideBudget = TimeSpan.FromSeconds(30);

    [Fact]
    public void AProbeThatNamesTheHolder_NamesIt()
    {
        var result = FileLockOwnerLookup.Within(
            "C:/tmp/x.m3d", _ => "KOMPAS.exe (pid 4242)", WideBudget);

        Assert.True(result.IsNamed);
        Assert.Equal("KOMPAS.exe (pid 4242)", result.Owner);
        Assert.Null(result.UnavailableReason);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void AProbeThatFindsNobody_NamesTheReason_NotASilentNull()
    {
        var result = FileLockOwnerLookup.Within("C:/tmp/x.m3d", _ => null, WideBudget);

        Assert.False(result.IsNamed);
        Assert.Null(result.Owner);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("не вернул", result.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeThatDoesNotFinishInTheBudget_IsAbandoned_AndTheCallReturnsFast()
    {
        // The probe sleeps far longer than the budget. The lookup must NOT wait for it: the refusal it
        // belongs to would otherwise arrive after the client's sync budget, where a late answer reads as
        // "running" and cannot be told from a hang (naryad PRE_RELEASE_0_6_0 П3.2). This is the one test
        // whose budget IS the subject, so it keeps a short one.
        var budget = TimeSpan.FromMilliseconds(50);
        var stopwatch = Stopwatch.StartNew();

        var result = FileLockOwnerLookup.Within(
            "C:/tmp/x.m3d", _ => { Thread.Sleep(5000); return "too late"; }, budget);

        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.IsNamed);
        Assert.Null(result.Owner);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("50 мс", result.UnavailableReason!, StringComparison.Ordinal);
        // The call returns in the budget's order, not the probe's: a wide margin keeps the assertion
        // robust on a loaded machine while still excluding "waited for the probe".
        Assert.True(stopwatch.ElapsedMilliseconds < 2000,
            $"the lookup waited {stopwatch.ElapsedMilliseconds} ms — it must not wait for a slow probe");
    }

    [Fact]
    public void AProbeThatFaults_IsNamedNotThrown()
    {
        var result = FileLockOwnerLookup.Within(
            "C:/tmp/x.m3d", _ => throw new InvalidOperationException("boom"), WideBudget);

        Assert.False(result.IsNamed);
        Assert.Null(result.Owner);
        Assert.NotNull(result.UnavailableReason);
        Assert.Contains("InvalidOperationException", result.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultBudget_IsAFewHundredMilliseconds_FarBelowTheSyncBudget()
    {
        // The number is a BUDGET, not a measurement of the API: it must be large enough to name a holder
        // in the common case and far below the client's sync budget, which is measured in seconds.
        Assert.InRange(FileLockOwnerLookup.DefaultBudget.TotalMilliseconds, 100d, 1000d);
    }
}

/// <summary>The pool-starvation case: the lookup must answer even when the shared pool has no free worker.
/// Kept in its own class and collection because it deliberately blocks every pool worker.</summary>
/// <remarks>History: docs/decisions/tests.md#file-locked-guard</remarks>
[Collection("file-lock-owner-lookup-pool-starvation")]
public class FileLockOwnerLookupPoolStarvationTests
{
    [Fact]
    public async Task AnInstantProbeIsNamed_EvenWhenEveryPoolWorkerIsBusy()
    {
        // INVARIANT: the answer does not depend on the shared pool having a free worker. MEASURED: with the
        // probe queued to the pool, a starved pool left an INSTANT probe unstarted past the 300 ms budget,
        // so the refusal named "did not finish" instead of the holder it could have named at once.
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var occupy = Math.Max(ThreadPool.ThreadCount, minWorkers);
        using var gate = new ManualResetEventSlim(false);
        var started = 0;
        var blockers = new List<Task>();
        try
        {
            for (var i = 0; i < occupy; i++)
            {
                blockers.Add(Task.Run(() =>
                {
                    Interlocked.Increment(ref started);
                    gate.Wait();
                }));
            }

            // Every blocker must be RUNNING on its own pool worker before the pool is starved for real.
            var spin = Stopwatch.StartNew();
            while (Volatile.Read(ref started) < occupy && spin.ElapsedMilliseconds < 30_000)
            {
                Thread.Sleep(5);
            }

            var result = FileLockOwnerLookup.Within(
                "C:/tmp/x.m3d", _ => "KOMPAS.exe (pid 4242)", FileLockOwnerLookup.DefaultBudget);

            Assert.True(result.IsNamed, "владелец не назван при занятом пуле: " + result.UnavailableReason);
            Assert.Equal("KOMPAS.exe (pid 4242)", result.Owner);
            Assert.False(result.TimedOut);
        }
        finally
        {
            // INVARIANT: the pool is released even when an assertion above fails, so a failing test cannot
            // starve the collections that run after it.
            gate.Set();
            await Task.WhenAll(blockers);
        }
    }
}
