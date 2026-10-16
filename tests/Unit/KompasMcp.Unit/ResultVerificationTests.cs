using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Verification;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: the envelope's verification describes the RESULT, never the call. A payload that
/// publishes <c>reached_level</c> sets the published level and its unverified aspects; the conservative
/// floor stays only for a payload that publishes neither.</summary>
/// <remarks>MEASURED defect that made this rule necessary: <c>kompas_export_step</c> answered
/// <c>reached_level=syntax_checked</c> inside the result while the envelope said <c>call_returned</c>, and
/// the aspects were one fixed sentence about the payload instead of the ones the result carried. The rule
/// lives in the Domain so it can be checked without COM.
/// History: docs/decisions/host.md#verification-aggregation</remarks>
public class ResultVerificationTests
{
    private static JsonNode Payload(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void PublishedLevel_BecomesTheEnvelopeLevel()
    {
        var block = ResultVerification.From(
            Payload("""{"reached_level":"syntax_checked","unverified_aspects":["geometry_roundtrip_not_checked"]}"""),
            OperationStatus.Succeeded);

        Assert.NotNull(block);
        Assert.Equal(VerificationLevel.SyntaxChecked, block!.Level);
        Assert.Equal(new[] { "geometry_roundtrip_not_checked" }, block.UnverifiedAspects);
    }

    /// <summary>The wire spelling carries an underscore the enum member does not: a comparison without
    /// dropping it would silently fall back to the conservative floor and re-create the very divergence
    /// this rule removes.</summary>
    [Theory]
    [InlineData("none", VerificationLevel.None)]
    [InlineData("argument_validated", VerificationLevel.ArgumentValidated)]
    [InlineData("call_returned", VerificationLevel.CallReturned)]
    [InlineData("file_created", VerificationLevel.FileCreated)]
    [InlineData("syntax_checked", VerificationLevel.SyntaxChecked)]
    [InlineData("structure_checked", VerificationLevel.StructureChecked)]
    [InlineData("geometry_checked", VerificationLevel.GeometryChecked)]
    public void EveryWireSpelling_Parses(string wire, VerificationLevel expected)
    {
        Assert.True(ResultVerification.TryParse(wire, out var level), wire);
        Assert.Equal(expected, level);
    }

    /// <summary>A level published WITHOUT aspects must not read as "everything is confirmed": the named
    /// aspect is what keeps an empty list from being an overclaim.</summary>
    [Fact]
    public void PublishedLevelWithoutAspects_NamesTheGap()
    {
        var block = ResultVerification.From(Payload("""{"reached_level":"structure_checked"}"""),
            OperationStatus.Succeeded);

        Assert.NotNull(block);
        Assert.Equal(VerificationLevel.StructureChecked, block!.Level);
        Assert.Equal(new[] { ResultVerification.NoAspects }, block.UnverifiedAspects);
    }

    [Fact]
    public void NonStringAndEmptyAspectItems_AreSkipped()
    {
        var block = ResultVerification.From(
            Payload("""{"reached_level":"file_created","unverified_aspects":["kept","",null,7]}"""),
            OperationStatus.Succeeded);

        Assert.Equal(new[] { "kept" }, block!.UnverifiedAspects);
    }

    /// <summary>A refusal carries no result payload, so a level there would be an invented one. The
    /// caller keeps its conservative floor instead.</summary>
    [Fact]
    public void FailedCall_IsNotRewritten()
    {
        Assert.Null(ResultVerification.From(Payload("""{"reached_level":"geometry_checked"}"""),
            OperationStatus.Failed));
    }

    [Fact]
    public void MissingPayloadOrLevel_LeavesTheFloorToTheCaller()
    {
        Assert.Null(ResultVerification.From(null, OperationStatus.Succeeded));
        Assert.Null(ResultVerification.From(Payload("""{"volume_mm3":12.0}"""), OperationStatus.Succeeded));
        Assert.Null(ResultVerification.From(Payload("""{"reached_level":"not_a_level"}"""),
            OperationStatus.Succeeded));
        Assert.Null(ResultVerification.From(Payload("""{"reached_level":7}"""), OperationStatus.Succeeded));
    }

    /// <summary>The block must name where the level came from: a level with no check at all would be
    /// indistinguishable from one the Host verified itself.</summary>
    [Fact]
    public void TheBlock_NamesThePayloadAsTheSource()
    {
        var block = ResultVerification.From(Payload("""{"reached_level":"call_returned"}"""),
            OperationStatus.Succeeded);

        var names = block!.Checks.Select(c => c.Name).ToArray();
        Assert.Contains("host_validation", names);
        Assert.Contains("worker_reached_level", names);
        Assert.True(block.Checks.All(c => c.Passed));
    }
}
