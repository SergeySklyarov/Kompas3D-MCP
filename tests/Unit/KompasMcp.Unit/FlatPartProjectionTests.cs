using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The standard view a flat part's normal selects.</summary>
/// <remarks>TEST: docs/decisions/flat-part.md#view-choice — the axis decides the view, a tilted normal is
/// refused, and the sign only swaps the opposite pair of the same axis.</remarks>
public sealed class FlatPartProjectionTests
{
    [Theory]
    [InlineData(0d, 0d, 1d, 3, "Сверху")]
    [InlineData(0d, 0d, -1d, 4, "Снизу")]
    [InlineData(0d, -1d, 0d, 1, "Спереди")]
    [InlineData(0d, 1d, 0d, 2, "Сзади")]
    [InlineData(-1d, 0d, 0d, 5, "Слева")]
    [InlineData(1d, 0d, 0d, 6, "Справа")]
    public void ChoosesTheStandardViewOfTheNormalAxis(double x, double y, double z, int flag, string name)
    {
        var view = FlatPartProjection.Choose(new[] { x, y, z });

        Assert.NotNull(view);
        Assert.Equal(flag, view.Flag);
        Assert.Equal(name, view.Name);
    }

    [Fact]
    public void ScalesANormalThatIsNotUnitLength()
    {
        var view = FlatPartProjection.Choose(new[] { 0d, 0d, 5d });

        Assert.Equal(3, view!.Flag);
        Assert.Equal("Z", view.Axis);
    }

    [Theory]
    [InlineData(0.1d, 0d, 1d)]
    [InlineData(0.7071d, 0.7071d, 0d)]
    [InlineData(0.02d, 0.02d, 1d)]
    public void RefusesANormalThatIsNotAlongAnAxis(double x, double y, double z)
    {
        Assert.Null(FlatPartProjection.Choose(new[] { x, y, z }));
    }

    [Fact]
    public void RefusesAMissingOrDegenerateNormal()
    {
        Assert.Null(FlatPartProjection.Choose(null));
        Assert.Null(FlatPartProjection.Choose(new[] { 0d, 0d }));
        Assert.Null(FlatPartProjection.Choose(new[] { 0d, 0d, 0d }));
        Assert.Null(FlatPartProjection.Choose(new[] { double.NaN, 0d, 1d }));
    }
}
