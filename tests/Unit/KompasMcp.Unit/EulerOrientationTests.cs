using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Decomposing a placement into Euler angles and rebuilding it — checked by MATRIX EQUIVALENCE.</summary>
/// <remarks>INVARIANT: Euler parametrisation is AMBIGUOUS at nutation 0/180, so validity is proved by the matrix
/// rebuilt from the read triple equalling the original, not by matching numbers.
/// MEASURED (probe <c>--reposition-params</c>, step RP.25): exactly ONE of six products matched — <c>PNR</c> —
/// with difference 0 on all three composite postures (the other five differ by 1); the triples <c>(0,0,90)</c>
/// and <c>(90,90,0)</c> are read FROM THE DOCUMENT.
/// ASSUMPTION: composite postures use a tilted axis because a symmetric part cannot tell an axis swap.
/// History: docs/decisions/tests.md#euler-tests-2</remarks>
public class EulerOrientationTests
{
    /// <summary>Tolerance for numbers read from the document — the same as the matrix tolerance.</summary>
    private const int Precision = 9;

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_AxisZThroughPoint()
    {
        // Posture C1: axis (0,0,1) by 90° through (5,0,0); the read triple is (0, 0, 90).
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(0d, 0d, 90d));
    }

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_TiltedAxis()
    {
        // Posture C2: axis (1,1,1)/√3 by 120° through (5,−3,7); the read triple is (90, 90, 0). Also controls
        // that PNR reproduces a TILT, not only a rotation about a coordinate axis.
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(90d, 90d, 0d));
    }

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_HalfTurn()
    {
        // Posture C3: half a turn — the skew part is zero, a separate branch.
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 180d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(0d, 0d, 180d));
    }

    [Fact]
    public void AnglesFromRotation_ReturnsMeasuredTriple_TiltedAxis()
    {
        // The triple is compared with the MEASURED one, not with any valid one — a matrix can have several
        // valid triples, so agreement with the document is a separate fact.
        var measured = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d);

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(measured);

        Assert.Equal(90d, precession, Precision);
        Assert.Equal(90d, nutation, Precision);
        Assert.Equal(0d, rotation, Precision);
    }

    [Fact]
    public void AnglesFromRotation_ReturnsMeasuredTriple_QuarterTurn()
    {
        // Here nutation is zero, so the triple is degenerate; the test checks the degenerate branch returns
        // EXACTLY the measured one.
        var quarter = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        var (p1, n1, r1) = EulerOrientation.AnglesFromRotation(quarter);
        Assert.Equal(0d, p1, Precision);
        Assert.Equal(0d, n1, Precision);
        Assert.Equal(90d, r1, Precision);

        var half = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 180d);
        var (p2, n2, r2) = EulerOrientation.AnglesFromRotation(half);
        Assert.Equal(0d, p2, Precision);
        Assert.Equal(0d, n2, Precision);
        Assert.Equal(180d, r2, Precision);
    }

    [Fact]
    public void WrongOrder_DoesNotReproduceTheMeasuredMatrix()
    {
        // NEGATIVE CONTROL, without which the earlier tests prove nothing: a different order must give a
        // DIFFERENT matrix, and a noticeably different one, not a machine epsilon.
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d));

        var wrongOrder = EulerOrientation.RotationFromAngles(0d, 90d, 90d);
        var difference = EulerOrientation.MaxDifference(measured, wrongOrder);

        Assert.True(difference > 0.5d,
            $"Чужой порядок спряжения дал расхождение {difference:G17} — контроль не различает порядок, "
            + "и тогда верность порядка ничем не доказана.");
    }

    [Fact]
    public void AnglesFromRotation_RoundTripsEveryMeasuredPosture()
    {
        // Round trip over all postures: the decomposition must be REVERSIBLE, else what was read cannot be
        // rewritten with the same placement.
        var postures = new[]
        {
            RepositionMatrix.RotateAboutAxis(new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d),
            RepositionMatrix.RotateAboutAxis(new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 180d),
            RepositionMatrix.RotateAboutAxis(new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d),
            RepositionMatrix.RotateAboutAxis(new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d),
            RepositionMatrix.RotateAboutAxis(new[] { 3d, 2d, 1d }, new[] { 1d, 2d, 3d }, 37d),
            RepositionMatrix.RotateAboutAxis(new[] { -4d, 8d, 2d }, new[] { -1d, 0d, 2d }, 251d),
        };

        foreach (var posture in postures)
        {
            var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(posture);
            var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);
            AssertSameMatrix(Rotation(posture), restored);
        }
    }

    [Fact]
    public void FullPlacement_IsReproducedByAnglesPlusTranslation()
    {
        // The FULL placement is compared: orientation from the angles, translation from slots 12…14.
        var placement = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d);

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(placement);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);
        var translation = EulerOrientation.TranslationOf(placement);
        restored[12] = translation[0];
        restored[13] = translation[1];
        restored[14] = translation[2];

        AssertSameMatrix(placement, restored);

        // This posture's translation is probe-measured — it equals c − R·c.
        Assert.Equal(-2d, translation[0], Precision);
        Assert.Equal(-8d, translation[1], Precision);
        Assert.Equal(10d, translation[2], Precision);
    }

    [Fact]
    public void AxisAngleFromRotation_RecoversArbitraryAxis()
    {
        // The axis is recovered up to sign, so the test compares the VALIDITY of the pair: rotating by the
        // found angle about the found axis must give the original matrix.
        var cases = new[]
        {
            (Point: new[] { 0d, 0d, 0d }, Axis: new[] { 0d, 0d, 1d }, Angle: 90d),
            (Point: new[] { 5d, 0d, 0d }, Axis: new[] { 0d, 0d, 1d }, Angle: 180d),
            (Point: new[] { 5d, -3d, 7d }, Axis: new[] { 1d, 1d, 1d }, Angle: 120d),
            (Point: new[] { 1d, 2d, 3d }, Axis: new[] { -2d, 5d, 1d }, Angle: 73d),
        };

        foreach (var item in cases)
        {
            var placement = RepositionMatrix.RotateAboutAxis(item.Point, item.Axis, item.Angle);
            var (axis, angleDeg) = EulerOrientation.AxisAngleFromRotation(placement);

            Assert.Equal(3, axis.Length);
            Assert.True(Math.Abs(angleDeg - item.Angle) <= 1e-6,
                $"угол восстановлен как {angleDeg:G17} вместо {item.Angle:G17}");

            var rebuilt = RepositionMatrix.RotateAboutAxis(new[] { 0d, 0d, 0d }, axis, angleDeg);
            AssertSameMatrix(Rotation(placement), Rotation(rebuilt));
        }
    }

    [Fact]
    public void AxisPointFromPlacement_ReturnsARepresentative_NotTheRecordedInput()
    {
        // The axis point is NOT read: two rotations about ONE line but through DIFFERENT points give the SAME
        // placement, so the input point cannot be recovered.
        var throughFirst = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        var throughSecond = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 7d }, new[] { 0d, 0d, 1d }, 90d);

        AssertSameMatrix(throughFirst, throughSecond);

        // What is recovered is a REPRESENTATIVE of the line — the point with zero component along the axis;
        // publishing a "read" point would pass a derived value off as a recorded one.
        var representative = EulerOrientation.AxisPointFromPlacement(throughSecond);

        Assert.NotNull(representative);
        Assert.Equal(5d, representative![0], Precision);
        Assert.Equal(0d, representative[1], Precision);
        Assert.Equal(0d, representative[2], Precision);

        // And it is still a point of the SAME line: rotating about it gives the same placement.
        AssertSameMatrix(throughSecond, RepositionMatrix.RotateAboutAxis(
            representative, new[] { 0d, 0d, 1d }, 90d));
    }

    [Fact]
    public void AxisPointFromPlacement_ReturnsNullWhenThereIsNoRotation()
    {
        // A translation has no axis, so no axis point: returning zero would substitute a value for a missing one.
        Assert.Null(EulerOrientation.AxisPointFromPlacement(RepositionMatrix.Translate(new[] { 7d, -11d, 13d })));
        Assert.Null(EulerOrientation.AxisPointFromPlacement(RepositionMatrix.Identity()));
    }

    [Fact]
    public void IsIdentity_SeparatesTranslationFromRotation()
    {
        Assert.True(EulerOrientation.IsIdentity(RepositionMatrix.Translate(new[] { 7d, -11d, 13d })));
        Assert.False(EulerOrientation.IsIdentity(
            RepositionMatrix.RotateAboutAxis(new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d)));

        // Discriminating control: a translation BY ZERO is also an identity rotation, and it looks like a
        // translation — the pair the product distinguishes when reading.
        Assert.True(EulerOrientation.IsIdentity(RepositionMatrix.Translate(new[] { 0d, 0d, 0d })));
    }

    [Fact]
    public void UnitAxis_NormalisesAndRefusesZero()
    {
        var unit = EulerOrientation.UnitAxis(new[] { 0d, 0d, 7d });
        Assert.Equal(0d, unit[0], Precision);
        Assert.Equal(0d, unit[1], Precision);
        Assert.Equal(1d, unit[2], Precision);

        Assert.Throws<ArgumentException>(() => EulerOrientation.UnitAxis(new[] { 0d, 0d, 0d }));
        Assert.Throws<ArgumentException>(() => EulerOrientation.UnitAxis(new[] { 1d, 0d }));
    }

    [Fact]
    public void MaxDifference_ReportsTheWorstCell()
    {
        var left = RepositionMatrix.Identity();
        var right = RepositionMatrix.Identity();
        right[6] = 0.25d;

        Assert.Equal(0.25d, EulerOrientation.MaxDifference(left, right), Precision);
        Assert.Equal(0d, EulerOrientation.MaxDifference(left, left), Precision);
    }

    /// <summary>INVARIANT: the parametrisation pole at nutation 180° — the rebuild must reproduce the matrix.</summary>
    /// <remarks>MEASURED: the product refused with GEOMETRY_FAILED and a difference of 2 because the rotation
    /// sign at this pole used the neighbouring convention's formula. The matrix here is GIVEN AS NUMBERS — else
    /// the test would compare the decomposition with itself.
    /// History: docs/decisions/tests.md#euler-tests-pole-2</remarks>
    [Fact]
    public void AnglesFromRotation_ReproducesMatrixAtTheHalfTurnPole()
    {
        // Rotation 180° about axis (1,1,0): R = 2·u·uᵀ − I, u = (1,1,0)/√2.
        var requested = RepositionMatrix.Identity();
        requested[0] = 0d; requested[1] = 1d;
        requested[4] = 1d; requested[5] = 0d;
        requested[10] = -1d;

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(requested);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);

        AssertSameMatrix(Rotation(requested), Rotation(restored));
    }

    /// <summary>The SAME POLE ON THE OPPOSITE BRANCH: 180° about an axis with a non-zero Z component.</summary>
    /// <remarks>ASSUMPTION: axis (1,1,1) touches all three axes, so a sign swap in any of them is visible here.</remarks>
    [Fact]
    public void AnglesFromRotation_ReproducesMatrixAtTheHalfTurnPole_TiltedAxis()
    {
        // Rotation 180° about (1,1,1)/√3: R = 2·u·uᵀ − I.
        const double third = 2d / 3d;
        var requested = RepositionMatrix.Identity();
        requested[0] = -1d / 3d; requested[1] = third; requested[2] = third;
        requested[4] = third; requested[5] = -1d / 3d; requested[6] = third;
        requested[8] = third; requested[9] = third; requested[10] = -1d / 3d;

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(requested);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);

        AssertSameMatrix(Rotation(requested), Rotation(restored));
    }

    /// <summary>Rotation block only: the orientation check must not depend on the translation.</summary>
    private static double[] Rotation(IReadOnlyList<double> placement) => new[]
    {
        placement[0], placement[1], placement[2], placement[3],
        placement[4], placement[5], placement[6], placement[7],
        placement[8], placement[9], placement[10], placement[11],
        0d, 0d, 0d, 1d,
    };

    private static void AssertSameMatrix(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        var difference = EulerOrientation.MaxDifference(expected, actual);
        Assert.True(difference <= EulerOrientation.MatrixTolerance,
            $"матрицы разошлись на {difference:G17}: собрано [{string.Join(", ", actual)}], "
            + $"ожидалось [{string.Join(", ", expected)}]");
    }
}
