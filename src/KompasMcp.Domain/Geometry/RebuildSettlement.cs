using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The rule that ties the volumes a rebuild was repeated over to the mark the caller sees.</summary>
/// <remarks>INVARIANT: a state of the BUILT model is a state a rebuild does not move, so stability is
/// established by two CONSECUTIVE readings agreeing — not by a reading returning to the value it had
/// before the first rebuild. MEASURED: the state read right after an operation's own rebuild can itself be
/// the unstable one (the volume was then 769.6902 mm3 above the built model), so a rule that compares
/// every later reading against THAT value never settles and reports a move that did not happen.
/// The decision is a pure function of three numbers and a flag, so a SUBSTITUTED read result exercises
/// it. History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public static class RebuildSettlement
{
    /// <summary>A volume was not read, so stability was not measured. "Not compared" is not "stable".</summary>
    public const string VolumeNotCompared =
        "rebuild_volume_not_compared — объём документа не прочитан, поэтому устойчивость состояния "
        + "модели не проверена числом";

    /// <summary>The first state was not stable: the model reached a stable state only after repeats.</summary>
    public const string Repeated =
        "rebuild_repeated — первое состояние модели оказалось неустойчивым (объём сдвинулся), и к "
        + "устойчивому состоянию модель пришла только после повторных перестроений";

    /// <summary>Two consecutive readings never agreed: stability was NOT established.</summary>
    public const string ChangedModel =
        "rebuild_changed_model — устойчивое состояние модели не установлено за отведённые попытки: "
        + "каждое перестроение меняло объём документа. Перестроение обязано пересчитывать УЖЕ построенное";

    /// <summary>The rebuild that was supposed to settle a just-built model did not run, so the state of
    /// that model was never compared. A separate name from the three above: there the rebuild happened and
    /// the volumes disagreed, here it never happened.</summary>
    public const string NotChecked =
        "rebuild_not_checked — контрольное перестроение не выполнено: вызов прервался, устойчивость "
        + "состояния модели НЕ проверена. Перечитайте объём и при расхождении вызовите kompas_rebuild";

    /// <summary>The warning a result carries when stability was not established: a success on such a state
    /// is not a geometry confirmation.</summary>
    public const string ChangedModelWarning =
        "Устойчивое состояние модели не установлено за отведённые попытки перестроения, поэтому геометрия "
        + "НЕ подтверждена. Перечитайте объём и повторите перестроение.";

    /// <summary>Whether two readings agree within the shared tolerance. The settle loop feeds it
    /// CONSECUTIVE readings, because that is what "a rebuild does not move the model" means.
    /// An unreadable side is NOT agreement: an unperformed comparison is not a passed one.</summary>
    public static bool Settled(double? baselineMm3, double? observedMm3) =>
        baselineMm3 is double baseline && observedMm3 is double observed
        && Math.Abs(observed - baseline) <= DeclaredExpectation.Tolerance(baseline);

    /// <summary>The mark WITH the numbers, so every call site answers in the same words. Null when the
    /// FIRST control rebuild did not move the model: that is exactly what the contract promises, and a
    /// note would be a false alarm.</summary>
    /// <param name="stable">Whether two consecutive readings agreed. A state that never stabilised is
    /// named as a moved model whatever the two numbers are.</param>
    /// <remarks>WHY THE NUMBERS ARE PART OF THE RULE. "The rebuild moved the model" is only actionable
    /// with the two volumes and the attempt count; formatting them at each call site made the same
    /// measurement read differently in different tools. History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
    public static string? Describe(int attempts, double? baselineMm3, double? observedMm3, bool stable)
    {
        if (baselineMm3 is not double baseline || observedMm3 is not double observed)
        {
            return VolumeNotCompared;
        }

        if (!stable)
        {
            return ChangedModel + Numbers(baseline, observed, attempts);
        }

        // Stable now. Anything other than "the very first control rebuild left it alone" is a state that
        // had to be corrected, and the client must be able to see that it was.
        return attempts > 1 || !Settled(baseline, observed)
            ? Repeated + Numbers(baseline, observed, attempts)
            : null;
    }

    /// <summary>Lower a level when stability was NOT established: a state of a model that is not built is
    /// never a geometry confirmation. A level already at or below <c>structure_checked</c> is returned
    /// as is — the rule only ever lowers. A state whose volume could not be READ is not "moved": nothing
    /// was compared, and that gap is named instead (see <see cref="VolumeNotCompared"/>).</summary>
    public static VerificationLevel CapLevel(VerificationLevel level, bool stabilityUnestablished) =>
        stabilityUnestablished && level > VerificationLevel.StructureChecked
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

/// <summary>What one settle measured: the state before the control rebuilds, the last state, how many
/// control rebuilds it took, and whether stability was established.</summary>
/// <remarks>INVARIANT: an unreadable side is NOT a stable one, and two readings that never agreed are not
/// a move of the model either — they are the ABSENCE of a stable state, and that is what the mark says.
/// The record carries no COM type, so the decision it feeds is exercised by substituted numbers.
/// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public readonly record struct RebuildSettlementOutcome(
    int Attempts,
    double? BaselineMm3,
    double? ObservedMm3,
    bool Stable)
{
    /// <summary>Two consecutive readings agreed: the state is a state of the built model.</summary>
    public bool Settled => Stable;

    /// <summary>Both volumes were read and the readings never agreed: no result check may then claim
    /// geometry. An unreadable side is NOT this state — nothing was compared.</summary>
    public bool StabilityUnestablished => !Stable && BaselineMm3 is not null && ObservedMm3 is not null;

    /// <summary>The mark for the caller's unverified aspects, or null when the first control rebuild
    /// left the model alone.</summary>
    public string? Note => RebuildSettlement.Describe(Attempts, BaselineMm3, ObservedMm3, Stable);

    /// <summary>The same rule applied to a level the caller already computed.</summary>
    public VerificationLevel CapLevel(VerificationLevel level) =>
        RebuildSettlement.CapLevel(level, StabilityUnestablished);
}
