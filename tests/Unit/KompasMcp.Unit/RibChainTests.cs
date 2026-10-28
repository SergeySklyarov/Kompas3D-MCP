using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rules a rib's sketch and its added volume are judged by when COM cannot be asked in a
/// test.</summary>
/// <remarks>WHY. The kernel answers a closed or branching contour with a bare <c>SetSketch = false</c>, so
/// the defect has to be named by the server; and the volume is the only confirmation that the rib was
/// built on the chain the server read.
/// History: docs/decisions/ribs.md#chain-check</remarks>
public class RibChainTests
{
    // The measured fixture: a checkmark chain (0,20) -> (20,40) -> (40,20) enclosing 400 mm2 with its
    // closing chord, and a 6 mm rib adding 2400 mm3.
    private const double FixtureArea = 400d;
    private const double FixtureThickness = 6d;
    private const double FixtureVolume = 2400d;

    [Fact]
    public void OpenChain_AreaIsShoelaceWithTheEndChord()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 20d, 20d, 40d),
            new RibSegment(20d, 40d, 40d, 20d),
        });

        Assert.True(outcome.IsChain);
        Assert.Equal(FixtureArea, outcome.AreaMm2!.Value, 9);
        Assert.Equal(2, outcome.SegmentCount);
        Assert.Equal(3, outcome.PointCount);
    }

    /// <summary>The same chain drawn the other way encloses the same region: the area is a property of
    /// the contour, not of the drawing direction, and the rib's position is what the direction changes.</summary>
    [Fact]
    public void ReversedChain_HasTheSameArea()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(40d, 20d, 20d, 40d),
            new RibSegment(20d, 40d, 0d, 20d),
        });

        Assert.True(outcome.IsChain);
        Assert.Equal(FixtureArea, outcome.AreaMm2!.Value, 9);
    }

    [Fact]
    public void SegmentsOutOfOrder_AreWalkedIntoOneChain()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(20d, 40d, 40d, 20d),
            new RibSegment(0d, 20d, 20d, 40d),
        });

        Assert.True(outcome.IsChain);
        Assert.Equal(FixtureArea, outcome.AreaMm2!.Value, 9);
    }

    /// <summary>A closed contour is refused BEFORE COM: MEASURED, the kernel answers <c>SetSketch = false</c>
    /// and names no cause.</summary>
    [Fact]
    public void ClosedContour_IsRefusedAndNamed()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 0d, 40d, 0d),
            new RibSegment(40d, 0d, 40d, 20d),
            new RibSegment(40d, 20d, 0d, 20d),
            new RibSegment(0d, 20d, 0d, 0d),
        });

        Assert.False(outcome.IsChain);
        Assert.Null(outcome.AreaMm2);
        Assert.Contains("замкнут", outcome.Reason);
    }

    [Fact]
    public void BranchingChain_IsRefused()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 20d, 20d, 40d),
            new RibSegment(20d, 40d, 40d, 20d),
            new RibSegment(20d, 40d, 20d, 0d),
        });

        Assert.False(outcome.IsChain);
        Assert.Contains("ветвится", outcome.Reason);
    }

    /// <summary>Two separate contours are not one chain: the count of free ends is what tells that apart
    /// from a single chain, and the refusal states it instead of "the geometry is wrong".</summary>
    [Fact]
    public void TwoChains_AreRefused()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 20d, 20d, 40d),
            new RibSegment(20d, 40d, 40d, 20d),
            new RibSegment(0d, 0d, 10d, 10d),
        });

        Assert.False(outcome.IsChain);
        Assert.Contains("свободных концов 4", outcome.Reason);
    }

    [Fact]
    public void EmptySketch_IsRefused()
    {
        var outcome = RibChain.Analyze(Array.Empty<RibSegment>());

        Assert.False(outcome.IsChain);
        Assert.Contains("нет ни одного отрезка", outcome.Reason);
    }

    /// <summary>A chain lying on one line encloses nothing, and a zero-area figure must not be published
    /// as a measured area: the rib would then be "confirmed" by 0 mm3 against 0 mm3.</summary>
    [Fact]
    public void CollinearChain_IsRefused()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 0d, 20d, 0d),
            new RibSegment(20d, 0d, 40d, 0d),
        });

        Assert.False(outcome.IsChain);
        Assert.Null(outcome.AreaMm2);
    }

    [Fact]
    public void DegenerateSegment_IsRefused()
    {
        var outcome = RibChain.Analyze(new[]
        {
            new RibSegment(0d, 20d, 0d, 20d),
            new RibSegment(0d, 20d, 40d, 20d),
        });

        Assert.False(outcome.IsChain);
        Assert.Contains("вырожден", outcome.Reason);
    }

    [Fact]
    public void ExpectedVolume_IsAreaTimesThickness()
    {
        Assert.Equal(FixtureVolume, RibChain.ExpectedVolumeMm3(FixtureArea, FixtureThickness)!.Value, 9);
    }

    /// <summary>The symmetric mode (<c>dtBoth</c>) puts the same thickness on both sides of the sketch
    /// plane, so the expectation is exactly twice the one-sided one at the same thickness.</summary>
    [Fact]
    public void ExpectedVolume_DoublesInTheSymmetricMode()
    {
        var oneSided = RibChain.ExpectedVolumeMm3(FixtureArea, FixtureThickness)!.Value;
        var both = RibChain.ExpectedVolumeMm3(FixtureArea, FixtureThickness, symmetric: true)!.Value;

        Assert.Equal(2d * oneSided, both, 9);
        Assert.Null(RibChain.ExpectedVolumeMm3(null, FixtureThickness, symmetric: true));
        Assert.Null(RibChain.ExpectedVolumeMm3(FixtureArea, 0d, symmetric: true));
    }

    /// <summary>Without an area there is no expectation at all — <c>null</c>, never zero: a zero
    /// expectation would be "confirmed" by a body that did not change.</summary>
    [Fact]
    public void ExpectedVolume_IsNullWithoutAnArea()
    {
        Assert.Null(RibChain.ExpectedVolumeMm3(null, FixtureThickness));
        Assert.Null(RibChain.ExpectedVolumeMm3(FixtureArea, 0d));
    }

    [Fact]
    public void VolumeMatches_WithinOnePercent()
    {
        Assert.True(RibChain.VolumeMatches(FixtureVolume, FixtureVolume));
        Assert.True(RibChain.VolumeMatches(FixtureVolume, FixtureVolume * 1.009d));
        Assert.False(RibChain.VolumeMatches(FixtureVolume, FixtureVolume * 1.011d));
        Assert.False(RibChain.VolumeMatches(FixtureVolume, 0d));
        Assert.False(RibChain.VolumeMatches(null, FixtureVolume));
        Assert.False(RibChain.VolumeMatches(FixtureVolume, null));
    }
}
