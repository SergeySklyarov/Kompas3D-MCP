namespace KompasMcp.Domain.Geometry;

/// <summary>The part of a model's geometry that a suppression can be checked against: the total volume of
/// the solid bodies, how many bodies there are and how many faces they carry.</summary>
/// <remarks>INVARIANT: an UNREAD quantity stays <c>null</c>/negative and is never substituted by zero —
/// "not read" and "zero" are different facts, and a zero would make an unreadable model look empty.
/// LIMIT: a difference that preserves ALL THREE numbers (a mirrored body replaced by its source) is
/// invisible here; the caller's own bounding-box check catches that class.
/// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
public readonly record struct ModelStateSnapshot(double? VolumeMm3, int BodyCount, int FaceCount)
{
    /// <summary>Nothing could be read: the comparison is then unavailable, not "matched".</summary>
    public static ModelStateSnapshot Unreadable => new(null, -1, -1);

    public bool IsReadable => VolumeMm3 is not null && BodyCount >= 0 && FaceCount >= 0;

    /// <summary>Both sides readable and all three numbers equal within the volume tolerance.</summary>
    public bool Matches(ModelStateSnapshot other) =>
        IsReadable && other.IsReadable
        && Math.Abs(other.VolumeMm3!.Value - VolumeMm3!.Value) <= ProfileArea.Tolerance(VolumeMm3!.Value)
        && other.BodyCount == BodyCount
        && other.FaceCount == FaceCount;
}

/// <summary>What THIS session recorded when it suppressed a feature: the handle, the revision the
/// suppression left behind, and both states — before the write and after it.</summary>
/// <remarks>WHY THE REVISION IS PART OF THE RECORD: the comparison is meaningful only while the model has
/// not moved — any other mutation rebuilds it, and "state before" is then about an older model. WHY
/// <see cref="BeforeRecheck"/>: MEASURED that a state read can lag by one operation, so "before" is read
/// twice and a DISAGREEMENT makes the comparison unavailable rather than a false refusal.
/// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
public sealed record SuppressionRecord(
    string FeatureRef,
    string FeatureName,
    long RevisionAfterSuppress,
    ModelStateSnapshot Before,
    ModelStateSnapshot Suppressed,
    ModelStateSnapshot? BeforeRecheck = null);

public enum RestoreVerdict
{
    /// <summary>The restore returned the model to the recorded pre-suppression state.</summary>
    Matched,

    /// <summary>The comparison ran and the model did NOT come back: the caller must not be told "success".</summary>
    Mismatched,

    /// <summary>Nothing to compare against — named in <see cref="RestoreComparison.Reason"/>.</summary>
    Unavailable,
}

public sealed record RestoreComparison(RestoreVerdict Verdict, string? Reason, double? VolumeDeltaMm3)
{
    public bool IsMatched => Verdict == RestoreVerdict.Matched;

    public bool IsAvailable => Verdict != RestoreVerdict.Unavailable;
}

/// <summary>The rule that decides whether removing a suppression may be reported as a success.</summary>
/// <remarks>INVARIANT: a restore is compared with the state the SAME session recorded before applying
/// the suppression. A mismatch is a REFUSAL, never a lowered level: the client asked for the suppression
/// to be removed and got a different model, and "the volume changed" is true of a wrong model too. An
/// unavailable comparison is NAMED, not skipped — "we could not check" and "we checked and it matched"
/// must not read the same. INVARIANT: the decision is a pure function of the record, the handle, the
/// revision and the measured state, so a SUBSTITUTED read result exercises every branch without a CAD
/// session. History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
public static class SuppressionRestorePolicy
{
    public static RestoreComparison Compare(
        SuppressionRecord? record, string featureRef, long revisionBeforeRestore, ModelStateSnapshot after)
    {
        if (record is null)
        {
            return Unavailable("подавление этим сеансом не выполнялось: состояние «до подавления» "
                + "не наблюдалось, сверять снятие не с чем. Подавление, выполненное вне этого сеанса "
                + "(другим клиентом, вручную в КОМПАС или прошлым процессом сервера), в память сеанса "
                + "не попадает.");
        }

        if (!string.Equals(record.FeatureRef, featureRef, StringComparison.Ordinal))
        {
            return Unavailable($"последнее подавление этого сеанса относится к ДРУГОЙ ссылке "
                + $"({record.FeatureName}), а не к '{featureRef}': сверять снятие с чужим состоянием "
                + "запрещено — совпавшие числа ничего не доказывали бы.");
        }

        if (record.RevisionAfterSuppress != revisionBeforeRestore)
        {
            return Unavailable($"модель изменилась между подавлением и снятием: после подавления ревизия "
                + $"была {record.RevisionAfterSuppress}, перед снятием {revisionBeforeRestore}. "
                + "Запомненное состояние относится к прежней модели.");
        }

        if (!record.Before.IsReadable || !after.IsReadable)
        {
            return Unavailable("состояние модели прочитано не полностью: "
                + (record.Before.IsReadable ? string.Empty : "«до подавления» ")
                + (after.IsReadable ? string.Empty : "«после снятия» ")
                + "— сравнение чисел невозможно.");
        }

        // The "before" state was read TWICE. Two reads that disagree mean the measurement is not
        // repeatable, so comparing against either of them could produce a FALSE refusal — the comparison
        // is named unavailable instead (naryad PRE_RELEASE_0_6_0 П2.2).
        if (record.BeforeRecheck is { } recheck && !record.Before.Matches(recheck))
        {
            return Unavailable("состояние «до подавления» прочитано ДВАЖДЫ и чтения разошлись: "
                + Describe(record.Before) + " и " + Describe(recheck)
                + ". Сверка с недостоверным «до» дала бы ложный отказ, поэтому сравнение названо "
                + "недоступным.");
        }

        var delta = after.VolumeMm3!.Value - record.Before.VolumeMm3!.Value;
        if (record.Before.Matches(after))
        {
            return new RestoreComparison(RestoreVerdict.Matched, null, delta);
        }

        var reasons = new List<string>();
        if (Math.Abs(delta) > ProfileArea.Tolerance(record.Before.VolumeMm3!.Value))
        {
            reasons.Add($"объём {record.Before.VolumeMm3!.Value:0.######} → {after.VolumeMm3!.Value:0.######} "
                + $"(расхождение {delta:+0.######;-0.######})");
        }

        if (after.BodyCount != record.Before.BodyCount)
        {
            reasons.Add($"тел {record.Before.BodyCount} → {after.BodyCount}");
        }

        if (after.FaceCount != record.Before.FaceCount)
        {
            reasons.Add($"граней {record.Before.FaceCount} → {after.FaceCount}");
        }

        return new RestoreComparison(
            RestoreVerdict.Mismatched,
            "снятие подавления не вернуло модель к состоянию ДО подавления: " + string.Join("; ", reasons),
            delta);
    }

    /// <summary>Compare the restore with the recorded state, RE-READING the after-state once when the first
    /// comparison says Mismatched.</summary>
    /// <remarks>WHY A SECOND READ. MEASURED: the model state sometimes reads one operation behind, so a
    /// single read of "after" can differ from the record while the model is in fact correct. INVARIANT: a
    /// refusal needs BOTH after-reads to be readable and to AGREE with each other; two reads that DISAGREE
    /// are a NAMED gap, not a refusal — an unrepeatable measurement must not become a false refusal.
    /// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
    public static RestoreComparison Compare(
        SuppressionRecord? record, string featureRef, long revisionBeforeRestore,
        ModelStateSnapshot after, Func<ModelStateSnapshot>? rereadAfter)
    {
        var first = Compare(record, featureRef, revisionBeforeRestore, after);
        if (rereadAfter is null || first.Verdict != RestoreVerdict.Mismatched)
        {
            return first;
        }

        var afterAgain = rereadAfter();
        var second = Compare(record, featureRef, revisionBeforeRestore, afterAgain);
        if (second.Verdict != RestoreVerdict.Mismatched)
        {
            // Matched on the second read, or the comparison itself became unavailable: take it.
            return second;
        }

        if (after.IsReadable && afterAgain.IsReadable && after.Matches(afterAgain))
        {
            // Both reads readable and in agreement, and neither returned to the record: a genuine refusal.
            return first;
        }

        return Unavailable("два чтения состояния ПОСЛЕ снятия разошлись: " + Describe(after) + " и "
            + Describe(afterAgain) + ". Отказ НЕ выдаётся: расхождение означает, что измерение "
            + "неповторяемо (устаревшее чтение), а не что модель не вернулась.");
    }

    private static string Describe(ModelStateSnapshot state) =>
        state.IsReadable
            ? $"объём {state.VolumeMm3!.Value:0.######}, тел {state.BodyCount}, граней {state.FaceCount}"
            : "состояние не прочитано";

    private static RestoreComparison Unavailable(string reason) =>
        new(RestoreVerdict.Unavailable, reason, null);
}
