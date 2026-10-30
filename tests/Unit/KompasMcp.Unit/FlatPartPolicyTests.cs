using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The plane check that separates a flat part of constant thickness from a stepped or drafted one,
/// without KOMPAS.</summary>
/// <remarks>TEST: a step is a third parallel plane, not a bigger area, so the areas alone cannot tell a plate
/// from a stepped plate. History: docs/decisions/flat-part.md#plane-check</remarks>
public class FlatPartPolicyTests
{
    private static readonly double[] Up = { 0d, 0d, 1d };

    private static FlatPartFace Plane(string id, double area, double z, double[]? normal = null) =>
        new(id, "plane", area, normal ?? Up, new[] { 0d, 0d, z });

    private static FlatPartFace Wall(string id, double area, double[] normal) =>
        new(id, "plane", area, normal, new[] { 0d, 0d, 0d });

    private static FlatPartFace Hole(string id, double area) =>
        new(id, "cylinder", area, new[] { 1d, 0d, 0d }, new[] { 5d, 5d, 0d }, new[] { 0d, 0d, 1d });

    // A 40 x 30 plate, 2 mm thick, one through hole and four walls.
    private static FlatPartFace[] Plate() => new[]
    {
        Plane("top", 1200d, 2d),
        Plane("bottom", 1200d, 0d),
        Wall("x0", 60d, new[] { 1d, 0d, 0d }),
        Wall("x1", 60d, new[] { -1d, 0d, 0d }),
        Wall("y0", 80d, new[] { 0d, 1d, 0d }),
        Wall("y1", 80d, new[] { 0d, -1d, 0d }),
        Hole("hole", 12.57d),
    };

    [Fact]
    public void Analyze_PlateWithHoleIsFlatWithItsOwnThickness()
    {
        var plan = FlatPartPolicy.Analyze(Plate());

        Assert.Equal(FlatPartVerdict.Flat, plan.Verdict);
        Assert.Equal(2d, plan.ThicknessMm!.Value, 6);
        Assert.Equal(1d, plan.Normal![2], 6);
        Assert.Empty(plan.OffendingFaceRefs);
    }

    // A stepped plate: the same outline, but the top is split into two planes at 2 and 5 mm.
    [Fact]
    public void Analyze_StepIsNotAConstantThickness()
    {
        var faces = Plate().Append(Plane("step_top", 300d, 5d)).ToArray();

        var plan = FlatPartPolicy.Analyze(faces);

        Assert.Equal(FlatPartVerdict.NotConstantThickness, plan.Verdict);
        Assert.Contains("step_top", plan.OffendingFaceRefs);
    }

    // A draft on a side wall: the wall is planar but no longer perpendicular to the flat normal.
    [Fact]
    public void Analyze_InclinedWallIsNotAConstantThickness()
    {
        var faces = Plate()
            .Select(face => face.FaceRef == "x0"
                ? face with { Normal = new[] { 0.966d, 0d, 0.259d } }
                : face)
            .ToArray();

        var plan = FlatPartPolicy.Analyze(faces);

        Assert.Equal(FlatPartVerdict.NotConstantThickness, plan.Verdict);
        Assert.Contains("x0", plan.OffendingFaceRefs);
    }

    // A cube: three pairs of equal area, none of them «the» flat pattern.
    [Fact]
    public void Analyze_CubeIsAmbiguous()
    {
        var faces = new[]
        {
            Plane("top", 100d, 10d),
            Plane("bottom", 100d, 0d),
            Wall("x0", 100d, new[] { 1d, 0d, 0d }),
            Wall("x1", 100d, new[] { -1d, 0d, 0d }),
            Wall("y0", 100d, new[] { 0d, 1d, 0d }),
            Wall("y1", 100d, new[] { 0d, -1d, 0d }),
        };

        var plan = FlatPartPolicy.Analyze(faces);

        Assert.Equal(FlatPartVerdict.Ambiguous, plan.Verdict);
        Assert.Null(plan.ThicknessMm);
    }

    [Fact]
    public void Analyze_OneFaceIsAmbiguous()
    {
        var plan = FlatPartPolicy.Analyze(new[] { Plane("only", 100d, 0d) });

        Assert.Equal(FlatPartVerdict.Ambiguous, plan.Verdict);
    }

    // An unread normal is «not read», never a zero vector that would silently pass: the face cannot be
    // verified, and an unverified body is refused rather than exported.
    [Fact]
    public void Analyze_FaceWithoutNormalIsRefused()
    {
        var faces = Plate().Append(Plane("unread", 50d, 1d) with { Normal = null }).ToArray();

        var plan = FlatPartPolicy.Analyze(faces);

        Assert.Equal(FlatPartVerdict.NotConstantThickness, plan.Verdict);
        Assert.Contains("unread", plan.OffendingFaceRefs);
        Assert.Contains("не прочитаны", plan.Reason, StringComparison.Ordinal);
    }
}
