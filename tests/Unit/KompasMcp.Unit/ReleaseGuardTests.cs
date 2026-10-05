using KompasMcp.Contracts;
using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The "may the session be released" decision — as a table, without KOMPAS and without a Worker.</summary>
/// <remarks>TEST: exactly the pure function is checked. INVARIANT (defect H3): each table row is a state in which
/// the decision must be made BEFORE contacting the Worker. INVARIANT: "Worker restarted after a break, inventory
/// empty → refusal" — an empty inventory of a new Worker is not "no edits" but "documents lost", and the
/// <c>DocumentStateUnknown</c> sign overrides it.
/// History: docs/decisions/tests.md#release-guard-2</remarks>
public class ReleaseGuardTests
{
    /// <summary>Default state: Worker ran, channel live, inventory read, no edits.</summary>
    private static ReleaseFacts Facts(
        bool workerStarted = true,
        bool canSendWithoutRestart = true,
        bool documentStateUnknown = false,
        bool acknowledge = false,
        bool inventoryRead = true,
        int dirtyCount = 0) =>
        new(workerStarted, canSendWithoutRestart, documentStateUnknown, acknowledge, inventoryRead, dirtyCount);

    [Fact]
    public void CleanSession_IsReleased()
    {
        var decision = ReleaseGuard.Decide(Facts());

        Assert.True(decision.Proceed);
        Assert.Null(decision.RefusalCode);
    }

    [Fact]
    public void WorkerNeverStarted_IsReleasedWithoutInventory()
    {
        // There was no COM session at all: no documents, and no one to ask for an inventory.
        var decision = ReleaseGuard.Decide(Facts(workerStarted: false, inventoryRead: false));

        Assert.True(decision.Proceed);
    }

    /// <summary>MANDATORY TABLE ROW (H3).</summary>
    [Fact]
    public void WorkerRestartedAfterBreakWithEmptyInventory_IsRefused()
    {
        // An intermediate CAD call raised a new Worker: the channel is live, the inventory was read and is EMPTY
        // — all the "old" checks are happy. But the previous Worker's documents are lost, and this state must
        // override the empty inventory.
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: true,
            documentStateUnknown: true,
            acknowledge: false,
            inventoryRead: true,
            dirtyCount: 0));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentStateUnknown, decision.RefusalCode);
        Assert.Contains("НЕИЗВЕСТНО", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownDocumentState_WithExplicitAcknowledgement_Proceeds()
    {
        var decision = ReleaseGuard.Decide(Facts(documentStateUnknown: true, acknowledge: true));

        Assert.True(decision.Proceed);
    }

    [Fact]
    public void UnknownDocumentState_StillRefusesOnDirtyDocumentsEvenWithAcknowledgement()
    {
        // INVARIANT: acknowledging an unknown state does NOT cancel the refusal for known unsaved edits —
        // "I don't know" and "I know there are edits" are different states, and the second is cured by saving.
        var decision = ReleaseGuard.Decide(Facts(
            documentStateUnknown: true, acknowledge: true, dirtyCount: 2));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentDirty, decision.RefusalCode);
    }

    [Fact]
    public void BrokenChannel_IsRefusedBecauseInventoryCannotBeRead()
    {
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: false, inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
        Assert.Contains("НЕИЗВЕСТНО", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>MANDATORY ROW (§4): an acknowledged unknown state removes the refusal for a BROKEN channel.</summary>
    [Fact]
    public void UnknownDocumentState_Acknowledged_SkipsTheBrokenChannelRefusal()
    {
        // Right after a channel break a client that has already accepted the unknown state must be able to
        // release. The old code cleared only step 1 while step 3 ("channel broken") still refused; with
        // acknowledgement the inventory is not needed.
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false,
            documentStateUnknown: true,
            acknowledge: true,
            inventoryRead: false));

        Assert.True(decision.Proceed);
        Assert.Null(decision.RefusalCode);
    }

    /// <summary>MANDATORY ROW (§4): acknowledgement also removes the "inventory not read" refusal.</summary>
    [Fact]
    public void UnknownDocumentState_Acknowledged_SkipsTheUnreadInventoryRefusal()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: true,
            documentStateUnknown: true,
            acknowledge: true,
            inventoryRead: false));

        Assert.True(decision.Proceed);
    }

    /// <summary>Negative control: acknowledgement does NOT remove the channel refusal when the unknown state is
    /// not flagged — "acknowledge" must not be a universal skeleton key for any inventory check.</summary>
    [Fact]
    public void BrokenChannel_WithoutUnknownStateFlag_StillRefusesEvenWithAcknowledgement()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false,
            documentStateUnknown: false,
            acknowledge: true,
            inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
    }

    [Fact]
    public void BrokenChannelRefusal_NamesTheAcknowledgementAsTheWayOut()
    {
        // INVARIANT: the refusal must NAME the way out, not leave the client in a dead end.
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: false, inventoryRead: false));

        Assert.Contains("acknowledge_unknown_document_state", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveChannelButUnreadInventory_IsRefused()
    {
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: true, inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
    }

    [Fact]
    public void DirtyDocuments_AreRefused()
    {
        var decision = ReleaseGuard.Decide(Facts(dirtyCount: 1));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentDirty, decision.RefusalCode);
        Assert.Contains("1", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>Negative control: an unknown state without acknowledgement refuses EARLIER than the broken
    /// channel is checked — otherwise the answer would name "channel broken" where the cause is lost documents.</summary>
    [Fact]
    public void UnknownDocumentState_IsCheckedBeforeTheChannel()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false, documentStateUnknown: true, acknowledge: false));

        Assert.Equal(ErrorCodes.DocumentStateUnknown, decision.RefusalCode);
    }
}
