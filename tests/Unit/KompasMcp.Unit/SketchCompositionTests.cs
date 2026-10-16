using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rule that decides whether a replace or a clear left the sketch as asked.</summary>
/// <remarks>The verdict is a pure function of two readings, so a substituted pair exercises every branch
/// without a CAD session. The kind names are the ones the model really answers
/// (<c>DrawingObjectType</c>); the leftover pair below is the reported defect: a four-object profile
/// replaced by four objects, with the two OLD arcs still in the sketch.
/// History: docs/decisions/adapter-sketch.md#sketch-replace</remarks>
public class SketchCompositionTests
{
    private static readonly IReadOnlyList<string> ArcLineBatch = new[]
    {
        "ksDrArc", "ksDrArc", "ksDrLineSeg", "ksDrLineSeg",
    };

    [Fact]
    public void TwoOldArcsLeftBehind_AreExtraObjects_NotASuccess()
    {
        var observed = new[]
        {
            "ksDrLineSeg", "ksDrLineSeg", "ksDrArc", "ksDrArc", "ksDrArc", "ksDrArc",
        };

        var verdict = SketchComposition.Verify(ArcLineBatch, observed);

        Assert.False(verdict.Matches);
        Assert.Equal("extra_objects", verdict.ReasonCode);
        Assert.Equal(2, verdict.Leftover.Count);
        Assert.Equal(new[] { "ksDrArc", "ksDrArc" }, verdict.Leftover);
        Assert.Empty(verdict.Missing);
    }

    [Fact]
    public void SameKindsInAnotherOrder_Match()
    {
        var observed = new[] { "ksDrLineSeg", "ksDrArc", "ksDrLineSeg", "ksDrArc" };

        Assert.True(SketchComposition.Verify(ArcLineBatch, observed).Matches);
    }

    [Fact]
    public void ARequestedPrimitiveThatIsAbsent_IsMissingObjects()
    {
        var verdict = SketchComposition.Verify(
            SketchComposition.ExpectedReadBackKinds(new[] { SketchEntityKind.Circle }),
            new[] { "ksDrArc" });

        Assert.False(verdict.Matches);
        Assert.Equal("extra_objects", verdict.ReasonCode);
        Assert.Equal(new[] { "ksDrArc" }, verdict.Leftover);
        Assert.Equal(new[] { "ksDrCircle" }, verdict.Missing);
    }

    [Fact]
    public void AClearedSketchHoldsNoGeometry()
    {
        Assert.True(SketchComposition.Verify(Array.Empty<string>(), Array.Empty<string>()).Matches);

        var leftover = SketchComposition.Verify(Array.Empty<string>(), new[] { "ksDrArc" });
        Assert.False(leftover.Matches);
        Assert.Equal("extra_objects", leftover.ReasonCode);
        Assert.Equal(new[] { "ksDrArc" }, leftover.Leftover);
    }

    [Fact]
    public void ARectangleIsFourSegments_AndASplineIsOneBezier()
    {
        Assert.Equal(
            new[] { "ksDrLineSeg", "ksDrLineSeg", "ksDrLineSeg", "ksDrLineSeg" },
            SketchComposition.ExpectedReadBackKinds(new[] { SketchEntityKind.Rectangle }));
        Assert.Equal(
            new[] { "ksDrPolyline" },
            SketchComposition.ExpectedReadBackKinds(new[] { SketchEntityKind.Polyline }));
        Assert.Equal(
            new[] { "ksDrBezier" },
            SketchComposition.ExpectedReadBackKinds(new[] { SketchEntityKind.Spline }));
    }

    [Fact]
    public void NonGeometryObjects_AreIgnoredAndCounted()
    {
        // A dimension is a drawing object too; the edit owns geometry only, so it is neither a leftover
        // nor a reason to refuse.
        var observed = new[] { "ksDrLineSeg", "ksDrLineSeg", "ksDrArc", "ksDrArc", "ksDrDimension" };

        var verdict = SketchComposition.Verify(ArcLineBatch, observed);

        Assert.True(verdict.Matches);
        Assert.Equal(1, verdict.IgnoredNonGeometry);
        Assert.Equal(0, verdict.UnreadableKinds);
    }

    [Fact]
    public void AnUnreadKind_IsNotReadAsAnAbsentObject()
    {
        var observed = new string?[] { "ksDrLineSeg", "ksDrLineSeg", "ksDrArc", null };

        var verdict = SketchComposition.Verify(ArcLineBatch, observed);

        Assert.False(verdict.Matches);
        Assert.Equal("kind_unreadable", verdict.ReasonCode);
        Assert.Equal(1, verdict.UnreadableKinds);
    }

    [Fact]
    public void GeometryKinds_CarryThePrimitiveVocabulary()
    {
        Assert.Contains("ksDrArc", SketchComposition.GeometryKinds);
        Assert.Contains("ksDrLineSeg", SketchComposition.GeometryKinds);
        Assert.DoesNotContain("ksDrDimension", SketchComposition.GeometryKinds);
    }
}
