using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The rule that ties a FAILED declared volume expectation to the mark the caller sees.</summary>
/// <remarks>INVARIANT: a mutation answers as an extrusion does (<c>Api5Session.Geometry.cs</c>,
/// <c>volume_delta_not_confirmed</c>): when the volume the caller DECLARED does not match the one read
/// back, the fact is named in the unverified aspects — "checked and did not match" must be visible where
/// a client reads what was not confirmed, not only as a <c>passed=false</c> entry among the checks.
/// An expectation never declared is not a failure, and a mutation already applied is not turned into a
/// refusal. The decision is a pure function of two numbers, so a SUBSTITUTED read result exercises it.
/// History: docs/decisions/adapter-core.md#pattern-declared-volume</remarks>
public static class DeclaredVolumeMarks
{
    /// <summary>The wording shared by every mutation that declares a volume expectation.</summary>
    public const string VolumeNotConfirmed =
        "document_volume_not_confirmed — заявленный объём не совпал с измеренным: КОМПАС сообщает только "
        + "об успехе вызова, поэтому признак считается недоказанным";

    /// <summary>True when an expectation was declared and the measurement contradicts it — including the
    /// case where the volume could not be read at all, which is "not confirmed", never "zero".</summary>
    public static bool Mismatched(double? expectedMm3, double? measuredMm3, double toleranceMm3) =>
        expectedMm3 is double expected
        && (measuredMm3 is not double measured || Math.Abs(measured - expected) > toleranceMm3);

    /// <summary>Add the mark when the declared volume is contradicted by the read-back.</summary>
    public static void MarkVolumeNotConfirmed(
        IList<string> unverified, double? expectedMm3, double? measuredMm3, double toleranceMm3)
    {
        ArgumentNullException.ThrowIfNull(unverified);

        if (Mismatched(expectedMm3, measuredMm3, toleranceMm3))
        {
            unverified.Add(VolumeNotConfirmed);
        }
    }
}
