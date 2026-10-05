using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Command budgets: the Host must never give up before the Worker.</summary>
/// <remarks>INVARIANT: the Host budget is derived from the Worker budget on one shared table, not set by an
/// independent number — otherwise the Host declares OUTCOME_UNKNOWN and breaks the channel before the Worker
/// reaches its own limit, and the next call kills a Worker that is still working.
/// History: docs/decisions/tests.md#command-budget-2</remarks>
public class CommandBudgetTests
{
    [Theory]
    [InlineData(WorkerCommands.Connect)]
    [InlineData(WorkerCommands.ExportStep)]
    [InlineData(WorkerCommands.ImportStep)]
    [InlineData(WorkerCommands.ExportImage)]
    [InlineData(WorkerCommands.Hole)]
    [InlineData(WorkerCommands.SolidBoolean)]
    [InlineData(WorkerCommands.Sweep)]
    [InlineData(WorkerCommands.Loft)]
    [InlineData(WorkerCommands.Shell)]
    [InlineData(WorkerCommands.CreateAuxGeometry)]
    [InlineData(WorkerCommands.SetSketchPlane)]
    [InlineData(WorkerCommands.InsertComponent)]
    [InlineData(WorkerCommands.ReplaceComponent)]
    [InlineData(WorkerCommands.SetComponentPlacement)]
    [InlineData(WorkerCommands.ListComponents)]
    [InlineData(WorkerCommands.CreateMate)]
    [InlineData(WorkerCommands.SetMateParameter)]
    [InlineData(WorkerCommands.DeleteMate)]
    [InlineData(WorkerCommands.Extrude)]
    [InlineData(WorkerCommands.Ping)]
    public void HostBudgetIsNeverShorterThanTheWorkerBudget(string command)
    {
        var worker = CommandBudgets.WorkerBudgetMs(command);
        var host = CommandBudgets.HostBudgetMs(command);

        Assert.True(host > worker,
            $"{command}: бюджет Хоста {host} мс обязан быть СТРОГО больше бюджета Worker {worker} мс, " +
            "иначе Хост сдаётся первым и превращает штатно долгую команду в «исход неизвестен»");
        Assert.Equal(worker + CommandBudgets.HostMarginMs, host);
    }

    /// <summary>INVARIANT: the default budget need no longer exceed the Worker budget, but it must be no LESS
    /// than the old flat one.</summary>
    [Fact]
    public void LongCommandsGetMoreThanTheOldFlatHostBudget()
    {
        const int oldFlatHostBudgetMs = 120_000;

        Assert.True(CommandBudgets.WorkerBudgetMs(WorkerCommands.Hole) > oldFlatHostBudgetMs);
        Assert.True(CommandBudgets.HostBudgetMs(WorkerCommands.Hole) > oldFlatHostBudgetMs);
    }

    [Fact]
    public void UnknownCommandFallsBackToTheDefault()
    {
        Assert.Equal(CommandBudgets.DefaultMs, CommandBudgets.WorkerBudgetMs("нет-такой-команды"));
        Assert.Equal(CommandBudgets.DefaultMs + CommandBudgets.HostMarginMs,
            CommandBudgets.HostBudgetMs("нет-такой-команды"));
    }
}
