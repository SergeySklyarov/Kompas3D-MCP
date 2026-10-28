using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The two numbers a moved face must produce, and the tolerance they are compared with.</summary>
/// <remarks>WHY. <c>Update() = true</c> is «accepted», not «applied»: the same route answered true for a
/// removal that changed nothing. The move is therefore confirmed by the swept volume <c>d·A</c> and the
/// lateral band <c>d·P</c>, and the sign of <c>d</c> is the displacement the volume shows — which side
/// <c>Direction = false</c> means is not established by measurement.
/// History: docs/decisions/direct-faces.md#implementation</remarks>
public class FaceMoveEffectTests
{
    // The measured fixture: a 40 x 30 x 20 box, its 40 x 30 top face moved by 5 mm along the normal.
    private const double Box = 24000d;
    private const double BoxArea = 5200d;
    private const double TopArea = 1200d;
    private const double TopPerimeter = 140d;

    [Fact]
    public void Volume_GrowsByTheSweptPrism()
    {
        Assert.Equal(30000d, FaceMoveEffect.ExpectedVolume(Box, TopArea, 5d), 6);
    }

    [Fact]
    public void Area_GrowsByOneBandOfThePerimeter()
    {
        Assert.Equal(5900d, FaceMoveEffect.ExpectedArea(BoxArea, TopPerimeter, 5d), 6);
    }

    [Fact]
    public void InwardMove_ShrinksBothNumbers()
    {
        Assert.Equal(18000d, FaceMoveEffect.ExpectedVolume(Box, TopArea, -5d), 6);
        Assert.Equal(4500d, FaceMoveEffect.ExpectedArea(BoxArea, TopPerimeter, -5d), 6);
    }

    [Fact]
    public void ObservedDisplacement_IsTheVolumeDeltaOverTheFaceArea()
    {
        Assert.Equal(5d, FaceMoveEffect.ObservedDisplacement(Box, 30000d, TopArea));
        Assert.Equal(-5d, FaceMoveEffect.ObservedDisplacement(Box, 18000d, TopArea));
    }

    /// <summary>A zero or unread face area divides by zero: "not computed" must not look like "moved by
    /// zero", or a no-op would pass as a completed move.</summary>
    [Fact]
    public void ObservedDisplacement_IsNullWhenItCannotBeComputed()
    {
        Assert.Null(FaceMoveEffect.ObservedDisplacement(Box, 30000d, 0d));
        Assert.Null(FaceMoveEffect.ObservedDisplacement(Box, 30000d, null));
        Assert.Null(FaceMoveEffect.ObservedDisplacement(null, 30000d, TopArea));
        Assert.Null(FaceMoveEffect.ObservedDisplacement(Box, null, TopArea));
    }

    [Fact]
    public void Matches_KeepsTheProfileTolerance()
    {
        Assert.True(FaceMoveEffect.Matches(30000.005d, 30000d));
        Assert.True(FaceMoveEffect.Matches(30000d * (1d + 1e-7d), 30000d));
        Assert.False(FaceMoveEffect.Matches(30001d, 30000d));
        Assert.False(FaceMoveEffect.Matches(null, 30000d));
        Assert.False(FaceMoveEffect.Matches(30000d, null));
        Assert.False(FaceMoveEffect.Matches(double.NaN, 30000d));
    }

    /// <summary>The two signs a tangent-group move is judged by, because <c>V + d·A</c> over the NAMED
    /// faces does not predict a swept set that the chain widened.</summary>
    /// <remarks>MEASURED: on a bar with R3 fillets the set grew from 1 to 8 faces and the volume moved by
    /// 22111 mm³ against the 4800 the named face alone would sweep, so the formula cannot be the judge.
    /// History: docs/decisions/direct-faces.md#tangent</remarks>
    [Fact]
    public void VolumeChanged_IsTheSignThatTheOperationWasApplied()
    {
        Assert.True(FaceMoveEffect.VolumeChanged(Box, 30000d));
        Assert.False(FaceMoveEffect.VolumeChanged(Box, Box));
        Assert.False(FaceMoveEffect.VolumeChanged(null, 30000d));
        Assert.False(FaceMoveEffect.VolumeChanged(Box, double.NaN));
    }

    [Fact]
    public void GroupWider_NeedsAReadSizeAboveTheNamedFaces()
    {
        Assert.True(FaceMoveEffect.GroupWider(8, 1));
        Assert.False(FaceMoveEffect.GroupWider(1, 1));
        Assert.False(FaceMoveEffect.GroupWider(null, 1));
        Assert.False(FaceMoveEffect.GroupWider(0, 1));
    }

    /// <summary>MEASURED: a single-mode move on a face with tangent neighbours reads back 6.25 mm for
    /// a requested 5, because the kernel moved the chain too. A shorter sweep is never excused.</summary>
    /// <remarks>History: docs/decisions/direct-faces.md#implicit-tangent</remarks>
    [Fact]
    public void SweptFurther_ExcusesOnlyTheLongerSweep()
    {
        Assert.True(FaceMoveEffect.SweptFurther(6.25d, 5d));
        Assert.True(FaceMoveEffect.SweptFurther(-6.25d, 5d));
        Assert.False(FaceMoveEffect.SweptFurther(5d, 5d));
        Assert.False(FaceMoveEffect.SweptFurther(3.75d, 5d));
        Assert.False(FaceMoveEffect.SweptFurther(5.01d, 5d));
        Assert.False(FaceMoveEffect.SweptFurther(null, 5d));
        Assert.False(FaceMoveEffect.SweptFurther(double.NaN, 5d));
    }
}
