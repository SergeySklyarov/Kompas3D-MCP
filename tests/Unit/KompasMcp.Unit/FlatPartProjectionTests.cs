using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The standard view a flat part's normal selects.</summary>
/// <remarks>TEST: docs/decisions/flat-part.md#view-choice — the axis of the normal picks the pair of views
/// that look along it («Спереди/Сзади» - XY, «Сверху/Снизу» - XZ, «Слева/Справа» - YZ), the sign picks the
/// member of that pair, and a tilted normal is refused.</remarks>
public sealed class FlatPartProjectionTests
{
    [Theory]
    [InlineData(0d, 0d, 1d, 1, "Спереди")]
    [InlineData(0d, 0d, -1d, 2, "Сзади")]
    [InlineData(0d, 1d, 0d, 3, "Сверху")]
    [InlineData(0d, -1d, 0d, 4, "Снизу")]
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

        Assert.Equal(1, view!.Flag);
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

    /// <summary>The 4×4 carries the same side the standard views pick, for every axis direction.</summary>
    /// <remarks>TEST: docs/decisions/flat-part.md#view-choice — the third row IS the line of sight, so the
    /// side of the pair is the sign of the normal and nothing else. A matrix that disagreed with
    /// <see cref="FlatPartProjection.Choose"/> would build the view of the opposite face.
    /// </remarks>
    [Theory]
    [InlineData(0d, 0d, 1d)]
    [InlineData(0d, 0d, -1d)]
    [InlineData(0d, 1d, 0d)]
    [InlineData(0d, -1d, 0d)]
    [InlineData(1d, 0d, 0d)]
    [InlineData(-1d, 0d, 0d)]
    public void MatrixCarriesTheSameAxisAsTheChosenView(double x, double y, double z)
    {
        var matrix = FlatPartProjection.Matrix(new[] { x, y, z });

        Assert.NotNull(matrix);
        Assert.Equal(16, matrix.Length);
        Assert.Equal(new[] { x, y, z }, new[] { matrix[8], matrix[9], matrix[10] });
        Assert.NotNull(FlatPartProjection.Choose(new[] { x, y, z }));
    }

    /// <summary>The triple is orthonormal and right-handed, and the translation is the last row.</summary>
    /// <remarks>TEST: docs/decisions/flat-part.md#view-choice — the measured convention is rows = the view's
    /// axes, last row = translation, element 15 unused; a left-handed triple would mirror the contour.
    /// </remarks>
    [Theory]
    [InlineData(0.7071d, 0.7071d, 0d)]
    [InlineData(0.5d, 0.5d, 0.7071d)]
    [InlineData(0d, -0.6d, 0.8d)]
    public void MatrixIsARightHandedOrthonormalFrame(double x, double y, double z)
    {
        var matrix = FlatPartProjection.Matrix(new[] { x, y, z })!;

        var rows = new[] { 0, 4, 8 }.Select(offset => new[] { matrix[offset], matrix[offset + 1], matrix[offset + 2] })
            .ToArray();
        foreach (var row in rows)
        {
            Assert.Equal(1d, Length(row), 9);
        }

        Assert.Equal(0d, Dot(rows[0], rows[1]), 9);
        Assert.Equal(0d, Dot(rows[1], rows[2]), 9);
        Assert.Equal(0d, Dot(rows[0], rows[2]), 9);
        Assert.Equal(1d, Dot(rows[0], Cross(rows[1], rows[2])), 9);
        Assert.Equal(new[] { 0d, 0d, 0d }, new[] { matrix[3], matrix[7], matrix[11] });
        Assert.Equal(new[] { 0d, 0d, 0d }, new[] { matrix[12], matrix[13], matrix[14] });
    }

    [Fact]
    public void RefusesAMatrixForANormalThatIsNotADirection()
    {
        Assert.Null(FlatPartProjection.Matrix(null));
        Assert.Null(FlatPartProjection.Matrix(new[] { 0d, 0d, 0d }));
        Assert.Null(FlatPartProjection.Matrix(new[] { 1d, double.PositiveInfinity, 0d }));
    }

    private static double Length(double[] vector) => Math.Sqrt(Dot(vector, vector));

    private static double Dot(double[] left, double[] right) =>
        (left[0] * right[0]) + (left[1] * right[1]) + (left[2] * right[2]);

    private static double[] Cross(double[] left, double[] right) => new[]
    {
        (left[1] * right[2]) - (left[2] * right[1]),
        (left[2] * right[0]) - (left[0] * right[2]),
        (left[0] * right[1]) - (left[1] * right[0]),
    };
}
