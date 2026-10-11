using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The rule that ties a rebuild's before/after volumes to the mark the caller sees.</summary>
/// <remarks>INVARIANT: a rebuild recomputes the model that is ALREADY built, so the document volume must
/// not move; a move means a feature was not applied, and it is named where the client reads what was not
/// confirmed. MEASURED: one <c>RebuildDocument()</c> can leave a feature unapplied, and the NEXT rebuild
/// restores the model, so the number of attempts is part of the answer rather than an implementation
/// detail. The decision is a pure function of two numbers and a count, so a SUBSTITUTED read result
/// exercises it. History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public static class RebuildSettlement
{
    /// <summary>The volumes did not come back, so "the rebuild changed nothing" was not measured.</summary>
    public const string VolumeNotCompared =
        "rebuild_volume_not_compared — объём документа до или после перестроения не прочитан, поэтому "
        + "«перестроение не изменило модель» не проверено числом";

    /// <summary>The model returned to its own volume, but only on a later attempt.</summary>
    public const string Repeated =
        "rebuild_repeated — первое перестроение изменило объём документа, модель вернулась к исходному "
        + "объёму только после повторных перестроений";

    /// <summary>The model never came back: the rebuild itself moved the geometry.</summary>
    public const string ChangedModel =
        "rebuild_changed_model — перестроение изменило объём документа и не вернуло его за отведённые "
        + "попытки. Перестроение обязано пересчитывать УЖЕ построенное";

    /// <summary>The rebuild that was supposed to settle a just-built model did not run, so the state of
    /// that model was never compared. A separate name from the three above: there the rebuild happened and
    /// the volumes disagreed, here it never happened.</summary>
    public const string NotChecked =
        "rebuild_not_checked — контрольное перестроение не выполнено: вызов прервался, устойчивость "
        + "состояния модели НЕ проверена. Перечитайте объём и при расхождении вызовите kompas_rebuild";

    /// <summary>The warning a result carries when the model never came back: a success on such a state is
    /// not a geometry confirmation.</summary>
    public const string ChangedModelWarning =
        "Перестроение не вернуло объём документа за отведённые попытки: модель не пришла к устойчивому "
        + "состоянию, поэтому геометрия НЕ подтверждена. Перечитайте объём и повторите перестроение.";

    /// <summary>Whether the volume after the rebuild is the volume before it, within the shared tolerance.
    /// An unreadable side is NOT settled: an unperformed comparison is not a passed one.</summary>
    public static bool Settled(double? baselineMm3, double? observedMm3) =>
        baselineMm3 is double baseline && observedMm3 is double observed
        && Math.Abs(observed - baseline) <= DeclaredExpectation.Tolerance(baseline);

    /// <summary>The mark for the caller's unverified aspects, or null when there is nothing to report:
    /// a rebuild that settled on its first attempt is exactly what the contract promises.</summary>
    public static string? Note(double? baselineMm3, double? observedMm3, int attempts)
    {
        if (baselineMm3 is not double || observedMm3 is not double)
        {
            return VolumeNotCompared;
        }

        if (!Settled(baselineMm3, observedMm3))
        {
            return ChangedModel;
        }

        return attempts > 1 ? Repeated : null;
    }

    /// <summary>The mark WITH the numbers, so every call site of the automatic settlement answers in the
    /// same words. Null when the first rebuild settled: a rebuild that recomputed what was already built
    /// is exactly what the contract promises, and a note would be a false alarm.</summary>
    /// <remarks>WHY THE NUMBERS ARE PART OF THE RULE. "The rebuild moved the model" is only actionable
    /// with the two volumes and the attempt count; formatting them at each call site made the same
    /// measurement read differently in different tools.
    /// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
    public static string? Describe(int attempts, double? baselineMm3, double? observedMm3)
    {
        if (baselineMm3 is not double baseline || observedMm3 is not double observed)
        {
            return VolumeNotCompared;
        }

        if (!Settled(baselineMm3, observedMm3))
        {
            return ChangedModel + Numbers(baseline, observed, attempts);
        }

        return attempts > 1 ? Repeated + Numbers(baseline, observed, attempts) : null;
    }

    /// <summary>Lower a level when the rebuild did not settle: a state of a model that is not built is
    /// never a geometry confirmation. A level already at or below <c>structure_checked</c> is returned
    /// as is — the rule only ever lowers.</summary>
    public static VerificationLevel CapLevel(VerificationLevel level, bool modelMoved) =>
        modelMoved && level > VerificationLevel.StructureChecked
            ? VerificationLevel.StructureChecked
            : level;

    private static string Numbers(double baselineMm3, double observedMm3, int attempts)
    {
        var fmt = System.Globalization.CultureInfo.InvariantCulture;
        return " (было " + baselineMm3.ToString("0.####", fmt)
            + ", стало " + observedMm3.ToString("0.####", fmt)
            + ", перестроений " + attempts.ToString(fmt) + ")";
    }
}

/// <summary>What one settled rebuild measured: the two volumes and the number of attempts it took.</summary>
/// <remarks>INVARIANT: the two volumes are the whole evidence, and an unreadable side is NOT a settled
/// one. The record carries no COM type, so the decision it feeds is exercised by substituted numbers.
/// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public readonly record struct RebuildSettlementOutcome(
    int Attempts,
    double? BaselineMm3,
    double? ObservedMm3)
{
    /// <summary>The volumes came back within the shared tolerance: the state is a state of the built model.</summary>
    public bool Settled => RebuildSettlement.Settled(BaselineMm3, ObservedMm3);

    /// <summary>Both volumes were read and did NOT come back: the model never reached a stable state, so
    /// no result check may claim geometry. An unreadable side is NOT "moved" — nothing was compared.</summary>
    public bool ModelMoved => !Settled && BaselineMm3 is not null && ObservedMm3 is not null;

    /// <summary>The mark for the caller's unverified aspects, or null when the first rebuild settled.</summary>
    public string? Note => RebuildSettlement.Describe(Attempts, BaselineMm3, ObservedMm3);

    /// <summary>The same rule applied to a level the caller already computed.</summary>
    public VerificationLevel CapLevel(VerificationLevel level) =>
        RebuildSettlement.CapLevel(level, ModelMoved);
}
