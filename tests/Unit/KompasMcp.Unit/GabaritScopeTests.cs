using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The document gabarit travels as TWO boxes, each with its scope in words.</summary>
/// <remarks>WHY this test exists. A client read the <c>fingerprint</c> field of <c>kompas_get_context</c> as
/// "the gabarit of the part" and got X = [-57.70; 23.49] while every body measured ±10.63. The fingerprint is
/// built from <c>ksPart.GetGabarit(full=true)</c>, which covers sketches and auxiliary geometry too; the
/// numbers matched a sketch arc 89 mm across. The contract now publishes both boxes and names what each
/// covers, so the distinction cannot be lost in the envelope.
/// History: docs/decisions/contracts.md#gabarit-scope</remarks>
public class GabaritScopeTests
{
    private static DocumentContextDto Context(GabaritDto? gabarit) => new()
    {
        Id = "doc-1",
        ApplicationId = "app-1",
        Kind = DocumentKind.Part,
        Dirty = false,
        Revision = 3,
        FeatureCount = 2,
        BodyCount = 1,
        ComponentCount = 0,
        UnitSystem = "mm",
        ExternalChangeDetection = ExternalChangeDetection.Conservative,
        Gabarit = gabarit,
    };

    private static GabaritDto Sample() => new(
        new GabaritBoxDto(
            "весь документ-модель: сплошные тела, эскизы и вспомогательная геометрия (ksPart.GetGabarit(full=true))",
            new[] { -23.486934, -69.289111, -1.8544 },
            new[] { 57.6954, 19.75763, 0.613032 },
            new[] { 81.182334, 89.046741, 2.467432 }),
        new GabaritBoxDto(
            "только сплошные тела, без эскизов и вспомогательной геометрии (ksPart.GetGabarit(full=false))",
            new[] { -10.627063, -10.627063, -1.8544 },
            new[] { 10.627063, 10.627063, 0.613032 },
            new[] { 21.254126, 21.254126, 2.467432 }));

    [Fact]
    public void BothBoxesCarryTheirScopeAndCoordinates()
    {
        var node = JsonSerializer.SerializeToNode(Context(Sample()), KompJson.Options)!.AsObject();

        var gabarit = node["gabarit"]!.AsObject();
        var full = gabarit["full"]!.AsObject();
        var bodies = gabarit["bodies"]!.AsObject();

        // Both boxes exist and are DIFFERENT: this is the whole point of the field.
        Assert.Contains("эскиз", full["scope"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("тел", bodies["scope"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEqual(
            full["size_mm"]!.AsArray()[0]!.GetValue<double>(),
            bodies["size_mm"]!.AsArray()[0]!.GetValue<double>());

        // The body box is the one to compare with list_bodies / kompas_measure.
        var size = bodies["size_mm"]!.AsArray();
        Assert.Equal(3, size.Count);
        Assert.Equal(21.254126, size[0]!.GetValue<double>(), 6);
        Assert.Equal(-10.627063, bodies["min_mm"]!.AsArray()[0]!.GetValue<double>(), 6);
        Assert.Equal(10.627063, bodies["max_mm"]!.AsArray()[0]!.GetValue<double>(), 6);
    }

    [Fact]
    public void AbsentGabarit_IsAbsent_NotAZeroBox()
    {
        // minimal detail (and any document without 3D model space) leaves the field out. A zero box would be
        // an invented measurement; null is the honest "not read".
        var node = JsonSerializer.SerializeToNode(Context(null), KompJson.Options)!.AsObject();

        Assert.True(node.ContainsKey("gabarit"));
        Assert.Null(node["gabarit"]);
    }

    [Fact]
    public void UnreadableBoxIsNull_NotZero()
    {
        var gabarit = new GabaritDto(
            new GabaritBoxDto("весь документ-модель", null, null, null),
            new GabaritBoxDto("только сплошные тела", null, null, null));

        var node = JsonSerializer.SerializeToNode(gabarit, KompJson.Options)!.AsObject();

        Assert.Null(node["full"]!.AsObject()["min_mm"]);
        Assert.Null(node["bodies"]!.AsObject()["size_mm"]);
    }

    /// <summary>Both scope words carry the LIMIT of the route: the box of a trimmed analytic surface can be
    /// larger than the exact shell, so a matching box is not proof that the geometry matched.</summary>
    /// <remarks>MEASURED: a torus cut at 0° and 180° reported 23,0000 mm on X while the exported mesh spans
    /// 22,98238 mm. This is a limit of ksPart.GetGabarit, not a defect to be "fixed" by nudging numbers.
    /// History: docs/decisions/adapter-core.md#gabarit-scope</remarks>
    [Fact]
    public void BothScopesNameTheConservativeBox()
    {
        Assert.Contains("БОЛЬШЕ точной оболочки", GabaritScopeWords.Bodies, StringComparison.Ordinal);
        Assert.Contains("не доказывает совпадения геометрии", GabaritScopeWords.Bodies, StringComparison.Ordinal);
        Assert.Contains("не доказательство неизменности", GabaritScopeWords.Full, StringComparison.Ordinal);
        Assert.Contains("22,98238", GabaritScopeWords.Bodies, StringComparison.Ordinal);
        Assert.Contains("gabarit_conservative", GabaritScopeWords.ConservationNote, StringComparison.Ordinal);
    }
}
