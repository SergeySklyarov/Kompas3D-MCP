using KompasMcp.Contracts;
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
        // A fourth mark for a FOURTH state: the control rebuild never ran. It must not read as any of the
        // three that describe a rebuild that DID run.
        Assert.NotEqual(RebuildSettlement.NotChecked, RebuildSettlement.Repeated);
        Assert.NotEqual(RebuildSettlement.NotChecked, RebuildSettlement.ChangedModel);
        Assert.NotEqual(RebuildSettlement.NotChecked, RebuildSettlement.VolumeNotCompared);
    }

    [Fact]
    public void Describe_CarriesTheTwoVolumesAndTheAttemptCount()
    {
        // The mark is only actionable with the numbers: "the rebuild moved the model" alone leaves the
        // client unable to tell a hole wall from a rounding difference.
        var repeated = RebuildSettlement.Describe(2, 132256.500735, 132256.500735);
        var moved = RebuildSettlement.Describe(3, 132256.500735, 133026.190951);

        Assert.NotNull(repeated);
        Assert.StartsWith(RebuildSettlement.Repeated, repeated);
        Assert.Contains("перестроений 2", repeated);
        Assert.NotNull(moved);
        Assert.StartsWith(RebuildSettlement.ChangedModel, moved);
        Assert.Contains("133026.191", moved);
        Assert.Contains("перестроений 3", moved);
    }

    [Fact]
    public void Describe_OnAFirstAttemptSettle_SaysNothing()
    {
        // The before-state of the NEXT operation is the after-state of this one: a settled operation
        // must leave no mark, else every following operation would inherit a false alarm.
        Assert.Null(RebuildSettlement.Describe(1, 132256.500735, 132256.500735));
    }

    [Fact]
    public void Outcome_MovingTheModel_CapsTheLevelAtStructureChecked()
    {
        var outcome = new RebuildSettlementOutcome(3, 132256.500735, 133026.190951);

        Assert.True(outcome.ModelMoved);
        Assert.False(outcome.Settled);
        Assert.Equal(
            VerificationLevel.StructureChecked,
            outcome.CapLevel(VerificationLevel.GeometryChecked));
    }

    [Fact]
    public void Outcome_SettledOnTheSecondAttempt_KeepsTheLevel_AndIsNamed()
    {
        var outcome = new RebuildSettlementOutcome(2, 132256.500735, 132256.500735);

        Assert.True(outcome.Settled);
        Assert.False(outcome.ModelMoved);
        Assert.Equal(VerificationLevel.GeometryChecked, outcome.CapLevel(VerificationLevel.GeometryChecked));
        Assert.StartsWith(RebuildSettlement.Repeated, outcome.Note);
    }

    [Fact]
    public void Outcome_WithAnUnreadVolume_IsNotMoved_AndTheGapIsNamed()
    {
        // "Not compared" is a named gap, not a moved model: capping the level here would turn a missing
        // read into a claim about the geometry that nobody made.
        var outcome = new RebuildSettlementOutcome(1, null, 132256.500735);

        Assert.False(outcome.Settled);
        Assert.False(outcome.ModelMoved);
        Assert.Equal(VerificationLevel.GeometryChecked, outcome.CapLevel(VerificationLevel.GeometryChecked));
        Assert.Equal(RebuildSettlement.VolumeNotCompared, outcome.Note);
    }

    [Fact]
    public void CapLevel_NeverRaisesALowerLevel()
    {
        Assert.Equal(
            VerificationLevel.CallReturned,
            RebuildSettlement.CapLevel(VerificationLevel.CallReturned, modelMoved: true));
        Assert.Equal(
            VerificationLevel.GeometryChecked,
            RebuildSettlement.CapLevel(VerificationLevel.GeometryChecked, modelMoved: false));
    }
}
