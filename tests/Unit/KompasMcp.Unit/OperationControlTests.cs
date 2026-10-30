using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: the state a client reads is the same state the Host acts on. A queued command and
/// an executing one must not answer alike, and a cancellation must not claim a stop that did not happen
/// (spec 1.8).</summary>
public sealed class OperationControlTests
{
    private static JournalRecord Record(
        JournalOutcome outcome,
        bool needsReconciliation = false,
        long? baseRevision = 17) =>
        new(
            "6b6a1f42-0000-4000-8000-000000000001",
            "kompas_extrude",
            "hash",
            "doc-1",
            baseRevision,
            DateTimeOffset.UnixEpoch,
            outcome,
            null,
            null,
            null,
            needsReconciliation);

    [Theory]
    [InlineData(JournalOutcome.InFlight, false, OperationStatus.Queued)]
    [InlineData(JournalOutcome.InFlight, true, OperationStatus.Running)]
    [InlineData(JournalOutcome.Succeeded, false, OperationStatus.Succeeded)]
    [InlineData(JournalOutcome.Failed, false, OperationStatus.Failed)]
    [InlineData(JournalOutcome.Cancelled, false, OperationStatus.Cancelled)]
    [InlineData(JournalOutcome.OutcomeUnknown, true, OperationStatus.OutcomeUnknown)]
    public void StateOf_FollowsTheJournalAndTheHostPhase(JournalOutcome outcome, bool dispatched, OperationStatus expected) =>
        Assert.Equal(expected, ToolInvoker.StateOf(Record(outcome), dispatched));

    [Fact]
    public void StateOf_TerminalWriteThatDidNotLandIsUnknownNotSucceeded()
    {
        // The record says `succeeded` because the mutation did complete, but the durable line never
        // landed: after a restart the journal demands reconciliation, so the client must not read it
        // as a success.
        Assert.Equal(
            OperationStatus.OutcomeUnknown,
            ToolInvoker.StateOf(Record(JournalOutcome.Succeeded, needsReconciliation: true), dispatched: true));
    }

    [Theory]
    [InlineData(OperationStatus.Queued, "queued")]
    [InlineData(OperationStatus.Running, "running")]
    [InlineData(OperationStatus.Succeeded, "succeeded")]
    [InlineData(OperationStatus.Failed, "failed")]
    [InlineData(OperationStatus.Cancelled, "cancelled")]
    [InlineData(OperationStatus.OutcomeUnknown, "outcome_unknown")]
    public void Wire_NamesEveryPublishedState(OperationStatus status, string expected) =>
        Assert.Equal(expected, ToolInvoker.Wire(status));

    [Fact]
    public void CancelReason_DoesNotCallAnExecutingOperationCancelled()
    {
        // A running operation keeps running: the reason must not read as a confirmed stop.
        var running = ToolInvoker.CancelReason(OperationStatus.Running);
        Assert.Contains("не прерывается", running, StringComparison.Ordinal);
        Assert.DoesNotContain("снята из очереди", running, StringComparison.Ordinal);
        Assert.Contains("не меняется", ToolInvoker.CancelReason(OperationStatus.Succeeded), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(17L, 17L, 0, ToolInvoker.ReconcileVerdict.NotApplied)]
    [InlineData(17L, 18L, 0, ToolInvoker.ReconcileVerdict.Applied)]
    [InlineData(17L, 18L, 1, ToolInvoker.ReconcileVerdict.Undetermined)]
    [InlineData(17L, 16L, 0, ToolInvoker.ReconcileVerdict.Undetermined)]
    [InlineData(17L, 19L, 2, ToolInvoker.ReconcileVerdict.Undetermined)]
    public void DecideReconcile_NamesThreeOutcomes(long baseRevision, long revisionNow, int others, ToolInvoker.ReconcileVerdict expected) =>
        Assert.Equal(expected, ToolInvoker.DecideReconcile(baseRevision, revisionNow, others));

    [Fact]
    public void DecideReconcile_GrownRevisionWithASecondOperationIsNotAttributedToThisOne()
    {
        // MEASURED reasoning: a grown revision says the document changed, not who changed it. Two
        // operations in one window leave no observation that separates them, so the honest answer is
        // "undetermined" rather than a guess.
        Assert.Equal(
            ToolInvoker.ReconcileVerdict.Undetermined,
            ToolInvoker.DecideReconcile(baseRevision: 5, revisionNow: 6, others: 1));
    }
}
