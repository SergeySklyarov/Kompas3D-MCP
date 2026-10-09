using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The server's analysis of a sketch profile is the only reason a refused extrusion can give
/// (the kernel returns a bare false), so it must name the PLACE and must not be tuned to the reference.
/// The contours are the client's own (card OBS-026, checkpoint CP06): the pinion contour the client could
/// not extrude and the escape wheel contour it could, plus the pinion contour after the generator fix.
/// WHY THE CLIENT'S CONTOURS. The reviewer's card names the joints of the pinion contour; reproducing that
/// from the server's own code is the point — a locally built bowtie would prove the detector runs, not that
/// it finds the defect the client actually hit.
/// History: docs/decisions/adapter-sketch.md#profile-input-diagnosis</summary>
public class SketchProfileDiagnosisTests
{
    /// <summary>The client file carries the pinion contour TWICE. LIMIT: at the commit this test was
    /// written the file had been regenerated after the reviewer's fix, so `outline_mcp` holds the REPAIRED
    /// contour and the client's original input lives under `outline_mcp_before_R067`. The counts are
    /// asserted below rather than assumed, so the day the file changes again the test says so instead of
    /// silently measuring another contour.</summary>
    private const string PinionCp06 = "outline_mcp_before_R067";

    private const string PinionRepaired = "outline_mcp";

    [Fact]
    public void ClientPinionContour_NamesTheSelfIntersectionAtPrimitives58And59And66And67()
    {
        var entities = Contour("pinion", PinionCp06);

        Assert.Equal(100, entities.Count);

        var outcome = ProfileArea.Compute(entities);

        Assert.Null(outcome.AreaMm2);
        Assert.Equal(ProfileInputState.Defect, outcome.State);
        Assert.NotNull(outcome.UnavailableReason);

        var reason = outcome.UnavailableReason!;
        Assert.Contains("самопересекается", reason, StringComparison.Ordinal);
        // The reviewer's joints, zero-based, in the order the caller passed the primitives. Both are
        // named: a client that fixes only the first would come back with the same refusal.
        Assert.Contains("примитивы 58 и 59", reason, StringComparison.Ordinal);
        Assert.Contains("(-0.465635; -1.758107)", reason, StringComparison.Ordinal);
        Assert.Contains("примитивы 66 и 67", reason, StringComparison.Ordinal);
        Assert.Contains("(-1.186706; -1.324632)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientPinionContour_RepairedVersion_IsAnArea()
    {
        // The same file after the generator fix: no crossing, and the area the client's own tooling
        // reports for it.
        var entities = Contour("pinion", PinionRepaired);

        Assert.Equal(257, entities.Count);

        var outcome = ProfileArea.Compute(entities);

        Assert.Equal(ProfileInputState.Consistent, outcome.State);
        Assert.Null(outcome.UnavailableReason);
        Assert.NotNull(outcome.AreaMm2);
        Assert.True(Math.Abs(outcome.AreaMm2!.Value - 9.861096585699007d) <= 1e-9,
            $"площадь {outcome.AreaMm2.Value:R} против измеренной на контуре файла 9.861096585699007");
    }

    [Fact]
    public void ClientEscapeWheelContour_MatchesTheAreaTheClientMeasured()
    {
        // The paired positive control: the contour the client DID extrude in CP06, 141 primitives, area
        // 63.29497 mm² in its own answer.
        var entities = Contour("escape_wheel", "outline_mcp");

        Assert.Equal(141, entities.Count);

        var outcome = ProfileArea.Compute(entities);

        Assert.Equal(ProfileInputState.Consistent, outcome.State);
        Assert.Null(outcome.UnavailableReason);
        Assert.NotNull(outcome.AreaMm2);
        Assert.True(Math.Abs(outcome.AreaMm2!.Value - 63.29497d) <= 5e-5,
            $"площадь {outcome.AreaMm2.Value:R} против ответа клиента 63.29497");
    }

    [Fact]
    public void SelfCrossingChain_NamesBothPrimitivesAndThePoint()
    {
        var outcome = ProfileArea.Compute(new[]
        {
            Line(0, 0, 10, 10),
            Line(10, 10, 10, 0),
            Line(10, 0, 0, 10),
            Line(0, 10, 0, 0),
        });

        Assert.Equal(ProfileInputState.Defect, outcome.State);
        Assert.Contains("примитивы 0 и 2", outcome.UnavailableReason!, StringComparison.Ordinal);
        Assert.Contains("(5; 5) мм", outcome.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenChain_AndBranching_AreDifferentReasons()
    {
        // The client cannot act on "не замкнут или ветвится": an open end is repaired by closing the
        // chain, a branch by removing the extra primitive. Each names its own vertices.
        var open = ProfileArea.Compute(new[]
        {
            Line(-10, 5, 10, 5),
            Arc(10, 0, 5, 90, -180),
            Line(10, -5, 0, -5),
        });

        Assert.Equal(ProfileInputState.Defect, open.State);
        Assert.Contains("не замкнута", open.UnavailableReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("ветвится", open.UnavailableReason!, StringComparison.Ordinal);
        Assert.Contains("(-10; 5) мм (примитивы 0)", open.UnavailableReason!, StringComparison.Ordinal);

        var branch = ProfileArea.Compute(new[]
        {
            Line(0, 0, 10, 0),
            Line(0, 0, 0, 10),
            Line(0, 0, -10, 0),
        });

        Assert.Equal(ProfileInputState.Defect, branch.State);
        Assert.Contains("ветвится", branch.UnavailableReason!, StringComparison.Ordinal);
        Assert.Contains("(0; 0) мм (примитивы 0, 1, 2)", branch.UnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void SplineAndEmptyProfile_AreNotAnalysable_NotDefects()
    {
        // "The server cannot analyse this" and "your contour is broken" are different answers: the first
        // one the caller cannot repair by editing the contour.
        var spline = ProfileArea.Compute(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Spline,
                PointsMm = new[] { new double[] { 0, 0 }, new double[] { 10, 10 } },
                Closed = false,
            },
        });

        Assert.Equal(ProfileInputState.NotAnalysable, spline.State);
        Assert.Null(spline.AreaMm2);

        var empty = ProfileArea.Compute(Array.Empty<SketchEntityDto>());
        Assert.Equal(ProfileInputState.NotAnalysable, empty.State);
    }

    [Fact]
    public void ClosedPolyline_CrossingItself_NamesItsOwnNumber()
    {
        var outcome = ProfileArea.Compute(new[]
        {
            new SketchEntityDto
            {
                Kind = SketchEntityKind.Polyline,
                Closed = true,
                PointsMm = new[]
                {
                    new double[] { 0, 0 },
                    new double[] { 10, 10 },
                    new double[] { 10, 0 },
                    new double[] { 0, 10 },
                },
            },
        });

        Assert.Equal(ProfileInputState.Defect, outcome.State);
        Assert.Contains("примитив 0 (замкнутая полилиния) самопересекается", outcome.UnavailableReason!, StringComparison.Ordinal);
    }

    private static SketchEntityDto Line(double x0, double y0, double x1, double y1) => new()
    {
        Kind = SketchEntityKind.Line,
        StartMm = new double[] { x0, y0 },
        EndMm = new double[] { x1, y1 },
    };

    private static SketchEntityDto Arc(double cx, double cy, double r, double startDeg, double sweepDeg) => new()
    {
        Kind = SketchEntityKind.Arc,
        CenterMm = new double[] { cx, cy },
        RadiusMm = r,
        StartDeg = startDeg,
        SweepDeg = sweepDeg,
    };

    /// <summary>One contour out of the client's file, deserialized with the client's own wire names.</summary>
    private static IReadOnlyList<SketchEntityDto> Contour(string level, string key)
    {
        var assembly = typeof(SketchProfileDiagnosisTests).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("wheels_cp05_3004019.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var document = JsonDocument.Parse(stream);
        var entities = document.RootElement
            .GetProperty("levels").GetProperty(level).GetProperty(key);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        return entities.Deserialize<List<SketchEntityDto>>(options)!;
    }
}
