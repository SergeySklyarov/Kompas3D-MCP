using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Naryad MANIA_FLAKES: a rebuild recomputes the model that is already built, so the document
/// volume must not move; a move is named in the unverified aspects together with the attempt count.</summary>
/// <remarks>The decision is a pure function of two volumes and a count, so a SUBSTITUTED read result
/// exercises it — the same discipline <see cref="DeclaredVolumeMarksTests"/> follows.
/// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public class RebuildSettlementTests
{
    [Fact]
    public void Rebuild_ReturningTheSameVolume_IsSettled_AndReportsNothing()
    {
        Assert.True(RebuildSettlement.Settled(132256.500735, 132256.500735));
        Assert.Null(RebuildSettlement.Note(132256.500735, 132256.500735, 1));
    }

    [Fact]
    public void Rebuild_ReturningASlightlyDifferentVolume_WithinTolerance_IsSettled()
    {
        // The tolerance is the shared declared-expectation one, so a last-digit difference is not a move.
        Assert.True(RebuildSettlement.Settled(132256.500735, 132256.500736));
    }

    [Fact]
    public void Rebuild_MovingTheVolumeByOneHoleWall_IsNotSettled_AndIsNamed()
    {
        // MEASURED in the scenario: one rebuild left one of the four Ø14 ear holes unapplied, and the
        // document answered exactly the wall of that hole more (π·7²·5 = 769.6902 mm³).
        const double moved = 132256.500735 + 769.690216;

        Assert.False(RebuildSettlement.Settled(132256.500735, moved));
        Assert.Equal(RebuildSettlement.ChangedModel, RebuildSettlement.Note(132256.500735, moved, 3));
    }

    [Fact]
    public void Rebuild_RestoredOnASecondAttempt_IsSettled_AndTheRepetitionIsNamed()
    {
        Assert.True(RebuildSettlement.Settled(132256.500735, 132256.500735));
        var note = RebuildSettlement.Note(132256.500735, 132256.500735, 2);

        Assert.Equal(RebuildSettlement.Repeated, note);
    }

    [Fact]
    public void UnreadVolume_IsNotSettled_AndIsNamedAsNotCompared()
    {
        // "Not read" is not "unchanged": an unperformed comparison is never a passed one.
        Assert.False(RebuildSettlement.Settled(null, 132256.500735));
        Assert.False(RebuildSettlement.Settled(132256.500735, null));
        Assert.Equal(RebuildSettlement.VolumeNotCompared,
            RebuildSettlement.Note(null, 132256.500735, 1));
        Assert.Equal(RebuildSettlement.VolumeNotCompared,
            RebuildSettlement.Note(132256.500735, null, 3));
    }

    [Fact]
    public void TheThreeMarksAreDistinct_SoAClientCanTellThemApart()
    {
        Assert.NotEqual(RebuildSettlement.Repeated, RebuildSettlement.ChangedModel);
        Assert.NotEqual(RebuildSettlement.Repeated, RebuildSettlement.VolumeNotCompared);
        Assert.NotEqual(RebuildSettlement.ChangedModel, RebuildSettlement.VolumeNotCompared);
    }
}
