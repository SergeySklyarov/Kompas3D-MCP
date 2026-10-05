using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>A sketch is built by several calls (append per contour, replace to clear), and the extrusion's
/// expected volume is the area of the WHOLE profile — so the accumulation, not just the formula, is
/// what the check stands on.</summary>
/// <remarks>MEASURED: a running-sum adapter gave the same figure for outer+inner circles as for both at once;
/// a scalar cannot carry nesting, so a later edit could never turn the remembered number into a hole.
/// TEST: these tests hold the contour list to that.
/// History: docs/decisions/tests.md#sketch-profile-2</remarks>
public class SketchProfileTests
{
    private static SketchEntityDto Circle(double cx, double cy, double r) => new()
    {
        Kind = SketchEntityKind.Circle,
        CenterMm = new double[] { cx, cy },
        RadiusMm = r,
    };

    private static SketchEntityDto Line() => new()
    {
        Kind = SketchEntityKind.Line,
        StartMm = new double[] { 0, 0 },
        EndMm = new double[] { 10, 0 },
    };

    [Fact]
    public void Append_ThenTheAreaIsTheRegionNotTheSum()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 10) });
        Assert.Equal(Math.PI * 100d, profile.AreaMm2!.Value, 9);

        // The second call turns the disc into a ring — the accumulated profile must see it.
        profile.Append(new[] { Circle(0, 0, 5) });
        Assert.Equal(Math.PI * 75d, profile.AreaMm2!.Value, 9);
    }

    [Fact]
    public void Append_IsOrderIndependent()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 5) });
        profile.Append(new[] { Circle(0, 0, 10) });

        Assert.Equal(Math.PI * 75d, profile.AreaMm2!.Value, 9);
    }

    [Fact]
    public void Append_DisjointContours_AddUp()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 5) });
        profile.Append(new[] { Circle(100, 0, 3) });

        Assert.Equal((Math.PI * 25d) + (Math.PI * 9d), profile.AreaMm2!.Value, 9);
    }

    [Fact]
    public void Replace_DropsWhatWasThereBefore()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 10), Circle(0, 0, 5) });

        profile.Replace(new[] { Circle(0, 0, 4) });

        Assert.Equal(Math.PI * 16d, profile.AreaMm2!.Value, 9);
        Assert.Single(profile.Entities);
    }

    [Fact]
    public void Clear_MakesTheProfileUnmeasurable()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 10) });

        profile.Clear();

        Assert.Null(profile.AreaMm2);
        Assert.Empty(profile.Entities);
    }

    [Fact]
    public void Append_UnknownShape_MakesTheAreaUnknown()
    {
        // A line is not a region; one unknown primitive makes the whole profile unknown.
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 10) });
        profile.Append(new[] { Line() });

        Assert.Null(profile.AreaMm2);
        Assert.Equal(2, profile.Entities.Count);
    }

    [Fact]
    public void Entities_KeepEveryPrimitiveInOrder()
    {
        var profile = new SketchProfile();
        profile.Append(new[] { Circle(0, 0, 10) });
        profile.Append(new[] { Circle(0, 0, 5), Circle(0, 0, 2) });

        Assert.Equal(new[] { 10d, 5d, 2d }, profile.Entities.Select(e => e.RadiusMm!.Value));
    }
}
