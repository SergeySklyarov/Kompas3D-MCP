using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The circular-pattern orientation flag: its default, its meaning, and the difference between
/// "the client asked for false" and "the client said nothing".</summary>
/// <remarks>INVARIANT: the effective default of <c>save_initial_orientation</c> is FALSE (instances turn to the
/// radial direction). DOC: <c>icircularpattern_saveinitialorientation.html</c> - «TRUE - сохранять исходную
/// ориентацию, FALSE - доворачивать до радиального направления»; the page names no default, so the default is a
/// product decision and is declared in exactly one place.
/// MEASURED: with the default at TRUE a non-symmetric source (a slot) built a WRONG geometry while the call
/// returned success, and the volume told it apart only against an outside expectation.
/// History: docs/decisions/contracts.md#circular-orientation-default</remarks>
public class PatternCircularOrientationTests
{
    /// <summary>A payload shaped exactly as the Host forwards it: the worker deserialises THIS text.</summary>
    private static string Payload(string? orientationField) =>
        """{"document_id":"0123456789abcdef0123456789abcdef","expected_revision":1,"copy_kind":"operations","source_refs":["feature:1"],"axis_point1_mm":[0,0,0],"axis_point2_mm":[0,0,1],"count1":1,"count2":73,"step2_deg":4.931506849315069"""
        + (orientationField is null ? "" : $""","save_initial_orientation":{orientationField}""")
        + "}";

    private static PatternCircularCommand Command(string? orientationField) =>
        KompJson.Deserialize<PatternCircularCommand>(Payload(orientationField))
        ?? throw new InvalidOperationException("Payload не разобрался.");

    /// <summary>INVARIANT: an omitted field takes the default, and the answer can say so. The two facts are
    /// checked TOGETHER: "the value is false" alone would not tell a deliberate false from a defaulted one.</summary>
    [Fact]
    public void Circular_Orientation_Omitted_TakesTheDefault_AndIsMarkedDefaulted()
    {
        var command = Command(null);

        Assert.Null(command.SaveInitialOrientation);
        Assert.False(command.EffectiveSaveInitialOrientation);
        Assert.True(command.SaveInitialOrientationDefaulted);
    }

    /// <summary>INVARIANT: an explicit value reaches the adapter command unchanged and is NOT marked defaulted -
    /// otherwise "false" and "true" would be indistinguishable from "not asked".</summary>
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Circular_Orientation_Explicit_ReachesTheCommand_NotDefaulted(string literal, bool expected)
    {
        var command = Command(literal);

        Assert.Equal(expected, command.SaveInitialOrientation);
        Assert.Equal(expected, command.EffectiveSaveInitialOrientation);
        Assert.False(command.SaveInitialOrientationDefaulted);
    }

    /// <summary>INVARIANT: the published schema declares the field nullable, so a legal <c>null</c> must be
    /// accepted and take the default, exactly like an omitted field.</summary>
    [Fact]
    public void Circular_Orientation_ExplicitNull_TakesTheDefault()
    {
        var command = Command("null");

        Assert.Null(command.SaveInitialOrientation);
        Assert.False(command.EffectiveSaveInitialOrientation);
        Assert.True(command.SaveInitialOrientationDefaulted);
    }

    /// <summary>Why the property is nullable at all: a non-nullable <c>bool</c> cannot receive a JSON null, so
    /// the nullable declaration in the schema would have been a promise the payload could not keep.
    /// MEASURED here on the framework itself, not assumed.</summary>
    [Fact]
    public void NonNullableBool_CannotReceiveJsonNull_SoTheNullableFormIsRequired()
    {
        var json = """{"save_initial_orientation":null}""";

        Assert.ThrowsAny<JsonException>(() => KompJson.Deserialize<NonNullableProbe>(json));
    }

    /// <summary>The shape the property had before this change: a non-nullable bool with an explicit default.</summary>
    private sealed record NonNullableProbe
    {
        public bool SaveInitialOrientation { get; init; } = true;
    }

    /// <summary>INVARIANT: the EDIT path applies no default - a member that was not passed must stay untouched.
    /// The pattern-edit member is nullable with no default, which is what makes "not passed" expressible.</summary>
    [Fact]
    public void PatternEdit_Orientation_Omitted_StaysUnset()
    {
        var edit = KompJson.Deserialize<PatternEditDto>("""{"count2":6}""");

        Assert.NotNull(edit);
        Assert.Null(edit!.SaveInitialOrientation);
    }

    /// <summary>INVARIANT: the PUBLISHED contract names the default and both meanings. The client reads the tool
    /// description, not the source; a description that omits the default is what let the wrong geometry pass
    /// unnoticed. Checked on the catalog the Host validates and advertises with.</summary>
    [Fact]
    public void Circular_ToolDescription_NamesTheDefaultAndBothMeanings()
    {
        var schema = ToolCatalog.All.Single(t => t.Name == "kompas_pattern_circular").InputSchema;
        var description = (schema["properties"] as JsonObject)?["save_initial_orientation"]?["description"]
            ?.GetValue<string>() ?? string.Empty;

        Assert.Contains("По умолчанию false", description, StringComparison.Ordinal);
        Assert.Contains("радиального направления", description, StringComparison.Ordinal);
        Assert.Contains("перенос", description, StringComparison.Ordinal);
    }
}
