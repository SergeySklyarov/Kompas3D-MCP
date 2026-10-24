using KompasMcp.Domain.SheetMetal;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The unfold request rule and the extent comparison that confirms it, without KOMPAS.</summary>
/// <remarks>TEST: the refusal decided before the first write (the unfold cannot be deleted by this
/// product) and the geometry rule that decides success instead of <c>UpdateParam()</c>, which answers FALSE
/// in every state. INVARIANT: an unread extent confirms nothing.
/// History: docs/decisions/sheet-metal.md#unfold-tools</remarks>
public class SheetMetalUnfoldPolicyTests
{
    [Theory]
    // The first unfold needs a face to hold still; a repeat and a fold-back do not.
    [InlineData(true, false, null, SheetMetalUnfoldPolicy.Refusal.MissingFixedFace)]
    [InlineData(true, false, "face-ref", SheetMetalUnfoldPolicy.Refusal.None)]
    [InlineData(true, true, null, SheetMetalUnfoldPolicy.Refusal.None)]
    [InlineData(false, false, null, SheetMetalUnfoldPolicy.Refusal.None)]
    // An unread IsCreated is not "false": the request is not refused on a guess.
    [InlineData(true, null, null, SheetMetalUnfoldPolicy.Refusal.None)]
    public void CheckSet_NeedsAFaceOnlyForTheFirstUnfold(
        bool unfold, bool? isCreated, string? fixedFaceRef, SheetMetalUnfoldPolicy.Refusal expected) =>
        Assert.Equal(expected, SheetMetalUnfoldPolicy.CheckSet(unfold, isCreated, fixedFaceRef));

    [Theory]
    // Unfolded: the extent along the normal IS the thickness.
    [InlineData(true, 2.5d, 2.5d, true)]
    [InlineData(true, 2.5000001d, 2.5d, true)]
    [InlineData(true, 12.5d, 2.5d, false)]
    // Folded back: the body no longer lies flat, so the extent is LARGER than the thickness.
    [InlineData(false, 12.5d, 2.5d, true)]
    [InlineData(false, 2.5d, 2.5d, false)]
    // Unread is never a confirmation, in either direction.
    [InlineData(true, null, 2.5d, false)]
    [InlineData(false, null, 2.5d, false)]
    public void ExtentConfirms_FollowsTheState(bool unfold, double? extentMm, double thicknessMm, bool expected) =>
        Assert.Equal(expected, SheetMetalUnfoldPolicy.ExtentConfirms(unfold, extentMm, thicknessMm));
}
