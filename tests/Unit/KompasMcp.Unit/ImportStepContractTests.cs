using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The two pure rules behind <c>kompas_import_step</c>: which ids the answer names and what a
/// failed route is allowed to leave behind.</summary>
/// <remarks>WHY. A client could not address the document an import had just created: the answer carried
/// a raw COM handle ("0x13B95153910") instead of a host document id, and a refused import left
/// registrations the refusal never named. Both are decided here, away from COM, so the rules are held by
/// tests rather than by a live run.
/// History: docs/decisions/adapter-core.md#import-step-contract</remarks>
public class ImportStepContractTests
{
    private static readonly string[] None = Array.Empty<string>();

    /// <summary>The answer names what the registry GAINED — the ids kompas_list_documents issues, which
    /// are the ones every document-addressing tool accepts.</summary>
    [Fact]
    public void CreatedDocumentIds_AreTheRegistryDiff()
    {
        var created = ImportStepContract.CreatedDocumentIds(
            registeredBefore: new[] { "aaaa", "bbbb" },
            registeredAfter: new[] { "aaaa", "bbbb", "cccc" });

        Assert.Equal(new[] { "cccc" }, created);
    }

    /// <summary>Nothing gained is an empty list, never a substituted handle: a failed import must not
    /// name a document it did not create.</summary>
    [Fact]
    public void CreatedDocumentIds_AreEmptyWhenTheRegistryDidNotGrow()
    {
        var created = ImportStepContract.CreatedDocumentIds(
            registeredBefore: new[] { "aaaa", "bbbb" },
            registeredAfter: new[] { "aaaa", "bbbb" });

        Assert.Empty(created);
    }

    /// <summary>The order is deterministic and duplicates collapse, so two runs over the same registry
    /// answer the same list.</summary>
    [Fact]
    public void CreatedDocumentIds_AreOrderedAndDeduplicated()
    {
        var created = ImportStepContract.CreatedDocumentIds(
            registeredBefore: None,
            registeredAfter: new[] { "cccc", "aaaa", "cccc", "bbbb" });

        Assert.Equal(new[] { "aaaa", "bbbb", "cccc" }, created);
    }

    /// <summary>A document a failed route created but could not remove is what forces
    /// <c>partial_effects = true</c>; everything it did remove is not named.</summary>
    [Fact]
    public void DocumentsLeftOpen_AreCreatedMinusRemoved()
    {
        var left = ImportStepContract.DocumentsLeftOpen(
            created: new[] { "aaaa", "bbbb" },
            removed: new[] { "aaaa" });

        Assert.Equal(new[] { "bbbb" }, left);
    }

    /// <summary>Everything removed — the normal case — leaves nothing to name, so the refusal may
    /// honestly answer <c>partial_effects = false</c>.</summary>
    [Fact]
    public void DocumentsLeftOpen_AreEmptyWhenEverythingWasRemoved()
    {
        var left = ImportStepContract.DocumentsLeftOpen(
            created: new[] { "aaaa", "bbbb" },
            removed: new[] { "aaaa", "bbbb" });

        Assert.Empty(left);
    }

    /// <summary>A removal of something that was never created does not invent a leftover, and the order
    /// is deterministic here too.</summary>
    [Fact]
    public void DocumentsLeftOpen_IgnoreForeignRemovals()
    {
        var left = ImportStepContract.DocumentsLeftOpen(
            created: new[] { "cccc", "aaaa" },
            removed: new[] { "zzzz" });

        Assert.Equal(new[] { "aaaa", "cccc" }, left);
    }
}
