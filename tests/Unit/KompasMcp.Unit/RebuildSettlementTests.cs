using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Naryad REBUILD_SETTLE: a state of the BUILT model is a state a rebuild does not move, so
/// stability is established by two CONSECUTIVE readings agreeing — not by a reading returning to the
/// value it had before the first rebuild.</summary>
/// <remarks>The decision is a pure function of three numbers and a flag, so a SUBSTITUTED read result
/// exercises it — the same discipline <see cref="DeclaredVolumeMarksTests"/> follows.
/// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public class RebuildSettlementTests
{
    [Fact]
    public void Rebuild_ReturningTheSameVolume_IsSettled_AndReportsNothing()
    {
        Assert.True(RebuildSettlement.Settled(132256.500735, 132256.500735));
        Assert.Null(RebuildSettlement.Describe(1, 132256.500735, 132256.500735, stable: true));
    }

    [Fact]
    public void Rebuild_ReturningASlightlyDifferentVolume_WithinTolerance_IsSettled()
    {
        // The tolerance is the shared declared-expectation one, so a last-digit difference is not a move.
        Assert.True(RebuildSettlement.Settled(132256.500735, 132256.500736));
    }

    [Fact]
    public void Rebuild_MovingTheVolumeByOneHoleWall_IsNotSettled()
    {
        // MEASURED in the scenario: one rebuild left one of the four Ø14 ear holes unapplied, and the
        // document answered exactly the wall of that hole more (π·7²·5 = 769.6902 mm³).
        const double moved = 132256.500735 + 769.690216;

        Assert.False(RebuildSettlement.Settled(132256.500735, moved));
    }

    [Fact]
    public void UnreadVolume_IsNotSettled_AndIsNamedAsNotCompared()
    {
        // "Not read" is not "unchanged": an unperformed comparison is never a passed one.
        Assert.False(RebuildSettlement.Settled(null, 132256.500735));
        Assert.False(RebuildSettlement.Settled(132256.500735, null));
        Assert.Equal(RebuildSettlement.VolumeNotCompared,
            RebuildSettlement.Describe(1, null, 132256.500735, stable: false));
        Assert.Equal(RebuildSettlement.VolumeNotCompared,
            RebuildSettlement.Describe(3, 132256.500735, null, stable: true));
    }

    [Fact]
    public void FirstControlRebuild_MovingTheVolumeButSettlingAfter_IsNamedWithTheNumbers()
    {
        // MEASURED: the state an operation reads right after its OWN rebuild can itself be the unstable
        // one (769.6902 mm³ above the built model). The control rebuild corrects it, so the mark is
        // "the first state was unstable", NOT "the model moved and never came back".
        var note = RebuildSettlement.Describe(2, 110813.6521, 110043.9619, stable: true);

        Assert.NotNull(note);
        Assert.StartsWith(RebuildSettlement.Repeated, note);
        Assert.Contains("110813.6521", note);
        Assert.Contains("110043.9619", note);
        Assert.Contains("перестроений 2", note);
    }

    [Fact]
    public void TwoReadingsNeverAgreeing_IsNamedAsAMovedModel()
    {
        // Stability NOT established: nothing may then be claimed about the geometry.
        var note = RebuildSettlement.Describe(3, 132256.500735, 133026.190951, stable: false);

        Assert.NotNull(note);
        Assert.StartsWith(RebuildSettlement.ChangedModel, note);
        Assert.Contains("133026.191", note);
        Assert.Contains("перестроений 3", note);
    }

    [Fact]
    public void SettledOnTheSecondAttemptAtTheSameValue_IsStillNamed()
    {
        // The volume came back to where it started, but only after a repeat: the first state was unstable.
        var note = RebuildSettlement.Describe(2, 132256.500735, 132256.500735, stable: true);

        Assert.NotNull(note);
        Assert.StartsWith(RebuildSettlement.Repeated, note);
    }

    [Fact]
    public void Outcome_WithUnestablishedStability_CapsTheLevelAtStructureChecked()
    {
        var outcome = new RebuildSettlementOutcome(3, 132256.500735, 133026.190951, Stable: false);

        Assert.False(outcome.Settled);
        Assert.True(outcome.StabilityUnestablished);
        Assert.Equal(
            VerificationLevel.StructureChecked,
            outcome.CapLevel(VerificationLevel.GeometryChecked));
    }

    [Fact]
    public void Outcome_SettledAfterARepeat_KeepsTheLevel_AndIsNamed()
    {
        var outcome = new RebuildSettlementOutcome(2, 110813.6521, 110043.9619, Stable: true);

        Assert.True(outcome.Settled);
        Assert.False(outcome.StabilityUnestablished);
        Assert.Equal(VerificationLevel.GeometryChecked, outcome.CapLevel(VerificationLevel.GeometryChecked));
        Assert.StartsWith(RebuildSettlement.Repeated, outcome.Note);
    }

    [Fact]
    public void Outcome_WithAnUnreadVolume_IsNotUnestablished_AndTheGapIsNamed()
    {
        // "Not compared" is a named gap, not a model whose stability was measured and failed: capping the
        // level here would turn a missing read into a claim about the geometry that nobody made.
        var outcome = new RebuildSettlementOutcome(1, null, 132256.500735, Stable: false);

        Assert.False(outcome.Settled);
        Assert.False(outcome.StabilityUnestablished);
        Assert.Equal(VerificationLevel.GeometryChecked, outcome.CapLevel(VerificationLevel.GeometryChecked));
        Assert.Equal(RebuildSettlement.VolumeNotCompared, outcome.Note);
    }

    [Fact]
    public void CapLevel_NeverRaisesALowerLevel()
    {
        Assert.Equal(
            VerificationLevel.CallReturned,
            RebuildSettlement.CapLevel(VerificationLevel.CallReturned, stabilityUnestablished: true));
        Assert.Equal(
            VerificationLevel.GeometryChecked,
            RebuildSettlement.CapLevel(VerificationLevel.GeometryChecked, stabilityUnestablished: false));
    }

    [Fact]
    public void TheFourMarksAreDistinct_SoAClientCanTellThemApart()
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
}
