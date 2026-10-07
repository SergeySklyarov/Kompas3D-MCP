namespace KompasMcp.Domain.Mates;

/// <summary>How an explicit mate alignment value is confirmed by the MEASURED geometry, and where
/// geometry cannot confirm it at all. Pure, no KOMPAS types.</summary>
/// <remarks>INVARIANT: a value is confirmed by the face-normal dot in assembly coordinates, NEVER by
/// the number the kernel stores — a requested `opposite` read back as `closest` still lands on -1.
/// MEASURED (flat faces, two placements): `opposite` lands on -1, `cooriented` on +1; `closest` names
/// no orientation; `perpendicular` leaves the dot uninformative. LIMIT: `tangency`/`concentric` need
/// curved faces, where no single normal exists, so the dot is not read at all.
/// TEST: MateAlignmentPolicyTests. History: docs/decisions/mates.md#alignment-geometry-criterion</remarks>
public static class MateAlignmentPolicy
{
    /// <summary>The face-normal dot a value NAMES: <c>opposite</c> is -1, <c>cooriented</c> is +1.
    /// <c>null</c> means the value names no orientation (<c>closest</c>, or none requested).</summary>
    public static double? ExpectedDot(string? requested) => requested switch
    {
        "opposite" => -1d,
        "cooriented" => 1d,
        _ => null,
    };

    /// <summary>Whether the face-normal dot can confirm a value for this mate type.
    /// <c>perpendicular</c> holds the faces at a right angle, so the dot is ~0 whatever the value says:
    /// there the value is NOT confirmable by geometry and the call is noted, not refused. The remaining
    /// types are unmeasured, but their faces still have a single normal, so the dot is read and compared
    /// — an unmeasured type is not an unconfirmed one.</summary>
    public static bool DotConfirmsValue(string? constraintType) => constraintType is not "perpendicular";

    /// <summary>Whether a measured dot satisfies the requested value; <c>null</c> when geometry cannot
    /// decide here (no orientation named, the type makes the dot uninformative, or the dot was not
    /// read). <c>null</c> is NOT "satisfied" — it is "nothing was measured".</summary>
    /// <remarks>The ONE place the "dot against expectation" decision lives, so a mismatch — which is
    /// the refusal — is held by a unit test without KOMPAS.</remarks>
    public static bool? DotSatisfies(
        string? constraintType, string? requested, double? measured, double tolerance)
    {
        var expected = DotConfirmsValue(constraintType) ? ExpectedDot(requested) : null;
        return expected is null || measured is null
            ? null
            : Math.Abs(measured.Value - expected.Value) <= tolerance;
    }
}
