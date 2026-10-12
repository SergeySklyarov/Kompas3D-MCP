using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Which component left the assembly — as a table, without KOMPAS.</summary>
/// <remarks>TEST: exactly the pure function is checked. INVARIANT: the removed entry is the MULTISET
/// difference before minus after, so a change of an ordinal cannot be mistaken for a change of a component;
/// the addressed identity occurring twice is NAMED as ambiguous rather than silently resolved.
/// History: docs/decisions/assembly.md#delete-component</remarks>
public class ComponentRemovalTests
{
    private const string A0 = "a.m3d|x=0";
    private const string A1 = "a.m3d|x=200";
    private const string B2 = "b.m3d|x=400";

    [Fact]
    public void MiddleOfThreeIdenticalFiles_AddressedByPlacement_IsTheOneRemoved()
    {
        // THE DISCRIMINATING CASE: three components, two of them the SAME FILE, distinguished only by
        // placement. Deleting the middle one must leave the two END entries — a comparison by ordinal or by
        // file name would report a different set.
        var verdict = ComponentRemoval.Decide([A0, A1, B2], [A0, B2], A1);

        Assert.True(verdict.CountFell);
        Assert.True(verdict.AddressedRemoved);
        Assert.False(verdict.Ambiguous);
        Assert.Equal([A1], verdict.Left);
    }

    [Fact]
    public void AddressedEntryStillPresent_IsNotRemoved()
    {
        // NEGATIVE CONTROL: the count fell, the ADDRESSED component is still there, and a NEIGHBOUR left
        // instead. This must NOT read as success.
        var verdict = ComponentRemoval.Decide([A0, A1, B2], [A0, A1], A0);

        Assert.True(verdict.CountFell);
        Assert.False(verdict.AddressedRemoved);
        Assert.Equal([B2], verdict.Left);
    }

    [Fact]
    public void CountUnchanged_IsNotRemoval()
    {
        var verdict = ComponentRemoval.Decide([A0, A1, B2], [A0, A1, B2], A1);

        Assert.False(verdict.CountFell);
        Assert.False(verdict.AddressedRemoved);
        Assert.Empty(verdict.Left);
    }

    [Fact]
    public void CountFellByMoreThanOne_IsNotAttributedToTheAddressedOne()
    {
        // A cascade took a neighbour too: the addressed one did leave, but the composition is not "exactly
        // one fewer", so it is not confirmed as the addressed removal.
        var verdict = ComponentRemoval.Decide([A0, A1, B2], [B2], A1);

        Assert.False(verdict.CountFell);
        Assert.False(verdict.AddressedRemoved);
        Assert.Equal(2, verdict.Left.Count);
    }

    [Fact]
    public void SurvivorMoved_IsNotRemovalOfTheAddressedOne()
    {
        // The addressed entry is still there and a SURVIVOR changed placement: "b.m3d|x=400" is gone and
        // "b.m3d|x=500" appeared, so the multiset difference is not the addressed key.
        var verdict = ComponentRemoval.Decide([A0, B2], ["b.m3d|x=500"], A0);

        Assert.True(verdict.CountFell);
        Assert.False(verdict.AddressedRemoved);
    }

    [Fact]
    public void DuplicateIdentity_IsNamedAmbiguous()
    {
        // TWO insertions of one file at ONE placement are indistinguishable: the addressed one cannot be told
        // apart from its twin, and that limit is NAMED rather than hidden behind a passing verdict.
        var verdict = ComponentRemoval.Decide([A0, A0, B2], [A0, B2], A0);

        Assert.True(verdict.CountFell);
        Assert.True(verdict.AddressedRemoved);
        Assert.True(verdict.Ambiguous);
    }

    [Fact]
    public void UnknownAddressedKey_IsNotConfirmed()
    {
        // The addressed ordinal was outside the structure: nothing can be confirmed, and "the count fell"
        // alone is not the claim.
        var verdict = ComponentRemoval.Decide([A0, B2], [B2], null);

        Assert.True(verdict.CountFell);
        Assert.False(verdict.AddressedRemoved);
    }
}
