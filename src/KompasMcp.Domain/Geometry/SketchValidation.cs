using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>Argument validation for sketch primitives. Its only job is to guarantee that nothing reaches
/// COM with a value KOMPAS would either silently misinterpret or reject mid-operation, leaving a
/// half-applied profile behind (test G09: the failure must happen before any CAD call).</summary>
public static class SketchValidation
{
    /// <summary>Anything below this is a zero-length segment in engineering terms.</summary>
    public const double MinimumExtentMm = 1e-6;

    public const double MaximumExtentMm = 1e7;

    public static void Validate(SketchEntityDto entity)
    {
        RequireNoForeignFields(entity);
        switch (entity.Kind)
        {
            case SketchEntityKind.Line:
                RequirePoint(entity.StartMm, "start_mm");
                RequirePoint(entity.EndMm, "end_mm");
                // RequirePoint has already refused a missing or short point, but the compiler
                // cannot see that through the nullable property; the guard keeps Release strict.
                if (entity.StartMm is { } from && entity.EndMm is { } to)
                {
                    RequireDistance(from, to, "start_mm/end_mm");
                }

                break;

            case SketchEntityKind.Circle:
                RequirePoint(entity.CenterMm, "center_mm");
                RequirePositive(entity.RadiusMm, "radius_mm");
                break;

            case SketchEntityKind.Arc:
                RequirePoint(entity.CenterMm, "center_mm");
                RequirePositive(entity.RadiusMm, "radius_mm");
                // DOC: <c>ksdocument2d_ksarcbypoint.html</c> documents a second route — centre, radius,
                // start point, end point, direction. The two forms are MUTUALLY EXCLUSIVE: mixing them
                // would leave it undecided which geometry was asked for, and the adapter would have to
                // guess. Every refusal here happens before COM.
                var byAngles = entity.StartDeg is not null || entity.SweepDeg is not null;
                var byPoints = entity.StartPointMm is not null || entity.EndPointMm is not null;
                if (byAngles && byPoints)
                {
                    throw Bad("arc", "Дуга задаётся ЛИБО углами (start_deg/sweep_deg), ЛИБО концами "
                        + "(start_point_mm/end_point_mm/clockwise), не обоими сразу.");
                }

                if (byPoints)
                {
                    RequirePoint(entity.StartPointMm, "start_point_mm");
                    RequirePoint(entity.EndPointMm, "end_point_mm");
                    RequireDistance(entity.StartPointMm!, entity.EndPointMm!, "start_point_mm/end_point_mm");
                    RequireOnCircle(entity, entity.StartPointMm!, "start_point_mm");
                    RequireOnCircle(entity, entity.EndPointMm!, "end_point_mm");
                    if (entity.Clockwise is null)
                    {
                        throw Bad("clockwise", "Для дуги по концам направление обязательно: те же две точки "
                            + "называют две разные дуги, и умолчание нарисовало бы дополняющую.");
                    }

                    break;
                }

                RequireFinite(entity.StartDeg, "start_deg");
                RequireFinite(entity.SweepDeg, "sweep_deg");
                if (entity.Clockwise is not null)
                {
                    throw Bad("clockwise", "clockwise задаётся только у дуги по концам; при задании углами "
                        + "направление несёт знак sweep_deg.");
                }

                if (Math.Abs(entity.SweepDeg!.Value) < MinimumExtentMm)
                {
                    throw Bad("sweep_deg", "Нулевой дуге не соответствует ни один элемент.");
                }

                if (Math.Abs(entity.SweepDeg.Value) > 360d + 1e-9)
                {
                    throw Bad("sweep_deg", $"Дуга не может перекрыть {entity.SweepDeg.Value} градусов.");
                }

                break;

            case SketchEntityKind.Rectangle:
                if (entity.StartMm is null && entity.CenterMm is null)
                {
                    throw Bad("start_mm", "Нужна начальная точка (или center_mm) прямоугольника.");
                }

                RequirePositive(entity.WidthMm, "width_mm");
                RequirePositive(entity.HeightMm, "height_mm");
                break;

            case SketchEntityKind.Polyline:
                var points = entity.PointsMm ?? throw Bad("points_mm", "Полилина требует список точек.");
                if (points.Count < 2)
                {
                    throw Bad("points_mm", $"Нужно минимум 2 точки, получено {points.Count}.");
                }

                for (var i = 0; i < points.Count; i++)
                {
                    RequirePoint(points[i], $"points_mm[{i}]");
                }

                var segments = entity.Closed == true ? points.Count : points.Count - 1;
                for (var i = 0; i < segments; i++)
                {
                    RequireDistance(points[i], points[(i + 1) % points.Count], $"points_mm[{i}]→[{(i + 1) % points.Count}]");
                }

                break;

            case SketchEntityKind.Spline:
                // DOC: <c>ksdocument2d_ksbezier.html</c> — the sketch spline is a Bezier curve through
                // the given vertices (<c>ksBezier(closed, style)</c> plus one <c>ksBezierPoint</c> per
                // vertex). A curve through two points has no shape, so the minimum is three — and it is
                // stated as a number here, not left to the kernel's own refusal.
                var splinePoints = entity.PointsMm
                    ?? throw Bad("points_mm", "Сплайн требует список вершин.");
                if (splinePoints.Count < MinimumSplineVertices)
                {
                    throw Bad("points_mm", $"Сплайну нужно минимум {MinimumSplineVertices} вершин, "
                        + $"получено {splinePoints.Count}.");
                }

                for (var i = 0; i < splinePoints.Count; i++)
                {
                    RequirePoint(splinePoints[i], $"points_mm[{i}]");
                }

                break;

            default:
                throw Bad("kind", $"Примитив {entity.Kind} не поддерживается контрактом v1.");
        }
    }

    /// <summary>Vertices below which a spline has no shape. Three points are the smallest set that
    /// defines a curve segment pair; two would make the "curve" the segment between them.</summary>
    public const int MinimumSplineVertices = 3;

    /// <summary>The published <c>sketch_entity</c> object says "лишние поля отклоняются": a field that
    /// belongs to another kind is refused by NAME instead of being accepted and ignored, which would
    /// make an applied parameter indistinguishable from a dropped one.</summary>
    /// <remarks>INVARIANT: the allowed set is exactly what the drawing route APPLIES for that kind —
    /// e.g. a rectangle legitimately takes either <c>start_mm</c> or <c>center_mm</c> as its corner, so
    /// both are allowed and neither is a foreign field.
    /// History: docs/decisions/adapter-sketch.md#sketch-entity-foreign-fields</remarks>
    private static void RequireNoForeignFields(SketchEntityDto entity)
    {
        var (allowed, sent) = entity.Kind switch
        {
            SketchEntityKind.Line => (new[] { "kind", "start_mm", "end_mm" }, Sent(entity)),
            SketchEntityKind.Circle => (new[] { "kind", "center_mm", "radius_mm" }, Sent(entity)),
            SketchEntityKind.Arc => (
                new[]
                {
                    "kind", "center_mm", "radius_mm", "start_deg", "sweep_deg",
                    "start_point_mm", "end_point_mm", "clockwise",
                },
                Sent(entity)),
            SketchEntityKind.Rectangle => (
                new[] { "kind", "start_mm", "center_mm", "width_mm", "height_mm" }, Sent(entity)),
            SketchEntityKind.Polyline => (new[] { "kind", "points_mm", "closed" }, Sent(entity)),
            SketchEntityKind.Spline => (new[] { "kind", "points_mm", "closed" }, Sent(entity)),
            _ => (Array.Empty<string>(), Sent(entity)),
        };

        var foreign = sent.Where(f => !allowed.Contains(f, StringComparer.Ordinal)).ToArray();
        if (foreign.Length > 0)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Поля {string.Join(", ", foreign)} не принадлежат виду {KindName(entity.Kind)}: "
                + "принятое и проигнорированное поле выглядело бы как выполненная правка. КОМПАС не вызывался.",
                details: new Dictionary<string, object?>
                {
                    ["foreign_fields"] = foreign,
                    ["allowed_fields"] = allowed,
                    ["kind"] = KindName(entity.Kind),
                });
        }
    }

    private static IReadOnlyList<string> Sent(SketchEntityDto entity)
    {
        var fields = new List<string> { "kind" };
        if (entity.StartMm is not null)
        {
            fields.Add("start_mm");
        }

        if (entity.EndMm is not null)
        {
            fields.Add("end_mm");
        }

        if (entity.CenterMm is not null)
        {
            fields.Add("center_mm");
        }

        if (entity.RadiusMm is not null)
        {
            fields.Add("radius_mm");
        }

        if (entity.StartDeg is not null)
        {
            fields.Add("start_deg");
        }

        if (entity.SweepDeg is not null)
        {
            fields.Add("sweep_deg");
        }

        if (entity.StartPointMm is not null)
        {
            fields.Add("start_point_mm");
        }

        if (entity.EndPointMm is not null)
        {
            fields.Add("end_point_mm");
        }

        if (entity.Clockwise is not null)
        {
            fields.Add("clockwise");
        }

        if (entity.WidthMm is not null)
        {
            fields.Add("width_mm");
        }

        if (entity.HeightMm is not null)
        {
            fields.Add("height_mm");
        }

        if (entity.PointsMm is not null)
        {
            fields.Add("points_mm");
        }

        if (entity.Closed is not null)
        {
            fields.Add("closed");
        }

        return fields;
    }

    private static string KindName(SketchEntityKind kind) => kind.ToString().ToLowerInvariant();

    private static void RequirePoint(IReadOnlyList<double>? point, string field)
    {
        if (point is null)
        {
            throw Bad(field, "Точка обязательна.");
        }

        if (point.Count != 2)
        {
            throw Bad(field, $"Ожидается 2 координаты эскиза, получено {point.Count}.");
        }

        foreach (var value in point)
        {
            RequireFiniteValue(value, field);
        }
    }

    /// <summary>An arc given by its end points must have both of them ON the circle: the kernel draws the
    /// arc through them, so a point off the circle would silently change the radius.</summary>
    private static void RequireOnCircle(SketchEntityDto entity, IReadOnlyList<double> point, string field)
    {
        var center = entity.CenterMm!;
        var radius = entity.RadiusMm!.Value;
        var dx = point[0] - center[0];
        var dy = point[1] - center[1];
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var tolerance = Math.Max(MinimumExtentMm, radius * 1e-6);
        if (Math.Abs(distance - radius) > tolerance)
        {
            throw Bad(field, $"Точка лежит на расстоянии {distance:R} мм от центра, а радиус {radius:R} мм: "
                + $"допуск {tolerance:R} мм. Дуга по концам строится по окружности (center_mm, radius_mm).");
        }
    }

    private static void RequireDistance(IReadOnlyList<double> from, IReadOnlyList<double> to, string fields)
    {
        var dx = to[0] - from[0];
        var dy = to[1] - from[1];
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (!double.IsFinite(length))
        {
            throw Bad(fields, "Длина сегмента не представима конечным числом.");
        }

        if (length < MinimumExtentMm)
        {
            throw Bad(fields, "Сегмент нулевой длины: эскиз с ним не даст корректного профиля.");
        }

        if (length > MaximumExtentMm)
        {
            throw Bad(fields, $"Сегмент {length:R} мм превышает допустимый размер модели.");
        }
    }

    private static void RequirePositive(double? value, string field)
    {
        if (value is null)
        {
            throw Bad(field, "Значение обязательно.");
        }

        RequireFiniteValue(value.Value, field);
        if (value.Value < MinimumExtentMm)
        {
            throw Bad(field, $"Значение {value.Value:R} должно быть строго положительным.");
        }

        if (value.Value > MaximumExtentMm)
        {
            throw Bad(field, $"Значение {value.Value:R} превышает допустимый размер модели.");
        }
    }

    private static void RequireFinite(double? value, string field)
    {
        if (value is null)
        {
            throw Bad(field, "Значение обязательно.");
        }

        RequireFiniteValue(value.Value, field);
    }

    private static void RequireFiniteValue(double value, string field)
    {
        if (!double.IsFinite(value))
        {
            throw Bad(field, $"Значение {value} не является конечным числом (NaN или бесконечность).");
        }

        if (Math.Abs(value) > MaximumExtentMm)
        {
            throw Bad(field, $"Значение {value:R} превышает допустимый порядок {MaximumExtentMm:R} мм.");
        }
    }

    private static KompasContractException Bad(string field, string reason) => new(
        ErrorCodes.InvalidArgument,
        $"Эскиз не прошёл проверку ({field}): {reason}. КОМПАС не вызывался.",
        details: new Dictionary<string, object?> { ["field"] = field, ["reason"] = reason });
}
