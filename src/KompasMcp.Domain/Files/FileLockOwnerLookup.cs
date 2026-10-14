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
/// <remarks>INVARIANT: a diagnostic lookup must never hold up the refusal it explains — a late answer
/// reads as <c>running</c> and cannot be told from a hang. A lookup that does not finish in the budget
/// is ABANDONED, and the refusal goes out with the holder UNREAD and the reason stated.
/// LIMIT: <c>RmGetList</c> is synchronous with no documented cancellation, so the probe runs on a thread
/// of its own and an abandoned probe is left to finish there.
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
        string? owner = null;
        Exception? fault = null;

        // INVARIANT: the probe runs on a thread of its OWN, never on a worker of the shared thread pool.
        // MEASURED: a work item queued to a fully busy pool waits for the runtime to inject a thread, so a
        // starved pool reported an INSTANT probe as "did not finish" - a false reason that hides the holder.
        // LIMIT: the thread is a background one, so an abandoned probe never holds the process open.
        // History: docs/decisions/files.md#file-locked-guard
        var worker = new Thread(() =>
        {
            try
            {
                owner = probe(path);
            }
            catch (Exception ex)
            {
                fault = ex;
            }
        })
        {
            IsBackground = true,
            Name = "kompas-owner-lookup",
        };

        try
        {
            worker.Start();
        }
        catch (Exception ex) when (ex is ThreadStateException or OutOfMemoryException)
        {
            return new OwnerLookupResult(null, "поиск владельца не запущен: " + ex.GetType().Name, false);
        }

        if (!worker.Join(budget))
        {
            return new OwnerLookupResult(null,
                "поиск владельца не уложился в " + (int)budget.TotalMilliseconds + " мс и оставлен: "
                + "прервать его документированным способом нельзя (LIMIT)", true);
        }

        if (fault is not null)
        {
            return new OwnerLookupResult(null, "поиск владельца отказал: " + fault.GetType().Name, false);
        }

        return owner is null
            ? new OwnerLookupResult(null, "владелец не назван: поиск не вернул ни одного процесса", false)
            : new OwnerLookupResult(owner, null, false);
    }
}
