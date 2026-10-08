using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rules of the sketch-bulk block (G2): the published limits come from ONE constant, a spline is a
/// curve with no analytic area, and a field of a foreign kind is refused by name rather than dropped.</summary>
/// <remarks>Every number asserted here was MEASURED on KOMPAS-3D v24 in this block's own run and is quoted
/// where it matters; nothing is asserted from the help alone.
/// History: docs/decisions/adapter-sketch.md#bulk-limits</remarks>
public class SketchBulkTests
{
    private static JsonObject SketchEntitySchema() =>
        ToolCatalog.SharedDefinitions["sketch_entity"]!.AsObject();

    private static JsonObject EditSketchEntities() =>
        ToolCatalog.All.Single(t => t.Name == "kompas_edit_sketch").InputSchema["properties"]!
            .AsObject()["entities"]!.AsObject();

    private static SketchEntityDto Spline(params double[][] points) => new()
    {
        Kind = SketchEntityKind.Spline,
        PointsMm = points,
        Closed = true,
    };

    [Fact]
    public void EntitiesLimit_IsTheSharedConstant()
    {
        Assert.Equal(SketchLimits.MaxEntitiesPerCall, (int)EditSketchEntities()["maxItems"]!);
    }

    [Fact]
    public void VerticesLimit_IsTheSharedConstant()
    {
        var points = SketchEntitySchema()["properties"]!.AsObject()["points_mm"]!.AsObject();
        Assert.Equal(SketchLimits.MaxPolylineVertices, (int)points["maxItems"]!);
    }

    [Fact]
    public void BothDescriptions_NameTheirNumberAndSayTheLimitIsTheContracts()
    {
        var entities = (string)EditSketchEntities()["description"]!;
        var points = (string)SketchEntitySchema()["properties"]!.AsObject()["points_mm"]!["description"]!;

        Assert.Contains(SketchLimits.MaxEntitiesPerCall.ToString(), entities, StringComparison.Ordinal);
        Assert.Contains(SketchLimits.MaxPolylineVertices.ToString(), points, StringComparison.Ordinal);
        // The kernel does not impose either limit: a client that hits one must not go looking for a
        // KOMPAS restriction that does not exist.
        Assert.Contains("СХЕМОЙ MCP", entities, StringComparison.Ordinal);
        Assert.Contains("СХЕМОЙ MCP", points, StringComparison.Ordinal);
    }

    [Fact]
    public void Spline_IsAnAdvertisedKind()
    {
        var kinds = SketchEntitySchema()["properties"]!.AsObject()["kind"]!.AsObject()["enum"]!
            .AsArray().Select(n => (string)n!).ToArray();

        Assert.Contains("spline", kinds);
        Assert.Contains("polyline", kinds);
    }

    [Fact]
    public void Degree_IsNotDeclared_SoNoRoutePromisesAnOrderItCannotApply()
    {
        // The chosen route is ksBezier, which has no order parameter; a declared `degree` would be a
        // parameter the server accepts and drops.
        Assert.False(SketchEntitySchema()["properties"]!.AsObject().ContainsKey("degree"));
    }

    [Fact]
    public void Spline_BelowTheMinimum_IsRefusedBeforeCom()
    {
        var failure = Assert.Throws<KompasContractException>(
            () => SketchValidation.Validate(Spline(new double[] { 0, 0 }, new double[] { 1, 1 })));

        Assert.Equal(ErrorCodes.InvalidArgument, failure.Code);
        Assert.Contains("КОМПАС не вызывался", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Spline_AtTheMinimum_Passes()
    {
        SketchValidation.Validate(Spline(
            new double[] { 0, 0 }, new double[] { 1, 0 }, new double[] { 1, 1 }));
    }

    [Fact]
    public void ForeignField_IsRefusedByName()
    {
        var circleWithPoints = new SketchEntityDto
        {
            Kind = SketchEntityKind.Circle,
            CenterMm = new double[] { 0, 0 },
            RadiusMm = 5,
            PointsMm = new[] { new double[] { 0, 0 }, new double[] { 1, 1 } },
        };

        var failure = Assert.Throws<KompasContractException>(
            () => SketchValidation.Validate(circleWithPoints));

        Assert.Equal(ErrorCodes.InvalidArgument, failure.Code);
        Assert.Contains("points_mm", failure.Message, StringComparison.Ordinal);
        Assert.Equal("points_mm", ((string[])failure.Details!["foreign_fields"]!)[0]);
    }

    [Fact]
    public void Rectangle_AcceptsEitherCornerForm_SoNeitherIsForeign()
    {
        // The drawing route reads `start_mm ?? center_mm`, so BOTH name the corner: refusing one would
        // refuse a form the server applies.
        SketchValidation.Validate(new SketchEntityDto
        {
            Kind = SketchEntityKind.Rectangle,
            CenterMm = new double[] { 0, 0 },
            WidthMm = 10,
            HeightMm = 5,
        });
    }

    [Fact]
    public void Spline_HasNoAnalyticArea_AndSaysWhy()
    {
        var outcome = ProfileArea.Compute(new[] { Spline(
            new double[] { 0, 0 }, new double[] { 10, 0 }, new double[] { 10, 10 }) });

        Assert.Null(outcome.AreaMm2);
        Assert.NotNull(outcome.UnavailableReason);
        Assert.Contains("сплайном", outcome.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Spline_BoxIsTheRectangleAroundItsVertices()
    {
        var box = ProfileBox.Of(Spline(
            new double[] { -3, 1 }, new double[] { 5, 1 }, new double[] { 5, 7 }));

        Assert.NotNull(box);
        Assert.Equal(-3d, box!.Value.MinU, 9);
        Assert.Equal(1d, box.Value.MinV, 9);
        Assert.Equal(5d, box.Value.MaxU, 9);
        Assert.Equal(7d, box.Value.MaxV, 9);
    }

    [Fact]
    public void ClosedPolyline_StillUsesTheShoelace()
    {
        // The polyline is now ONE native object, but the profile area the extrusion verifies against is
        // still computed from the vertices — the contract change must not move that figure.
        var polygon = new SketchEntityDto
        {
            Kind = SketchEntityKind.Polyline,
            Closed = true,
            PointsMm = new[]
            {
                new double[] { -10, -10 }, new double[] { 10, -10 },
                new double[] { 10, 10 }, new double[] { -10, 10 },
            },
        };

        Assert.Equal(400d, ProfileArea.Of(new[] { polygon })!.Value, 9);
    }
}
