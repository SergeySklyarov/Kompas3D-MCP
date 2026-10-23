using KompasMcp.Contracts;
using KompasMcp.Domain.SheetMetal;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The bend and hole input rules and their read-back comparison as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure rules the adapter refuses a bend or a hole by, before <c>Add()</c> leaves a
/// record this product cannot delete, and the comparison that decides success. INVARIANT: an unread field
/// is a mismatch, never a confirmation.
/// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
public class SheetMetalFeaturePolicyTests
{
    [Theory]
    [InlineData("continue", 0)]
    [InlineData("contour", 1)]
    [InlineData("touch", 2)]
    [InlineData("contour_internal", 3)]
    [InlineData("tangent_internal", 4)]
    [InlineData("middle", null)]
    [InlineData(null, null)]
    public void LengthType_FollowsTheEnumPage(string? name, int? value) =>
        Assert.Equal(value, SheetMetalFeaturePolicy.LengthTypeValue(name));

    [Theory]
    [InlineData("by_width", 0)]
    [InlineData("by_depth", 1)]
    // The page names it, the route needs DepthObject: not a value of this contract.
    [InlineData("up_to_surface", null)]
    public void CutType_IsOneOfTwo(string? name, int? value) =>
        Assert.Equal(value, SheetMetalFeaturePolicy.CutTypeValue(name));

    [Theory]
    [InlineData(90d, 2d, "contour", 5d, SheetMetalFeaturePolicy.Refusal.None)]
    [InlineData(90d, null, null, null, SheetMetalFeaturePolicy.Refusal.None)]
    [InlineData(0d, 2d, "contour", 5d, SheetMetalFeaturePolicy.Refusal.AngleNotPositive)]
    [InlineData(-45d, 2d, "contour", 5d, SheetMetalFeaturePolicy.Refusal.AngleNotPositive)]
    [InlineData(double.NaN, 2d, "contour", 5d, SheetMetalFeaturePolicy.Refusal.ValueNotFinite)]
    [InlineData(90d, 0d, "contour", 5d, SheetMetalFeaturePolicy.Refusal.RadiusNotPositive)]
    [InlineData(90d, 2d, "length", 5d, SheetMetalFeaturePolicy.Refusal.UnknownLengthType)]
    [InlineData(90d, 2d, "contour", 0d, SheetMetalFeaturePolicy.Refusal.LengthNotPositive)]
    public void Bend_Rule(
        double angle, double? radius, string? lengthType, double? length,
        SheetMetalFeaturePolicy.Refusal expected) =>
        Assert.Equal(expected, SheetMetalFeaturePolicy.CheckBend(angle, radius, lengthType, length));

    [Theory]
    [InlineData(6d, "by_width", null, SheetMetalFeaturePolicy.Refusal.None)]
    [InlineData(6d, "by_depth", 1d, SheetMetalFeaturePolicy.Refusal.None)]
    [InlineData(6d, null, null, SheetMetalFeaturePolicy.Refusal.None)]
    [InlineData(0d, "by_width", null, SheetMetalFeaturePolicy.Refusal.DiameterNotPositive)]
    [InlineData(-6d, "by_width", null, SheetMetalFeaturePolicy.Refusal.DiameterNotPositive)]
    [InlineData(6d, "up_to_surface", null, SheetMetalFeaturePolicy.Refusal.UnknownCutType)]
    // The depth belongs to the «на глубину» type: the kernel would store a number it never applies.
    [InlineData(6d, "by_width", 1d, SheetMetalFeaturePolicy.Refusal.DepthWithoutItsCutType)]
    [InlineData(6d, "by_depth", null, SheetMetalFeaturePolicy.Refusal.MissingDepth)]
    [InlineData(6d, "by_depth", 0d, SheetMetalFeaturePolicy.Refusal.DepthNotPositive)]
    public void Hole_Rule(
        double diameter, string? cutType, double? depth, SheetMetalFeaturePolicy.Refusal expected) =>
        Assert.Equal(expected, SheetMetalFeaturePolicy.CheckHole(diameter, cutType, depth));

    [Theory]
    [InlineData(SheetMetalFeaturePolicy.Refusal.AngleNotPositive, "angle_deg")]
    [InlineData(SheetMetalFeaturePolicy.Refusal.UnknownLengthType, "length_type")]
    [InlineData(SheetMetalFeaturePolicy.Refusal.DiameterNotPositive, "diameter_mm")]
    [InlineData(SheetMetalFeaturePolicy.Refusal.MissingDepth, "depth_mm")]
    public void Refusal_NamesItsField(SheetMetalFeaturePolicy.Refusal refusal, string field) =>
        Assert.Equal(field, SheetMetalFeaturePolicy.RefusalField(refusal));

    [Fact]
    public void Bend_ReadBack_ConfirmsTheRequestedFields()
    {
        var read = new SheetMetalBendDto
        {
            AngleDeg = 90d,
            Direction = true,
            InternalRadius = true,
            RadiusMm = 2d,
            LengthType = "contour",
            LengthMm = 5d,
            EdgeIsRequested = true,
        };

        Assert.Empty(SheetMetalFeaturePolicy.BendMismatches(read, 90d, true, true, 2d, "contour", 5d));
    }

    [Fact]
    public void Bend_ReadBack_Mismatch_IsNamedPerField()
    {
        var read = new SheetMetalBendDto
        {
            AngleDeg = 45d,
            InternalRadius = false,
            LengthType = "touch",
            LengthMm = 5d,
        };

        var mismatched = SheetMetalFeaturePolicy.BendMismatches(read, 90d, true, true, 2d, "contour", 5d);
        Assert.Contains("angle_deg", mismatched);
        Assert.Contains("direction", mismatched);
        Assert.Contains("internal_radius", mismatched);
        Assert.Contains("radius_mm", mismatched);
        Assert.Contains("length_type", mismatched);
        Assert.DoesNotContain("length_mm", mismatched);
    }

    [Fact]
    public void Hole_ReadBack_Mismatch_IsNamedPerField()
    {
        var read = new SheetMetalHoleDto { XMm = 5d, YMm = 5d, DiameterMm = 6d, CutType = "by_depth", DepthMm = 1d };

        Assert.Empty(SheetMetalFeaturePolicy.HoleMismatches(read, 5d, 5d, 6d, "by_depth", 1d));
        var mismatched = SheetMetalFeaturePolicy.HoleMismatches(read, 5d, 4d, 8d, "by_depth", 1d);
        Assert.Contains("y_mm", mismatched);
        Assert.Contains("diameter_mm", mismatched);
        Assert.DoesNotContain("x_mm", mismatched);
    }

    [Fact]
    public void UnreadFeature_IsNeverAConfirmation()
    {
        Assert.NotEmpty(SheetMetalFeaturePolicy.BendMismatches(null, 90d, null, null, null, null, null));
        Assert.NotEmpty(SheetMetalFeaturePolicy.HoleMismatches(null, 5d, 5d, 6d, null, null));
    }
}
