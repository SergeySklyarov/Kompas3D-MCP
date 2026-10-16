using System.Globalization;

namespace KompasMcp.Domain.Geometry;

/// <summary>How the material an extrusion actually moved relates to the analytic volume of the profile it
/// swept (area × depth). That figure is an UPPER BOUND of the moved material, not its exact value.</summary>
public enum VolumeBoundVerdict
{
    /// <summary>There is no analytic figure to compare against (the profile area is unknown).</summary>
    NoBound,

    /// <summary>The moved material was not read — not zero, and not a verdict about the bound.</summary>
    Unread,

    /// <summary>Measured equals the prism within <see cref="ProfileArea.Tolerance"/>: the expectation held.</summary>
    Confirmed,

    /// <summary>Measured lies in (0, prism]: inside the bound, so the prism is only an upper bound here.</summary>
    WithinBound,

    /// <summary>Measured is outside the bound: above the prism, zero, or of the wrong sign.</summary>
    OutsideBound,
}

/// <summary>The verdict with the two numbers it was taken from and the reason it must be published with.</summary>
public readonly record struct VolumeBoundOutcome(
    VolumeBoundVerdict Verdict,
    double? MeasuredMm3,
    double? BoundMm3,
    double ToleranceMm3,
    string? Reason = null)
{
    /// <summary>Measured equals the prism: the exact expectation is confirmed.</summary>
    public bool IsConfirmed => Verdict == VolumeBoundVerdict.Confirmed;

    /// <summary>Inside the bound but not equal: NOT a failure, and not a confirmation either.</summary>
    public bool IsWithinBound => Verdict == VolumeBoundVerdict.WithinBound;

    /// <summary>Outside the bound: the one case the prism still judges as a failure.</summary>
    public bool IsOutsideBound => Verdict == VolumeBoundVerdict.OutsideBound;

    /// <summary>Measured minus the prism, or null when one of the two is missing.</summary>
    public double? DifferenceMm3 => MeasuredMm3 is double measured && BoundMm3 is double bound
        ? measured - bound
        : null;
}

/// <summary>The single rule that turns a measured material gain and the analytic prism into a verdict.</summary>
/// <remarks>INVARIANT: the prism (area × depth) is an UPPER BOUND, not an equality. A boss that overlaps
/// the body adds LESS than the prism and a cut through a cavity removes LESS, so an inequality inside the
/// bound must not read as a failed check. INVARIANT: the bound still judges the cases it can — a gain above
/// the prism (material from nowhere), a zero gain (nothing happened) and a gain of the wrong sign are
/// failures, because neither adding nor removing can exceed the prism.
/// History: docs/decisions/adapter-core.md#volume-bound</remarks>
public static class VolumeBound
{
    /// <summary>The named gap for a gain that lies inside the bound: the exact expectation is unknown.</summary>
    public const string NotComputedCode = "expected_volume_not_computed";

    /// <summary>The code the failed check and the named gap carry when the gain is outside the bound.</summary>
    public const string OutsideCode = "volume_delta_outside_bound";

    /// <summary>Compare a measured gain with the analytic prism. A bound that is not a finite positive
    /// number is "no bound"; an unread gain is "unread", never zero and never a verdict.</summary>
    public static VolumeBoundOutcome Evaluate(double? measuredMm3, double? boundMm3)
    {
        if (boundMm3 is not double bound || !double.IsFinite(bound) || !(bound > 0d))
        {
            return new VolumeBoundOutcome(VolumeBoundVerdict.NoBound, measuredMm3, null, 0d);
        }

        var tolerance = ProfileArea.Tolerance(bound);
        if (measuredMm3 is not double measured)
        {
            return new VolumeBoundOutcome(VolumeBoundVerdict.Unread, null, bound, tolerance);
        }

        if (Math.Abs(measured - bound) <= tolerance)
        {
            return new VolumeBoundOutcome(VolumeBoundVerdict.Confirmed, measured, bound, tolerance);
        }

        if (measured > 0d && measured <= bound + tolerance)
        {
            return new VolumeBoundOutcome(
                VolumeBoundVerdict.WithinBound, measured, bound, tolerance,
                NotComputedReason(measured, bound));
        }

        return new VolumeBoundOutcome(
            VolumeBoundVerdict.OutsideBound, measured, bound, tolerance,
            OutsideReason(measured, bound, tolerance));
    }

    /// <summary>The named gap for a gain inside the bound: it is NOT a mismatch, because the prism is a
    /// bound and the exact volume of the intersection with the body is not read by any documented route.</summary>
    public static string NotComputedReason(double measuredMm3, double boundMm3) =>
        NotComputedCode + " — призма профиля (площадь × глубина) " + Num(boundMm3)
        + " мм³ — ВЕРХНЯЯ граница, а не точное ожидание: измеренная прибавка " + Num(measuredMm3)
        + " мм³ меньше призмы, потому что инструмент пересекается с телом (приклеивание) или проходит "
        + "через полость (вырезание). Проверка верхней границы пройдена; точный объём пересечения "
        + "инструмента с телом документированным маршрутом не читается, поэтому ожидание названо "
        + "невычисленным, а не проваленным.";

    /// <summary>The named gap for a gain outside the bound: the one case the prism judges, with the reason
    /// the call is still a success (the feature exists; only its declared figure did not hold).</summary>
    public static string OutsideReason(double measuredMm3, double boundMm3, double toleranceMm3) =>
        OutsideCode + " — измеренная прибавка " + Num(measuredMm3) + " мм³ вне верхней границы призмы "
        + Num(boundMm3) + " мм³ (допуск " + Num(toleranceMm3) + "): больше призмы, ноль или знак не тот. "
        + "Ни приклеивание, ни вырезание не может сдвинуть материал больше призмы, а нулевая прибавка "
        + "означает, что признак ничего не изменил.";

    /// <summary>Measured minus the prism as the client reads it, or the "not read" word.</summary>
    public static string Difference(VolumeBoundOutcome outcome) => Num(outcome.DifferenceMm3);

    private static string Num(double? value) => value is double number
        ? number.ToString("0.######", CultureInfo.InvariantCulture)
        : "не читается";
}
