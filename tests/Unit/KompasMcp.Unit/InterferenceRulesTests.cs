using KompasMcp.Domain.Interference;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The interference block's pure rules, without KOMPAS.</summary>
/// <remarks>TEST: pair enumeration, the naming of `Intersection_Type`, the meaning of a NULL kernel
/// answer versus a failed call, the input rules and the "half-read segment is not published" rule.
/// These do NOT confirm the geometry: the live group `--interference-only` does that.
/// History: docs/decisions/assembly.md#interference-contracts</remarks>
public class InterferenceRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(20, 190)]
    public void Pairs_CountIsHalfOfTheOrderedProduct(int count, int expected) =>
        Assert.Equal(expected, InterferenceRules.Pairs(count).Count);

    [Fact]
    public void Pairs_EachUnorderedPairOnceAndNeverWithItself()
    {
        var pairs = InterferenceRules.Pairs(4);
        var seen = new HashSet<string>();
        foreach (var (a, b) in pairs)
        {
            Assert.True(a < b, $"пара ({a}, {b}) не упорядочена или содержит себя");
            Assert.True(seen.Add($"{a}-{b}"), $"пара ({a}, {b}) повторена");
        }

        Assert.Equal(6, seen.Count);
    }

    [Theory]
    [InlineData(1, "itTangentPoint")]
    [InlineData(2, "itTangentCurve")]
    [InlineData(3, "itTangentSurface")]
    [InlineData(4, "itBody")]
    public void TypeName_NamesTheDocumentedValues(int value, string expected) =>
        Assert.Equal(expected, InterferenceRules.TypeName(value));

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void TypeName_LeavesAnUnknownNumberAsANumber(int value) =>
        Assert.Equal($"unread_type_{value}", InterferenceRules.TypeName(value));

    [Fact]
    public void Decide_NoHitsAndNoFailures_IsNoIntersectionWithItsBasis()
    {
        var outcome = InterferenceRules.Decide(Array.Empty<string>(), 0);
        Assert.False(outcome.Intersecting);
        Assert.False(outcome.Volumetric);
        Assert.Equal(InterferenceRules.NoIntersectionBasis, outcome.Basis);
    }

    [Fact]
    public void Decide_FailedCallIsUnknownNotNoIntersection()
    {
        // THE NEGATIVE CONTROL of the whole rule: a call that threw must never read as "no
        // intersection". `false` there would answer a question nobody asked the kernel.
        var outcome = InterferenceRules.Decide(Array.Empty<string>(), 1);
        Assert.Null(outcome.Intersecting);
        Assert.Null(outcome.Volumetric);
        Assert.Equal(InterferenceRules.FailedBasis, outcome.Basis);
    }

    [Fact]
    public void Decide_BodyTypeIsVolumetric_TangencyIsNot()
    {
        var body = InterferenceRules.Decide(new[] { "itBody" }, 0);
        Assert.True(body.Intersecting);
        Assert.True(body.Volumetric);
        Assert.Equal(InterferenceRules.IntersectionBasis, body.Basis);

        var tangent = InterferenceRules.Decide(new[] { "itTangentSurface" }, 0);
        Assert.True(tangent.Intersecting);
        Assert.False(tangent.Volumetric);
    }

    [Fact]
    public void Decide_HitsSurviveAFailedSiblingCall()
    {
        // A found intersection is a fact even if another body pair did not answer: the pair stays
        // "intersecting", and the basis names the intersection rather than the failure.
        var outcome = InterferenceRules.Decide(new[] { "itBody" }, 1);
        Assert.True(outcome.Intersecting);
        Assert.Equal(InterferenceRules.IntersectionBasis, outcome.Basis);
    }

    [Fact]
    public void HasRepeats_RefusesTheSameComponentTwice() =>
        Assert.True(InterferenceRules.HasRepeats(new[] { "component:a", "component:b", "component:a" }));

    [Fact]
    public void HasRepeats_AcceptsDistinctComponents() =>
        Assert.False(InterferenceRules.HasRepeats(new[] { "component:a", "component:b" }));

    [Fact]
    public void SameObject_ComparesComponentAndFaceTogether()
    {
        Assert.True(InterferenceRules.SameObject("component:a", 2, "component:a", 2));
        Assert.False(InterferenceRules.SameObject("component:a", 2, "component:a", 3));
        Assert.False(InterferenceRules.SameObject("component:a", 2, "component:b", 2));
        Assert.True(InterferenceRules.SameObject("component:a", null, "component:a", null));
    }

    [Fact]
    public void Segment_IsPublishedOnlyWhenBothEndsWereRead()
    {
        var first = new double[] { 5, 0, 0 };
        var second = new double[] { 10, 0, 0 };
        var segment = InterferenceRules.Segment(true, first, true, second);
        Assert.NotNull(segment);
        Assert.Equal(2, segment!.Count);
        Assert.Equal(5d, segment[0][0]);
        Assert.Equal(10d, segment[1][0]);

        // A HALF-READ SEGMENT IS NOT PUBLISHED: a substituted zero would claim a measured coordinate.
        Assert.Null(InterferenceRules.Segment(true, first, false, Array.Empty<double>()));
        Assert.Null(InterferenceRules.Segment(false, Array.Empty<double>(), true, second));
    }
}
