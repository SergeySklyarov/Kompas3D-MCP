using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The single Worker-command table: lane, action kind and budget in one source.</summary>
/// <remarks>INVARIANT: the Host and the Worker wait on the SAME number, taken from the SAME table. Two
/// tables are not a style problem: when the Worker's own switch grew entries the shared one did not
/// have, the Host kept waiting its default plus margin and declared OUTCOME_UNKNOWN - restarting a
/// Worker that was still running the command - on every command the shared table did not name.
/// History: docs/decisions/contracts.md#budgets-single-table</remarks>
public sealed class WorkerCommandTableTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static readonly (string Name, string Value)[] CommandConstants = typeof(WorkerCommands)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (field.Name, (string)field.GetRawConstantValue()!))
        .ToArray();

    private static readonly Dictionary<string, string> NameByCommand =
        CommandConstants.ToDictionary(row => row.Value, row => row.Name, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> CommandByName =
        CommandConstants.ToDictionary(row => row.Name, row => row.Value, StringComparer.Ordinal);

    /// <summary>Classification and budgets AS MEASURED before the single table, by executing the former
    /// private Worker code. One line per command: name, the three classification flags
    /// (changes_model, must_not_answer_across_a_loss, observes_connection_loss), the budget the Worker
    /// enforced, the budget the shared table declared.</summary>
    /// <remarks>INVARIANT: this is a SNAPSHOT, not a restatement of the table. The refactor was allowed
    /// to change one thing - the budget the Host waits - and to take the LARGER of the two former
    /// numbers; every classification flag must read exactly as it did before.
    /// History: docs/decisions/contracts.md#budgets-single-table</remarks>
    private static readonly string[] SnapshotBeforeTheTable =
    {
        "app.connect|101|180000|180000",
        "app.disconnect|100|120000|120000",
        "asm.check_interference|011|240000|120000",
        "asm.check_links|011|240000|240000",
        "asm.delete_component|101|240000|120000",
        "asm.insert_component|101|240000|240000",
        "asm.list_components|011|240000|240000",
        "asm.measure_gap|011|240000|120000",
        "asm.replace_component|101|240000|240000",
        "asm.set_placement|101|240000|240000",
        "aux.create|101|240000|240000",
        "aux.list|011|240000|240000",
        "aux.update_plane|101|240000|240000",
        "body.list|011|120000|120000",
        "doc.close|101|120000|120000",
        "doc.context|011|120000|120000",
        "doc.create|101|120000|120000",
        "doc.list|011|120000|120000",
        "doc.open|101|120000|120000",
        "doc.rebuild|101|120000|120000",
        "doc.save|101|120000|120000",
        "drawing.add_dimension|101|240000|120000",
        "drawing.create_views|101|240000|120000",
        "drawing.edit_view|101|240000|120000",
        "drawing.export|101|240000|120000",
        "drawing.get_technical_demand|011|240000|120000",
        "drawing.get_title_block|011|240000|120000",
        "drawing.list_dimensions|011|120000|120000",
        "drawing.list_views|011|120000|120000",
        "drawing.rebuild_views|101|120000|120000",
        "drawing.set_technical_demand|101|240000|120000",
        "drawing.set_title_block|101|240000|120000",
        "env.probe|000|10000|10000",
        "export.image|011|240000|240000",
        "export.step|011|300000|300000",
        "export.stl|011|300000|120000",
        "feat.bind_parameter|101|240000|120000",
        "feat.chamfer|101|120000|120000",
        "feat.delete|101|120000|120000",
        "feat.extrude|101|120000|120000",
        "feat.fillet|101|120000|120000",
        "feat.get|011|120000|120000",
        "feat.hole|101|240000|240000",
        "feat.list|011|120000|120000",
        "feat.loft|101|240000|240000",
        "feat.parameters|011|240000|120000",
        "feat.rotated|101|120000|120000",
        "feat.shell|101|240000|240000",
        "feat.suppress|101|120000|120000",
        "feat.sweep|101|240000|240000",
        "feat.update|101|120000|120000",
        "geom.measure|011|120000|120000",
        "geom.resolve|011|120000|120000",
        "import.step|101|300000|300000",
        "mat.get|011|120000|120000",
        "mat.set|101|240000|120000",
        "mate.create|101|240000|240000",
        "mate.delete|101|240000|240000",
        "mate.list|011|240000|240000",
        "mate.set_fixed|101|240000|240000",
        "mate.set_parameter|101|240000|240000",
        "pattern.circular|101|120000|120000",
        "pattern.grid|101|120000|120000",
        "pattern.mirror|101|120000|120000",
        "pattern.read|011|120000|120000",
        "probe.units|101|120000|120000",
        "session.inventory|001|120000|120000",
        "sketch.constraint.create|101|240000|120000",
        "sketch.constraint.delete|101|240000|120000",
        "sketch.constraint.list|011|240000|120000",
        "sketch.create|101|120000|120000",
        "sketch.dimension.create|101|240000|120000",
        "sketch.edit|101|120000|120000",
        "sketch.entities|011|240000|240000",
        "sketch.entity_edit|101|240000|240000",
        "sketch.finish|101|120000|120000",
        "sketch.list|011|120000|120000",
        "sketch.set_plane|101|240000|240000",
        "sketch.status|011|120000|120000",
        "solid.boolean|101|240000|240000",
        "solid.cut_by_plane|101|240000|240000",
        "solid.reposition|101|240000|240000",
        "solid.split|101|240000|240000",
        "sys.ping|000|2000|2000",
        "sys.shutdown|001|120000|120000",
        "topo.read|011|120000|120000",
        "var.create|101|240000|120000",
        "var.list|011|120000|120000",
        "var.set_expression|101|240000|120000",
        "var.set_value|101|240000|120000",
    };

    private static IEnumerable<SnapshotRow> Snapshot() => SnapshotBeforeTheTable.Select(line =>
    {
        var parts = line.Split('|');
        Assert.Equal(4, parts.Length);
        return new SnapshotRow(
            parts[0],
            parts[1][0] == '1',
            parts[1][1] == '1',
            parts[1][2] == '1',
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    });

    private sealed record SnapshotRow(
        string Command, bool ChangesModel, bool MustNotAnswer, bool Observes, int WorkerMs, int ContractsMs);

    [Fact]
    public void EveryCommandConstantHasExactlyOneRowAndEveryRowIsAConstant()
    {
        var values = CommandConstants.Select(row => row.Value).ToArray();
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());

        var commands = WorkerCommandTable.All.Select(row => row.Command).ToArray();
        Assert.Equal(commands.Length, commands.Distinct(StringComparer.Ordinal).Count());

        // No service exceptions: every constant of WorkerCommands names a command the table describes,
        // and the table names nothing else. A row without a constant would be a command nobody can send,
        // and a constant without a row would be answered by the unknown-command fallback.
        var withoutRow = values.Except(commands, StringComparer.Ordinal).ToArray();
        var withoutConstant = commands.Except(values, StringComparer.Ordinal).ToArray();

        Assert.True(withoutRow.Length == 0, "константы без строки таблицы: " + string.Join(", ", withoutRow));
        Assert.True(withoutConstant.Length == 0,
            "строки таблицы без константы: " + string.Join(", ", withoutConstant));
    }

    [Fact]
    public void EveryCadLaneCommandHasAHandlerInTheDispatcher()
    {
        var arms = HandlerArms();

        var unhandled = WorkerCommandTable.All
            .Where(row => row.Lane == WorkerLane.Cad)
            .Where(row => !NameByCommand.TryGetValue(row.Command, out var name) || !arms.Contains(name))
            .Select(row => row.Command)
            .ToArray();

        // LIMIT: the check is by SOURCE - the dispatcher does not load without KOMPAS, so a handler is
        // proved by its switch arm, not by an executed call. An absent arm is the Worker's own refusal
        // CAPABILITY_UNAVAILABLE, which is exactly what this test must not let through.
        Assert.True(unhandled.Length == 0,
            "команды без обработчика в диспетчере Worker: " + string.Join(", ", unhandled));
    }

    [Fact]
    public void NoHandlerNamesACommandOutsideTheTable()
    {
        var table = WorkerCommandTable.All.Select(row => row.Command).ToHashSet(StringComparer.Ordinal);

        var outside = HandlerArms()
            .Where(name => !CommandByName.TryGetValue(name, out var command) || !table.Contains(command))
            .ToArray();

        Assert.True(outside.Length == 0,
            "диспетчер обрабатывает команды вне таблицы: " + string.Join(", ", outside));
    }

    [Fact]
    public void TheWorkerRestatesNoBudgetOfItsOwn()
    {
        // A second budget table can only appear as a number in the Worker. INVARIANT: the dispatcher
        // carries no millisecond literal at all, so the budget cannot drift from the table again.
        var source = File.ReadAllText(DispatcherPath());
        var numbers = Regex.Matches(source, @"\b\d{1,3}_\d{3}\b|\b\d{5,}\b")
            .Select(match => match.Value)
            .ToArray();

        Assert.True(numbers.Length == 0,
            "в диспетчере снова есть число, похожее на бюджет: " + string.Join(", ", numbers));
    }

    [Fact]
    public void TheHostWaitsStrictlyLongerThanTheWorkerForEveryCommand()
    {
        foreach (var row in WorkerCommandTable.All)
        {
            // The Host reads the number the Worker enforces, from the table the Worker reads.
            Assert.Equal(row.WorkerBudgetMs, CommandBudgets.WorkerBudgetMs(row.Command));

            var host = CommandBudgets.HostBudgetMs(row.Command);
            Assert.True(host > row.WorkerBudgetMs,
                $"{row.Command}: бюджет Хоста {host} мс обязан быть СТРОГО больше бюджета Worker " +
                $"{row.WorkerBudgetMs} мс, иначе Хост первым объявляет «исход неизвестен» и перезапускает " +
                "работающий Worker");
            Assert.Equal(row.WorkerBudgetMs + CommandBudgets.HostMarginMs, host);
        }
    }

    [Fact]
    public void ClassificationIsTheSnapshotTakenBeforeTheTable()
    {
        foreach (var row in Snapshot())
        {
            Assert.Equal(row.ChangesModel, WorkerCommandTable.ChangesModel(row.Command));
            Assert.Equal(row.MustNotAnswer, WorkerCommandTable.MustNotAnswerAcrossALoss(row.Command));
            Assert.Equal(row.Observes, WorkerCommandTable.ObservesConnectionLoss(row.Command));
        }
    }

    [Fact]
    public void NoBudgetBecameShorterAndTheMergedValueIsTheLargerOfTheTwo()
    {
        foreach (var row in Snapshot())
        {
            var merged = WorkerCommandTable.BudgetMs(row.Command);

            Assert.True(merged >= row.WorkerMs,
                $"{row.Command}: бюджет Worker стал короче - {merged} мс вместо {row.WorkerMs} мс");
            Assert.Equal(Math.Max(row.WorkerMs, row.ContractsMs), merged);
        }
    }

    [Fact]
    public void AnUnknownCommandIsAnsweredAsACadReadWithTheDefaultBudget()
    {
        const string unknown = "нет-такой-команды";

        // Both former tables answered an unknown name the same way, and the dispatcher still refuses it
        // as CAPABILITY_UNAVAILABLE after this lookup; the fallback must not invent a shorter wait.
        Assert.Equal(WorkerLane.Cad, WorkerCommandTable.Of(unknown).Lane);
        Assert.Equal(WorkerAction.Read, WorkerCommandTable.Of(unknown).Action);
        Assert.Equal(CommandBudgets.DefaultMs, WorkerCommandTable.BudgetMs(unknown));
        Assert.False(WorkerCommandTable.ChangesModel(unknown));
        Assert.True(WorkerCommandTable.MustNotAnswerAcrossALoss(unknown));
        Assert.True(WorkerCommandTable.ObservesConnectionLoss(unknown));
    }

    /// <summary>Tools whose command WRITES A FILE and therefore carries an operation_id, while the model
    /// is untouched: the table says <see cref="WorkerAction.FileWrite"/>, the Host says mutation, and
    /// both are right - the journal guards the file, not the model.
    /// History: docs/decisions/adapter-core.md#disconnect-exports</summary>
    private static readonly string[] FileWritingToolsWithAnOperationId =
    {
        "kompas_export_step",
        "kompas_export_stl",
        "kompas_export_image",
    };

    [Fact]
    public void TheTableActionDoesNotContradictTheHostMutationFlag()
    {
        var contradictions = new List<string>();
        var fileWrites = new List<string>();

        foreach (var tool in ToolCatalog.All)
        {
            var action = WorkerCommandTable.Of(tool.WorkerCommand).Action;

            if (action == WorkerAction.ModelMutation && !tool.IsMutation)
            {
                contradictions.Add($"{tool.Name} -> {tool.WorkerCommand}");
            }

            if (action == WorkerAction.FileWrite && tool.IsMutation)
            {
                fileWrites.Add(tool.Name);
            }
        }

        Assert.True(contradictions.Count == 0,
            "таблица называет команду изменением модели, а инструмент не требует operation_id: " +
            string.Join(", ", contradictions));

        // The named exception list is the ONLY place a file-writing tool may carry an operation_id: a
        // new one must be added here deliberately, not slip through.
        var unexpected = fileWrites
            .Distinct(StringComparer.Ordinal)
            .Except(FileWritingToolsWithAnOperationId, StringComparer.Ordinal)
            .ToArray();
        var missing = FileWritingToolsWithAnOperationId
            .Except(fileWrites, StringComparer.Ordinal)
            .ToArray();

        Assert.True(unexpected.Length == 0 && missing.Length == 0,
            "перечень пишущих файл инструментов с operation_id разошёлся с каталогом; " +
            "вне перечня: " + string.Join(", ", unexpected) +
            "; в перечне, но не найден: " + string.Join(", ", missing));
    }

    private static string DispatcherPath() =>
        Path.Combine(RepoRoot, "src", "KompasMcp.Worker", "CommandDispatcher.cs");

    /// <summary>Names of the constants used as a switch arm in the dispatcher.</summary>
    private static HashSet<string> HandlerArms() =>
        Regex.Matches(File.ReadAllText(DispatcherPath()), @"WorkerCommands\.(\w+)\s*=>")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KompasMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }
}
