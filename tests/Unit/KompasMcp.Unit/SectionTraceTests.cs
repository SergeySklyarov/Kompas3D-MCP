using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rule a cut-line trace must satisfy, and the reading of a built section's picture.</summary>
/// <remarks>TEST: only numbers go in, so a trace that must be refused is refused without КОМПАС, and the
/// refusal happens before any object is written. History: docs/decisions/drawings.md#section-route</remarks>
public class SectionTraceTests
{
    /// <summary>The trace of the order §0: three cutting runs joined by two transitions.</summary>
    private static readonly double[] Stepped =
    {
        -10d, 30d, 45d, 30d, 45d, 60d, 75d, 60d, 75d, 50d, 130d, 50d,
    };

    [Fact]
    public void SteppedTraceOfTheOrderIsAccepted()
    {
        Assert.Empty(SectionTrace.Problems(SectionMode.Stepped, Stepped));
    }

    [Fact]
    public void SimpleTraceOfTwoPointsIsAccepted()
    {
        Assert.Empty(SectionTrace.Problems(SectionMode.Simple, new[] { -10d, 30d, 130d, 30d }));
    }

    [Fact]
    public void SimpleModeRefusesMoreThanTwoPoints()
    {
        var problems = SectionTrace.Problems(SectionMode.Simple, Stepped);
        Assert.Contains(problems, p => p.Contains("ровно двумя", StringComparison.Ordinal));
    }

    [Fact]
    public void OddNumberOfCoordinatesIsRefused()
    {
        var problems = SectionTrace.Problems(SectionMode.Stepped, new[] { 0d, 0d, 10d, 10d, 10d });
        Assert.Contains(problems, p => p.Contains("чётным", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreePointsAreNotASteppedTrace()
    {
        var problems = SectionTrace.Problems(SectionMode.Stepped, new[] { 0d, 0d, 10d, 0d, 10d, 10d });
        Assert.Contains(problems, p => p.Contains("четырьмя", StringComparison.Ordinal));
    }

    [Fact]
    public void NonPerpendicularBendIsRefused()
    {
        // The transition leaves the first cutting run at 45 degrees instead of 90.
        var problems = SectionTrace.Problems(
            SectionMode.Stepped,
            new[] { 0d, 0d, 20d, 0d, 30d, 10d, 30d, 40d });
        Assert.Contains(problems, p => p.Contains("не перпендикулярен", StringComparison.Ordinal));
    }

    [Fact]
    public void CuttingRunsMustBeParallel()
    {
        // Both runs are "cutting" (even segments) but the second one is tilted by 10 degrees.
        var problems = SectionTrace.Problems(
            SectionMode.Stepped,
            new[] { 0d, 0d, 20d, 0d, 20d, 20d, 20d + (20d * Math.Cos(10d * Math.PI / 180d)),
                    20d + (20d * Math.Sin(10d * Math.PI / 180d)) });
        Assert.Contains(problems, p => p.Contains("не параллельны", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroLengthSegmentIsRefused()
    {
        var problems = SectionTrace.Problems(
            SectionMode.Stepped,
            new[] { 0d, 0d, 0d, 0d, 20d, 0d, 20d, 20d });
        Assert.Contains(problems, p => p.Contains("нулевой длины", StringComparison.Ordinal));
    }

    [Fact]
    public void SelfCrossingTraceIsRefused()
    {
        // A U whose long run doubles back through its own transition.
        var problems = SectionTrace.Problems(
            SectionMode.Stepped,
            new[] { 0d, 0d, 40d, 0d, 40d, 40d, -10d, 40d, -10d, 10d, 50d, 10d });
        Assert.Contains(problems, p => p.Contains("самопересекается", StringComparison.Ordinal));
    }

    [Fact]
    public void LookLeftIsTheDocumentedArrowPosTrue()
    {
        Assert.True(SectionTrace.ArrowPos(lookLeft: true));
        Assert.False(SectionTrace.ArrowPos(lookLeft: false));
    }

    [Fact]
    public void LookVectorAlongTheTraceIsRefused()
    {
        // The first cutting run goes along +X, so a look vector along +X is not perpendicular to it.
        var accepted = SectionTrace.TryLookSide(new[] { 1d, 0d }, Stepped, out _, out var problem);
        Assert.False(accepted);
        Assert.Contains("не перпендикулярен", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void LookVectorAboveTheTracePointsToTheLeft()
    {
        // +Y is the left of a run going along +X, so the arrows sit left of the polyline.
        var accepted = SectionTrace.TryLookSide(new[] { 0d, 1d }, Stepped, out var lookLeft, out var problem);
        Assert.True(accepted);
        Assert.Null(problem);
        Assert.True(lookLeft);
    }

    [Fact]
    public void LookVectorBelowTheTracePointsToTheRight()
    {
        var accepted = SectionTrace.TryLookSide(new[] { 0d, -1d }, Stepped, out var lookLeft, out _);
        Assert.True(accepted);
        Assert.False(lookLeft);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(27, true)]
    public void ASectionIsBuiltOnlyWhenTheKernelDrewSomething(int? objectCount, bool expected)
    {
        Assert.Equal(expected, SectionTrace.Built(objectCount));
    }
}
