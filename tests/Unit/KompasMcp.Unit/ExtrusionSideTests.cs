using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The side of an extrusion asked for in PART coordinates, translated to KOMPAS's positive/negative.</summary>
/// <remarks>WHY. The v24 help points the two families OPPOSITE ways — «Прямое направление совпадает с
/// нормалью, проведенной к плоскости эскиза» for a base/boss and «Для вырезаемого элемента выдавливания
/// направление противоположно нормали» for a cut — so a client that asks for a side in part coordinates
/// cannot guess the word, and the wrong word does not fail loudly: KOMPAS creates the feature and changes
/// nothing. The rule here is the INVERSE of <see cref="MaterialDirectionRule.Toward"/>, and the inverse is
/// checked against that forward rule for every combination, so the two cannot drift apart.
/// History: docs/decisions/adapter-core.md#material-toward-input</remarks>
public class ExtrusionSideTests
{
    private static readonly double[] Xp = [1d, 0d, 0d];
    private static readonly double[] Xn = [-1d, 0d, 0d];
    private static readonly double[] Yp = [0d, 1d, 0d];
    private static readonly double[] Yn = [0d, -1d, 0d];
    private static readonly double[] Zp = [0d, 0d, 1d];
    private static readonly double[] Zn = [0d, 0d, -1d];

    /// <summary>The normals the rule is checked on: the six axis directions the words can name.</summary>
    public static TheoryData<string, double[]> Normals() => new()
    {
        { "+x", Xp }, { "-x", Xn }, { "+y", Yp }, { "-y", Yn }, { "+z", Zp }, { "-z", Zn },
    };

    /// <summary>Six sides times three operations times six normals, with the expectation taken from the
    /// DOCUMENTED rule rather than from a table that could be edited to match the code.</summary>
    /// <remarks>The expectation is built from <see cref="MaterialDirectionRule.Toward"/>: the side the answer
    /// would name for a given word. The resolver must return the word whose forward side IS the requested side
    /// — that is exactly "the answer and the request do not diverge", which is the property the naryad makes a
    /// stopping condition.</remarks>
    [Theory]
    [MemberData(nameof(Normals))]
    public void InverseOfThePublishedRule_ForEverySideAndOperation(string normalWord, double[] normal)
    {
        var operations = new[] { ("base", false), ("boss", false), ("cut", true) };

        foreach (var (name, removing) in operations)
        {
            foreach (var side in ExtrusionSide.Words)
            {
                var toward = ExtrusionSide.Parse(side)!;
                var resolved = ExtrusionSide.DirectionFor(removing, normal, toward, out var angleDeg);

                // Is the requested side expressible at all? Only when the two name the same axis.
                var sameAxis = ExtrusionSide.Parse(normalWord) is { } n
                               && ExtrusionSide.Parse(side) is { } t
                               && Math.Abs(n[0] * t[0] + n[1] * t[1] + n[2] * t[2]) > 0.5d;

                if (!sameAxis)
                {
                    Assert.Null(resolved);
                    Assert.Equal(90d, angleDeg, 6);
                    continue;
                }

                Assert.NotNull(resolved);
                var negative = resolved == "negative";
                var forward = MaterialDirectionRule.Toward(normal, removing, negative, symmetric: false);
                Assert.True(
                    MaterialDirectionRule.SameSide(forward!, toward),
                    $"{name} normal={normalWord} side={side}: резолвер вернул {resolved}, "
                    + $"а прямое правило для этого слова даёт сторону "
                    + $"{MaterialDirectionRule.Describe(forward!, false)} — правила разошлись");
            }
        }
    }

    [Theory]
    // The documented sign, written out independently of the code: base/boss add ALONG the normal, a cut
    // removes AGAINST it, and the requested side is in part coordinates.
    [InlineData(false, "+z", "+z", "positive")]   // base along the normal
    [InlineData(false, "+z", "-z", "negative")]
    [InlineData(false, "-z", "-z", "positive")]   // the sign flips with the normal, not with the word
    [InlineData(false, "-z", "+z", "negative")]
    [InlineData(true, "+z", "-z", "positive")]    // cut positive removes AGAINST the normal
    [InlineData(true, "+z", "+z", "negative")]
    [InlineData(true, "-z", "+z", "positive")]    // sketch on the BOTTOM face (normal -z)
    [InlineData(true, "-z", "-z", "negative")]
    [InlineData(true, "-x", "-x", "negative")]    // sketch on the base YZ plane (measured normal -x)
    [InlineData(true, "-x", "+x", "positive")]
    [InlineData(true, "+y", "-y", "positive")]
    [InlineData(true, "+y", "+y", "negative")]
    public void SignTable(bool removing, string normalWord, string side, string expected)
    {
        var normal = ExtrusionSide.Parse(normalWord)!;
        var toward = ExtrusionSide.Parse(side)!;
        Assert.Equal(expected, ExtrusionSide.DirectionFor(removing, normal, toward, out _));
    }

    [Fact]
    public void TiltedNormal_IsRefused_WithTheMeasuredAngle()
    {
        // 30 degrees off the +z axis: the word would name a different direction, so the rule refuses rather
        // than rounding to the nearest axis.
        double[] tilted = [0d, 0.5d, Math.Sqrt(3d) / 2d];
        var toward = ExtrusionSide.Parse("+z")!;
        Assert.Null(ExtrusionSide.DirectionFor(removing: true, tilted, toward, out var angleDeg));
        Assert.Equal(30d, angleDeg, 6);
    }

    [Fact]
    public void PerpendicularAxis_IsRefused_AtNinetyDegrees()
    {
        var toward = ExtrusionSide.Parse("+x")!;
        Assert.Null(ExtrusionSide.DirectionFor(removing: false, Zp, toward, out var angleDeg));
        Assert.Equal(90d, angleDeg, 6);
    }

    [Fact]
    public void Parse_AcceptsBothMinusSigns_AndNothingElse()
    {
        Assert.Equal(Zp, ExtrusionSide.Parse("+z"));
        Assert.Equal(Zn, ExtrusionSide.Parse("-z"));
        // The answer spells a side with U+2212; copying it back is the same request.
        Assert.Equal(Zn, ExtrusionSide.Parse("\u2212z"));
        Assert.Equal(Xn, ExtrusionSide.Parse("\u2212x"));
        Assert.Null(ExtrusionSide.Parse("+w"));
        Assert.Null(ExtrusionSide.Parse("z"));
        Assert.Null(ExtrusionSide.Parse(""));
        Assert.Null(ExtrusionSide.Parse(null));
    }

    [Fact]
    public void AngleTolerance_IsDerivedFromTheAxisTolerance()
    {
        // The bound is NAMED, and it is the same constant the axis naming uses — otherwise "the normal is +z"
        // and "material_toward=+z lies along the normal" could disagree about one vector.
        Assert.Equal(MaterialDirectionRule.AxisTolerance, ExtrusionSide.AxisTolerance);
        Assert.True(ExtrusionSide.AngleToleranceDeg is > 0d and < 0.2d);
        // A vector just inside the tolerance still resolves; just outside it does not.
        var inside = Math.Acos(1d - ExtrusionSide.AxisTolerance / 2d);
        double[] near = [0d, Math.Sin(inside), Math.Cos(inside)];
        Assert.NotNull(ExtrusionSide.DirectionFor(removing: false, near, Zp, out _));
        var outside = Math.Acos(1d - ExtrusionSide.AxisTolerance * 4d);
        double[] far = [0d, Math.Sin(outside), Math.Cos(outside)];
        Assert.Null(ExtrusionSide.DirectionFor(removing: false, far, Zp, out _));
    }

    [Fact]
    public void Words_AreTheSixPublishedOnes()
    {
        Assert.Equal(new[] { "+x", "-x", "+y", "-y", "+z", "-z" }, ExtrusionSide.Words);
    }
}
