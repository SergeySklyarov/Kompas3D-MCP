using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Бюджеты команды: Хост не имеет права сдаваться раньше Worker (дефект M11 ревью 05.10.2026).
/// </summary>
/// <remarks>
/// Прежде бюджет Хоста задавался одной настройкой (120 с по умолчанию), а бюджеты Worker —
/// отдельным <c>switch</c> в диспетчере (180–300 с). Следствие: Хост объявлял OUTCOME_UNKNOWN и
/// ломал канал раньше, чем Worker доходил до своего предела, а следующий вызов убивал Worker,
/// который ещё работал. Проверка — на общей таблице, а не на двух независимых числах.
/// </remarks>
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

    /// <summary>
    /// Бюджет умолчания (120 с по умолчанию у настройки) больше бюджета Worker уже не обязан быть —
    /// он обязан быть НЕ меньше обычного: прежде 120 с Хоста против 240 с Worker были ровно
    /// расхождением, которое здесь закрыто.
    /// </summary>
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
