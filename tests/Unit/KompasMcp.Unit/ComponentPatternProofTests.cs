using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Expected instance origins of a component pattern, and their comparison with the model.</summary>
/// <remarks>INVARIANT: «число или начала не совпали» is a refusal, so the arithmetic that DECIDES "совпали"
/// is tested without a live КОМПАС — otherwise the only test of the rule would be a run.
/// MEASURED (probe, ASM_PATTERN_COMPONENTS basis): a grid of one component at the origin with step 30 and
/// count 3 puts its instances at (0,0,0), (30,0,0), (60,0,0).
/// History: docs/decisions/assembly-ext.md#component-arrays</remarks>
public class ComponentPatternProofTests
{
    private static readonly double[] Origin = [0d, 0d, 0d];
    private static readonly double[] AxisX = [1d, 0d, 0d];
    private static readonly double[] AxisY = [0d, 1d, 0d];
    private static readonly double[] AxisZ = [0d, 0d, 1d];

    /// <summary>MEASURED shape: three instances along one direction, the source included as instance 0.</summary>
    [Fact]
    public void Grid_ThreeAlongAxis_ProducesMeasuredOrigins()
    {
        var plan = ComponentPatternProof.Grid(Origin, AxisX, 30d, 3, null, 0d, 1);

        Assert.Null(plan.Unavailable);
        Assert.Equal(3, plan.Expected.Count);
        Assert.Equal(new double[] { 0d, 0d, 0d }, plan.Expected[0]);
        Assert.Equal(new double[] { 30d, 0d, 0d }, plan.Expected[1]);
        Assert.Equal(new double[] { 60d, 0d, 0d }, plan.Expected[2]);
    }

    /// <summary>Two directions: four nodes of a 2x2 grid, counted exactly.</summary>
    [Fact]
    public void Grid_TwoDirections_ProducesEveryNode()
    {
        var plan = ComponentPatternProof.Grid([10d, 5d, 0d], AxisX, 20d, 2, AxisY, 15d, 2);

        Assert.Equal(4, plan.Expected.Count);
        Assert.Contains(plan.Expected, p => Same(p, new double[] { 30d, 20d, 0d }));
    }

    /// <summary>INVARIANT: one measured origin closes one expectation; a duplicate is an extra, not a match.</summary>
    [Fact]
    public void Match_TwoInstancesOnOneNode_IsNotAMatch()
    {
        var expected = ComponentPatternProof.Grid(Origin, AxisX, 30d, 3, null, 0d, 1).Expected;
        var measured = new List<double[]> { new double[] { 0d, 0d, 0d }, new double[] { 0d, 0d, 0d }, new double[] { 60d, 0d, 0d } };

        var (matched, detail) = ComponentPatternProof.Match(measured, expected);

        Assert.False(matched);
        Assert.Contains("не найдено 1", detail);
    }

    /// <summary>INVARIANT (negative control): the measured set matches itself, and the tolerance is 0.01 mm.</summary>
    [Fact]
    public void Match_WithinTolerance_IsAMatch()
    {
        var expected = ComponentPatternProof.Grid(Origin, AxisX, 30d, 3, null, 0d, 1).Expected;
        var measured = new List<double[]> { new double[] { 0.005d, 0d, 0d }, new double[] { 29.995d, 0d, 0d }, new double[] { 60.004d, 0d, 0d } };

        var (matched, _) = ComponentPatternProof.Match(measured, expected);

        Assert.True(matched);
    }

    /// <summary>INVARIANT (negative control): a step off by more than the tolerance is not a match.</summary>
    [Fact]
    public void Match_ShiftedByMoreThanTolerance_Fails()
    {
        var expected = ComponentPatternProof.Grid(Origin, AxisX, 30d, 3, null, 0d, 1).Expected;
        var measured = new List<double[]> { new double[] { 0d, 0d, 0d }, new double[] { 30.02d, 0d, 0d }, new double[] { 60d, 0d, 0d } };

        var (matched, _) = ComponentPatternProof.Match(measured, expected);

        Assert.False(matched);
    }

    /// <summary>Circular: four instances at 90° about Z stand on the axes, the source keeping its own place.</summary>
    [Fact]
    public void Circular_QuarterTurn_ProducesFourPositions()
    {
        var plan = ComponentPatternProof.Circular([10d, 0d, 0d], Origin, AxisZ, 1, 4, 90d);

        Assert.Null(plan.Unavailable);
        Assert.Equal(4, plan.Expected.Count);
        Assert.Contains(plan.Expected, p => Same(p, new double[] { 10d, 0d, 0d }));
        Assert.Contains(plan.Expected, p => Same(p, new double[] { 0d, 10d, 0d }));
        Assert.Contains(plan.Expected, p => Same(p, new double[] { -10d, 0d, 0d }));
        Assert.Contains(plan.Expected, p => Same(p, new double[] { 0d, -10d, 0d }));
    }

    /// <summary>LIMIT: a radial layer is not derived, and the reason is NAMED instead of a guessed set.</summary>
    [Fact]
    public void Circular_RadialLayers_IsNotDerived()
    {
        var plan = ComponentPatternProof.Circular([10d, 0d, 0d], Origin, AxisZ, 2, 4, 90d);

        Assert.NotNull(plan.Unavailable);
        Assert.Empty(plan.Expected);
    }

    /// <summary>Angular steps: consecutive differences of the sorted angles equal the requested step.</summary>
    [Fact]
    public void SuccessiveSteps_EqualStep_Matches()
    {
        var angles = ComponentPatternProof.Angles(
            RingOrigins(), Origin, AxisZ);

        var (matched, detail) = ComponentPatternProof.SuccessiveSteps(angles, 90d);

        Assert.True(matched, detail);
    }

    /// <summary>INVARIANT (negative control): «360/(count-1)» instead of the requested step is caught.</summary>
    [Fact]
    public void SuccessiveSteps_WrongStep_Fails()
    {
        var angles = ComponentPatternProof.Angles(
            RingOrigins(), Origin, AxisZ);

        var (matched, _) = ComponentPatternProof.SuccessiveSteps(angles, 120d);

        Assert.False(matched);
    }

    /// <summary>INVARIANT: an instance standing ON the axis has no angle, and a made-up zero would read as a
    /// real ring position — the angle set is then empty rather than wrong.</summary>
    [Fact]
    public void Angles_InstanceOnAxis_IsNotARingPosition()
    {
        var angles = ComponentPatternProof.Angles(new List<double[]> { new double[] { 0d, 0d, 0d }, new double[] { 10d, 0d, 0d } }, Origin, AxisZ);

        Assert.Empty(angles);
    }

    /// <summary>A degenerate direction answers null, so the caller names "not derivable" instead of dividing
    /// by zero.</summary>
    [Fact]
    public void Unit_DegenerateVector_IsNull()
    {
        Assert.Null(ComponentPatternProof.Unit(new double[] { 0d, 0d, 0d }));
        Assert.Equal(new double[] { 1d, 0d, 0d }, ComponentPatternProof.Unit(new double[] { 8d, 0d, 0d }));
    }

    private static List<double[]> RingOrigins() => new()
    {
        new double[] { 10d, 0d, 0d },
        new double[] { 0d, 10d, 0d },
        new double[] { -10d, 0d, 0d },
        new double[] { 0d, -10d, 0d },
    };

    private static bool Same(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) <= 1e-9 && Math.Abs(a[1] - b[1]) <= 1e-9 && Math.Abs(a[2] - b[2]) <= 1e-9;
}
