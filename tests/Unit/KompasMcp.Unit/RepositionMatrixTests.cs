using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Body placement matrix and plane basis for B3 — checked on the SAME geometry the KOMPAS route was measured on.</summary>
/// <remarks>MEASURED: the numbers below are reference §6.6 of the order, obtained by probe <c>--reposition</c>
/// (run <c>929f08886f1348fe921943052a4026b0</c>, steps RP.3, RP.4, RP.5). If the matrix builder diverges from
/// them, the adapter moves the body elsewhere and KOMPAS does not err — it just performs a different
/// transform. ASSUMPTION: the asymmetric bar <c>[10,30]×[0,10]×[0,5]</c> is deliberate — on a symmetric part
/// the angle sign and axis direction are indistinguishable and the test would pass on a wrong matrix.</remarks>
public class RepositionMatrixTests
{
    private static readonly double[][] BarCorners = BuildCorners(10d, 30d, 0d, 10d, 0d, 5d);

    [Fact]
    public void Identity_LeavesCornersInPlace()
    {
        foreach (var corner in BarCorners)
        {
            AssertClose(corner, RepositionMatrix.Apply(RepositionMatrix.Identity(), corner));
        }
    }

    [Fact]
    public void Translate_MovesBarToMeasuredBox()
    {
        var matrix = RepositionMatrix.Translate(new[] { 7d, -11d, 13d });
        AssertClose(new[] { 17d, -11d, 13d, 37d, -1d, 18d }, Bounds(matrix));
    }

    [Fact]
    public void RotateAboutZThroughOrigin_FollowsRightHandRule()
    {
        // Right-hand rule about Z: (x,y) → (−y,x). This is what measurement RP.4 gave.
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        AssertClose(new[] { -10d, 10d, 0d, 0d, 30d, 5d }, Bounds(matrix));
    }

    [Fact]
    public void RotateAboutZ_StoresAxisImagesInTheMeasuredArrayLayout()
    {
        // DISCRIMINATING control of the layout — the thing that was absent here, and whose absence made a rotation through MCP be rejected as NO_GEOMETRY_CHANGE on 18.09.2026. All the other tests in this class read the matrix through Apply, i.e. check AGREEMENT of build with read, not the layout itself: two mutually transposed errors preserve that agreement entirely. A translation does not catch the defect for the same reason — an identity rotation is symmetric.
        //
        // Here the RAW array is compared, exactly what goes into Position.InitByMatrix3D. The reference is the layout by which the rotation was measured in KOMPAS (probe RP.4, RotationZ): three consecutive numbers are the IMAGE of an axis. For +90° about Z: image X = (0,1,0), image Y = (−1,0,0), Z = (0,0,1).
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);

        AssertClose(new[] { 0d, 1d, 0d }, new[] { matrix[0], matrix[1], matrix[2] });
        AssertClose(new[] { -1d, 0d, 0d }, new[] { matrix[4], matrix[5], matrix[6] });
        AssertClose(new[] { 0d, 0d, 1d }, new[] { matrix[8], matrix[9], matrix[10] });
        AssertClose(new[] { 0d, 0d, 0d }, new[] { matrix[12], matrix[13], matrix[14] });
        Assert.Equal(1d, matrix[15], 12);

        // Negative control: the transposed layout must NOT match the reference — without it the test would not
        // tell a correct layout from a swapped one.
        var transposed = new[] { 0d, -1d, 0d };
        Assert.False(Math.Abs(transposed[0] - matrix[0]) <= 1e-9
                     && Math.Abs(transposed[1] - matrix[1]) <= 1e-9
                     && Math.Abs(transposed[2] - matrix[2]) <= 1e-9,
            "образ оси X совпал с транспонированной раскладкой — раскладка перепутана");
    }

    [Fact]
    public void RotateAboutZThroughPoint_KeepsAxisPointFixed()
    {
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        AssertClose(new[] { -5d, 5d, 0d, 5d, 25d, 5d }, Bounds(matrix));

        // INVARIANT: the axis point must stay in place — that is what distinguishes a rotation "about an axis"
        // from one "about the origin followed by a translation".
        var fixedPoint = RepositionMatrix.Apply(matrix, new[] { 5d, 0d, 0d });
        Assert.Equal(5d, fixedPoint[0], 12);
        Assert.Equal(0d, fixedPoint[1], 12);
        Assert.Equal(0d, fixedPoint[2], 12);
    }

    [Fact]
    public void RotateBackwards_IsNotTheSameAsForwards()
    {
        // Negative control: on an asymmetric part −90° must give a DIFFERENT bounding box — a test that also
        // passes on a swapped sign proves nothing.
        var forward = Bounds(RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d));
        var backward = Bounds(RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, -90d));
        AssertClose(new[] { 0d, -30d, 0d, 10d, -10d, 5d }, backward);

        // Only X and Y are compared: a rotation about Z does not change Z, and a match in Z is not a sign of a
        // swapped sign but a consequence of the axis being perpendicular to it.
        foreach (var index in new[] { 0, 1, 3, 4 })
        {
            Assert.True(Math.Abs(forward[index] - backward[index]) > 1d,
                $"Прямой и обратный поворот совпали в позиции {index}: {forward[index]:G17} против {backward[index]:G17}.");
        }
    }

    [Fact]
    public void RotateAboutAxisThroughPoint_MatchesAnalyticTranslation()
    {
        // The translation of a rotation about point c equals c − R·c. With c = (5,0,0) and +90° about Z this is
        // (5,0,0) − (0,5,0) = (5,−5,0) — what reference §6.6 records.
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        Assert.Equal(5d, matrix[12], 12);
        Assert.Equal(-5d, matrix[13], 12);
        Assert.Equal(0d, matrix[14], 12);
    }

    [Fact]
    public void Rotate_AboutTiltedAxis_KeepsLengths()
    {
        // A tilted axis: distances between vertices must be preserved. This catches a matrix that "looks like a
        // rotation" but is not orthogonal.
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 3d, 2d, 1d }, new[] { 1d, 2d, 3d }, 37d);
        foreach (var corner in BarCorners)
        {
            var image = RepositionMatrix.Apply(matrix, corner);
            var origin = RepositionMatrix.Apply(matrix, new[] { 10d, 0d, 0d });
            Assert.Equal(Distance(corner, new[] { 10d, 0d, 0d }), Distance(image, origin), 9);
        }
    }

    [Fact]
    public void ZeroAxisDirection_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 0d }, 90d));
    }

    [Fact]
    public void NonFiniteVector_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => RepositionMatrix.Translate(new[] { 1d, double.NaN, 0d }));
        Assert.Throws<ArgumentException>(() => RepositionMatrix.Translate(new[] { 1d, 0d, double.PositiveInfinity }));
    }

    [Fact]
    public void WrongLength_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => RepositionMatrix.Translate(new[] { 1d, 2d }));
        Assert.Throws<ArgumentException>(() => RepositionMatrix.Apply(new double[12], new[] { 0d, 0d, 0d }));
    }

    [Fact]
    public void PlaneBasis_ReproducesRequestedNormal()
    {
        // The measured plane-creation route is by three model points, where the normal equals (P2−P1)×(P3−P1).
        // INVARIANT: the basis must reproduce the REQUESTED normal, not a rotation of it within the plane.
        foreach (var normal in new[]
        {
            new[] { 1d, 0d, 0d },
            new[] { 0d, 1d, 0d },
            new[] { 0d, 0d, 1d },
            new[] { 1d, 1d, 0d },
            new[] { -3d, 2d, -5d },
            new[] { 1d, 1d, 1d },
        })
        {
            var (p1, p2, p3, unit) = PlaneBasis.ThreePoints(new[] { 10d, 0d, 0d }, normal);
            var cross = RigidFrame.Cross(
                new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] },
                new[] { p3[0] - p1[0], p3[1] - p1[1], p3[2] - p1[2] });

            Assert.Equal(unit[0], cross[0], 12);
            Assert.Equal(unit[1], cross[1], 12);
            Assert.Equal(unit[2], cross[2], 12);
        }
    }

    [Fact]
    public void PlaneBasis_NormalisesLengthButKeepsDirection()
    {
        // The sign s = n·(p − p₀) does not depend on the normal length, so a unit normal gives the same answer
        // but a reproducible one.
        var (_, _, _, unit) = PlaneBasis.ThreePoints(new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 7d });
        Assert.Equal(0d, unit[0], 12);
        Assert.Equal(0d, unit[1], 12);
        Assert.Equal(1d, unit[2], 12);
    }

    [Fact]
    public void PlaneBasis_ZeroNormal_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => PlaneBasis.FromNormal(new[] { 0d, 0d, 0d }));
    }

    [Fact]
    public void PlaneBasis_NonFiniteNormal_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => PlaneBasis.FromNormal(new[] { 1d, double.NaN, 0d }));
        Assert.Throws<ArgumentException>(() => PlaneBasis.FromNormal(new[] { 1d, 0d, double.NegativeInfinity }));
    }

    [Fact]
    public void PlaneBasis_PointsLieInPlaneThroughGivenPoint()
    {
        var (p1, p2, p3, unit) = PlaneBasis.ThreePoints(new[] { 10d, 3d, -4d }, new[] { 1d, 0d, 0d });
        foreach (var point in new[] { p1, p2, p3 })
        {
            var offset = new[] { point[0] - 10d, point[1] - 3d, point[2] + 4d };
            Assert.Equal(0d, RigidFrame.Dot(offset, unit), 12);
        }
    }

    private static double[][] BuildCorners(double x0, double x1, double y0, double y1, double z0, double z1) =>
        new[]
        {
            new[] { x0, y0, z0 }, new[] { x1, y0, z0 }, new[] { x0, y1, z0 }, new[] { x1, y1, z0 },
            new[] { x0, y0, z1 }, new[] { x1, y0, z1 }, new[] { x0, y1, z1 }, new[] { x1, y1, z1 },
        };

    private static double[] Bounds(double[] matrix)
    {
        var images = BarCorners.Select(c => RepositionMatrix.Apply(matrix, c)).ToList();
        return new[]
        {
            images.Min(p => p[0]), images.Min(p => p[1]), images.Min(p => p[2]),
            images.Max(p => p[0]), images.Max(p => p[1]), images.Max(p => p[2]),
        };
    }

    private static double Distance(double[] a, double[] b) =>
        RigidFrame.Norm(new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] });

    /// <summary>Coordinate comparison with a tolerance. Exact equality is unusable here: <c>cos 90°</c> is not
    /// zero, and a difference of <c>6.1e-16</c> is a way of writing zero, not a build error.</summary>
    private static void AssertClose(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-9,
                $"Координата {i}: ожидалось {expected[i]:G17}, получено {actual[i]:G17}.");
        }
    }
}
