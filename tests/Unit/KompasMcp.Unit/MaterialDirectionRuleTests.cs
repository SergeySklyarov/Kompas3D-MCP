using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The side a material change went to, from the sketch normal — the rule that names a side where the
/// measured gabarit says nothing.</summary>
/// <remarks>WHY. The side used to be read only from the measured shift of the target body's gabarit, so a hole
/// cut INSIDE a body (the commonest cut) moved no bound and the field stayed silent. The v24 help fixes the
/// sign: «Для вырезаемого элемента выдавливания направление противоположно нормали», «Прямое направление
/// совпадает с нормалью, проведенной к плоскости эскиза» — so a cut goes AGAINST the normal and base/boss
/// ALONG it, and `negative` reverses the request.
/// History: docs/decisions/adapter-core.md#material-direction-toward</remarks>
public class MaterialDirectionRuleTests
{
    private const string Minus = "\u2212";   // the sign character the surface uses

    private static readonly double[] UpZ = [0d, 0d, 1d];
    private static readonly double[] DownZ = [0d, 0d, -1d];
    private static readonly double[] UpX = [1d, 0d, 0d];

    [Theory]
    // normal +z: a cut removes AGAINST the normal, base/boss add ALONG it; negative reverses.
    [InlineData(false, false, "+z")]   // boss/base positive -> along normal
    [InlineData(false, true, "-z")]    // boss/base negative -> against normal
    [InlineData(true, false, "-z")]    // cut positive      -> against normal
    [InlineData(true, true, "+z")]     // cut negative      -> along normal
    public void SignRule_OnPositiveZ(bool removing, bool negative, string expected)
    {
        var direction = MaterialDirectionRule.Toward(UpZ, removing, negative, symmetric: false);
        Assert.NotNull(direction);
        Assert.Equal(expected.Replace("-", Minus), MaterialDirectionRule.Describe(direction!, both: false));
    }

    [Theory]
    // normal -z: the whole rule flips with the normal, which is exactly why the normal must be MEASURED.
    [InlineData(false, false, "-z")]
    [InlineData(true, false, "+z")]
    [InlineData(true, true, "-z")]
    public void SignRule_OnNegativeZ(bool removing, bool negative, string expected)
    {
        var direction = MaterialDirectionRule.Toward(DownZ, removing, negative, symmetric: false);
        Assert.Equal(expected.Replace("-", Minus), MaterialDirectionRule.Describe(direction!, both: false));
    }

    [Fact]
    public void Symmetric_IsBothSides_NotMissing()
    {
        // symmetric is a VALUE (both sides), not a null to be read as "no direction".
        Assert.Null(MaterialDirectionRule.Toward(UpZ, removing: true, negative: false, symmetric: true));
        Assert.Equal("±z (в обе стороны)", MaterialDirectionRule.DescribeSymmetric(UpZ));
        Assert.Equal("±x (в обе стороны)", MaterialDirectionRule.DescribeSymmetric(UpX));
    }

    [Fact]
    public void TiltedPlane_NamesTheVector_NotAGuessedAxis()
    {
        double[] tilted = [0.5773502691896258d, 0.5773502691896258d, 0.5773502691896258d];
        Assert.Null(MaterialDirectionRule.AxisLabel(tilted));
        var text = MaterialDirectionRule.Describe(tilted, both: false);
        Assert.StartsWith("(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("z", text[1..^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AxisLabel_RejectsANearAxisVector()
    {
        Assert.Equal("+z", MaterialDirectionRule.AxisLabel(UpZ));
        Assert.Equal(Minus + "z", MaterialDirectionRule.AxisLabel(DownZ));
        Assert.Equal("+x", MaterialDirectionRule.AxisLabel(UpX));
        // A vector that is close to an axis but not aligned is NOT named as that axis.
        Assert.Null(MaterialDirectionRule.AxisLabel([0.001d, 0d, 0.999999d]));
    }

    [Fact]
    public void AxisIndex_NamesTheAxisOrNothing()
    {
        Assert.Equal(0, MaterialDirectionRule.AxisIndex(UpX));
        Assert.Equal(2, MaterialDirectionRule.AxisIndex(UpZ));
        Assert.Equal(2, MaterialDirectionRule.AxisIndex(DownZ));
        Assert.Null(MaterialDirectionRule.AxisIndex([0.5d, 0.5d, 0.7071067811865476d]));
        Assert.Null(MaterialDirectionRule.AxisIndex(null));
    }

    [Fact]
    public void SameSide_ComparesSense()
    {
        Assert.True(MaterialDirectionRule.SameSide(UpZ, [0d, 0d, 1d]));
        Assert.False(MaterialDirectionRule.SameSide(UpZ, DownZ));
        Assert.False(MaterialDirectionRule.SameSide(UpZ, UpX));
    }
}
