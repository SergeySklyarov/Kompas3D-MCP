using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Card OBS-014: a declared volume expectation that did not hold must be named in the unverified
/// aspects, not left as a lone failed entry among the checks.</summary>
/// <remarks>The decision is a pure function of the declared and the read-back numbers, so a SUBSTITUTED
/// read result exercises it — the same discipline <see cref="PatternReadBackMarks"/> follows.
/// History: docs/decisions/tests.md#declared-volume-mark</remarks>
public class DeclaredVolumeMarksTests
{
    private const double Tolerance = 0.01d;

    [Fact]
    public void DeclaredVolume_Matching_IsNotMarked()
    {
        var unverified = new List<string>();

        DeclaredVolumeMarks.MarkVolumeNotConfirmed(unverified, 36000d, 36000.005d, Tolerance);

        Assert.Empty(unverified);
    }

    [Fact]
    public void DeclaredVolume_Contradicted_IsMarked()
    {
        var unverified = new List<string>();

        DeclaredVolumeMarks.MarkVolumeNotConfirmed(unverified, 36000d, 37000d, Tolerance);

        Assert.Single(unverified);
        Assert.Equal(DeclaredVolumeMarks.VolumeNotConfirmed, unverified[0]);
        Assert.StartsWith("document_volume_not_confirmed", unverified[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadVolume_WithDeclaredExpectation_IsMarked_NotTreatedAsZero()
    {
        var unverified = new List<string>();

        DeclaredVolumeMarks.MarkVolumeNotConfirmed(unverified, 36000d, null, Tolerance);

        Assert.Single(unverified);
    }

    [Fact]
    public void NoDeclaredExpectation_IsNotAMismatch()
    {
        var unverified = new List<string>();

        DeclaredVolumeMarks.MarkVolumeNotConfirmed(unverified, null, 37000d, Tolerance);
        DeclaredVolumeMarks.MarkVolumeNotConfirmed(unverified, null, null, Tolerance);

        Assert.Empty(unverified);
        Assert.False(DeclaredVolumeMarks.Mismatched(null, 0d, Tolerance));
    }

    [Fact]
    public void Mismatch_RespectsTheTolerance()
    {
        Assert.False(DeclaredVolumeMarks.Mismatched(1000d, 1000.009d, Tolerance));
        Assert.True(DeclaredVolumeMarks.Mismatched(1000d, 1000.02d, Tolerance));
    }
}
