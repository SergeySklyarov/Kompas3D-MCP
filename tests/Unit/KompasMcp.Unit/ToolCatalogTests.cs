using System.Text.Json.Nodes;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// A registered tool must be callable with exactly the arguments its own published schema demands.
/// Two defects broke that at once, and both were invisible to every acceptance run until a test
/// finally called <c>kompas_read_topology</c> over MCP: an entry declared through <c>Mutation()</c>
/// with <c>requiresOperationId: false</c> never published the field the Host then routed through
/// the journal, and <c>IsMutation</c> is <c>Destructive || RequiresOperationId</c> — so the journal
/// received a null operation id and the call died inside the Host as ArgumentNullException before
/// КОМПАС was ever reached. A tool that cannot be called is exactly the stub the catalog forbids.
/// </summary>
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    public static TheoryData<string> MutationNames { get; } = new(
        ToolCatalog.All.Where(t => t.IsMutation).Select(t => t.Name));

    [Theory]
    [MemberData(nameof(MutationNames))]
    public void Mutation_PublishesTheOperationIdItDemands(string name)
    {
        var schema = Tool(name).InputSchema;
        var properties = schema["properties"] as JsonObject;
        var required = schema["required"] as JsonArray;

        Assert.NotNull(properties);
        Assert.True(
            properties!.ContainsKey("operation_id"),
            $"{name}: помечен как мутация, но схема не объявляет operation_id — обязательное поле " +
            "остаётся недостижимым для клиента, а strict-валидация отвергает вызов.");
        Assert.NotNull(required);
        Assert.Contains("operation_id", required!.Select(node => node!.GetValue<string>()));
    }

    [Theory]
    [InlineData("kompas_read_topology")]
    [InlineData("kompas_resolve_selection")]
    public void ReadsThatMintHandles_AreNotRoutedThroughTheJournal(string name)
    {
        var tool = Tool(name);

        // These two register structural references, but they change neither document nor model, so
        // docs/02 §2.1 (operation_id is demanded of mutations) makes them reads. The assertion that
        // matters is IsMutation == false: that is the flag choosing between the journal and a
        // direct dispatch, and while it was true the schema published no operation_id to fill it.
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
    }

    [Fact]
    public void NonMutations_DoNotAdvertiseOperationId()
    {
        foreach (var tool in ToolCatalog.All.Where(t => !t.IsMutation))
        {
            var properties = tool.InputSchema["properties"] as JsonObject;
            Assert.True(
                properties is null || !properties.ContainsKey("operation_id"),
                $"{tool.Name}: не мутация, но схема обещает operation_id — поле, которое клиент " +
                "может прислать и которое журнал не записывает.");
        }
    }

    [Fact]
    public void Catalog_NamesEveryWorkerCommandOnce()
    {
        Assert.Equal(
            ToolCatalog.All.Count,
            ToolCatalog.All.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ToolCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t.WorkerCommand)));
    }

    [Fact]
    public void SketchStatus_IsAReadThatMintsNoHandles()
    {
        // Инструмент отвечает на вопрос о СОСТОЯНИИ уже существующего эскиза: он не создаёт
        // ссылок, не меняет модель и не входит в режим правки. Значит, он обязан быть чтением —
        // иначе вызов попадёт в журнал мутаций и поднимет ревизию за операцию, которой не было.
        var tool = Tool("kompas_get_sketch_status");

        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.Equal("sketch.status", tool.WorkerCommand);
    }

    [Fact]
    public void SketchStatus_DemandsASketchRefAndNothingElse()
    {
        // Адресация объявлена явной ссылкой. Ни активного документа, ни выделения, ни
        // «первого попавшегося эскиза»: без этой проверки следующий рефактор может добавить
        // удобное необязательное поле и вернуть угадывание в контракт.
        var schema = Tool("kompas_get_sketch_status").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("sketch_ref", required);
        Assert.Single(required);
        Assert.True(
            properties.Count is 1 or 2,
            "kompas_get_sketch_status: схема обещает больше полей, чем контракт — лишнее поле " +
            "клиент может прислать, и никто не скажет, что оно значило.");
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void SketchStatus_DescriptionCarriesTheHonestLimits()
    {
        // Описание — единственное место, где клиент прочитает, чего инструмент НЕ говорит.
        // Три утверждения из постановки должны выжить следующую правку каталога: значение «!»
        // не подтверждено живьём, степеней свободы нет, чтение не меняет модель.
        var description = Tool("kompas_get_sketch_status").Description;

        Assert.Contains("degrees_of_freedom всегда null", description, StringComparison.Ordinal);
        Assert.Contains("unresolved_redundancy_not_verified", description, StringComparison.Ordinal);
        Assert.Contains("не меняет ревизию", description, StringComparison.Ordinal);
        Assert.Contains("+", description, StringComparison.Ordinal);
        Assert.Contains("−", description, StringComparison.Ordinal);
        Assert.Contains("!", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ListBodies_PublishesTheHandleContractItsReadersDependOn()
    {
        // Measured on 16.09.2026 (rows EX34/EX43): four consecutive calls on an unchanged body
        // returned four different strings, while the earlier string stayed usable for kompas_measure.
        // That makes a body reference a handle, not an identifier — and a tester who does not know it
        // writes an assertion that can only fail, as EX43 did twice before switching to bbox
        // comparison. The description is the only place a client of this tool will read it, so it is
        // asserted here rather than left to survive by luck through the next catalog edit.
        var description = Tool("kompas_list_bodies").Description;

        Assert.Contains("ручка", description, StringComparison.Ordinal);
        Assert.Contains("габарит", description, StringComparison.Ordinal);
        Assert.Contains("STALE_REFERENCE", description, StringComparison.Ordinal);
    }
}
