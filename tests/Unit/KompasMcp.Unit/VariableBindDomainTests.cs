using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Variable creation and feature-parameter binding (block G3): the contract of the three tools,
/// the honesty of their descriptions, and the PURE addressing rule.</summary>
/// <remarks>TEST: only what is checkable WITHOUT KOMPAS — registration, worker-command mapping, the mandatory
/// fields of mutations, the read route that must carry no revision, the nullability that keeps "not read"
/// distinct from "read zero", the exact-name addressing rule, and the constant expression a created variable
/// carries. "The route works" is NOT included and not substituted: it needs a run on the target version.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public class VariableBindDomainTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static readonly (string Name, string Command)[] Domain =
    {
        ("kompas_create_variable", "var.create"),
        ("kompas_list_feature_parameters", "feat.parameters"),
        ("kompas_bind_parameter", "feat.bind_parameter"),
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
    [MemberData(nameof(AllTools))]
    public void DomainSchema_IsStrictAndRequiresDocument(string name)
    {
        var schema = Tool(name).InputSchema;
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("document_id", required);
    }

    /// <summary>The two mutating tools declare operation_id and expected_revision.</summary>
    /// <remarks>TEST: both writes go through the shared mutation/revision/journal mechanism, so both must
    /// publish the fields their own refusal paths demand. History: docs/decisions/variables-material.md</remarks>
    [Theory]
    [InlineData("kompas_create_variable")]
    [InlineData("kompas_bind_parameter")]
    public void WriteRoute_DeclaresOperationIdAndExpectedRevision(string name)
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

    /// <summary>The read route is NOT a mutation and demands no revision.</summary>
    /// <remarks>TEST: reading the parameter array must leave the revision alone and need no
    /// <c>expected_revision</c>; a read that demanded one would refuse a legitimate call on a document someone
    /// else had mutated. History: docs/decisions/variables-material.md#g3-route</remarks>
    [Fact]
    public void ReadRoute_IsNotAMutationAndCarriesNoRevision()
    {
        var tool = Tool("kompas_list_feature_parameters");
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.False(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.DoesNotContain("expected_revision", properties.Select(p => p.Key));
    }

    [Fact]
    public void CreateVariable_RequiresNameAndValueButExpressionIsOptional()
    {
        var schema = Tool("kompas_create_variable").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("name", required);
        Assert.Contains("value", required);
        // INVARIANT: an empty expression is not a supported kernel state, so an omitted expression becomes a
        // constant equal to the value - the field is optional and nullable, never required-empty.
        Assert.Contains("expression", properties.Select(p => p.Key));
        Assert.DoesNotContain("expression", required);
        Assert.Equal(1, properties["name"]!["minLength"]!.GetValue<int>());
    }

    [Fact]
    public void BindParameter_RequiresFeatureParameterAndNonEmptyExpression()
    {
        var schema = Tool("kompas_bind_parameter").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("feature_ref", required);
        Assert.Contains("parameter_name", required);
        Assert.Contains("expression", required);
        Assert.Equal(1, properties["parameter_name"]!["minLength"]!.GetValue<int>());
        // An empty expression is refused before COM, so the schema forbids the empty string outright.
        Assert.Equal(1, properties["expression"]!["minLength"]!.GetValue<int>());
        // The declared expectation is optional.
        Assert.DoesNotContain("expected_volume_mm3", required);
    }

    [Fact]
    public void ExternalField_IsNullableBooleanDefaultingToTrue()
    {
        var properties = (JsonObject)Tool("kompas_create_variable").InputSchema["properties"]!;
        var external = (JsonObject)properties["external"]!;
        var types = ((JsonArray)external["type"]!).Select(t => t!.GetValue<string>()).ToArray();
        Assert.Contains("boolean", types);
        Assert.Contains("null", types);
        Assert.True(external["default"]!.GetValue<bool>());
    }

    [Fact]
    public void CreateVariable_DescriptionNamesTheNameRuleAndTheEmptyExpressionBoundary()
    {
        var description = Tool("kompas_create_variable").Description;
        Assert.Contains("IsVariableNameValid", description, StringComparison.Ordinal);
        Assert.Contains("НЕ поддерживается", description, StringComparison.Ordinal);
        Assert.Contains("ПЕРЕЧИТЫВАНИЕМ", description, StringComparison.Ordinal);
        // The third AddVariable argument is a NOTE, not a formula - a boundary the client must not misread.
        Assert.Contains("ПРИМЕЧАНИЕ", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ListFeatureParameters_DescriptionNamesTheAddressByNameAndTheNullRule()
    {
        var description = Tool("kompas_list_feature_parameters").Description;
        Assert.Contains("parameterNote", description, StringComparison.Ordinal);
        Assert.Contains("ИМЯ", description, StringComparison.Ordinal);
        Assert.Contains("null", description, StringComparison.Ordinal);
        Assert.Contains("пустой список", description, StringComparison.Ordinal);
    }

    [Fact]
    public void BindParameter_DescriptionNamesTheExactAddressAndThatRebuildIsNotConfirmation()
    {
        var description = Tool("kompas_bind_parameter").Description;
        Assert.Contains("ТОЧНЫМ именем", description, StringComparison.Ordinal);
        Assert.Contains("НЕ происходит", description, StringComparison.Ordinal);
        Assert.Contains("expression_read_back", description, StringComparison.Ordinal);
        Assert.Contains("value_after", description, StringComparison.Ordinal);
        Assert.Contains("rebuild_succeeded", description, StringComparison.Ordinal);
        Assert.Contains("volume_delta", description, StringComparison.Ordinal);
        Assert.Contains("СНЯТИЕ", description, StringComparison.Ordinal);
    }

    // ── the PURE addressing rule ────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectParameter_FindsTheOnlyExactMatch()
    {
        var result = VariableBindRules.SelectParameter(new[] { "v20", "v21", "Расстояние 2" }, "v21");
        Assert.Equal(ParameterAddressVerdict.Found, result.Verdict);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void SelectParameter_DoesNotMatchAPartialName()
    {
        // The address is the FULL name: "v2" must not resolve to "v20".
        var result = VariableBindRules.SelectParameter(new[] { "v20" }, "v2");
        Assert.Equal(ParameterAddressVerdict.NotFound, result.Verdict);
    }

    [Fact]
    public void SelectParameter_ReportsAnAbsentNameAsNotFoundWithTheNamesItSaw()
    {
        var result = VariableBindRules.SelectParameter(new[] { "v20", "v21" }, "ghost");
        Assert.Equal(ParameterAddressVerdict.NotFound, result.Verdict);
        Assert.Equal(-1, result.Index);
        Assert.Equal(new string?[] { "v20", "v21" }, result.Names);
    }

    [Fact]
    public void SelectParameter_ReportsSeveralExactMatchesAsAmbiguousAndResolvesNoIndex()
    {
        // INVARIANT: an ambiguous address is never resolved by position - the value cannot tell them apart.
        var result = VariableBindRules.SelectParameter(new[] { "v20", "v20" }, "v20");
        Assert.Equal(ParameterAddressVerdict.Ambiguous, result.Verdict);
        Assert.Equal(-1, result.Index);
    }

    [Fact]
    public void SelectParameter_NeverMatchesAnUnreadName()
    {
        var result = VariableBindRules.SelectParameter(new string?[] { null, "v20" }, "v20");
        Assert.Equal(ParameterAddressVerdict.Found, result.Verdict);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void ConstantExpression_RoundTripsThroughTheExpressionText()
    {
        Assert.Equal("10", VariableBindRules.ConstantExpression(10d));
        Assert.Equal("0.5", VariableBindRules.ConstantExpression(0.5d));
        // The expression reads back exactly the number the caller asked for.
        Assert.Equal(12.34, double.Parse(VariableBindRules.ConstantExpression(12.34),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SameValue_ComparesNumericallyNotTextually()
    {
        Assert.True(VariableBindRules.SameValue(10d, 10d));
        Assert.True(VariableBindRules.SameValue(176000d, 176000d + 1e-6));
        Assert.False(VariableBindRules.SameValue(80000d, 160000d));
    }

    // ── the response shape keeps "not read" apart from "read zero" ───────────────────────────────

    [Fact]
    public void ParameterRow_KeepsUnreadFieldsNullable()
    {
        var row = new FeatureParameterRowDto(0, null, null, null, null, null, null);
        Assert.Null(row.Name);
        Assert.Null(row.ParameterNote);
        Assert.Null(row.Value);
        Assert.Null(row.External);
    }

    [Fact]
    public void BindResult_ExposesTheReadBackFieldsTheRefusalUses()
    {
        var result = new BindParameterResult(
            "feature:1", "v20", "Расстояние 1", "depth", "depth", 10d, true,
            84000d, 84000d, 0d, null, null, 3L, 4L,
            new VerificationDto(VerificationLevel.GeometryChecked, Array.Empty<NamedCheck>(),
                Array.Empty<string>()),
            Array.Empty<string>());
        Assert.Equal("depth", result.ExpressionReadBack);
        Assert.Equal(10d, result.ValueAfter);
        Assert.True(result.RebuildSucceeded);
        Assert.Null(result.ExpectedVolumeMatched);
    }
}
