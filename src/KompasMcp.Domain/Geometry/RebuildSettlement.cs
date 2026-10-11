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
}
