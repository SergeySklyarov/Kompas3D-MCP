using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The pure part of <c>kompas_create_sketch_dimension</c>: which fields a kind takes, what its
/// value means and which sketch object it measures.</summary>
/// <remarks>INVARIANT: a call that contradicts its own <c>kind</c> is refused before COM, and an omitted
/// <c>kind</c> means the behaviour the tool had before the field existed.
/// History: docs/decisions/contracts.md#sketch-dimension</remarks>
public class SketchDimensionRuleTests
{
    [Theory]
    [InlineData(null, SketchDimensionKind.Linear)]
    [InlineData("", SketchDimensionKind.Linear)]
    [InlineData("   ", SketchDimensionKind.Linear)]
    [InlineData("linear", SketchDimensionKind.Linear)]
    [InlineData("diametral", SketchDimensionKind.Diametral)]
    [InlineData("radial", SketchDimensionKind.Radial)]
    [InlineData("angular", SketchDimensionKind.Angular)]
    public void KnownAndOmittedKinds_Resolve(string? name, SketchDimensionKind expected)
    {
        Assert.True(SketchDimensionRule.TryResolve(name, out var kind, out var problem));
        Assert.Equal(expected, kind);
        Assert.Null(problem);
    }

    [Fact]
    public void UnknownKind_IsRefusedAndNamesTheAcceptedOnes()
    {
        Assert.False(SketchDimensionRule.TryResolve("diameter", out _, out var problem));

        Assert.NotNull(problem);
        foreach (var accepted in SketchDimensionKinds.All)
        {
            Assert.Contains(accepted, problem, StringComparison.Ordinal);
        }

        // The refusal must say what an omitted field would have meant, or a client cannot tell whether
        // it lost the default or asked for something that does not exist.
        Assert.Contains(SketchDimensionKinds.Default, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVocabulary_IsTheFourKindsAndIsDeclaredInOnePlace()
    {
        Assert.Equal(new[] { "linear", "diametral", "radial", "angular" }, SketchDimensionKinds.All);
        Assert.True(SketchDimensionKinds.IsDeclared(SketchDimensionKinds.Default));
        Assert.False(SketchDimensionKinds.IsDeclared("diameter"));
    }

    [Theory]
    [InlineData("linear", "mm")]
    [InlineData("diametral", "mm")]
    [InlineData("radial", "mm")]
    [InlineData("angular", "deg")]
    public void TheValueUnitFollowsTheKind(string kind, string unit) =>
        Assert.Equal(unit, SketchDimensionKinds.ValueUnit(kind));

    [Theory]
    [InlineData(SketchDimensionKind.Linear, "length_mm")]
    [InlineData(SketchDimensionKind.Diametral, "diameter_mm")]
    [InlineData(SketchDimensionKind.Radial, "radius_mm")]
    [InlineData(SketchDimensionKind.Angular, "angle_deg")]
    public void TheGeometryNameFollowsTheKind(SketchDimensionKind kind, string name) =>
        Assert.Equal(name, SketchDimensionRule.GeometryName(kind));

    [Fact]
    public void OnlyTheAngularKindTakesASecondObject()
    {
        Assert.True(SketchDimensionRule.NeedsSecondObject(SketchDimensionKind.Angular));
        Assert.False(SketchDimensionRule.NeedsSecondObject(SketchDimensionKind.Linear));
        Assert.False(SketchDimensionRule.NeedsSecondObject(SketchDimensionKind.Diametral));
        Assert.False(SketchDimensionRule.NeedsSecondObject(SketchDimensionKind.Radial));
    }

    [Theory]
    [InlineData(SketchDimensionKind.Linear, SketchObjectShape.Segment)]
    [InlineData(SketchDimensionKind.Diametral, SketchObjectShape.Circle)]
    [InlineData(SketchDimensionKind.Radial, SketchObjectShape.Arc)]
    [InlineData(SketchDimensionKind.Radial, SketchObjectShape.Circle)]
    [InlineData(SketchDimensionKind.Angular, SketchObjectShape.Segment)]
    public void TheAcceptedShapesContainTheShapeTheKindMeasures(SketchDimensionKind kind,
        SketchObjectShape shape) =>
        Assert.Contains(shape, SketchDimensionRule.AcceptedShapes(kind));

    [Fact]
    public void TheDiametralKindDoesNotAcceptAnArc_AndTheRadialOneDoes()
    {
        // NAMED LIMIT: the help allows a circle OR an arc for the diametral base object; the order names
        // a circle for this kind, so an arc is refused there and accepted by the radial kind.
        Assert.DoesNotContain(SketchObjectShape.Arc, SketchDimensionRule.AcceptedShapes(
            SketchDimensionKind.Diametral));
        Assert.Contains(SketchObjectShape.Arc, SketchDimensionRule.AcceptedShapes(
            SketchDimensionKind.Radial));
    }

    [Fact]
    public void NoFieldIsForeignWhenEveryFieldBelongsToItsKind()
    {
        Assert.Empty(SketchDimensionRule.ForeignFields(SketchDimensionKind.Linear,
            secondObjectRef: false, valueMm: true, valueDeg: false, offsetMm: true));
        Assert.Empty(SketchDimensionRule.ForeignFields(SketchDimensionKind.Diametral,
            secondObjectRef: false, valueMm: true, valueDeg: false, offsetMm: false));
        Assert.Empty(SketchDimensionRule.ForeignFields(SketchDimensionKind.Radial,
            secondObjectRef: false, valueMm: true, valueDeg: false, offsetMm: false));
        Assert.Empty(SketchDimensionRule.ForeignFields(SketchDimensionKind.Angular,
            secondObjectRef: true, valueMm: false, valueDeg: true, offsetMm: false));
    }

    [Fact]
    public void TheSecondObjectIsForeignForEveryKindButTheAngularOne()
    {
        foreach (var kind in new[]
                 {
                     SketchDimensionKind.Linear, SketchDimensionKind.Diametral, SketchDimensionKind.Radial,
                 })
        {
            Assert.Contains("second_object_ref", SketchDimensionRule.ForeignFields(kind,
                secondObjectRef: true, valueMm: true, valueDeg: false, offsetMm: false));
        }
    }

    [Fact]
    public void TheValueMembersAreForeignForTheOtherKind()
    {
        Assert.Contains("value_deg", SketchDimensionRule.ForeignFields(SketchDimensionKind.Diametral,
            secondObjectRef: false, valueMm: true, valueDeg: true, offsetMm: false));
        Assert.Contains("value_mm", SketchDimensionRule.ForeignFields(SketchDimensionKind.Angular,
            secondObjectRef: true, valueMm: true, valueDeg: true, offsetMm: false));
    }

    [Fact]
    public void TheOffsetIsForeignForEveryKindButTheLinearOne()
    {
        foreach (var kind in new[]
                 {
                     SketchDimensionKind.Diametral, SketchDimensionKind.Radial, SketchDimensionKind.Angular,
                 })
        {
            Assert.Contains("offset_mm", SketchDimensionRule.ForeignFields(kind,
                secondObjectRef: kind == SketchDimensionKind.Angular, valueMm: false, valueDeg: true,
                offsetMm: true));
        }
    }

    [Theory]
    [InlineData(SketchDimensionKind.Linear, "отрезок")]
    [InlineData(SketchDimensionKind.Diametral, "окружность")]
    [InlineData(SketchDimensionKind.Radial, "дуга или окружность")]
    [InlineData(SketchDimensionKind.Angular, "два отрезка")]
    public void TheRequiredShapeIsNamedPerKind(SketchDimensionKind kind, string text) =>
        Assert.Equal(text, SketchDimensionRule.RequiredShapeText(kind));

    [Fact]
    public void TheValueMeaningIsNamedPerKind()
    {
        Assert.Contains("ДИАМЕТР", SketchDimensionRule.ValueMeaning(SketchDimensionKind.Diametral));
        Assert.Contains("РАДИУС", SketchDimensionRule.ValueMeaning(SketchDimensionKind.Radial));
        Assert.Contains("УГОЛ", SketchDimensionRule.ValueMeaning(SketchDimensionKind.Angular));
        Assert.Contains("ДЛИНУ", SketchDimensionRule.ValueMeaning(SketchDimensionKind.Linear));
    }

    [Fact]
    public void TheNameRoundTripsThroughResolve()
    {
        foreach (var kind in new[]
                 {
                     SketchDimensionKind.Linear, SketchDimensionKind.Diametral, SketchDimensionKind.Radial,
                     SketchDimensionKind.Angular,
                 })
        {
            var name = SketchDimensionRule.Name(kind);
            Assert.True(SketchDimensionRule.TryResolve(name, out var back, out _));
            Assert.Equal(kind, back);
        }
    }
}
