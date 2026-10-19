using KompasMcp.Contracts;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The draft contract's pure rules: the angle bound, the single base form and the side, which
/// is derived from the base orientation rather than passed through.</summary>
/// <remarks>MEASURED: on the reference bar a 10° draft gives 382367.30 (material removed) or 417632.70
/// mm³ (added), and the same product flag produces the opposite one on a plane and on a face — the case
/// these rules exist to keep out of the adapter.
/// History: docs/decisions/adapter-features.md#draft-route-and-direction</remarks>
public sealed class DraftRulesTests
{
    [Theory]
    [InlineData(10d, true)]
    [InlineData(-10d, true)]
    [InlineData(89.999d, true)]
    [InlineData(0.001d, true)]
    [InlineData(0d, false)]
    [InlineData(90d, false)]
    [InlineData(-90d, false)]
    [InlineData(120d, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Angle_IsInsideTheContractBound(double angle, bool expected) =>
        Assert.Equal(expected, DraftRules.AngleInRange(angle));

    [Theory]
    [InlineData("face", null, true)]
    [InlineData(null, "xy", true)]
    [InlineData(null, null, false)]
    [InlineData("face", "xy", false)]
    public void Base_IsExactlyOneForm(string? face, string? plane, bool expected) =>
        Assert.Equal(expected, DraftRules.SingleBase(face, plane));

    // The four cases the probe measured, as the contract must see them: the caller's word is the same,
    // the product flag flips with the base orientation.
    [Theory]
    [InlineData(DraftDirection.Inward, false, true)]   // XOY plane (normal into the body) -> TRUE
    [InlineData(DraftDirection.Outward, false, false)] // XOY plane -> FALSE
    [InlineData(DraftDirection.Outward, true, true)]   // bottom face (normal out of the body) -> TRUE
    [InlineData(DraftDirection.Inward, true, false)]   // bottom face -> FALSE
    public void Side_IsDerivedFromTheBaseOrientation(string direction, bool baseOut, bool expectedFlag) =>
        Assert.Equal(expectedFlag, DraftRules.ProductDirection(direction, baseOut));

    [Theory]
    [InlineData("Outward")]
    [InlineData("outside")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownSide_IsRefused(string? direction) =>
        Assert.Null(DraftRules.ProductDirection(direction, baseNormalPointsOutOfBody: true));

    [Theory]
    [InlineData(true, true, DraftDirection.Outward)]
    [InlineData(false, true, DraftDirection.Inward)]
    [InlineData(true, false, DraftDirection.Inward)]
    [InlineData(false, false, DraftDirection.Outward)]
    public void ReadBack_NamesTheSideTheModelReports(bool flag, bool baseOut, string expected) =>
        Assert.Equal(expected, DraftRules.ContractDirection(flag, baseOut));
}
