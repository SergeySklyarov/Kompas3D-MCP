using KompasMcp.Domain.Imaging;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Computing <c>extScale</c> from a probe render: the pure table.</summary>
/// <remarks>INVARIANT: an unread or non-positive probe long side is a REFUSAL with a named reason, never a
/// division. INVARIANT: the computed scale is clamped to the range the kernel MEASURABLY accepts, and a
/// clamp is named rather than silent.
/// History: docs/decisions/contracts.md#raster-sizing</remarks>
public class RasterSizingTests
{
    [Fact]
    public void ProbeLongSide_ComputesTheMultiplier()
    {
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 1024, probeLongSidePx: 512);

        Assert.Equal(2.0, outcome.Scale!.Value, 6);
        Assert.Null(outcome.RefusalReason);
        Assert.False(outcome.Clamped);
    }

    [Fact]
    public void ProbeLongSide_LargerThanTarget_Shrinks()
    {
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 1024, probeLongSidePx: 4848);

        Assert.Equal(1024d / 4848d, outcome.Scale!.Value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnreadOrNonPositiveProbe_IsRefusedWithAReason(int? probe)
    {
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 1024, probeLongSidePx: probe);

        Assert.Null(outcome.Scale);
        Assert.Contains("probe_long_side_unread", outcome.RefusalReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleAboveTheMeasuredLimit_IsClampedAndNamed()
    {
        // A probe of 1 px would ask for 1024; the kernel measurably fails above 10.
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 1024, probeLongSidePx: 1);

        Assert.Equal(RasterLimits.MaxAutoScale, outcome.Scale!.Value, 6);
        Assert.True(outcome.Clamped);
        Assert.Contains("scale_clamped_to_measured_limit", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleBelowTheMeasuredLimit_IsClampedAndNamed()
    {
        // A huge probe would ask for a sub-pixel multiplier.
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 16, probeLongSidePx: 1_000_000);

        Assert.Equal(RasterLimits.MinAutoScale, outcome.Scale!.Value, 9);
        Assert.True(outcome.Clamped);
        Assert.NotNull(outcome.Note);
    }

    [Fact]
    public void ExactFit_IsNotClampedAndHasNoNote()
    {
        var outcome = RasterSizing.FromProbe(targetLongSidePx: 1024, probeLongSidePx: 1024);

        Assert.Equal(1.0, outcome.Scale!.Value, 6);
        Assert.False(outcome.Clamped);
        Assert.Null(outcome.Note);
    }
}
