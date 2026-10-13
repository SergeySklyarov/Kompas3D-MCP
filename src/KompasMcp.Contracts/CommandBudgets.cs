namespace KompasMcp.Contracts.Ipc;

/// <summary>The Host's waiting rule over the single command table: how long each side may wait.</summary>
/// <remarks>
/// <para>INVARIANT: the Host budget is the Worker budget plus <see cref="HostMarginMs"/>, and BOTH come
/// from <see cref="WorkerCommandTable"/>. The Host is responsible for delivering the frame, not for the
/// COM call, so it must wait LONGER; a smaller Host budget turns every long but normal call into
/// "outcome unknown" plus a Worker restart.
/// History: docs/decisions/contracts.md#budgets-single-table</para>
/// </remarks>
public static class CommandBudgets
{
    /// <summary>Default budget for a command the table does not name.</summary>
    public const int DefaultMs = 120_000;

    /// <summary>How much longer than the Worker budget the Host waits. Named as a number, not a "small
    /// margin": the margin IS the difference between "the Host saw the Worker time out" and "the Host
    /// gave up first".</summary>
    public const int HostMarginMs = 30_000;

    /// <summary>Worker-side command budget, milliseconds: the number the Worker itself enforces.</summary>
    public static int WorkerBudgetMs(string command) => WorkerCommandTable.BudgetMs(command);

    /// <summary>How long the Host waits for a command's answer: the Worker budget plus <see
    /// cref="HostMarginMs"/>.</summary>
    public static int HostBudgetMs(string command) => WorkerBudgetMs(command) + HostMarginMs;

    /// <summary>How long the Host ACTUALLY waits: the larger of the shared command budget and the
    /// operator setting.</summary>
    /// <remarks>INVARIANT: the setting may LENGTHEN the wait but never shorten it below the Worker
    /// budget, or the Host would be first to declare "outcome unknown" on a merely slow command and
    /// restart a Worker that is still running it.
    /// History: docs/decisions/host.md#budget-per-command</remarks>
    public static int HostWaitMs(string command, int operationBudgetMs) =>
        Math.Max(HostBudgetMs(command), operationBudgetMs);
}
