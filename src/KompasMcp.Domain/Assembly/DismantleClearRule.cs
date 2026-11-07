namespace KompasMcp.Domain.Assembly;

/// <summary>Whether a rewrite of the exploded-view steps may start over an empty list.</summary>
/// <remarks>DOC: <c>idismantleparameters_cleardismantleparameters.html</c> — «TRUE - в случае успеха, FALSE
/// - в случае неудачи»; the interface has no «edit a step», so a rewrite is «clear, then add».
/// INVARIANT: steps are added only after the clear answered TRUE and the re-read count is 0 — an
/// unconfirmed clear would append the new steps to the old ones while the note claimed they were gone.
/// History: docs/decisions/dismantle.md#rewrite</remarks>
public static class DismantleClearRule
{
    /// <summary><paramref name="clearReturned"/> is the BOOL of the clear (<c>null</c> when the call did
    /// not answer), <paramref name="stepsAfterClear"/> the count read back after it (<c>null</c> when
    /// unread). Only <c>TRUE</c> with exactly 0 confirms the empty list.</summary>
    public static bool IsConfirmed(bool? clearReturned, int? stepsAfterClear) =>
        clearReturned == true && stepsAfterClear == 0;
}
