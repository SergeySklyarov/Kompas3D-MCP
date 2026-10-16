using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The session-end budget: how long the Worker may still take rescue copies before it must close
/// and leave.</summary>
/// <remarks>TEST: the pure rule only - a fake clock, no KOMPAS and no Worker. INVARIANT: the budget is
/// what keeps the Worker from being killed by the Host mid-sequence; a killed Worker never terminates the
/// PID of the instance it launched, so the instance is orphaned. The rule therefore has to be exact at
/// the boundary, not "about right".
/// History: docs/decisions/host.md#shutdown-many-docs</remarks>
public class SessionEndBudgetTests
{
    /// <summary>A clock the test moves by hand, in milliseconds.</summary>
    private sealed class FakeClock
    {
        public long Now { get; set; }

        public long Read() => Now;
    }

    [Fact]
    public void FreshBudget_AllowsRescues()
    {
        var clock = new FakeClock { Now = 1_000 };
        var budget = new SessionEndBudget(15_000, 5_000, clock.Read, startMs: 1_000);

        Assert.True(budget.CanRescueMore);
        Assert.False(budget.Exhausted);
        Assert.Equal(15_000, budget.RemainingMs);
    }

    [Fact]
    public void RescueStopsWhenTheCloseReserveIsReached()
    {
        var clock = new FakeClock { Now = 0 };
        var budget = new SessionEndBudget(15_000, 5_000, clock.Read, startMs: 0);

        clock.Now = 9_999;
        Assert.True(budget.CanRescueMore);

        // At exactly the reserve boundary the rescue phase is CLOSED: the remaining 5 s belong to the
        // close, Quit() and the exit wait.
        clock.Now = 10_000;
        Assert.False(budget.CanRescueMore);
        Assert.False(budget.Exhausted);
    }

    [Fact]
    public void ExhaustedOnlyWhenTheWholeBudgetIsSpent()
    {
        var clock = new FakeClock { Now = 0 };
        var budget = new SessionEndBudget(15_000, 5_000, clock.Read, startMs: 0);

        clock.Now = 14_999;
        Assert.False(budget.Exhausted);
        Assert.Equal(1, budget.RemainingMs);

        clock.Now = 15_000;
        Assert.True(budget.Exhausted);
        Assert.Equal(0, budget.RemainingMs);
    }

    [Fact]
    public void RemainingNeverGoesNegative()
    {
        var clock = new FakeClock { Now = 0 };
        var budget = new SessionEndBudget(15_000, 5_000, clock.Read, startMs: 0);

        clock.Now = 60_000;

        Assert.Equal(0, budget.RemainingMs);
    }

    /// <summary>The default budget is strictly SHORTER than the Host's window, or it would be no bound at
    /// all: the Host kills the Worker when the window ends.</summary>
    [Fact]
    public void DefaultBudgetFitsInsideTheHostWindow()
    {
        // The Host's window is 20 000 ms (WorkerSupervisor.GracefulShutdownWindowMs). The default budget
        // must leave a margin for the process to actually leave after the sequence returns.
        Assert.True(SessionEndBudget.DefaultTotalMs < 20_000,
            "бюджет завершения обязан быть меньше окна Хоста");
        Assert.True(SessionEndBudget.DefaultCloseReserveMs < SessionEndBudget.DefaultTotalMs,
            "резерв на закрытие обязан оставлять время на спасение");
    }

    [Fact]
    public void AReserveThatSwallowsTheWholeBudget_IsRefused()
    {
        var clock = new FakeClock();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionEndBudget(5_000, 5_000, clock.Read, startMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionEndBudget(5_000, 6_000, clock.Read, startMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionEndBudget(0, 0, clock.Read, startMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionEndBudget(5_000, -1, clock.Read, startMs: 0));
    }

    /// <summary>The default constructor reads the machine clock, so a budget created with no arguments is
    /// already running: the rule is not "the caller must remember to start it".</summary>
    [Fact]
    public void DefaultConstructor_StartsFromTheMachineClock()
    {
        var budget = new SessionEndBudget();

        Assert.True(budget.ElapsedMs >= 0);
        Assert.True(budget.CanRescueMore);
    }
}
