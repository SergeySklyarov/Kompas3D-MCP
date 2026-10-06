using System.Reflection;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Variable and material domain (block VM): the contract of the four tools, the honesty of their
/// descriptions, and the DOCUMENTED unit conversions.</summary>
/// <remarks>TEST: only what is checkable WITHOUT KOMPAS — registration, worker-command mapping, the mandatory
/// fields of mutations, the read routes that must carry no revision, the nullability that keeps "not read"
/// distinct from "read zero", the named boundaries in the descriptions, and the three density conversions
/// with the documented round trip. The "the route works" check is NOT included and not substituted: it needs
/// a run on the target version. History: docs/decisions/variables-material.md#tests</remarks>
public class VariableMaterialDomainTests
{
    private static ToolDefinition Tool(string name) =>
        ToolCatalog.All.SingleOrDefault(t => t.Name == name)
        ?? throw new InvalidOperationException($"Инструмент {name} в каталоге не найден.");

    private static readonly (string Name, string Command)[] Domain =
    {
        ("kompas_list_variables", "var.list"),
        ("kompas_set_variable", "var.set_value"),
        ("kompas_get_material", "mat.get"),
        ("kompas_set_material", "mat.set"),
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

    [Theory]
    [InlineData("kompas_set_variable")]
    [InlineData("kompas_set_material")]
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

        // The command name actually dispatched by the Worker must differ from the tool name: both writes go
        // through the shared mutation/revision/journal mechanism, and the expression mode shares the handler.
        Assert.Equal(Domain.Single(d => d.Name == name).Command, tool.WorkerCommand);
    }

    /// <summary>The two read routes must NOT be mutations and must NOT demand a revision.</summary>
    /// <remarks>TEST: reading variables is explicitly required to leave the revision alone and to need no
    /// <c>expected_revision</c>; a read that demanded a revision would refuse a legitimate call on a document
    /// that was mutated by someone else. History: docs/decisions/variables-material.md#read-variables</remarks>
    [Theory]
    [InlineData("kompas_list_variables")]
    [InlineData("kompas_get_material")]
    public void ReadRoute_IsNotAMutationAndCarriesNoRevision(string name)
    {
        var tool = Tool(name);
        var properties = (JsonObject)tool.InputSchema["properties"]!;
        Assert.False(tool.IsMutation);
        Assert.False(tool.Behaviour.Destructive);
        Assert.False(tool.Behaviour.RequiresOperationId);
        Assert.False(tool.Behaviour.RequiresExpectedRevision);
        Assert.True(tool.Behaviour.RequiresDocument);
        Assert.DoesNotContain("expected_revision", properties.Select(p => p.Key));
    }

    [Fact]
    public void SetVariable_ValueAndExpressionAreBothNullableButNeitherRequired()
    {
        // INVARIANT: the mode is "exactly one of two", which JSON Schema cannot express as a required field.
        // Both are therefore declared nullable and OPTIONAL, and the refusal for "both or neither" is the
        // contract's job — before COM, named by a code.
        var schema = Tool("kompas_set_variable").InputSchema;
        var properties = (JsonObject)schema["properties"]!;
        var required = ((JsonArray)schema["required"]!).Select(n => n!.GetValue<string>()).ToArray();

        Assert.Contains("value", properties.Select(p => p.Key));
        Assert.Contains("expression", properties.Select(p => p.Key));
        Assert.DoesNotContain("value", required);
        Assert.DoesNotContain("expression", required);
        Assert.Contains("name", required);
    }

    [Fact]
    public void SetVariable_DescriptionNamesTheExpressionRefusalAndThatRebuildIsNotConfirmation()
    {
        // INVARIANT: the client must see that writing `value` over an active expression is refused, and that
        // RebuildModel = TRUE is an outcode rather than proof the model accepted the value.
        var description = Tool("kompas_set_variable").Description;
        Assert.Contains("выражени", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ОТКЛОНЯЕТСЯ", description, StringComparison.Ordinal);
        Assert.Contains("НЕ подтверждение", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ListVariables_DescriptionNamesTheScopeAndTheNullRule()
    {
        // INVARIANT: the collection must not be sold as a model-wide parameter editor, and an unread field
        // must not be filled with zero or an empty string.
        var description = Tool("kompas_list_variables").Description;
        Assert.Contains("ВНЕШНИЕ", description, StringComparison.Ordinal);
        Assert.Contains("НЕ редактор", description, StringComparison.Ordinal);
        Assert.Contains("null", description, StringComparison.Ordinal);
        Assert.Contains("external/top_part", description, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMaterial_DescriptionNamesTheUnconfirmedUnitAndTheNoSubstitutionRule()
    {
        var description = Tool("kompas_get_material").Description;
        // The unit is NOT confirmed, and the client must be able to see that from the description alone:
        // the page's unit, the measured divergence, the status and the absent normalized field are named.
        Assert.Contains("г/куб.мм", description, StringComparison.Ordinal);
        Assert.Contains("НЕ ПОДТВЕРЖДЕНА", description, StringComparison.Ordinal);
        Assert.Contains("density_raw", description, StringComparison.Ordinal);
        Assert.Contains("density_raw_unit_documented", description, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", description, StringComparison.Ordinal);
        Assert.Contains("density_normalized_kg_per_m3", description, StringComparison.Ordinal);
        Assert.Contains("НЕ подставляется", description, StringComparison.Ordinal);
        Assert.Contains("ИЗМЕРЕНО", description, StringComparison.Ordinal);
    }

    [Fact]
    public void SetMaterial_DescriptionSeparatesTheConfirmedNameFromTheUnconfirmedDensity()
    {
        var description = Tool("kompas_set_material").Description;
        Assert.Contains("г/куб.см", description, StringComparison.Ordinal);
        // The unit of the READING is not confirmed, so the description must not sell the raw equality as a
        // confirmation of the physical density: the status and the diagnostic-only field are named.
        Assert.Contains("НЕ ПОДТВЕРЖДЕНА", description, StringComparison.Ordinal);
        Assert.Contains("unconfirmed", description, StringComparison.Ordinal);
        Assert.Contains("density_raw_numeric_matches", description, StringComparison.Ordinal);
        // The documented limits must be visible, not only in the code.
        Assert.Contains("библиотеки моделей", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ВЕРХНИЙ компонент", description, StringComparison.Ordinal);
    }

    /// <summary>The write response keeps the CONFIRMED name apart from the UNCONFIRMED physical density.</summary>
    /// <remarks>TEST: with the reading unit unestablished, equal raw numbers prove nothing about the physical
    /// density, so the response must carry the confirmed name/outcodes separately from the unconfirmed density
    /// and must never fill a normalized value. History: docs/decisions/variables-material.md#material</remarks>
    [Fact]
    public void SetMaterialResult_KeepsConfirmedNameApartFromUnconfirmedDensity()
    {
        var result = new SetMaterialResult(
            RequestedName: "Сталь 45",
            RequestedDensityKgPerM3: 7850d,
            WrittenDensityGPerCm3: 7.85d,
            ReadNameAfter: "Сталь 45",
            NameMatches: true,
            ReadDensityRawAfter: 7.85d,
            DensityRawUnitDocumented: "g/mm3",
            DensityUnitStatus: "unconfirmed",
            DensityNormalizedKgPerM3: null,
            DensityRawNumericMatches: true,
            SetMaterialReturned: true,
            UpdateReturned: true,
            RevisionBefore: 3,
            RevisionAfter: 4,
            Diagnostics: new[] { "density_unit_unconfirmed" });

        Assert.True(result.NameMatches);
        // The numeric equality is present as a DIAGNOSTIC, and it does not turn into a density confirmation.
        Assert.True(result.DensityRawNumericMatches);
        Assert.Equal("unconfirmed", result.DensityUnitStatus);
        Assert.Null(result.DensityNormalizedKgPerM3);
    }

    /// <summary>The ONE documented density conversion, and the absence of the undocumented one.</summary>
    /// <remarks>TEST: <c>SetMaterial</c> documents its density argument in g/cm3, so the caller's kg/m3 is
    /// divided by 1000. The READ side has no documented unit — the SDK page names g/mm3 for both
    /// <c>GetDensity</c> and <c>IMassInertiaParam7.Density</c>, while the installed build returns the value in
    /// g/cm3 — so <c>DensityUnits</c> deliberately exposes NO g/cm3 → kg/m3 conversion: that would be the
    /// server's own unit claim. History: docs/decisions/variables-material.md#units</remarks>
    [Fact]
    public void DensityWriteConversion_DividesByOneThousand()
    {
        Assert.Equal(7.85d, DensityUnits.KgPerM3ToGramsPerCm3(7850d), 12);
        Assert.Equal(8.5d, DensityUnits.KgPerM3ToGramsPerCm3(8500d), 12);

        // The undocumented direction must not exist at all: no member may turn a raw reading into kg/m3.
        Assert.Null(typeof(DensityUnits).GetMethod("GramsPerCm3ToKgPerM3"));
    }

    [Theory]
    [InlineData(1000d, 1d)]
    [InlineData(2700d, 2.7d)]
    [InlineData(7850d, 7.85d)]
    public void KgPerM3ToGramsPerCm3_DividesBy1000(double kgPerM3, double expected)
    {
        Assert.Equal(expected, DensityUnits.KgPerM3ToGramsPerCm3(kgPerM3), 12);
    }

    [Fact]
    public void MassFormula_UsesTheCallersDensityAndTheMeasuredVolume()
    {
        // The mass chain: the caller states a density and an independent measurement gives the volume. The
        // reference geometry of the acceptance window is a 100x80 rectangle extruded 10 → 20 mm, i.e.
        // 80000 → 160000 mm3; at 7850 kg/m3 the mass is 0.628 → 1.256 kg.
        Assert.Equal(0.628d, DensityUnits.MassKg(80000d, 7850d), 9);
        Assert.Equal(1.256d, DensityUnits.MassKg(160000d, 7850d), 9);

        // A DOUBLED depth doubles the mass — the reason the acceptance row can discriminate.
        Assert.Equal(2d, DensityUnits.MassKg(160000d, 7850d) / DensityUnits.MassKg(80000d, 7850d), 12);
    }

    /// <summary>A constant assignment method is the value; a formula or a reference computes it.</summary>
    /// <remarks>TEST: MEASURED — the kernel gives every user variable a non-empty <c>Expression</c> (a freshly
    /// added one carries the constant it was added with, and clearing it is not a supported state), so a rule
    /// refusing every non-empty expression would make the documented <c>value</c> mode unusable.
    /// History: docs/decisions/variables-material.md#set-variable</remarks>
    [Theory]
    [InlineData("10", true)]
    [InlineData("10.5", true)]
    [InlineData("-5", true)]
    [InlineData("1e3", true)]
    [InlineData("depth*2", false)]
    [InlineData("depth", false)]
    [InlineData("10 мм", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void VariableExpression_ClassifiesConstants(string? expression, bool expected)
    {
        Assert.Equal(expected, VariableExpression.IsPlainConstant(expression));
    }

    /// <summary>An unread value must not be representable as a read zero.</summary>
    /// <remarks>TEST: <see cref="GetMaterialResult"/> carries the name and the raw density as SEPARATE reads
    /// with their own flags, so a failed density read cannot be mistaken for a measured 0. The normalized
    /// density is present as a field but never filled, and the unit status says so by name. This is the same
    /// class of defect as the <c>4/tan</c> instrument fault: the field must not describe itself.
    /// History: docs/decisions/variables-material.md#units</remarks>
    [Fact]
    public void GetMaterialResult_SeparatesNameReadFromDensityRead()
    {
        var result = new GetMaterialResult(
            MaterialName: "Сталь 45",
            NameRead: true,
            DensityRaw: null,
            DensityRead: false,
            DensityRawUnitDocumented: "g/mm3",
            DensityUnitStatus: "unconfirmed",
            DensityNormalizedKgPerM3: null,
            Revision: 3,
            Diagnostics: new[] { "density_read_failed" });

        Assert.True(result.NameRead);
        Assert.False(result.DensityRead);
        Assert.Null(result.DensityRaw);
        Assert.Null(result.DensityNormalizedKgPerM3);
        Assert.Equal("g/mm3", result.DensityRawUnitDocumented);
        Assert.Equal("unconfirmed", result.DensityUnitStatus);
    }

    [Fact]
    public void ListVariablesResult_KeepsTotalAndReturnedDistinct()
    {
        // INVARIANT: a truncated answer must not be mistaken for a short collection — both numbers travel.
        var result = new ListVariablesResult(
            Variables: Array.Empty<VariableRowDto>(),
            Total: 7,
            Returned: 0,
            Truncated: true,
            Scope: "external/top_part",
            Revision: 1,
            Diagnostics: new[] { "truncated" });

        Assert.Equal(7, result.Total);
        Assert.Equal(0, result.Returned);
        Assert.True(result.Truncated);
        Assert.Equal("external/top_part", result.Scope);
    }

    [Fact]
    public void AllFourTools_ArePresentInTheCatalog()
    {
        // The block is one coherent unit: all four tools, or none.
        foreach (var (name, _) in Domain)
        {
            Assert.NotNull(Tool(name));
        }
    }

    /// <summary>A write is confirmed only by the VALUE read back, not by the write having been issued.</summary>
    /// <remarks>TEST: the defect this closes is <c>read_back_verified = (object exists)</c> — a value that was
    /// ignored by the kernel still looked confirmed. A stale number and an unread number must both withhold
    /// confirmation. History: docs/decisions/variables-material.md#set-variable</remarks>
    [Fact]
    public void WriteConfirmation_ValueMode_RequiresTheReadBackNumberToMatch()
    {
        var stale = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeValue,
            RequestedValue = 20d,
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = string.Empty,
            ValueAfterState = VariableFieldState.Read,
            ValueAfter = 10d, // the setter was ignored: the old value is still there
            ApplyResult = true,
        });
        Assert.False(stale.Confirmed);
        Assert.Contains("value_mismatch", stale.Reasons);

        var applied = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeValue,
            RequestedValue = 20d,
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = string.Empty,
            ValueAfterState = VariableFieldState.Read,
            ValueAfter = 20d,
            ApplyResult = true,
        });
        Assert.True(applied.Confirmed);
        Assert.Empty(applied.Reasons);
    }

    /// <summary>A field that was NOT read cannot confirm anything, whatever the object's existence.</summary>
    /// <remarks>TEST: this is the same class of defect as <c>4/tan</c>: the answer must not describe itself.
    /// A missing read is named (<c>value_not_read</c>), and the mere presence of the variable is not enough.
    /// History: docs/decisions/variables-material.md#set-variable</remarks>
    [Fact]
    public void WriteConfirmation_UnreadFieldDoesNotConfirm()
    {
        var unread = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeValue,
            RequestedValue = 20d,
            ExpressionAfterState = VariableFieldState.NotRead,
            ValueAfterState = VariableFieldState.NotRead,
            ApplyResult = true,
        });
        Assert.False(unread.Confirmed);
        Assert.Contains("value_not_read", unread.Reasons);
    }

    /// <summary>A false or unreported apply outcode is never turned into a confirmation.</summary>
    /// <remarks>TEST: <c>RebuildModel = FALSE</c> is the documented failure outcode, and a null means the call
    /// was not reported at all; neither may pass as a successful write.
    /// History: docs/decisions/variables-material.md#set-variable</remarks>
    [Theory]
    [InlineData(false, "apply_returned_false")]
    [InlineData(null, "apply_not_reported")]
    public void WriteConfirmation_FailedApplyDoesNotConfirm(bool? applyResult, string expectedReason)
    {
        var decision = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeValue,
            RequestedValue = 20d,
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = string.Empty,
            ValueAfterState = VariableFieldState.Read,
            ValueAfter = 20d,
            ApplyResult = applyResult,
        });

        Assert.False(decision.Confirmed);
        Assert.Contains(expectedReason, decision.Reasons);
    }

    /// <summary>An expression write is confirmed by the SAVED expression and the READ computed value.</summary>
    /// <remarks>TEST: the expression governs the number, so confirming only the object would let a rejected
    /// formula pass. The string must come back unchanged AND the kernel-computed value must have been read;
    /// the server never evaluates the expression itself.
    /// History: docs/decisions/variables-material.md#set-variable</remarks>
    [Fact]
    public void WriteConfirmation_ExpressionMode_RequiresSavedExpressionAndComputedValue()
    {
        var confirmed = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeExpression,
            RequestedExpression = "Глубина*2",
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = "Глубина*2",
            ValueAfterState = VariableFieldState.Read,
            ValueAfter = 20d,
            ApplyResult = true,
        });
        Assert.True(confirmed.Confirmed);

        var dropped = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeExpression,
            RequestedExpression = "Глубина*2",
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = string.Empty, // the kernel did not keep the formula
            ValueAfterState = VariableFieldState.Read,
            ValueAfter = 10d,
            ApplyResult = true,
        });
        Assert.False(dropped.Confirmed);
        Assert.Contains("expression_mismatch", dropped.Reasons);

        var uncomputed = VariableWriteConfirmation.Decide(new VariableWriteFacts
        {
            Mode = VariableWriteConfirmation.ModeExpression,
            RequestedExpression = "Глубина*2",
            ExpressionAfterState = VariableFieldState.Read,
            ExpressionAfter = "Глубина*2",
            ValueAfterState = VariableFieldState.NotRead,
            ApplyResult = true,
        });
        Assert.False(uncomputed.Confirmed);
        Assert.Contains("computed_value_not_read", uncomputed.Reasons);
    }
}
