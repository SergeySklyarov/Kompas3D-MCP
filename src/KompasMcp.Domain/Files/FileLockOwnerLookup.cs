namespace KompasMcp.Domain.Files;

/// <summary>The outcome of looking for the process that holds a file: the holder when it was named within
/// the budget, or a NAMED reason it was not.</summary>
public readonly record struct OwnerLookupResult(string? Owner, string? UnavailableReason, bool TimedOut)
{
    /// <summary>True when a holder was named; false means the field is UNREAD and the reason says why.</summary>
    public bool IsNamed => Owner is not null;
}

/// <summary>Runs a best-effort holder lookup under its OWN budget, so a slow lookup never delays the
/// refusal it belongs to.</summary>
/// <remarks>INVARIANT: a diagnostic lookup must never hold up the refusal it explains — waiting for a
/// process name can push the refusal past the client's sync budget, where a late answer reads as
/// <c>running</c> and cannot be told from a hang. A lookup that does not finish in the budget is
/// ABANDONED, and the refusal goes out with the holder UNREAD and the reason stated.
/// LIMIT: <c>RmGetList</c> is synchronous with no documented cancellation, so it runs on its own task.
/// History: docs/decisions/files.md#file-locked-guard</remarks>
public static class FileLockOwnerLookup
{
    /// <summary>A few hundred milliseconds. WHY THIS ORDER: a normal lookup answers in single-digit
    /// milliseconds, and the budget must stay far below the client's sync budget (seconds), because a late
    /// refusal is indistinguishable from a hang. The number is a budget, not a measurement of the API.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>Run <paramref name="probe"/> under <paramref name="budget"/> and describe the outcome.
    /// A probe that faults or does not finish yields an unread holder with a named reason, never a
    /// thrown exception and never a silent null.</summary>
    public static OwnerLookupResult Within(string path, Func<string, string?> probe, TimeSpan budget)
    {
        Task<string?> lookup;
        try
        {
            lookup = Task.Run(() => probe(path));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TaskCanceledException)
        {
            return new OwnerLookupResult(null, "поиск владельца не запущен: " + ex.GetType().Name, false);
        }

        try
        {
            if (!lookup.Wait(budget))
            {
                return new OwnerLookupResult(null,
                    "поиск владельца не уложился в " + (int)budget.TotalMilliseconds + " мс и оставлен: "
                    + "прервать его документированным способом нельзя (LIMIT)", true);
            }
        }
        catch (AggregateException ex)
        {
            return new OwnerLookupResult(null,
                "поиск владельца отказал: " + ex.GetBaseException().GetType().Name, false);
        }

        var owner = lookup.Result;
        return owner is null
            ? new OwnerLookupResult(null, "владелец не назван: поиск не вернул ни одного процесса", false)
            : new OwnerLookupResult(owner, null, false);
    }
}
