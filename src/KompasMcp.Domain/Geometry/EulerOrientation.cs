namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Ориентация размещения в документированном режиме углов Эйлера КОМПАС-3D v24
/// (<c>ILocalCoordinateSystem.OrientationType = ksEulerCorners</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем отдельный тип.</b> Положение тела продукт писал матрицей
/// (<c>ILocalCoordinateSystem.InitByMatrix3D</c>), и это измеренно не переживало переоткрытие:
/// документ хранит ПАРАМЕТРЫ ориентации, а не матрицу. Проба <c>--reposition-params</c>, прогон
/// <c>a336120926fc4652a8bf737562568271</c>, шаг <c>RP.25</c> измерил документированный
/// параметрический маршрут целиком: тройка углов <c>RotationAngle</c>, <c>NutationAngle</c>,
/// <c>PrecessionAngle</c> переоткрытие ПЕРЕЖИВАЕТ, а переносная часть читается через
/// <c>ParameterType = ksPDisplace</c> + <c>IPoint3DParamDisplace.DX/DY/DZ</c>. Поэтому матрица
/// раскладывается на ориентацию и перенос, ориентация выражается углами, а перенос — смещением.
/// </para>
/// <para>
/// <b>Порядок композиции ИЗМЕРЕН, а не подобран.</b> Справка задаёт его РИСУНКОМ
/// (<c>rotation_pict.html</c> → <c>images/_praezession.jpg</c>: вращение R — вокруг собственной оси
/// тела, прецессия P — вокруг вертикали, нутация N — наклон), а не текстом. Поэтому шаг <c>RP.25</c>
/// поставил каждый угол ОДИН (90°, остальные нули), вычислил ось каждого фактора из ИЗМЕРЕННОЙ
/// матрицы и сравнил три составные постановки со всеми шестью произведениями этих матриц. Совпало
/// РОВНО ОДНО произведение — <c>PNR</c>, и оно же даёт ноль расхождения на всех трёх постановках
/// (<c>order_PNR_max_diff = 0</c>; у остальных пяти — 1). Отсюда:
/// <code>M = Rz(прецессия) · Rx(нутация) · Rz(вращение)</code>
/// — классические углы Эйлера <c>z-x-z</c>. Оси факторов измерены: P → <c>(0,0,1)</c>,
/// N → <c>(1,0,0)</c>, R → <c>(0,0,1)</c>.
/// </para>
/// <para>
/// <b>Единицы — ГРАДУСЫ, и это измерено, а не предположено:</b> угол 30 дал поворот на 30°
/// (<c>angle_deg_for_30 = 29.99999999999998</c>).
/// </para>
/// <para>
/// <b>Раскладка матрицы — ТА ЖЕ, что у <see cref="RepositionMatrix"/>:</b> 16 чисел, три подряд —
/// образ очередной базисной оси (постолбцово), перенос в 12…14. Отдельной раскладки здесь не
/// заводится намеренно: вторая раскладка — это второй повод перепутать строки со столбцами, а
/// такой дефект невидим на переносе и виден только на повороте.
/// </para>
/// <para>
/// <b>Параметризация углами неоднозначна</b> (при нутации 0 или 180° сумма прецессии и вращения
/// определена с точностью до перераспределения), поэтому годность преобразования доказывается
/// ЭКВИВАЛЕНТНОСТЬЮ МАТРИЦ, а не совпадением чисел: <see cref="AnglesFromRotation"/> обязан
/// воспроизводить исходную матрицу при обратной сборке. Это и проверяется модульными тестами.
/// </para>
/// </remarks>
public static class EulerOrientation
{
    /// <summary>Допуск сравнения матриц: величины безразмерные, числа порядка единицы.</summary>
    public const double MatrixTolerance = 1e-9;

    /// <summary>Допуск, при котором нутация считается вырожденной (полюс параметризации).</summary>
    private const double DegenerateNutation = 1e-9;

    /// <summary>Матрица поворота из трёх углов Эйлера в ИЗМЕРЕННОМ порядке <c>PNR</c>.</summary>
    public static double[] RotationFromAngles(
        double precessionDeg, double nutationDeg, double rotationDeg)
    {
        var p = precessionDeg * Math.PI / 180d;
        var n = nutationDeg * Math.PI / 180d;
        var r = rotationDeg * Math.PI / 180d;
        var cp = Math.Cos(p);
        var sp = Math.Sin(p);
        var cn = Math.Cos(n);
        var sn = Math.Sin(n);
        var cr = Math.Cos(r);
        var sr = Math.Sin(r);

        // M = Rz(P)·Rx(N)·Rz(R), записано построчно:
        //   [ cp·cr − sp·cn·sr   −cp·sr − sp·cn·cr    sp·sn ]
        //   [ sp·cr + cp·cn·sr   −sp·sr + cp·cn·cr   −cp·sn ]
        //   [ sn·sr               sn·cr               cn   ]
        return FromRows(
            (cp * cr) - (sp * cn * sr), (-cp * sr) - (sp * cn * cr), sp * sn,
            (sp * cr) + (cp * cn * sr), (-sp * sr) + (cp * cn * cr), -cp * sn,
            sn * sr, sn * cr, cn);
    }

    /// <summary>
    /// Три угла Эйлера из матрицы поворота — обращение <see cref="RotationFromAngles"/>.
    /// </summary>
    /// <remarks>
    /// На полюсе (<c>sn = 0</c>) прецессия и вращение по отдельности не определены; тогда
    /// прецессия берётся нулевой, а вращение несёт всю сумму. Это НЕ потеря: матрица, собранная из
    /// возвращённой тройки, равна исходной, и именно так равенство и проверяется.
    /// </remarks>
    public static (double PrecessionDeg, double NutationDeg, double RotationDeg) AnglesFromRotation(
        IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var m00 = Cell(matrix, 0, 0);
        var m01 = Cell(matrix, 0, 1);
        var m02 = Cell(matrix, 0, 2);
        var m10 = Cell(matrix, 1, 0);
        var m12 = Cell(matrix, 1, 2);
        var m20 = Cell(matrix, 2, 0);
        var m21 = Cell(matrix, 2, 1);
        var m22 = Cell(matrix, 2, 2);

        var nutation = Math.Acos(Math.Clamp(m22, -1d, 1d));
        var sin = Math.Sin(nutation);
        double precession;
        double rotation;
        if (Math.Abs(sin) > DegenerateNutation)
        {
            precession = Math.Atan2(m02, -m12);
            rotation = Math.Atan2(m20, m21);
        }
        else
        {
            // Полюс: nutation = 0 либо 180. Прецессия берётся нулевой, и тогда вращение несёт всю
            // разность, но ЗНАК ЭТОЙ РАЗНОСТИ У ДВУХ ПОЛЮСОВ РАЗНЫЙ — и это не косметика, а
            // измеренный дефект: при nutation = 180 знак был взят неверно, и сборка матрицы из
            // возвращённой тройки давала [[0,−1,0],[−1,0,0],[0,0,−1]] вместо [[0,1,0],[1,0,0],[0,0,−1]]
            // — расхождение 2 в двух клетках. Поймано строкой приёмки B3.59 (поворот 180° вокруг
            // оси (1,1,0)): продукт отказал GEOMETRY_FAILED, потому что собственная проверка
            // RequirePlacementRoundTrip не смогла воспроизвести записанное размещение.
            //
            // ВЫВОД ЗНАКА, чтобы это не пришлось перевыводить заново. При nutation = 180 (cb = −1,
            // sb = 0) элементы матрицы равны m00 = cos(a − c), m01 = sin(a − c), m10 = sin(a − c),
            // m11 = −cos(a − c). При a := 0 получается cos c = m00 и sin c = −m01, то есть
            // c = atan2(−m01, m00). Прежняя редакция брала atan2(m01, m00) — формулу, годную для
            // ПРОТИВОПОЛОЖНОГО соглашения (c := 0), и смешивала два соглашения в одной ветке.
            // При nutation = 0 знак остаётся прежним: m00 = cos c, m10 = sin c, c = atan2(m10, m00).
            precession = 0d;
            rotation = m22 > 0d ? Math.Atan2(m10, m00) : Math.Atan2(-m01, m00);
        }

        return (Degrees(precession), Degrees(nutation), Degrees(rotation));
    }

    /// <summary>
    /// Ось и угол поворота из матрицы. Ось — собственный вектор с собственным значением 1; при
    /// 180° кососимметричная часть нулевая, и ось берётся из <c>R + I</c>.
    /// </summary>
    public static (double[] Axis, double AngleDeg) AxisAngleFromRotation(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var trace = Cell(matrix, 0, 0) + Cell(matrix, 1, 1) + Cell(matrix, 2, 2);
        var angle = Math.Acos(Math.Clamp((trace - 1d) / 2d, -1d, 1d));

        var axis = new[]
        {
            Cell(matrix, 2, 1) - Cell(matrix, 1, 2),
            Cell(matrix, 0, 2) - Cell(matrix, 2, 0),
            Cell(matrix, 1, 0) - Cell(matrix, 0, 1),
        };
        var norm = Norm(axis);
        if (norm > 1e-9)
        {
            return (Scale(axis, 1d / norm), Degrees(angle));
        }

        if (angle < 1e-9)
        {
            // Поворота нет: ось не определена, и выдумывать её нельзя.
            return (Array.Empty<double>(), 0d);
        }

        // 180°: R + I симметрична, её столбцы параллельны оси; берётся наибольший по норме.
        var candidates = new[]
        {
            new[] { Cell(matrix, 0, 0) + 1d, Cell(matrix, 1, 0), Cell(matrix, 2, 0) },
            new[] { Cell(matrix, 0, 1), Cell(matrix, 1, 1) + 1d, Cell(matrix, 2, 1) },
            new[] { Cell(matrix, 0, 2), Cell(matrix, 1, 2), Cell(matrix, 2, 2) + 1d },
        };
        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (Norm(candidate) > Norm(best))
            {
                best = candidate;
            }
        }

        var bestNorm = Norm(best);
        return bestNorm > 1e-9
            ? (Scale(best, 1d / bestNorm), Degrees(angle))
            : (Array.Empty<double>(), Degrees(angle));
    }

    /// <summary>
    /// Точка на оси поворота, ВЫВЕДЕННАЯ из размещения: <c>c = ((1−cos θ)·t + sin θ·(d × t)) / (2(1−cos θ))</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Точка оси не является свойством размещения: поворот вокруг любой точки ОДНОЙ прямой даёт то
    /// же самое размещение. Поэтому из пары «ориентация + перенос» восстанавливается не «та самая»
    /// точка, а представитель прямой — и это записано здесь прямо, чтобы вызывающий не принимал его
    /// за прочитанное значение.
    /// </para>
    /// <para>
    /// Вывод формулы. Для поворота на угол θ вокруг единичного направления <c>d</c> точка <c>c</c>
    /// переходит в <c>c·cos θ + (d × c)·sin θ + d·(d·c)·(1−cos θ)</c>, поэтому перенос равен
    /// <c>t = (1−cos θ)·u − sin θ·(d × u)</c>, где <c>u = c − (c·d)·d</c> — составляющая,
    /// перпендикулярная оси. Умножив векторно на <c>d</c>, получаем <c>d × t = sin θ·u + (1−cos θ)·(d × u)</c>;
    /// решение этой пары даёт <c>u</c>, откуда и берётся <c>c = u</c> (представитель с нулевой
    /// составляющей вдоль оси). При θ = 0 перенос точку оси не определяет — возвращается
    /// <c>null</c>, а не ноль.
    /// </para>
    /// </remarks>
    public static double[]? AxisPointFromPlacement(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        var (axis, angleDeg) = AxisAngleFromRotation(matrix);
        if (axis.Length != 3 || angleDeg <= 1e-9)
        {
            return null;
        }

        var angle = angleDeg * Math.PI / 180d;
        var oneMinusCos = 1d - Math.Cos(angle);
        var sin = Math.Sin(angle);
        var denominator = 2d * oneMinusCos;
        if (Math.Abs(denominator) < 1e-12)
        {
            return null;
        }

        var t = new[] { matrix[12], matrix[13], matrix[14] };
        var cross = Cross(axis, t);
        return new[]
        {
            ((oneMinusCos * t[0]) + (sin * cross[0])) / denominator,
            ((oneMinusCos * t[1]) + (sin * cross[1])) / denominator,
            ((oneMinusCos * t[2]) + (sin * cross[2])) / denominator,
        };
    }

    /// <summary>Переносная часть размещения — те же 12…14, что читает <see cref="RepositionMatrix.Apply"/>.</summary>
    public static double[] TranslationOf(IReadOnlyList<double> matrix)
    {
        Require(matrix, nameof(matrix));
        return new[] { matrix[12], matrix[13], matrix[14] };
    }

    /// <summary>Единичный поворот ли это: тогда вид преобразования — перенос, а не поворот.</summary>
    public static bool IsIdentity(IReadOnlyList<double> matrix, double tolerance = MatrixTolerance)
    {
        Require(matrix, nameof(matrix));
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var expected = row == column ? 1d : 0d;
                if (Math.Abs(Cell(matrix, row, column) - expected) > tolerance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Наибольшее расхождение двух матриц — то, чем доказывается эквивалентность.</summary>
    public static double MaxDifference(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        Require(left, nameof(left));
        Require(right, nameof(right));
        var worst = 0d;
        for (var i = 0; i < RepositionMatrix.Size; i++)
        {
            worst = Math.Max(worst, Math.Abs(left[i] - right[i]));
        }

        return worst;
    }

    /// <summary>Единичное направление оси либо исключение: нулевое направление ось не задаёт.</summary>
    public static double[] UnitAxis(IReadOnlyList<double> direction)
    {
        if (direction is not { Count: 3 })
        {
            throw new ArgumentException(
                $"Направление оси обязано состоять из трёх чисел, а состоит из {direction?.Count ?? 0}.",
                nameof(direction));
        }

        var raw = new[] { direction[0], direction[1], direction[2] };
        var norm = Norm(raw);
        if (!double.IsFinite(norm) || norm <= 0d)
        {
            throw new ArgumentException("Нулевое направление ось не задаёт.", nameof(direction));
        }

        return Scale(raw, 1d / norm);
    }

    private static double Cell(IReadOnlyList<double> matrix, int row, int column) =>
        matrix[(column * 4) + row];

    /// <summary>Построчная запись 3×3 в раскладку <see cref="RepositionMatrix"/> (постолбцово).</summary>
    private static double[] FromRows(
        double r00, double r01, double r02,
        double r10, double r11, double r12,
        double r20, double r21, double r22) => new[]
    {
        r00, r10, r20, 0d,
        r01, r11, r21, 0d,
        r02, r12, r22, 0d,
        0d, 0d, 0d, 1d,
    };

    private static void Require(IReadOnlyList<double>? matrix, string name)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Count != RepositionMatrix.Size)
        {
            throw new ArgumentException(
                $"Матрица обязана содержать {RepositionMatrix.Size} чисел, а содержит {matrix.Count}.",
                name);
        }
    }

    private static double Degrees(double radians) => radians * 180d / Math.PI;

    private static double Norm(double[] value) =>
        Math.Sqrt((value[0] * value[0]) + (value[1] * value[1]) + (value[2] * value[2]));

    private static double[] Scale(double[] value, double factor) =>
        new[] { value[0] * factor, value[1] * factor, value[2] * factor };

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    };
}
