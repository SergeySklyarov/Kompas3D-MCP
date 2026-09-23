using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Разложение размещения на углы Эйлера и обратная сборка — проверяются ЭКВИВАЛЕНТНОСТЬЮ МАТРИЦ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему эквивалентность матриц, а не равенство чисел.</b> Параметризация углами Эйлера
/// НЕОДНОЗНАЧНА: при нутации 0 или 180° сумма прецессии и вращения определена с точностью до
/// перераспределения. Требование равенства тройки отвергло бы ВЕРНОЕ разложение, поэтому годность
/// преобразования доказывается тем, что матрица, собранная из прочитанной тройки, равна исходной.
/// </para>
/// <para>
/// <b>Числа здесь измеренные, а не выведенные.</b> Порядок спряжения и единицы измерены пробой
/// <c>--reposition-params</c> (прогон <c>a336120926fc4652a8bf737562568271</c>, шаг RP.25): совпало
/// РОВНО ОДНО произведение из шести — <c>PNR</c> — и оно же дало расхождение 0 на всех трёх
/// составных постановках, тогда как у остальных пяти расхождение равно 1. Тройки <c>(0,0,90)</c>
/// (ось Z через (5,0,0)) и <c>(90,90,0)</c> (ось (1,1,1)/√3 на 120°) прочитаны ИЗ ДОКУМЕНТА, а не
/// подобраны под ответ.
/// </para>
/// <para>
/// <b>Асимметрия здесь обязательна так же, как в <see cref="RepositionMatrixTests"/>:</b> по
/// симметричной детали перестановка осей неразличима, и тест «прошёл бы» на неверном порядке.
/// Поэтому составные постановки взяты с наклонной осью.
/// </para>
/// </remarks>
public class EulerOrientationTests
{
    /// <summary>Допуск сверки чисел, прочитанных из документа: там же, где и допуск матриц.</summary>
    private const int Precision = 9;

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_AxisZThroughPoint()
    {
        // Эталон RP.25, постановка C1: ось (0,0,1) на 90° через (5,0,0). Прочитанная из документа
        // тройка — (прецессия, нутация, вращение) = (0, 0, 90).
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(0d, 0d, 90d));
    }

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_TiltedAxis()
    {
        // Эталон RP.25, постановка C2: ось (1,1,1)/√3 на 120° через (5,−3,7). Прочитанная тройка —
        // (90, 90, 0). Она же — контроль того, что порядок PNR воспроизводит НАКЛОН, а не только
        // поворот вокруг координатной оси.
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(90d, 90d, 0d));
    }

    [Fact]
    public void RotationFromAngles_ReproducesMeasuredPosture_HalfTurn()
    {
        // Эталон RP.25, постановка C3: пол-оборота. Здесь кососимметричная часть нулевая, и это
        // отдельная ветвь разложения — та, на которой вырожденный вывод оси вернул бы ноль.
        var measured = Rotation(RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 180d));

        AssertSameMatrix(measured, EulerOrientation.RotationFromAngles(0d, 0d, 180d));
    }

    [Fact]
    public void AnglesFromRotation_ReturnsMeasuredTriple_TiltedAxis()
    {
        // Тройка сверяется с ИЗМЕРЕННОЙ (RP.25: euler_angles_C2 = (90, 90, 0)), а не с любой годной:
        // годных троек у одной матрицы может быть несколько, и совпадение с документом — отдельный
        // факт, который стоит проверять отдельно от эквивалентности.
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
        // RP.25: euler_angles_C1 = (0, 0, 90) и euler_angles_C3 = (0, 0, 180). Здесь нутация нулевая,
        // то есть тройка вырождена, и проверяется, что ветвь вырождения даёт ИМЕННО измеренное.
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
        // ОТРИЦАТЕЛЬНЫЙ КОНТРОЛЬ, без которого предыдущие тесты ничего не доказывают: если бы
        // RotationFromAngles собирал ЛЮБОЙ порядок, они проходили бы всё равно. Те же три числа,
        // прочитанные в другом порядке, обязаны дать ДРУГУЮ матрицу — и притом заметно другую,
        // а не на машинный эпсилон.
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
        // Круговой прогон на всех постановках RP.25 сразу: разложение обязано быть ОБРАТИМЫМ, иначе
        // прочитанное из документа нельзя ни сверить, ни перезаписать тем же размещением.
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
        // Сверяется ПОЛНОЕ размещение, а не только поворот: ориентация берётся углами, перенос —
        // из слотов 12…14. Именно так продукт и записывает признак (RP.25), поэтому проверять
        // половину здесь значило бы не проверять маршрут.
        var placement = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, -3d, 7d }, new[] { 1d, 1d, 1d }, 120d);

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(placement);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);
        var translation = EulerOrientation.TranslationOf(placement);
        restored[12] = translation[0];
        restored[13] = translation[1];
        restored[14] = translation[2];

        AssertSameMatrix(placement, restored);

        // Перенос этой постановки измерен пробой (RP.25: displacement_written_C2 = (−2, −8, 10)) —
        // он равен c − R·c, и его совпадение с документом проверяется отдельно от эквивалентности.
        Assert.Equal(-2d, translation[0], Precision);
        Assert.Equal(-8d, translation[1], Precision);
        Assert.Equal(10d, translation[2], Precision);
    }

    [Fact]
    public void AxisAngleFromRotation_RecoversArbitraryAxis()
    {
        // Ось восстанавливается с точностью до знака: (d, θ) и (−d, −θ) дают одну матрицу. Поэтому
        // сверяется не тройка чисел, а ГОДНОСТЬ пары — поворот на найденный угол вокруг найденной оси
        // обязан дать исходную матрицу.
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
        // ЗДЕСЬ ДОКАЗЫВАЕТСЯ, ЧТО ТОЧКА ОСИ НЕ ЧИТАЕТСЯ, и это довод в пользу того, что продукт её
        // не публикует. Два поворота вокруг ОДНОЙ прямой, но через РАЗНЫЕ её точки, дают ОДНО И ТО ЖЕ
        // размещение — значит из размещения исходная точка не восстанавливается в принципе.
        var throughFirst = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d);
        var throughSecond = RepositionMatrix.RotateAboutAxis(
            new[] { 5d, 0d, 7d }, new[] { 0d, 0d, 1d }, 90d);

        AssertSameMatrix(throughFirst, throughSecond);

        // Восстанавливается ПРЕДСТАВИТЕЛЬ прямой — точка с нулевой составляющей вдоль оси. Для
        // (5,0,0) он совпадает с исходным входом, для (5,0,7) — нет, и это ровно тот случай, в
        // котором публикация «прочитанной» точки выдала бы выведенное за записанное.
        var representative = EulerOrientation.AxisPointFromPlacement(throughSecond);

        Assert.NotNull(representative);
        Assert.Equal(5d, representative![0], Precision);
        Assert.Equal(0d, representative[1], Precision);
        Assert.Equal(0d, representative[2], Precision);

        // И это по-прежнему точка ТОЙ ЖЕ прямой: поворот вокруг неё даёт то же размещение.
        AssertSameMatrix(throughSecond, RepositionMatrix.RotateAboutAxis(
            representative, new[] { 0d, 0d, 1d }, 90d));
    }

    [Fact]
    public void AxisPointFromPlacement_ReturnsNullWhenThereIsNoRotation()
    {
        // Перенос точку оси не определяет: у него оси нет. Вернуть здесь ноль значило бы подставить
        // значение вместо отсутствующего — то, что запрещено на всём маршруте чтения.
        Assert.Null(EulerOrientation.AxisPointFromPlacement(RepositionMatrix.Translate(new[] { 7d, -11d, 13d })));
        Assert.Null(EulerOrientation.AxisPointFromPlacement(RepositionMatrix.Identity()));
    }

    [Fact]
    public void IsIdentity_SeparatesTranslationFromRotation()
    {
        Assert.True(EulerOrientation.IsIdentity(RepositionMatrix.Translate(new[] { 7d, -11d, 13d })));
        Assert.False(EulerOrientation.IsIdentity(
            RepositionMatrix.RotateAboutAxis(new[] { 5d, 0d, 0d }, new[] { 0d, 0d, 1d }, 90d)));

        // Различающий контроль: перенос НА НОЛЬ — тоже единичный поворот, и вид у него перенос.
        // Именно эту пару продукт и различает при чтении (RP.25, постановка C1 против D1).
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

    /// <summary>
    /// ПОЛЮС ПАРАМЕТРИЗАЦИИ ПРИ НУТАЦИИ 180°: сборка обязана воспроизвести матрицу.
    /// </summary>
    /// <remarks>
    /// ЭТО РЕГРЕССИОННЫЙ ТЕСТ, А НЕ УКРАШЕНИЕ. Дефект был найден строкой приёмки B3.59, а не
    /// модульными тестами: прежние шесть поз проверяли либо нутацию 0, либо координатную ось, и
    /// полюс при 180° не покрывался ни одной из них. Продукт отказывал кодом GEOMETRY_FAILED с
    /// расхождением 2 — собственная проверка записи не могла воспроизвести размещение из
    /// прочитанных углов, потому что знак вращения на этом полюсе брался по формуле соседнего
    /// соглашения. Матрица здесь ЗАДАНА ЧИСЛАМИ, а не собрана из углов: иначе тест сверял бы
    /// разложение с самим собой и прошёл бы на любой ошибке знака.
    /// </remarks>
    [Fact]
    public void AnglesFromRotation_ReproducesMatrixAtTheHalfTurnPole()
    {
        // Поворот 180° вокруг оси (1,1,0): R = 2·u·uᵀ − I, u = (1,1,0)/√2.
        var requested = RepositionMatrix.Identity();
        requested[0] = 0d; requested[1] = 1d;
        requested[4] = 1d; requested[5] = 0d;
        requested[10] = -1d;

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(requested);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);

        AssertSameMatrix(Rotation(requested), Rotation(restored));
    }

    /// <summary>
    /// ТОТ ЖЕ ПОЛЮС, НО НА ПРОТИВОПОЛОЖНОЙ ВЕТВИ: 180° вокруг оси с ненулевой Z-компонентой.
    /// </summary>
    /// <remarks>
    /// Вторая половина пары в ОДНОЙ постановке: тест на одной оси прошёл бы и на разложении,
    /// которое верно только для плоскостей XY. Ось (1,1,1) задевает все три оси, поэтому подмена
    /// знака в любой из них здесь видна.
    /// </remarks>
    [Fact]
    public void AnglesFromRotation_ReproducesMatrixAtTheHalfTurnPole_TiltedAxis()
    {
        // Поворот 180° вокруг (1,1,1)/√3: R = 2·u·uᵀ − I.
        const double third = 2d / 3d;
        var requested = RepositionMatrix.Identity();
        requested[0] = -1d / 3d; requested[1] = third; requested[2] = third;
        requested[4] = third; requested[5] = -1d / 3d; requested[6] = third;
        requested[8] = third; requested[9] = third; requested[10] = -1d / 3d;

        var (precession, nutation, rotation) = EulerOrientation.AnglesFromRotation(requested);
        var restored = EulerOrientation.RotationFromAngles(precession, nutation, rotation);

        AssertSameMatrix(Rotation(requested), Rotation(restored));
    }

    /// <summary>Только блок поворота: сверка ориентации не должна зависеть от переноса.</summary>
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
