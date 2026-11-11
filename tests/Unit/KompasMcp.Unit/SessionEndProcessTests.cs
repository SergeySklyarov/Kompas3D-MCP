using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>What the session-end shutdown ends: every entry of the process it quits, and in which order.</summary>
/// <remarks>TEST: the pure rule only — no KOMPAS, no Worker, no COM. MEASURED: a session that launched KOMPAS
/// and then attached to the SAME process held two entries, and the shutdown rescued the documents of the
/// launched entry only, so the attached entry's unsaved documents died with the process. The rule is
/// therefore "the process is the unit", pinned here so a later change cannot narrow it to "the entry".
/// History: docs/decisions/host.md#client-shutdown</remarks>
public class SessionEndProcessTests
{
    private const int LaunchedPid = 2532;
    private const int ForeignPid = 4100;

    private static readonly SessionApplication[] Session =
    {
        new("0a387c53", LaunchedPid, Launched: true),
        new("6c19dd56", LaunchedPid, Launched: false),
        new("6c19dd56-b", LaunchedPid, Launched: false),
        new("foreign", ForeignPid, Launched: false),
        new("foreign-b", ForeignPid, Launched: true),
    };

    /// <summary>The entries the Quit() of one process ends: its own, both modes, and nothing else.</summary>
    [Fact]
    public void EndingWith_TakesEveryEntryOfTheProcessWhateverRegisteredIt()
    {
        var ending = SessionEndProcess.EndingWith(LaunchedPid, Session);

        Assert.Equal(new[] { "0a387c53", "6c19dd56", "6c19dd56-b" },
            ending.Select(entry => entry.ApplicationId));
        // The rule is the PROCESS, not the mode: an attached entry of the launched process is part of it.
        Assert.Contains(ending, entry => !entry.Launched);
    }

    /// <summary>An entry of another process is not ended by this Quit(), however it was registered.</summary>
    [Fact]
    public void EndingWith_LeavesAnotherProcessAlone()
    {
        var ending = SessionEndProcess.EndingWith(ForeignPid, Session);

        Assert.Equal(new[] { "foreign", "foreign-b" }, ending.Select(entry => entry.ApplicationId));
        Assert.DoesNotContain(ending, entry => entry.ProcessId == LaunchedPid);
    }

    /// <summary>The launched entry runs first, whatever position it has in the session's own order: it is the
    /// one that quits the process, and the attached entries it ends with must follow it, not precede it.</summary>
    [Fact]
    public void ShutdownOrder_PutsTheLaunchedEntryOfEachProcessFirst()
    {
        // The launched entry is LAST in the input on purpose: insertion order is not the rule.
        var session = new[]
        {
            new SessionApplication("attached-first", LaunchedPid, Launched: false),
            new SessionApplication("foreign-attached", ForeignPid, Launched: false),
            new SessionApplication("launched-last", LaunchedPid, Launched: true),
            new SessionApplication("foreign-launched", ForeignPid, Launched: true),
        };

        var order = SessionEndProcess.ShutdownOrder(session);

        Assert.Equal(2, order.Count(entry => entry.Launched));
        // Both launched entries come before every attached one, so each process is ended by its owner.
        Assert.All(order.Take(2), entry => Assert.True(entry.Launched));
        Assert.All(order.Skip(2), entry => Assert.False(entry.Launched));
        // The order is stable inside each group: the rule reorders the two groups, not the entries.
        Assert.Equal(new[] { "launched-last", "foreign-launched", "attached-first", "foreign-attached" },
            order.Select(entry => entry.ApplicationId));
    }

    /// <summary>An empty session ends nothing: the rule must not invent an entry, else the shutdown would
    /// call a disconnect that has nothing to disconnect.</summary>
    [Fact]
    public void EmptySession_EndsNothing()
    {
        Assert.Empty(SessionEndProcess.EndingWith(LaunchedPid, Array.Empty<SessionApplication>()));
        Assert.Empty(SessionEndProcess.ShutdownOrder(Array.Empty<SessionApplication>()));
    }
}
