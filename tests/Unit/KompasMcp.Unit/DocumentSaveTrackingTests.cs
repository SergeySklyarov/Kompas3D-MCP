using KompasMcp.Contracts;
using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Document saved-ness: state transitions and the close decision table.</summary>
/// <remarks>DOC: the target version has no documented "document modified" sign on <c>ksDocument3D</c>, so the
/// product tracks the state and the only place to check it without KOMPAS is the transitions themselves;
/// behaviour in CAD is proved by the <c>DL</c> acceptance rows on shipped binaries.
/// INVARIANT: a mutation never declares the document saved — only a confirmed write does.
/// History: docs/decisions/tests.md#document-save-2</remarks>
public sealed class DocumentSaveTrackingTests
{
    [Fact]
    public void Mutation_NeverDeclaresTheDocumentSaved()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterMutation());
        Assert.True(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterMutation()));
    }

    [Fact]
    public void RevisionBumpIsNotASave()
    {
        // INVARIANT: a revision bump does not declare the document saved — after a save it is clean, but
        // the next mutation makes it dirty again.
        var afterSave = DocumentSaveTracking.AfterConfirmedSave();
        Assert.False(DocumentSaveTracking.IsDirty(afterSave));
        Assert.True(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterMutation()));
    }

    [Fact]
    public void CreateAndExternalChangeAreDirty()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterCreate());
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterExternalChange());
    }

    [Fact]
    public void OpenIsClean()
    {
        // Opening reads the file from disk: model and file agree.
        Assert.False(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterOpen()));
    }

    [Fact]
    public void FailedSaveDoesNotClearTheState()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Dirty));
        Assert.Equal(DocumentSaveState.Unknown, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Unknown));
        // INVARIANT: "clean" does not stay clean after a failure — the save was not confirmed and the model
        // has already diverged from the file (otherwise there would be nothing to save).
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Clean));
    }

    [Fact]
    public void UnreadableStateIsNotReportedAsClean()
    {
        var unknown = DocumentSaveTracking.AfterUnreadableObservation();
        Assert.Equal(DocumentSaveState.Unknown, unknown);
        Assert.True(DocumentSaveTracking.IsDirty(unknown));
    }

    [Theory]
    // refuse: closes only a confirmed-clean document
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Refuse, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Refuse, CloseAction.Refuse)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Refuse, CloseAction.Refuse)]
    // save: saves before closing everything not confirmed clean
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Save, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Save, CloseAction.SaveThenClose)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Save, CloseAction.SaveThenClose)]
    // discard: discarding changes closes in any state, including unknown
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Discard, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Discard, CloseAction.Close)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Discard, CloseAction.Close)]
    public void CloseDecisionTable(DocumentSaveState state, DirtyPolicy policy, CloseAction expected)
    {
        Assert.Equal(expected, DocumentSaveTracking.Decide(state, policy));
    }

    [Fact]
    public void UnreadableFingerprintIsNotAValue()
    {
        // INVARIANT: a distinct string, not a number — zero bodies of an empty document and "the collection
        // did not answer" are different facts, and the first must not read as the second.
        Assert.False(string.IsNullOrWhiteSpace(DocumentSaveTracking.UnreadableFingerprint));
        Assert.DoesNotContain(":", DocumentSaveTracking.UnreadableFingerprint);
    }
}
