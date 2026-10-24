namespace KompasMcp.Domain.SheetMetal;

/// <summary>Rules of the unfold request and of the comparison that confirms it.</summary>
/// <remarks>INVARIANT: a refusal is decided from the request alone, before COM, because a written unfold
/// leaves the body in the unfolded state and this product cannot delete sheet-metal parameters.
/// MEASURED: <c>UpdateParam()</c> answers FALSE in every state, so the confirmation is the read flag plus
/// the extent along the normal. History: docs/decisions/sheet-metal.md#unfold-tools</remarks>
public static class SheetMetalUnfoldPolicy
{
    /// <summary>Numeric tolerance of every extent comparison, mm.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>Why an unfold request is refused. <see cref="None"/> — it is accepted.</summary>
    public enum Refusal
    {
        None,
        MissingFixedFace,
    }

    /// <summary>The rule of a request: the first unfold needs a face to hold still, a repeat does not.</summary>
    public static Refusal CheckSet(bool unfold, bool? isCreated, string? fixedFaceRef) =>
        unfold && isCreated == false && fixedFaceRef is null
            ? Refusal.MissingFixedFace
            : Refusal.None;

    /// <summary>Does the extent confirm the requested state? Unfolded — the extent along the normal IS the
    /// thickness; folded back — the body no longer lies flat, so the extent is LARGER than the thickness.
    /// An unread extent never confirms anything.</summary>
    /// <remarks>LIMIT: the exact bent extent of a previous call is not remembered between calls, so folding
    /// back is confirmed by "no longer flat", not by "equal to the extent read before the unfold"; the
    /// round trip is checked by the acceptance rows. History: docs/decisions/sheet-metal.md#unfold-tools</remarks>
    public static bool ExtentConfirms(bool unfold, double? extentMm, double thicknessMm) =>
        extentMm is { } extent && (unfold
            ? Math.Abs(extent - thicknessMm) <= Tolerance
            : extent > thicknessMm + Tolerance);
}
