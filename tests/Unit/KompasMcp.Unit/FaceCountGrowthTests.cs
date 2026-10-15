using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rule that decides whether a fillet or a chamfer grew the face count.</summary>
/// <remarks>The decision is a pure function of two readings, so a substituted pair exercises every branch
/// without a CAD session. The counts below are the ones a cube of 100 mm with all 12 edges rounded at R3
/// really answers.
/// History: docs/decisions/adapter-features.md#face-count-growth-rule</remarks>
public class FaceCountGrowthTests
{
    [Fact]
    public void ConvergingEdges_ThatLeaveCornerFaces_StillCountAsGrown()
    {
        // The reported defect: 12 edges, but 20 new faces - the 12 edge surfaces and 8 corner surfaces.
        Assert.True(FaceCountGrowth.Grew(6d, 26d));
        Assert.Equal(20d, FaceCountGrowth.Delta(6d, 26d));
    }

    [Fact]
    public void OneNewFacePerEdge_CountsAsGrown()
    {
        Assert.True(FaceCountGrowth.Grew(6d, 18d));
    }

    [Fact]
    public void UnchangedOrShrunkCount_IsNotGrowth()
    {
        Assert.False(FaceCountGrowth.Grew(6d, 6d));
        Assert.False(FaceCountGrowth.Grew(26d, 6d));
    }

    [Fact]
    public void UnreadSide_IsNeverGrowth()
    {
        Assert.False(FaceCountGrowth.Grew(null, 26d));
        Assert.False(FaceCountGrowth.Grew(6d, null));
        Assert.False(FaceCountGrowth.Grew(null, null));
        Assert.Null(FaceCountGrowth.Delta(null, 26d));
    }

    [Fact]
    public void ObservedCarriesBothCountsAndTheChange()
    {
        Assert.Equal("6→26 (+20)", FaceCountGrowth.Observed(6d, 26d));
        Assert.Equal("6→18 (+12)", FaceCountGrowth.Observed(6d, 18d));
        Assert.Equal("6→6 (+0)", FaceCountGrowth.Observed(6d, 6d));
        Assert.Equal("6→не читается", FaceCountGrowth.Observed(6d, null));
    }

    [Fact]
    public void ExpectedIsAnInequality_NotAFixedNumberOfNewFaces()
    {
        Assert.Equal("> 6", FaceCountGrowth.Expected(6d));
        Assert.Equal("число граней выросло", FaceCountGrowth.Expected(null));
    }
}
