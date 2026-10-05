using System.Text.Json;
using KompasMcp.Domain.Journaling;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Session-ownership races: two simultaneous acquires cannot both get the right to work.</summary>
/// <remarks>INVARIANT: a race is a claim about ORDER, not state, so sequential calls cannot check it. Each
/// participant is a separate <see cref="HostOwnership"/> and all start SIMULTANEOUSLY from a barrier.
/// LIMIT: a real cross-process race is measured by two independent MCP clients on the shipped binaries; this
/// class checks the atomicity of the state TRANSITION, not cross-process exclusivity as a whole.
/// History: docs/decisions/tests.md#session-ownership-race-2</remarks>
public class SessionOwnershipRaceTests : IDisposable
{
    private readonly string _journal;
    private readonly string _directory;

    public SessionOwnershipRaceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "session-race-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _journal = Path.Combine(_directory, "operations.jsonl");
    }

    /// <summary>Run <paramref name="count"/> participants simultaneously and collect their outcomes.</summary>
    private OwnershipOutcome[] RunInParallel(int count, Func<HostOwnership, OwnershipOutcome> action)
    {
        var participants = Enumerable.Range(0, count).Select(_ => HostOwnership.Open(_journal)).ToArray();
        try
        {
            // A barrier: without it "simultaneously" would mean "whoever got there first", and the race would not happen.
            using var barrier = new Barrier(count);
            var outcomes = new OwnershipOutcome[count];

            var threads = participants.Select((ownership, index) => new Thread(() =>
            {
                barrier.SignalAndWait();
                outcomes[index] = action(ownership);
            })).ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            return outcomes;
        }
        finally
        {
            foreach (var participant in participants)
            {
                participant.Dispose();
            }
        }
    }

    [Fact]
    public void ConcurrentAcquire_ExactlyOneOwner()
    {
        var outcomes = RunInParallel(8, ownership => ownership.TryAcquire(explicitRequest: false).Outcome);

        Assert.Equal(1, outcomes.Count(o => o == OwnershipOutcome.Acquired));
        Assert.Equal(7, outcomes.Count(o => o == OwnershipOutcome.RefusedActiveOwner));
        Assert.DoesNotContain(OwnershipOutcome.RecordUnavailable, outcomes);
    }

    [Fact]
    public void ConcurrentBeginRelease_ExactlyOneTransitionsToReleasing()
    {
        using var owner = HostOwnership.Open(_journal);
        Assert.Equal(OwnershipOutcome.Acquired, owner.TryAcquire(explicitRequest: true).Outcome);

        // The participants are OTHER "identities" with the same pid: they claim to release a foreign session.
        var results = RunInParallel(8, ownership => ownership.BeginRelease() ? OwnershipOutcome.Acquired : OwnershipOutcome.RefusedActiveOwner);

        Assert.True(results.Count(o => o == OwnershipOutcome.Acquired) == 0,
            "освободить сеанс имеет право только владелец: чужое поколение не трогается");

        // The owner meanwhile stays the owner and can release the session itself.
        Assert.True(owner.BeginRelease());
        Assert.True(owner.CompleteRelease());
    }

    [Fact]
    public void ConcurrentCompleteRelease_ExactlyOnePublishes()
    {
        using var owner = HostOwnership.Open(_journal);
        owner.TryAcquire(explicitRequest: true);
        owner.BeginRelease();

        var published = RunInParallel(8, ownership => ownership.CompleteRelease() ? OwnershipOutcome.Acquired : OwnershipOutcome.RefusedActiveOwner);
        Assert.True(published.Count(o => o == OwnershipOutcome.Acquired) == 0);

        Assert.True(owner.CompleteRelease(), "владелец публикует released ровно один раз");
        Assert.False(owner.CompleteRelease(), "повторная публикация не выдаётся за успех");
    }

    /// <summary>Session hand-over: the owner releases, the next Host takes the session EXPLICITLY and gets a
    /// NEW generation; the previous owner then has no right to work or to release.</summary>
    [Fact]
    public void HandOver_PreviousOwnerCannotActAfterNewOwnerAcquired()
    {
        using var first = HostOwnership.Open(_journal);
        using var second = HostOwnership.Open(_journal);

        var firstGeneration = first.TryAcquire(explicitRequest: true).Generation;
        Assert.True(first.BeginRelease());
        Assert.True(first.CompleteRelease());

        var secondAcquire = second.TryAcquire(explicitRequest: true);
        Assert.Equal(OwnershipOutcome.Acquired, secondAcquire.Outcome);
        Assert.NotEqual(firstGeneration, secondAcquire.Generation);

        // THE OLD OWNER: neither work nor release a foreign session.
        Assert.False(first.MarkServing());
        Assert.False(first.BeginRelease());
        Assert.False(first.StillOwned());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp directory cleanup is best effort.
        }
    }
}
