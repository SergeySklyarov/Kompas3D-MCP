using System.Diagnostics;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The holder lookup of the FILE_LOCKED refusal runs under its OWN budget: a slow lookup must be
/// abandoned and the refusal must go out with the holder named unread and the reason stated, never wait
/// for a process name. The decision is a pure function of a substituted probe, so it is tested without the
/// Restart Manager and without a locked file.</summary>
/// <remarks>History: docs/decisions/tests.md#file-locked-guard</remarks>
public class FileLockOwnerLookupTests
{
    [Fact]
    public void AProbeThatNamesTheHolder_NamesIt()
    {
        var result = FileLockOwnerLookup.Within(
            "C:/tmp/x.m3d", _ => "KOMPAS.exe (pid 4242)", TimeSpan.FromMilliseconds(500));

        Assert.True(result.IsNamed);
        Assert.Equal("KOMPAS.exe (pid 4242)", result.Owner);
        Assert.Null(result.UnavailableReason);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void AProbeThatFindsNobody_NamesTheReason_NotASilentNull()
    {
        var result = FileLockOwnerLookup.Within("C:/tmp/x.m3d", _ => null, TimeSpan.FromMilliseconds(500));

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
        // "running" and cannot be told from a hang (naryad PRE_RELEASE_0_6_0 П3.2).
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
            "C:/tmp/x.m3d", _ => throw new InvalidOperationException("boom"), TimeSpan.FromMilliseconds(500));

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
