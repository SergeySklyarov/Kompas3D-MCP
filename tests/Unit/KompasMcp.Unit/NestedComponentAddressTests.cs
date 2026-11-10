using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The address of a component inside a subassembly: the path a reference id carries, and the pure
/// rule that decides whether the resolved instance may be written.</summary>
/// <remarks>TEST: exactly what is checkable without KOMPAS — the id format stayed ADDITIVE (a top-level id
/// parses as "no path"), a broken tail is not silently read as "top-level", and the refusal decisions name
/// their code. The route itself ("the write lands and reads back") is NOT claimed here and is not
/// substituted: it needs a run on the target version.
/// History: docs/decisions/assembly-ext.md#nested-implemented, docs/decisions/tests.md</remarks>
public class NestedComponentAddressTests
{
    private const string TopLevelId = "component:0123456789abcdef0123456789abcdef";

    [Fact]
    public void TopLevelReferenceId_CarriesNoPath()
    {
        // INVARIANT: the format grew ADDITIVELY — every reference issued before the path existed keeps its
        // form and must still parse as "a top-level component".
        Assert.False(ComponentPath.TryParse(TopLevelId, out var path));
        Assert.Empty(path);
    }

    [Theory]
    [InlineData("1", new[] { 1 })]
    [InlineData("0/0", new[] { 0, 0 })]
    [InlineData("2/1/0", new[] { 2, 1, 0 })]
    public void NestedReferenceId_CarriesItsPathInOrder(string tail, int[] expected)
    {
        var id = TopLevelId + ComponentPath.Separator + tail;

        Assert.True(ComponentPath.TryParse(id, out var path));
        Assert.Equal(expected, path);
        Assert.Equal(tail, ComponentPath.Format(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("component:")]
    [InlineData("component:0123456789abcdef0123456789abcdef/")]
    [InlineData("component:0123456789abcdef0123456789abcdef//0")]
    [InlineData("component:0123456789abcdef0123456789abcdef/1/")]
    [InlineData("component:0123456789abcdef0123456789abcdef/-1")]
    [InlineData("component:0123456789abcdef0123456789abcdef/1/x")]
    [InlineData("no-colon/1")]
    public void BrokenOrAbsentTail_IsNotAPath(string id)
    {
        // INVARIANT: "top-level" and "a broken address" are different claims — a tail that does not parse is
        // answered false, and no reference is minted with one.
        Assert.False(ComponentPath.TryParse(id, out var path));
        Assert.Empty(path);
    }

    [Fact]
    public void SameInstance_IsAddressableAndConfirmed()
    {
        var verdict = NestedComponentAddress.Decide(1073741869, 1073741869, "ksLCompletely");

        Assert.Equal(NestedAddressVerdictKind.Addressable, verdict.Kind);
        Assert.Contains("IPart7.Reference=1073741869", verdict.Detail, StringComparison.Ordinal);
        Assert.Null(verdict.Unconfirmed);
    }

    [Fact]
    public void AnotherInstance_IsRefusedAsStale()
    {
        // INVARIANT: an index addresses a POSITION; a different number is a different instance, and a write
        // would land on a neighbour.
        var verdict = NestedComponentAddress.Decide(1073741869, 1073741870, "ksLCompletely");

        Assert.Equal(NestedAddressVerdictKind.StaleReference, verdict.Kind);
        Assert.Contains("1073741869", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("1073741870", verdict.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ksLUnload")]
    [InlineData("ksLTriangles")]
    [InlineData("ksLPartially")]
    [InlineData("ksLGabarit")]
    [InlineData("ksLUnknown")]
    public void NotFullyLoaded_IsRefusedAsCapability(string loadState)
    {
        // DOC: ksloadstateenum.html — only ksLCompletely allows editing.
        var verdict = NestedComponentAddress.Decide(7, 7, loadState);

        Assert.Equal(NestedAddressVerdictKind.CapabilityUnavailable, verdict.Kind);
        Assert.Contains(loadState, verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadReferenceAndLoadState_AreNamedNotGuessed()
    {
        // INVARIANT: "not read" is not "different" — the address still resolves, and the gaps are NAMED so a
        // client is not told more was confirmed than it was.
        var verdict = NestedComponentAddress.Decide(7, null, null);

        Assert.Equal(NestedAddressVerdictKind.Addressable, verdict.Kind);
        Assert.NotNull(verdict.Unconfirmed);
        Assert.Contains("unread", verdict.Unconfirmed, StringComparison.Ordinal);
    }
}
