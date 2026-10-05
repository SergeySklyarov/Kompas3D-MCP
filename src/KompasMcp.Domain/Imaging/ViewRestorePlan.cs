namespace KompasMcp.Domain.Imaging;

/// <summary>What to do with the window view after a snapshot: put the previous projection back, or leave
/// the requested one.</summary>
public enum RestoreDecision
{
    /// <summary>No restore is required: the projection was not requested, the caller consented to keep
    /// the new view, or the type it was applied over could not be read — the swap must not have replaced
    /// it.</summary>
    NotNeeded,

    /// <summary>Put the previous projection back and read the type again.</summary>
    Restore,

    /// <summary>The switch was attempted but unconfirmed: try to put the previous type back, because the
    /// window view may already have moved. The outcome belongs in the refusal's `details`.</summary>
    AttemptAfterUnconfirmedSwitch,
}

/// <summary>The restore decision as a PURE function: the cases an instrument cannot reach through COM —
/// "the caller consented and the restore must not run", "the switch was attempted and not confirmed",
/// "the previous view was never read" — are testable here without KOMPAS. It takes three plain values and
/// answers; no document, no COM, nothing that could throw.</summary>
/// <remarks>INVARIANT: <c>keep_view=true</c> is a CONSENT, checked FIRST — the caller who asked to keep
/// the new view is never put back, and is not told a restore is missing when none was wanted. MEASURED: a
/// previous type the published list does not name is not restorable, so a read but unpublished value is
/// treated as unread. History: docs/decisions/adapter-core.md#view-swap</remarks>
public static class ViewRestorePlan
{
    /// <param name="requested">The caller asked for a projection at all.</param>
    /// <param name="keepView">The caller consented to keep the requested projection.</param>
    /// <param name="previousView">The previous projection's published name, or null when it was not read
    /// or is not in the published list.</param>
    public static RestoreDecision Decide(bool requested, bool keepView, string? previousView)
    {
        if (!requested || keepView)
        {
            return RestoreDecision.NotNeeded;
        }

        // A read AND published previous type is required to put anything back. An unread one is not a
        // "restore by default": restoring an unknown value would be a guess dressed as a measurement.
        return previousView is { Length: > 0 }
            ? RestoreDecision.Restore
            : RestoreDecision.NotNeeded;
    }
}
