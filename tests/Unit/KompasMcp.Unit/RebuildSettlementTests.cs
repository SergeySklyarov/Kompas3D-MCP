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
    public void Settle_StopsOnEitherFact_AndNeitherAloneIsEnough()
    {
        const double built = 132256.500735;
        const double unstable = built + 769.690216;

        // (1) The reading returned to the value the settle started from.
        Assert.True(RebuildSettlement.Reached(built, unstable, built));
        // (2) Two consecutive readings agree: a fixed point of the rebuild. MEASURED: the state read right
        // after an operation's own rebuild can itself be the unstable one, so this is the ONLY fact that
        // closes that case.
        Assert.True(RebuildSettlement.Reached(unstable, built, built));
        // Neither fact: the volume keeps moving, so the settle must continue.
        Assert.False(RebuildSettlement.Reached(built, unstable, unstable + 1d));
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

    /// <summary>The volume with the hole applied, and the volume a control rebuild returned when it took
    /// that removal away again (one Ø14 ear wall, MEASURED).</summary>
    private const double Applied = 108504.5815;

    private const double Returned = 109274.2717;

    /// <summary>A material-removing mutation's build took material away; a control rebuild that gives it
    /// back has dropped the operation's effect, so the reading may not stand as a built state - whatever
    /// the next reading agrees with it.</summary>
    [Fact]
    public void Acceptable_RefusesAReadingAboveTheLowestTheSettleSaw()
    {
        var lowest = RebuildSettlement.Lowest(Applied, Returned);

        Assert.Equal(Applied, lowest);
        // The disagreement is taken at the shared tolerance: the reading is 769.6902 mm3 higher, far
        // beyond it, so it is a lost effect and not noise.
        Assert.False(RebuildSettlement.Acceptable(SettleDirection.RemovesMaterial, lowest, Returned));
        // The state the operation's own build left IS the lowest, so it may stand.
        Assert.True(RebuildSettlement.Acceptable(SettleDirection.RemovesMaterial, lowest, Applied));
        // An unknown direction keeps the old rule: a fixed point is accepted wherever it landed.
        Assert.True(RebuildSettlement.Acceptable(SettleDirection.Any, lowest, Returned));
    }

    /// <summary>The whole rule on the measured trajectory: a control rebuild that returns the material,
    /// twice, is never a fixed point the settle may stop on.</summary>
    [Fact]
    public void HoleSettle_TwoAgreeingReadingsOnTheReturnedState_AreNotAccepted()
    {
        Assert.False(SettleStops(Applied, [Returned, Returned]));
    }

    /// <summary>The case REBUILD_SETTLE fixed stays fixed: the operation read an unstable state that was
    /// one hole wall TOO HIGH, the control rebuild brought it down to the built model, and the settle
    /// stops there on the second attempt.</summary>
    [Fact]
    public void HoleSettle_RebuildThatAppliesThePendingRemoval_IsAccepted()
    {
        Assert.True(SettleStops(110813.6521, [110043.9619, 110043.9619]));
    }

    [Fact]
    public void Lowest_KeepsTheSmallestReading_AndToleratesAnUnreadOne()
    {
        Assert.Equal(Applied, RebuildSettlement.Lowest(null, Applied));
        Assert.Equal(Applied, RebuildSettlement.Lowest(Applied, null));
        Assert.Null(RebuildSettlement.Lowest(null, null));
        Assert.Equal(Applied, RebuildSettlement.Lowest(Returned, Applied));
        Assert.Equal(Applied, RebuildSettlement.Lowest(Applied, Returned));
    }

    /// <summary>The settle loop's decision, mirrored: does it stop on any of these readings?</summary>
    private static bool SettleStops(double baseline, double[] readings)
    {
        var lowest = (double?)baseline;
        double? previous = baseline;
        foreach (var observed in readings)
        {
            lowest = RebuildSettlement.Lowest(lowest, observed);
            if (RebuildSettlement.Reached(baseline, previous, observed)
                && RebuildSettlement.Acceptable(SettleDirection.RemovesMaterial, lowest, observed))
            {
                return true;
            }

            previous = observed;
        }

        return false;
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
