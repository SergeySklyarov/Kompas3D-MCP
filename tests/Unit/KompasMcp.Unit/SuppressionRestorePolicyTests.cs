using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>A restore that did not bring the model back must NOT be reported as a success. The decision is
/// a pure function of the record, the handle, the revision and the measured state, so a SUBSTITUTED read
/// result exercises every branch without a CAD session.</summary>
/// <remarks>History: docs/decisions/tests.md#suppression-restore-comparison</remarks>
public class SuppressionRestorePolicyTests
{
    private const string Ref = "feature:aaaa";
    private const long Revision = 11;

    private static SuppressionRecord Record(
        double before = 78429.20367320509d, int bodies = 1, int faces = 8,
        double suppressed = 79214.60183660254d, string featureRef = Ref, long revision = Revision,
        ModelStateSnapshot? beforeRecheck = null) =>
        new(featureRef, "Зеркальный массив:1", revision,
            new ModelStateSnapshot(before, bodies, faces),
            new ModelStateSnapshot(suppressed, bodies, faces),
            beforeRecheck);

    private static ModelStateSnapshot After(double volume = 78429.20367320509d, int bodies = 1, int faces = 8) =>
        new(volume, bodies, faces);

    [Fact]
    public void Restored_ToTheRecordedState_IsMatched()
    {
        var comparison = SuppressionRestorePolicy.Compare(Record(), Ref, Revision, After());

        Assert.Equal(RestoreVerdict.Matched, comparison.Verdict);
        Assert.True(comparison.IsMatched);
        Assert.True(comparison.IsAvailable);
        Assert.Null(comparison.Reason);
    }

    [Fact]
    public void VolumeNotReturned_IsMismatched_AndCarriesTheDelta()
    {
        // The live case: the suppressed volume stood, the restore returned the bare plate instead of the
        // state with both holes. The volume CHANGED, so "volume_changed_on_restore" passed; the
        // comparison with the recorded pre-suppression state is what catches it.
        var comparison = SuppressionRestorePolicy.Compare(Record(), Ref, Revision, After(79999.99999999999d));

        Assert.Equal(RestoreVerdict.Mismatched, comparison.Verdict);
        Assert.True(comparison.IsAvailable);
        Assert.False(comparison.IsMatched);
        Assert.NotNull(comparison.Reason);
        Assert.Contains("не вернуло", comparison.Reason!, StringComparison.Ordinal);
        Assert.NotNull(comparison.VolumeDeltaMm3);
        Assert.True(Math.Abs(comparison.VolumeDeltaMm3!.Value - 1570.7963267949d) < 1e-6);
    }

    [Fact]
    public void SameVolumeButDifferentBodyCount_IsMismatched()
    {
        var comparison = SuppressionRestorePolicy.Compare(Record(), Ref, Revision, After(bodies: 2, faces: 12));

        Assert.Equal(RestoreVerdict.Mismatched, comparison.Verdict);
        Assert.Contains("тел 1 → 2", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void SameVolumeAndBodiesButDifferentFaceCount_IsMismatched()
    {
        var comparison = SuppressionRestorePolicy.Compare(Record(), Ref, Revision, After(faces: 6));

        Assert.Equal(RestoreVerdict.Mismatched, comparison.Verdict);
        Assert.Contains("граней 8 → 6", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeWithinTheTolerance_IsMatched_NotMismatched()
    {
        // The same tolerance the declared-volume check uses: a rebuild may move the last bits.
        var comparison = SuppressionRestorePolicy.Compare(
            Record(before: 1000d, suppressed: 1200d), Ref, Revision, After(1000.0005d));

        Assert.Equal(RestoreVerdict.Matched, comparison.Verdict);
    }

    [Fact]
    public void NoSuppressionRecordedByThisSession_IsUnavailable_AndSaysSo()
    {
        var comparison = SuppressionRestorePolicy.Compare(null, Ref, Revision, After());

        Assert.Equal(RestoreVerdict.Unavailable, comparison.Verdict);
        Assert.False(comparison.IsAvailable);
        Assert.False(comparison.IsMatched);
        Assert.Contains("не выполнялось", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordForAnotherHandle_IsUnavailable_NeverMatched()
    {
        var comparison = SuppressionRestorePolicy.Compare(
            Record(featureRef: "feature:bbbb"), Ref, Revision, After());

        Assert.Equal(RestoreVerdict.Unavailable, comparison.Verdict);
        Assert.Contains("ДРУГОЙ ссылке", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelMovedBetweenSuppressAndRestore_IsUnavailable_AndNamesBothRevisions()
    {
        var comparison = SuppressionRestorePolicy.Compare(Record(), Ref, Revision + 3, After());

        Assert.Equal(RestoreVerdict.Unavailable, comparison.Verdict);
        Assert.Contains("11", comparison.Reason!, StringComparison.Ordinal);
        Assert.Contains("14", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableVolume_IsUnavailable_NeverTreatedAsZeroOrAsMatched()
    {
        var unreadable = new ModelStateSnapshot(null, -1, -1);

        Assert.False(unreadable.IsReadable);
        Assert.Equal(RestoreVerdict.Unavailable,
            SuppressionRestorePolicy.Compare(Record(), Ref, Revision, unreadable).Verdict);
        Assert.Equal(RestoreVerdict.Unavailable,
            SuppressionRestorePolicy.Compare(
                Record(before: 0d) with { Before = unreadable }, Ref, Revision, After()).Verdict);
    }

    [Fact]
    public void ZeroIsAReadableState_DistinctFromUnreadable()
    {
        // A suppressed BASE extrusion legitimately leaves no body at all: volume null, bodies 0. The
        // snapshot built from "no bodies" is readable and comparable, unlike the unread one above.
        var empty = new ModelStateSnapshot(0d, 0, 0);

        Assert.True(empty.IsReadable);
        Assert.False(empty.Matches(new ModelStateSnapshot(0d, 1, 6)));
    }

    [Fact]
    public void ABeforeReadThatDisagrees_IsUnavailable_NotARefusal()
    {
        // MEASURED: a read of the model state can lag by one operation. The state "before" is read twice;
        // when the two reads disagree the comparison must be NAMED unavailable — comparing against a
        // stale "before" would produce a FALSE refusal on a correct model (naryad PRE_RELEASE_0_6_0 П2.2).
        var record = Record(beforeRecheck: new ModelStateSnapshot(128334.30559701854d, 1, 8));

        var comparison = SuppressionRestorePolicy.Compare(record, Ref, Revision, After());

        Assert.Equal(RestoreVerdict.Unavailable, comparison.Verdict);
        Assert.False(comparison.IsMatched);
        Assert.Contains("разошлись", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void ABeforeRecheckThatAgrees_ComparesNormally()
    {
        var record = Record(beforeRecheck: new ModelStateSnapshot(78429.20367320509d, 1, 8));

        Assert.Equal(RestoreVerdict.Matched,
            SuppressionRestorePolicy.Compare(record, Ref, Revision, After()).Verdict);
        Assert.Equal(RestoreVerdict.Mismatched,
            SuppressionRestorePolicy.Compare(record, Ref, Revision, After(79999.99999999999d)).Verdict);
    }

    [Fact]
    public void ASecondAfterReadThatConfirms_IsMatched()
    {
        // The first read of "after" was stale; the repeat of the MEASUREMENT returned the recorded state.
        var comparison = SuppressionRestorePolicy.Compare(
            Record(), Ref, Revision, After(128334.30559701854d), () => After());

        Assert.Equal(RestoreVerdict.Matched, comparison.Verdict);
    }

    [Fact]
    public void TwoAfterReadsThatDisagree_AreUnavailable_NotARefusal()
    {
        var comparison = SuppressionRestorePolicy.Compare(
            Record(), Ref, Revision, After(79999.99999999999d), () => After(81000d));

        Assert.Equal(RestoreVerdict.Unavailable, comparison.Verdict);
        Assert.Contains("разошлись", comparison.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoAfterReadsThatAgreeAndBothMiss_AreARefusal()
    {
        var comparison = SuppressionRestorePolicy.Compare(
            Record(), Ref, Revision, After(79999.99999999999d), () => After(79999.99999999999d));

        Assert.Equal(RestoreVerdict.Mismatched, comparison.Verdict);
        Assert.NotNull(comparison.VolumeDeltaMm3);
    }
}
