using KompasMcp.Contracts;
using KompasMcp.Domain.SheetMetal;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The sheet-metal body input rule and read-back comparison as a table, without KOMPAS.</summary>
/// <remarks>TEST: the pure rule the adapter refuses a sheet-metal input by, before <c>Add()</c> puts a body
/// in the model, and the comparison that decides success. INVARIANT: thickness and radius are positive,
/// the unfold method is one of three and carries exactly one matching field, and "not read" is never
/// "equal". The negative controls are the refusals SMB.04 requires.
/// History: docs/decisions/sheet-metal.md#body-tools</remarks>
public class SheetMetalBodyPolicyTests
{
    [Theory]
    [InlineData("coefficient", 0)]
    [InlineData("bend_value", 1)]
    [InlineData("bend_reduction", 2)]
    // The measured refusal: the kernel does not take the type without a bend table.
    [InlineData("bend_table", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void UnfoldType_IsOneOfThree(string? name, int? value) =>
        Assert.Equal(value, SheetMetalBodyPolicy.UnfoldTypeValue(name));

    [Theory]
    [InlineData("normal", 0)]
    [InlineData("reverse", 1)]
    [InlineData("both", 2)]
    [InlineData("middle_plane", 3)]
    [InlineData("outward", null)]
    public void Direction_FollowsTheEnumPage(string? name, int? value) =>
        Assert.Equal(value, SheetMetalBodyPolicy.DirectionValue(name));

    [Fact]
    public void UnfoldTypeName_ReportsAnUnknownNumberAsTheNumber() =>
        Assert.Equal("3", SheetMetalBodyPolicy.UnfoldTypeName(3));

    [Theory]
    [InlineData(1.5d, 2d, "normal", "coefficient", 0.4d, null, null, SheetMetalBodyPolicy.Refusal.None)]
    // The three measured kernel silences: it accepts these and keeps a body.
    [InlineData(0d, 2d, "normal", "coefficient", 0.4d, null, null,
        SheetMetalBodyPolicy.Refusal.ThicknessNotPositive)]
    [InlineData(-1.5d, 2d, "normal", "coefficient", 0.4d, null, null,
        SheetMetalBodyPolicy.Refusal.ThicknessNotPositive)]
    [InlineData(1.5d, 0d, "normal", "coefficient", 0.4d, null, null,
        SheetMetalBodyPolicy.Refusal.RadiusNotPositive)]
    // The method must carry its own field, and only one.
    [InlineData(1.5d, 2d, "normal", "coefficient", null, null, null,
        SheetMetalBodyPolicy.Refusal.MissingUnfoldValue)]
    [InlineData(1.5d, 2d, "normal", "coefficient", 0.4d, 1.2d, null,
        SheetMetalBodyPolicy.Refusal.TwoUnfoldValues)]
    [InlineData(1.5d, 2d, "normal", "coefficient", null, 1.2d, null,
        SheetMetalBodyPolicy.Refusal.FieldOfAnotherUnfoldType)]
    [InlineData(1.5d, 2d, "normal", "bend_table", null, null, 0.3d,
        SheetMetalBodyPolicy.Refusal.UnknownUnfoldType)]
    [InlineData(1.5d, 2d, "sideways", "coefficient", 0.4d, null, null,
        SheetMetalBodyPolicy.Refusal.UnknownDirection)]
    [InlineData(double.NaN, 2d, "normal", "coefficient", 0.4d, null, null,
        SheetMetalBodyPolicy.Refusal.ThicknessNotPositive)]
    public void Create_InputRule(
        double thickness, double radius, string? direction, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction,
        SheetMetalBodyPolicy.Refusal expected) =>
        Assert.Equal(expected, SheetMetalBodyPolicy.CheckCreate(
            thickness, radius, direction, unfoldType, coefficient, bendValue, reduction));

    [Fact]
    public void Create_NonFiniteFieldValue_IsRefused() =>
        Assert.Equal(SheetMetalBodyPolicy.Refusal.ValueNotFinite,
            SheetMetalBodyPolicy.CheckCreate(1.5d, 2d, "normal", "coefficient",
                double.PositiveInfinity, null, null));

    [Theory]
    // Nothing to change.
    [InlineData(null, null, null, null, null, null, SheetMetalBodyPolicy.Refusal.NoEditFields)]
    // Values the kernel would accept and record.
    [InlineData(0d, null, null, null, null, null, SheetMetalBodyPolicy.Refusal.ThicknessNotPositive)]
    [InlineData(null, -1d, null, null, null, null, SheetMetalBodyPolicy.Refusal.RadiusNotPositive)]
    [InlineData(null, null, "bend_table", null, null, null, SheetMetalBodyPolicy.Refusal.UnknownUnfoldType)]
    // A field of another method than the one the MODEL holds: refused by name.
    [InlineData(null, null, null, null, 1.2d, null, SheetMetalBodyPolicy.Refusal.FieldOfAnotherUnfoldType)]
    // Switching the method and sending that method's field is the accepted case.
    [InlineData(null, null, "bend_value", null, 1.2d, null, SheetMetalBodyPolicy.Refusal.None)]
    // The current method's own field, without naming the method.
    [InlineData(null, null, null, 0.5d, null, null, SheetMetalBodyPolicy.Refusal.None)]
    [InlineData(2d, null, null, null, null, null, SheetMetalBodyPolicy.Refusal.None)]
    public void Edit_InputRule(
        double? thickness, double? radius, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction,
        SheetMetalBodyPolicy.Refusal expected) =>
        Assert.Equal(expected, SheetMetalBodyPolicy.CheckEdit(
            "coefficient", thickness, radius, unfoldType, coefficient, bendValue, reduction));

    [Fact]
    public void Create_ReadBack_MatchesWithinTolerance()
    {
        var read = new SheetMetalBodyDto
        {
            ThicknessMm = 1.5d,
            BendRadiusMm = 2d,
            UnfoldType = "coefficient",
            UnfoldValue = 0.4d,
        };

        Assert.Empty(SheetMetalBodyPolicy.CreateMismatches(read, 1.5d, 2d, "coefficient", 0.4d, null, null));
    }

    [Fact]
    public void Create_ReadBack_Mismatch_IsNamedPerField()
    {
        var read = new SheetMetalBodyDto
        {
            ThicknessMm = 1.5d,
            BendRadiusMm = 3d,
            // The kernel answered bend_value although coefficient was asked: the measured table refusal.
            UnfoldType = "bend_value",
            UnfoldValue = 1.2d,
        };

        var mismatched = SheetMetalBodyPolicy.CreateMismatches(read, 1.5d, 2d, "coefficient", 0.4d, null, null);
        Assert.Contains("bend_radius_mm", mismatched);
        Assert.Contains("unfold_type", mismatched);
        Assert.Contains("unfold_value", mismatched);
        Assert.DoesNotContain("thickness_mm", mismatched);
    }

    [Fact]
    public void UnreadBody_IsNeverAConfirmation()
    {
        Assert.NotEmpty(SheetMetalBodyPolicy.CreateMismatches(null, 1.5d, 2d, "coefficient", 0.4d, null, null));
        Assert.NotEmpty(SheetMetalBodyPolicy.EditMismatches(null, 2d, null, null, null, null, null));
    }

    [Fact]
    public void Edit_RepeatOfTheSameValues_IsIdempotent()
    {
        var before = new SheetMetalBodyDto
        {
            ThicknessMm = 2d,
            BendRadiusMm = 3d,
            UnfoldType = "bend_value",
            UnfoldValue = 1.2d,
        };

        Assert.True(SheetMetalBodyPolicy.IsRepeat(before, 2d, 3d, "bend_value", null, 1.2d, null));
        Assert.False(SheetMetalBodyPolicy.IsRepeat(before, 2.5d, 3d, "bend_value", null, 1.2d, null));
    }
}
