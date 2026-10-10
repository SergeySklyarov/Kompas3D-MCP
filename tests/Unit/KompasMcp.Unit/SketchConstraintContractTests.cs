using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: the constraint-type vocabulary published in the schema and the one the adapter
/// resolves must be the SAME set, and the three G6 tools must be registered under the commands the
/// Worker handles.</summary>
/// <remarks>TEST: the adapter's own vocabulary is checked against the constants assembly at runtime
/// (<c>Api7SketchConstraints.VocabularyProblems</c>); here the CONTRACT side is pinned, so a name
/// dropped from the list or an unregistered tool is caught without КОМПАС.
/// History: docs/decisions/tests.md#sketch-constraints</remarks>
public sealed class SketchConstraintContractTests
{
    [Fact]
    public void Vocabulary_IsTheTwentyTwoNamedTypes()
    {
        Assert.Equal(22, SketchConstraintTypes.All.Count);
        Assert.Equal(SketchConstraintTypes.All.Count, SketchConstraintTypes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("ksCUnknown", SketchConstraintTypes.All);
        Assert.Contains(SketchConstraintTypes.DimensionWithVariable, SketchConstraintTypes.All);
    }

    /// <summary>The agreement check must NAME a difference in both directions — a silently empty answer
    /// would make "the two sides agree" indistinguishable from "nothing was compared".</summary>
    [Fact]
    public void Agreement_NamesDifferencesInBothDirections()
    {
        var declared = SketchConstraintTypes.All
            .Where(name => name != "ksCVertical")
            .Append("ksCInvented")
            .ToList();

        var problems = SketchConstraintTypes.AgreementProblems(declared);

        Assert.Contains(problems, p => p.Contains("ksCVertical", StringComparison.Ordinal)
                                       && p.Contains("отсутствует в ksConstraintTypeEnum", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ksCInvented", StringComparison.Ordinal)
                                       && p.Contains("отсутствует здесь", StringComparison.Ordinal));
    }

    [Fact]
    public void Agreement_IsEmptyForTheDeclaredSet()
    {
        Assert.Empty(SketchConstraintTypes.AgreementProblems(SketchConstraintTypes.All));
    }

    /// <summary>The zero value is declared in the enum and deliberately NOT offered. It must not be
    /// reported as a disagreement, and it must not be offered either — otherwise a call could ask for
    /// "some constraint".</summary>
    [Fact]
    public void Agreement_IgnoresTheZeroValueAndTheEnumCompanion()
    {
        Assert.DoesNotContain("ksCUnknown", SketchConstraintTypes.All);
        Assert.True(SketchConstraintTypes.DeliberatelyNotOffered.ContainsKey("ksCUnknown"));
        Assert.Empty(SketchConstraintTypes.AgreementProblems(
            SketchConstraintTypes.All.Append("ksCUnknown").Append("value__")));
    }

    [Theory]
    [InlineData("kompas_create_sketch_constraint", "sketch.constraint.create", true)]
    [InlineData("kompas_list_sketch_constraints", "sketch.constraint.list", false)]
    [InlineData("kompas_delete_sketch_constraint", "sketch.constraint.delete", true)]
    public void Tool_IsRegisteredWithItsCommand(string toolName, string command, bool mutation)
    {
        var tool = ToolCatalog.All.SingleOrDefault(t => t.Name == toolName);
        Assert.NotNull(tool);
        Assert.Equal(command, tool!.WorkerCommand);
        Assert.Equal(mutation, !tool.Behaviour.ReadOnly);
    }

    /// <summary>INVARIANT: the schema refuses a type outside the declared set BEFORE COM. The check
    /// happens in the Host's validator, so it is exercised here on the published schema.</summary>
    [Fact]
    public void Schema_RefusesAnUnknownConstraintType()
    {
        var tool = ToolCatalog.All.Single(t => t.Name == "kompas_create_sketch_constraint");
        var schema = tool.InputSchema;

        var declared = schema["properties"]!["constraint_type"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToList();
        Assert.Equal(SketchConstraintTypes.All.Count, declared.Count);

        // The validator is exercised through the Host's own entry point, so a schema that merely LISTS
        // the values but does not enforce them would fail here.
        var valid = JsonNode.Parse("""
            {"sketch_ref":"sketch:1","expected_revision":1,"constraint_type":"ksCHorizontal",
             "object_ref":"addr-1","operation_id":"11111111-1111-1111-1111-111111111111"}
            """)!;
        var invalid = JsonNode.Parse("""
            {"sketch_ref":"sketch:1","expected_revision":1,"constraint_type":"ksCInvented",
             "object_ref":"addr-1","operation_id":"22222222-2222-2222-2222-222222222222"}
            """)!;

        Assert.Empty(JsonSchemaValidator.Validate(schema, valid));
        Assert.NotEmpty(JsonSchemaValidator.Validate(schema, invalid));
    }
}
