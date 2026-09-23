namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Однородная матрица 4×4 в той раскладке, которой КОМПАС-3D v24 пишет положение тела.
/// </summary>
/// <remarks>
/// <para>
/// Раскладка измерена 18.09.2026 пробой <c>--reposition</c> (шаг RP.2, прогон
/// <c>929f08886f1348fe921943052a4026b0</c>): положение пишет <b>только</b> матрица из 16 чисел.
/// Маршруты из 12 чисел («оси, затем начало» и «начало, затем оси») и <c>SetDisplacementByAxis</c>
/// возвращают <c>Update() = true</c> и тело не двигают — то есть успешный <c>Update()</c> здесь не
/// является доказательством, и это отдельный факт о продукте, а не о приборе.
/// </para>
/// <para>
/// Строчно: 3×3, где <b>столбцы — образы базисных векторов</b>, затем строка переноса, последним
/// числом 1:
/// <code>
/// [ R00 R01 R02 0 ]
/// [ R10 R11 R12 0 ]
/// [ R20 R21 R22 0 ]
/// [ tx  ty  tz  1 ]
/// </code>
/// Правое правило подтверждено поворотом, а не переносом: на переносе раскладку строк и столбцов
/// различить нельзя, потому что единичный поворот симметричен.
/// </para>
/// <para>
/// <b>Массив хранит эти тройки подряд</b>, то есть три первых числа — образ оси X, следующие три —
/// образ оси Y, затем оси Z. Иначе говоря, 3×3 лежит <b>постолбцово</b>, и порядок индексов обратен
/// математической записи <c>R[i, j]</c>. Это не украшение: именно здесь 18.09.2026 жил дефект.
/// Перенос работал, а поворот отвергался как «тело осталось на прежнем месте» (`NO_GEOMETRY_CHANGE`),
/// потому что 3×3 уходила в КОМПАС транспонированной, и на симметричном единичном повороте это
/// невидимо. Отсюда правило: <b>у раскладки обязан быть различающий контроль — поворот</b>, а
/// согласие <see cref="Apply"/> с построением доказывает лишь то, что две ошибки совпали.
/// </para>
/// </remarks>
public static class RepositionMatrix
{
    /// <summary>Число элементов: 16, и никакое другое не двигает тело.</summary>
    public const int Size = 16;

    public static double[] Identity() => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        0d, 0d, 0d, 1d,
    };

    /// <summary>Чистый перенос: единичный поворот и вектор смещения в последней строке.</summary>
    public static double[] Translate(IReadOnlyList<double> vectorMm)
    {
        RequireTriple(vectorMm, nameof(vectorMm));
        return new[]
        {
            1d, 0d, 0d, 0d,
            0d, 1d, 0d, 0d,
            0d, 0d, 1d, 0d,
            vectorMm[0], vectorMm[1], vectorMm[2], 1d,
        };
    }

    /// <summary>
    /// Поворот на <paramref name="angleDeg"/> вокруг оси через <paramref name="axisPointMm"/> с
    /// направлением <paramref name="axisDirectionMm"/>. Перенос равен <c>c − R·c</c>, поэтому точка
    /// оси остаётся на месте.
    /// </summary>
    /// <remarks>
    /// Знак угла — правое правило вокруг направления оси. Измерено (RP.4): поворот бруска
    /// <c>[10,30]×[0,10]×[0,5]</c> на +90° вокруг Z через начало даёт <c>(−10,10,0)…(0,30,5)</c>,
    /// то есть <c>(x,y) → (−y,x)</c>.
    /// </remarks>
    public static double[] RotateAboutAxis(
        IReadOnlyList<double> axisPointMm,
        IReadOnlyList<double> axisDirectionMm,
        double angleDeg)
    {
        RequireTriple(axisPointMm, nameof(axisPointMm));
        RequireTriple(axisDirectionMm, nameof(axisDirectionMm));

        var length = RigidFrame.Norm(new[] { axisDirectionMm[0], axisDirectionMm[1], axisDirectionMm[2] });
        if (!double.IsFinite(length) || length <= 0d)
        {
            throw new ArgumentException(
                "Направление оси поворота обязано быть ненулевым и конечным.", nameof(axisDirectionMm));
        }

        var k = new[] { axisDirectionMm[0] / length, axisDirectionMm[1] / length, axisDirectionMm[2] / length };
        var angle = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var oneMinusCos = 1d - cos;

        // Формула Родрига: R = I·cosθ + sinθ·[k]× + (1−cosθ)·k⊗k.
        var r = new[,]
        {
            {
                (cos + (oneMinusCos * k[0] * k[0])),
                ((oneMinusCos * k[0] * k[1]) - (sin * k[2])),
                ((oneMinusCos * k[0] * k[2]) + (sin * k[1])),
            },
            {
                ((oneMinusCos * k[1] * k[0]) + (sin * k[2])),
                (cos + (oneMinusCos * k[1] * k[1])),
                ((oneMinusCos * k[1] * k[2]) - (sin * k[0])),
            },
            {
                ((oneMinusCos * k[2] * k[0]) - (sin * k[1])),
                ((oneMinusCos * k[2] * k[1]) + (sin * k[0])),
                (cos + (oneMinusCos * k[2] * k[2])),
            },
        };

        var c = new[] { axisPointMm[0], axisPointMm[1], axisPointMm[2] };
        var t = new[]
        {
            c[0] - ((r[0, 0] * c[0]) + (r[0, 1] * c[1]) + (r[0, 2] * c[2])),
            c[1] - ((r[1, 0] * c[0]) + (r[1, 1] * c[1]) + (r[1, 2] * c[2])),
            c[2] - ((r[2, 0] * c[0]) + (r[2, 1] * c[1]) + (r[2, 2] * c[2])),
        };

        return new[]
        {
            // ПОСТОЛБЦОВО, а не построчно: три числа подряд — это ОБРАЗ ОЧЕРЕДНОЙ БАЗИСНОЙ ОСИ
            // (R00,R10,R20 — образ X; R01,R11,R21 — образ Y; R02,R12,R22 — образ Z). Раскладка
            // измерена пробой RP.2/RP.4 и записана в заголовке класса; здесь она же, и порядок
            // индексов для этого перевёрнут относительно математической записи R[i,j].
            r[0, 0], r[1, 0], r[2, 0], 0d,
            r[0, 1], r[1, 1], r[2, 1], 0d,
            r[0, 2], r[1, 2], r[2, 2], 0d,
            t[0], t[1], t[2], 1d,
        };
    }

    /// <summary>
    /// Применить матрицу к точке — контроль, не зависящий от КОМПАСа: по нему проверяется, что
    /// построенная матрица действительно даёт посчитанный габарит.
    /// </summary>
    /// <remarks>
    /// Читает массив в ТОЙ ЖЕ раскладке, в которой его пишет <see cref="RotateAboutAxis"/>:
    /// образы осей лежат тройками подряд. Ошибка в согласии этих двух мест невидима на переносе
    /// (единичный поворот симметричен) и видна только на повороте — именно так дефект раскладки и
    /// дожил до приёмки, где поворот отвергался как «тело не сдвинулось».
    /// </remarks>
    public static double[] Apply(double[] matrix, double[] point)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(point);
        if (matrix.Length != Size)
        {
            throw new ArgumentException($"Матрица обязана содержать {Size} чисел, а содержит {matrix.Length}.", nameof(matrix));
        }

        RequireTriple(point, nameof(point));
        var x = point[0];
        var y = point[1];
        var z = point[2];
        return new[]
        {
            (matrix[0] * x) + (matrix[4] * y) + (matrix[8] * z) + matrix[12],
            (matrix[1] * x) + (matrix[5] * y) + (matrix[9] * z) + matrix[13],
            (matrix[2] * x) + (matrix[6] * y) + (matrix[10] * z) + matrix[14],
        };
    }

    private static void RequireTriple(IReadOnlyList<double>? value, string name)
    {
        if (value is not { Count: 3 })
        {
            throw new ArgumentException($"Ожидались три числа, а получено {value?.Count ?? 0}.", name);
        }

        foreach (var number in value)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Координаты обязаны быть конечными числами.", name);
            }
        }
    }
}

/// <summary>
/// Ортонормированный базис плоскости — то, чем нормаль превращается в три точки модели.
/// </summary>
/// <remarks>
/// Нужен потому, что измеренный маршрут создания плоскости в API7 (шаг SP.1) — «по трём точкам
/// модели»: <c>Planes3D.Add(o3d_plane3Points)</c> → <c>IPlane3DBy3Points</c>, где нормаль равна
/// <c>(P2−P1)×(P3−P1)</c>. Базис строится так, чтобы это произведение совпало с ЗАДАННОЙ нормалью,
/// а не с её случайным поворотом в плоскости.
/// </remarks>
public static class PlaneBasis
{
    /// <summary>
    /// Базис <c>(e1, e2)</c> в плоскости с нормалью <paramref name="normal"/>, такой что
    /// <c>e1 × e2 = n̂</c>. Нормаль нормализуется: знак <c>s = n·(p − p₀)</c> от длины не зависит,
    /// поэтому единичная нормаль — тот же ответ, но воспроизводимый.
    /// </summary>
    public static (double[] E1, double[] E2, double[] UnitNormal) FromNormal(IReadOnlyList<double> normal)
    {
        if (normal is not { Count: 3 })
        {
            throw new ArgumentException($"Нормаль обязана состоять из трёх чисел, а состоит из {normal?.Count ?? 0}.", nameof(normal));
        }

        foreach (var number in normal)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Нормаль обязана состоять из конечных чисел.", nameof(normal));
            }
        }

        var raw = new[] { normal[0], normal[1], normal[2] };
        var length = RigidFrame.Norm(raw);
        if (length <= 0d)
        {
            throw new ArgumentException("Нулевая нормаль плоскость не задаёт.", nameof(normal));
        }

        var n = new[] { raw[0] / length, raw[1] / length, raw[2] / length };

        // Опорный вектор берётся наименее параллельным нормали: у него наибольшая составляющая
        // перпендикулярна n, поэтому вырожденного векторного произведения не будет.
        var ax = Math.Abs(n[0]);
        var ay = Math.Abs(n[1]);
        var az = Math.Abs(n[2]);
        var seed = ax <= ay && ax <= az ? new[] { 1d, 0d, 0d }
            : ay <= az ? new[] { 0d, 1d, 0d }
            : new[] { 0d, 0d, 1d };

        var e1 = RigidFrame.Cross(n, seed);
        var e1Length = RigidFrame.Norm(e1);
        e1 = new[] { e1[0] / e1Length, e1[1] / e1Length, e1[2] / e1Length };
        var e2 = RigidFrame.Cross(n, e1);

        // Проверка внутри самого построения: e1 × e2 обязано равняться n̂, иначе плоскость
        // получит нормаль обратного знака, а знак здесь — это выбор стороны, а не мелочь.
        var check = RigidFrame.Cross(e1, e2);
        if (Math.Abs(check[0] - n[0]) > 1e-12 || Math.Abs(check[1] - n[1]) > 1e-12 || Math.Abs(check[2] - n[2]) > 1e-12)
        {
            throw new InvalidOperationException("Построенный базис не воспроизводит заданную нормаль.");
        }

        return (e1, e2, n);
    }

    /// <summary>
    /// Три точки модели для плоскости, проходящей через <paramref name="pointMm"/> с заданной
    /// нормалью. Порядок точек — тот, у которого измерено <c>(P2−P1)×(P3−P1) = n</c>.
    /// </summary>
    public static (double[] P1, double[] P2, double[] P3, double[] UnitNormal) ThreePoints(
        IReadOnlyList<double> pointMm, IReadOnlyList<double> normal)
    {
        if (pointMm is not { Count: 3 })
        {
            throw new ArgumentException($"Точка обязана состоять из трёх чисел, а состоит из {pointMm?.Count ?? 0}.", nameof(pointMm));
        }

        foreach (var number in pointMm)
        {
            if (!double.IsFinite(number))
            {
                throw new ArgumentException("Точка обязана состоять из конечных чисел.", nameof(pointMm));
            }
        }

        var (e1, e2, unit) = FromNormal(normal);
        var p1 = new[] { pointMm[0], pointMm[1], pointMm[2] };
        return (
            p1,
            new[] { p1[0] + e1[0], p1[1] + e1[1], p1[2] + e1[2] },
            new[] { p1[0] + e2[0], p1[1] + e2[1], p1[2] + e2[2] },
            unit);
    }
}
