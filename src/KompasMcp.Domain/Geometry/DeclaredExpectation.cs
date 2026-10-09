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
    double ToleranceMm3)
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
/// NOT a success — a refusal, not a lowered verification level. The reason is that the declared expectation
/// IS the geometry check of such a tool: without it there is nothing else to compare, so a wrong model would
/// otherwise be reported as a success. An expectation that could not be compared is NAMED, not refused.
/// The decision is a pure function of two numbers, so a SUBSTITUTED read result exercises every branch
/// without a CAD session.
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

    /// <summary>The named gap for a declared expectation that could not be compared.</summary>
    public static string UnreadableReason(string quantity) =>
        "declared_expectation_unreadable — заявленное ожидание (" + quantity + ") не с чем сравнить: "
        + "величина после операции не прочитана. Это НЕ подтверждение и НЕ отказ: сравнивать было нечего.";

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
