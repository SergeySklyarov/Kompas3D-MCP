namespace KompasMcp.Domain.Files;

/// <summary>One application entry of the session as the session-end shutdown sees it: the process it lives
/// in, and whether THIS session launched that process.</summary>
public readonly record struct SessionApplication(string ApplicationId, int ProcessId, bool Launched);

/// <summary>Which application entries a session-end shutdown ends, and in what order.</summary>
/// <remarks>INVARIANT: a shutdown that calls <c>Quit()</c> ends a PROCESS, so it owns EVERY entry bound to
/// that process: an entry attached to a process this session launched has no document that survives the
/// Quit, and its unsaved documents must be rescued by the same sequence.
/// INVARIANT: the launched entry runs first — it issues the Quit(), and the entries that end with it must be
/// gone before the loop reaches them. Otherwise they are disconnected again, and the failure reported would
/// name a step that was already done.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public static class SessionEndProcess
{
    /// <summary>The entries that a <c>Quit()</c> of <paramref name="processId"/> ends: every entry of the
    /// session that lives in that process, whichever mode registered it.</summary>
    public static IReadOnlyList<SessionApplication> EndingWith(
        int processId, IEnumerable<SessionApplication> applications) =>
        applications.Where(application => application.ProcessId == processId).ToArray();

    /// <summary>The session's entries in shutdown order: a launched entry (it ends its process) before any
    /// attached entry of that same process.</summary>
    public static IReadOnlyList<SessionApplication> ShutdownOrder(IEnumerable<SessionApplication> applications) =>
        applications.OrderByDescending(application => application.Launched).ToArray();
}
