using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Домен сборок (наряд C1, профиль <c>assemblies-minimal-v1</c>): контракт пяти инструментов и
/// честность их описаний.
/// </summary>
/// <remarks>
/// Живого прогона по сборке не было, поэтому тесты проверяют ровно то, что проверяемо без
/// КОМПАСа: инструменты зарегистрированы, мутации объявляют обязательные поля, схемы строгие, а
/// описания называют неподтверждённость, а не молчат о ней. Проверка «маршрут работает» сюда НЕ
/// входит и не подменяется — она требует прогона на v24.0.0.2799.
/// </remarks>
public class AssemblyDomainTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static readonly (string Name, string Command)[] Domain =
    {
        ("kompas_list_components", "asm.list_components"),
        ("kompas_insert_component", "asm.insert_component"),
        ("kompas_set_component_placement", "asm.set_placement"),
        ("kompas_replace_component", "asm.replace_component"),
        ("kompas_check_component_links", "asm.check_links"),
    };

    public static TheoryData<string> AllTools { get; } = new(Domain.Select(d => d.Name));

    [Theory]
    [MemberData(nameof(AllTools))]
    public void DomainTool_IsRegisteredWithItsOwnWorkerCommand(string name)
    {
        var tool = Tool(name);
        Assert.Equal(Domain.Single(d => d.Name == name).Command, tool.WorkerCommand);
        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
    }

    [Theory]
    [InlineData("kompas_insert_component")]
    [InlineData("kompas_set_component_placement")]
    [InlineData("kompas_replace_component")]
    public void AssemblyMutation_DeclaresOperationIdAndExpectedRevision(string name)
    {
        var tool = Tool(name);
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        var required = ((JsonArray)tool.InputSchema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.True(tool.IsMutation);
        Assert.Contains("operation_id", properties.Select(p => p.Key));
        Assert.Contains("operation_id", required);
        Assert.Contains("expected_revision", required);
        Assert.Contains("document_id", required);
    }

    [Theory]
    [InlineData("kompas_list_components")]
    [InlineData("kompas_check_component_links")]
    public void AssemblyRead_IsNotAMutation(string name)
    {
        var tool = Tool(name);
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.True(tool.Behaviour.RequiresDocument);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void AssemblySchema_IsStrictAndRequiresDocument(string name)
    {
        var schema = Tool(name).InputSchema;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("document_id", required);
    }

    [Theory]
    [MemberData(nameof(AllTools))]
    public void AssemblyTool_DescriptionNamesTheUnverifiedState(string name)
    {
        // Единственное место, где клиент прочитает, что маршрут НЕ подтверждён живьём. Молчание
        // сделало бы «реализовано» неотличимым от «проверено», и это ровно тот дефект, который
        // проект запрещает. Проверка держит формулировку живой через следующую правку каталога.
        var description = Tool(name).Description;
        Assert.Contains("приёмк", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InsertComponent_RequiresSourcePathAndKeepsTransformOptional()
    {
        // Источник обязателен (без него вставлять нечего), а размещение — нет: сервер не выдумывает
        // начало координат, а оставляет документированное умолчание КОМПАСа.
        var schema = Tool("kompas_insert_component").InputSchema;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("source_path", required);
        Assert.DoesNotContain("transform", required);
    }

    [Fact]
    public void ComponentRow_KeepsUnreadFieldsNullable()
    {
        // Пустое поле означает «не прочитано», а не ноль: кратность и признак «деталь/сборка»
        // объявлены nullable, чтобы «не прочитал» было отличимо от «прочитал ноль».
        var row = new ComponentRowDto
        {
            ComponentRef = "component:00000000000000000000000000000000",
            Depth = 0,
        };

        Assert.Null(row.InstanceCount);
        Assert.Null(row.IsDetail);
        Assert.Null(row.LoadState);
        Assert.Null(row.Matrix);
    }
}
