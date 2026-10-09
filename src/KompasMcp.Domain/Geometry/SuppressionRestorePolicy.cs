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

/// <summary>What THIS session recorded when it suppressed a feature: the handle the caller used, the
/// revision the suppression left behind, and both states — before the write and after it.</summary>
/// <remarks>WHY THE REVISION IS PART OF THE RECORD. The comparison is only meaningful while the model has
/// not moved between the two calls: any other mutation rebuilds the model and the "state before" is then a
/// statement about an older model. The revision is the session's own counter, so "no mutation happened
/// since" is decided from a number the server owns, not from a guess.
/// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
public sealed record SuppressionRecord(
    string FeatureRef,
    string FeatureName,
    long RevisionAfterSuppress,
    ModelStateSnapshot Before,
    ModelStateSnapshot Suppressed);

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
/// <remarks>INVARIANT: a restore is compared with the state the SAME session recorded before it applied the
/// suppression. A mismatch is a REFUSAL, never a lowered verification level: the client asked for the
/// suppression to be removed and got a model that is not the one it started from, and "the volume changed"
/// is true of that wrong model too. An unavailable comparison is NAMED, not silently skipped — "we could
/// not check" and "we checked and it matched" must not read the same.
/// INVARIANT: the decision is a pure function of the record, the handle, the revision and the measured
/// state, so a SUBSTITUTED read result exercises every branch without a CAD session.
/// History: docs/decisions/adapter-core.md#suppression-restore-comparison</remarks>
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

    private static RestoreComparison Unavailable(string reason) =>
        new(RestoreVerdict.Unavailable, reason, null);
}
