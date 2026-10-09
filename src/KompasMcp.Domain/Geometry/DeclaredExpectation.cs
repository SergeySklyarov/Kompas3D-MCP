using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>How the client's declared expectation of a mutation's outcome relates to what was measured.</summary>
/// <remarks>INVARIANT: an expectation the client did not declare is neither confirmed nor refused; a
/// declared expectation that could not be COMPARED (the measurement was not read) is not a refusal but a
/// named gap, because "we could not check" and "we checked and it did not match" are different facts.
/// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
public enum DeclaredExpectationVerdict
{
    /// <summary>No expectation was declared: the mutation is decided by its own checks.</summary>
    NotDeclared,

    /// <summary>Declared and matched within <see cref="ProfileArea.Tolerance"/>.</summary>
    Confirmed,

    /// <summary>Declared and contradicted by the measurement: the call must not answer as a success.</summary>
    NotConfirmed,

    /// <summary>Declared but not comparable — the measured value could not be read.</summary>
    Unverifiable,
}

/// <summary>The outcome of comparing a declared expectation with a measurement, with the numbers a refusal
/// or a named gap must carry.</summary>
public readonly record struct DeclaredExpectationOutcome(
    DeclaredExpectationVerdict Verdict,
    double? ExpectedMm3,
    double? MeasuredMm3,
    double ToleranceMm3,
    string? Reason = null)
{
    public bool IsDeclared => Verdict != DeclaredExpectationVerdict.NotDeclared;

    public bool IsConfirmed => Verdict == DeclaredExpectationVerdict.Confirmed;

    /// <summary>Declared and contradicted — the caller must be told "not a success".</summary>
    public bool IsRefusal => Verdict == DeclaredExpectationVerdict.NotConfirmed;

    /// <summary>Declared but not comparable — named in <c>unverified_aspects</c>, never a refusal.</summary>
    public bool IsUnverifiable => Verdict == DeclaredExpectationVerdict.Unverifiable;

    /// <summary>Measured minus declared, or null when one of the two is missing.</summary>
    public double? DifferenceMm3 => ExpectedMm3 is double expected && MeasuredMm3 is double measured
        ? measured - expected
        : null;
}

/// <summary>The single rule that turns a client-declared expectation of a mutation's result into a verdict.</summary>
/// <remarks>INVARIANT: a declared expectation that did not hold within the project tolerance makes the call
/// NOT a success — a refusal, not a lowered level: the declared expectation IS the geometry check of such
/// a tool, so without it a wrong model would be reported as a success. An expectation that could not be
/// compared is NAMED, not refused. INVARIANT (order): the comparison is the geometry check of an operation
/// that HAPPENED, so a "route was not applied" refusal comes first, else the declared mismatch masks it.
/// The decision is a pure function of two numbers, so a SUBSTITUTED read result exercises every branch.
/// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
public static class DeclaredExpectation
{
    /// <summary>The one code every refusal by this rule carries, whatever the tool.</summary>
    public const string NotConfirmedCode = "declared_expectation_not_confirmed";

    /// <summary>The one tolerance, shared with the profile area: an absolute floor plus a relative part.</summary>
    public static double Tolerance(double expectedMm3) => ProfileArea.Tolerance(expectedMm3);

    /// <summary>Compare a declared expectation with a measurement. A null expectation is "not declared";
    /// a declared expectation with no measurement is "not comparable", not "zero".</summary>
    public static DeclaredExpectationOutcome Evaluate(double? expectedMm3, double? measuredMm3)
    {
        if (expectedMm3 is not double expected)
        {
            return new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.NotDeclared, null, measuredMm3, 0d);
        }

        var tolerance = Tolerance(expected);
        if (measuredMm3 is not double measured)
        {
            return new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.Unverifiable, expected, null, tolerance);
        }

        return Math.Abs(measured - expected) <= tolerance
            ? new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.Confirmed, expected, measured, tolerance)
            : new DeclaredExpectationOutcome(
                DeclaredExpectationVerdict.NotConfirmed, expected, measured, tolerance);
    }

    /// <summary>Compare a declared expectation with a measurement that may be RE-READ once when the first
    /// read did not confirm it; <paramref name="rereadMm3"/> repeats the MEASUREMENT by the same route.</summary>
    /// <remarks>WHY A SECOND READ. MEASURED: the model state sometimes reads one operation behind, so a
    /// single read can disagree while the model is correct. INVARIANT: a refusal needs BOTH reads readable,
    /// in AGREEMENT, and both missing the expectation; reads that DISAGREE are a NAMED gap, not a refusal —
    /// a stale read must not become a false refusal.
    /// History: docs/decisions/adapter-core.md#declared-expectation-rule</remarks>
    public static DeclaredExpectationOutcome Evaluate(
        double? expectedMm3, double? measuredMm3, Func<double?>? rereadMm3)
    {
        var first = Evaluate(expectedMm3, measuredMm3);
        if (rereadMm3 is null || !first.IsDeclared || first.IsConfirmed)
        {
            return first;
        }

        var second = Evaluate(expectedMm3, rereadMm3());
        if (second.IsConfirmed)
        {
            return second;
        }

        if (first.MeasuredMm3 is double a && second.MeasuredMm3 is double b
            && Math.Abs(a - b) <= Tolerance(first.ExpectedMm3 ?? 0d))
        {
            // Both reads readable and in agreement, and neither matched: a genuine refusal.
            return first;
        }

        if (first.MeasuredMm3 is null && second.MeasuredMm3 is null)
        {
            // Both reads failed: that is "could not be compared", not "two reads disagreed".
            return first;
        }

        return new DeclaredExpectationOutcome(
            DeclaredExpectationVerdict.Unverifiable,
            first.ExpectedMm3,
            first.MeasuredMm3,
            first.ToleranceMm3,
            DivergentReadsReason(first.MeasuredMm3, second.MeasuredMm3));
    }

    /// <summary>The named gap for a declared expectation that could not be compared.</summary>
    public static string UnreadableReason(string quantity) =>
        "declared_expectation_unreadable — заявленное ожидание (" + quantity + ") не с чем сравнить: "
        + "величина после операции не прочитана. Это НЕ подтверждение и НЕ отказ: сравнивать было нечего.";

    /// <summary>The named gap for two reads of the same quantity that DISAGREE — the measurement is not
    /// repeatable, so neither number can be held against the declared expectation.</summary>
    public static string DivergentReadsReason(double? firstMm3, double? secondMm3) =>
        "declared_expectation_reads_diverged — два чтения объёма разошлись: " + Num(firstMm3)
        + " и " + Num(secondMm3) + ". Отказ НЕ выдаётся: расхождение означает, что измерение "
        + "неповторяемо (устаревшее чтение), а не что модель не совпала с заявленным; уровень "
        + "оставлен не выше structure_checked.";

    /// <summary>The reason to publish for an unverifiable outcome: the divergence text when the two reads
    /// disagreed, otherwise the standard "could not be compared" text.</summary>
    public static string UnverifiableReason(string quantity, DeclaredExpectationOutcome outcome) =>
        outcome.Reason ?? UnreadableReason(quantity);

    /// <summary>The refusal for a declared expectation that did not hold. <paramref name="operation"/>
    /// names the tool/family, <paramref name="quantity"/> what was compared ("volume after the operation",
    /// "volume delta", …), and <paramref name="consequence"/> optionally adds what the client should know
    /// about the model that was left behind. <paramref name="extraDetails"/> carries tool-specific numbers
    /// (a feature's name, its state, …) beside the shared ones.</summary>
    public static KompasContractException Refusal(
        DeclaredExpectationOutcome outcome,
        string operation,
        string quantity,
        long? revisionAfter,
        string? consequence = null,
        IReadOnlyDictionary<string, object?>? extraDetails = null)
    {
        var message = "Операция «" + operation + "» применена, но " + quantity
            + " не совпала с заявленной: измерено " + Num(outcome.MeasuredMm3)
            + ", ожидалось " + Num(outcome.ExpectedMm3)
            + " (разность " + Num(outcome.DifferenceMm3)
            + ", допуск " + Num(outcome.ToleranceMm3) + "). Вызов НЕ считается успешным: заявленное "
            + "ожидание и есть проверка геометрии этого инструмента. Модель оставлена в измеренном "
            + "состоянии — сервер её молча не откатывает."
            + (string.IsNullOrEmpty(consequence) ? string.Empty : " " + consequence);

        var details = new Dictionary<string, object?>
        {
            ["code"] = NotConfirmedCode,
            ["operation"] = operation,
            ["quantity"] = quantity,
            ["expected_volume_mm3"] = outcome.ExpectedMm3,
            ["measured_volume_mm3"] = outcome.MeasuredMm3,
            ["volume_delta_mm3"] = outcome.DifferenceMm3,
            ["tolerance_mm3"] = outcome.ToleranceMm3,
            ["revision_after"] = revisionAfter,
        };
        if (extraDetails is not null)
        {
            foreach (var (key, value) in extraDetails)
            {
                details[key] = value;
            }
        }

        return new KompasContractException(
            ErrorCodes.GeometryFailed,
            message,
            RetryPolicy.AfterReconciliation,
            partialEffects: true,
            details: details);
    }

    /// <summary>The short check name a tool adds for the comparison, so the response reads the same way
    /// whatever the tool.</summary>
    public static NamedCheck Check(string name, DeclaredExpectationOutcome outcome) =>
        new(name,
            outcome.IsConfirmed,
            Observed: Num(outcome.MeasuredMm3),
            Expected: outcome.IsDeclared ? Num(outcome.ExpectedMm3) : "не задано");

    private static string Num(double? value) => value is double number
        ? number.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)
        : "не читается";
}
