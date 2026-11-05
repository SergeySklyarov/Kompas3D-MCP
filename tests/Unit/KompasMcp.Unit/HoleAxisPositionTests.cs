using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The position check of a drilled hole: the axis read from the body against the requested
/// point on the support face.</summary>
/// <remarks>WHY THE NEGATIVE CELL EXISTS AS A TEST. The instrument proves the POSITIVE side on the real
/// body (a through cone with offsets reports <c>position_applied=true</c>); a wrong point cannot be forced
/// through the product, because every mode builds the hole where it was asked. The comparison is a pure
/// rule, so the shifted-axis branch is exercised here on the same predicate.
/// History: docs/decisions/adapter-features.md#hole-conic-position</remarks>
public sealed class HoleAxisPositionTests
{
    [Fact]
    public void TheAxisThroughTheRequestedPointMatches()
    {
        Assert.True(FacePlacementMatch.AxisOriginAt(new[] { 25d, 15d, 10d }, 25d, 15d));
    }

    [Fact]
    public void AShiftedAxisDoesNotMatch()
    {
        Assert.False(FacePlacementMatch.AxisOriginAt(new[] { 25d, 15d, 10d }, 27d, 15d));
    }

    [Fact]
    public void TheBandIsThePublishedHalfMillimetre()
    {
        Assert.True(FacePlacementMatch.AxisOriginAt(new[] { 25.49d, 14.51d, 0d }, 25d, 15d));
        Assert.False(FacePlacementMatch.AxisOriginAt(new[] { 25.51d, 15d, 0d }, 25d, 15d));
    }

    [Fact]
    public void AnUnreadOriginIsNotAPosition()
    {
        Assert.False(FacePlacementMatch.AxisOriginAt(Array.Empty<double>(), 0d, 0d));
    }
}
