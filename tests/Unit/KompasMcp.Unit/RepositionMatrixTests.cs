using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Матрица положения тела и базис плоскости для B3 — проверяются на ТОЙ ЖЕ геометрии, которой
/// измерен маршрут в КОМПАСе.
/// </summary>
/// <remarks>
/// <para>
/// Это не «тест ради покрытия»: числа ниже — эталон §6.6 наряда, и они же получены пробой
/// <c>--reposition</c> (прогон <c>929f08886f1348fe921943052a4026b0</c>, шаги RP.3, RP.4, RP.5).
/// Если построитель матрицы разойдётся с этими числами, адаптер начнёт двигать тело не туда, а
/// КОМПАС на это не ошибается — он просто выполнит другое преобразование.
/// </para>
/// <para>
/// Асимметричный брусок <c>[10,30]×[0,10]×[0,5]</c> выбран намеренно: по симметричной детали
/// знак угла и направление оси неразличимы, и тест «прошёл бы» на неверной матрице.
/// </para>
/// </remarks>
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
        // Правое правило вокруг Z: (x,y) → (−y,x). Именно это дало измерение RP.4.
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        AssertClose(new[] { -10d, 10d, 0d, 0d, 30d, 5d }, Bounds(matrix));
    }

    [Fact]
    public void RotateAboutZ_StoresAxisImagesInTheMeasuredArrayLayout()
    {
        // РАЗЛИЧАЮЩИЙ контроль раскладки — того, чего здесь не было и из-за отсутствия чего
        // 18.09.2026 поворот через MCP отвергался как NO_GEOMETRY_CHANGE. Все прочие тесты этого
        // класса читают матрицу через Apply, то есть проверяют СОГЛАСИЕ построения с чтением, а не
        // саму раскладку: две взаимно транспонированные ошибки такое согласие сохраняют целиком.
        // Перенос дефект не ловит по той же причине — единичный поворот симметричен.
        //
        // Здесь сверяется СЫРОЙ массив, то есть ровно то, что уходит в Position.InitByMatrix3D.
        // Эталон — раскладка, которой поворот измерен в КОМПАСе (проба RP.4, RotationZ): три числа
        // подряд есть ОБРАЗ оси. Для +90° вокруг Z: образ X = (0,1,0), образ Y = (−1,0,0),
        // образ Z = (0,0,1).
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);

        AssertClose(new[] { 0d, 1d, 0d }, new[] { matrix[0], matrix[1], matrix[2] });
        AssertClose(new[] { -1d, 0d, 0d }, new[] { matrix[4], matrix[5], matrix[6] });
        AssertClose(new[] { 0d, 0d, 1d }, new[] { matrix[8], matrix[9], matrix[10] });
        AssertClose(new[] { 0d, 0d, 0d }, new[] { matrix[12], matrix[13], matrix[14] });
        Assert.Equal(1d, matrix[15], 12);

        // Отрицательный контроль: транспонированная раскладка обязана НЕ совпасть с эталоном.
        // Без него тест не отличал бы верную раскладку от перепутанной.
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

        // Точка оси обязана остаться на месте — это то, чем поворот «вокруг оси» отличается от
        // поворота «вокруг начала с последующим переносом».
        var fixedPoint = RepositionMatrix.Apply(matrix, new[] { 5d, 0d, 0d });
        Assert.Equal(5d, fixedPoint[0], 12);
        Assert.Equal(0d, fixedPoint[1], 12);
        Assert.Equal(0d, fixedPoint[2], 12);
    }

    [Fact]
    public void RotateBackwards_IsNotTheSameAsForwards()
    {
        // Отрицательный контроль: на асимметричной детали −90° обязан дать ДРУГОЙ габарит.
        // Тест, который проходит и на перепутанном знаке, ничего не доказывает.
        var forward = Bounds(RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d));
        var backward = Bounds(RepositionMatrix.RotateAboutAxis(
            new[] { 0d, 0d, 0d }, new[] { 0d, 0d, 1d }, -90d));
        AssertClose(new[] { 0d, -30d, 0d, 10d, -10d, 5d }, backward);

        // Сравниваются только X и Y: поворот вокруг Z не меняет Z, и совпадение по Z — не признак
        // перепутанного знака, а следствие того, что ось поворота ей перпендикулярна.
        foreach (var index in new[] { 0, 1, 3, 4 })
        {
            Assert.True(Math.Abs(forward[index] - backward[index]) > 1d,
                $"Прямой и обратный поворот совпали в позиции {index}: {forward[index]:G17} против {backward[index]:G17}.");
        }
    }

    [Fact]
    public void RotateAboutAxisThroughPoint_MatchesAnalyticTranslation()
    {
        // Перенос поворота вокруг точки c равен c − R·c. При c = (5,0,0) и +90° вокруг Z это
        // (5,0,0) − (0,5,0) = (5,−5,0) — то, что записано в эталоне §6.6.
        var matrix = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        Assert.Equal(5d, matrix[12], 12);
        Assert.Equal(-5d, matrix[13], 12);
        Assert.Equal(0d, matrix[14], 12);
    }

    [Fact]
    public void Rotate_AboutTiltedAxis_KeepsLengths()
    {
        // Наклонная ось: расстояния между вершинами обязаны сохраниться. Это ловит матрицу,
        // которая «выглядит вращением», но не ортогональна.
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
        // Измеренный маршрут создания плоскости — по трём точкам модели, где нормаль равна
        // (P2−P1)×(P3−P1). Базис обязан воспроизвести ЗАДАННУЮ нормаль, а не её поворот в плоскости.
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
        // Знак s = n·(p − p₀) от длины нормали не зависит, поэтому единичная нормаль — тот же
        // ответ, но воспроизводимый.
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

    /// <summary>
    /// Сравнение координат с допуском. Точное равенство здесь непригодно: <c>cos 90°</c> не равен
    /// нулю, и разность <c>6.1e-16</c> — это форма записи нуля, а не ошибка построения.
    /// </summary>
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
