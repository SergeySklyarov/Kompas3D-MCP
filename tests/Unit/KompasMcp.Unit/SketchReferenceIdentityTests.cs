using KompasMcp.Contracts;
using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>One live reference per object: the rule that makes an enumeration hand back the handle it
/// already issued instead of minting a second address for the same model object.</summary>
/// <remarks>INVARIANT: "live" is decided by the reference's OWN revision, so the search must not read a
/// stale copy of it. MEASURED: a first draft searched the insertion-order map, which
/// <c>RevisionForward</c> deliberately does not re-stamp, and therefore answered "no live reference"
/// for a sketch whose reference was live — the sketch then got a second address and the profile the
/// server had just drawn looked unrecorded.
/// History: docs/decisions/tests.md#sketch-reference-identity</remarks>
public class SketchReferenceIdentityTests
{
    private const string Document = "0123456789abcdef0123456789abcdef";
    private const string Identity = "000001eac71938d0";

    private static ReferenceRegistry RegistryWithSketch(out string id)
    {
        var registry = new ReferenceRegistry();
        id = registry.Register("sketch", Document, 1, payload: new object(), identity: Identity).Id;
        return registry;
    }

    /// <summary>INVARIANT: the reference minted for an object is the one handed back for it.</summary>
    [Fact]
    public void SameObject_SameRevision_ReturnsTheReferenceAlreadyIssued()
    {
        var registry = RegistryWithSketch(out var id);

        var found = registry.FindLiveByIdentity("sketch", Document, 1, Identity);

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    /// <summary>INVARIANT (the measured defect): an ORDINARY revision move re-stamps the reference in
    /// place, and the search must still find it. A search reading the un-restamped order map did not.</summary>
    [Fact]
    public void AfterOrdinaryRevisionForward_TheReferenceIsStillFound()
    {
        var registry = RegistryWithSketch(out var id);
        registry.RevisionForward(Document, 2, invalidateAll: false);

        var found = registry.FindLiveByIdentity("sketch", Document, 2, Identity);

        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    /// <summary>INVARIANT: after a rebuild or reopen nothing from the previous revision survives, so a
    /// new reference is minted and the old one is refused as stale — the invalidation is not weakened.</summary>
    [Fact]
    public void AfterInvalidateAll_NoLiveReference_AndTheOldIdIsStale()
    {
        var registry = RegistryWithSketch(out var id);
        registry.RevisionForward(Document, 2, invalidateAll: true);

        Assert.Null(registry.FindLiveByIdentity("sketch", Document, 2, Identity));
        var again = registry.Register("sketch", Document, 2, payload: new object(), identity: Identity);
        Assert.NotEqual(id, again.Id);

        var stale = Assert.Throws<KompasContractException>(
            () => registry.Require(id, Document, 2));
        Assert.Equal(ErrorCodes.StaleReference, stale.Code);
    }

    /// <summary>INVARIANT: a reference from an earlier revision is not "live" — handing it back would
    /// return an address that is refused the moment it is used.</summary>
    [Fact]
    public void ReferenceFromAnEarlierRevision_IsNotLive()
    {
        var registry = RegistryWithSketch(out _);

        Assert.Null(registry.FindLiveByIdentity("sketch", Document, 2, Identity));
    }

    /// <summary>INVARIANT: an unknown identity is not a match — a second address is minted rather than
    /// the first one reused.</summary>
    [Fact]
    public void UnknownIdentity_IsNotMatched()
    {
        var registry = RegistryWithSketch(out _);

        Assert.Null(registry.FindLiveByIdentity("sketch", Document, 1, "0000000000000001"));
    }

    /// <summary>INVARIANT: two objects whose identity cannot be stated must never collapse into one
    /// address; the search never matches on a null identity.</summary>
    [Fact]
    public void NoIdentity_NeverMatches_AndNeverMerges()
    {
        var registry = new ReferenceRegistry();
        var first = registry.Register("sketch", Document, 1, payload: new object()).Id;
        var second = registry.Register("sketch", Document, 1, payload: new object()).Id;

        Assert.NotEqual(first, second);
        Assert.Null(registry.FindLiveByIdentity("sketch", Document, 1, "0000000000000000"));
    }
}
