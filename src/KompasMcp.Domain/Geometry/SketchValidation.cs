using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Argument validation for sketch primitives. Its only job is to guarantee that nothing reaches
/// COM with a value КОМПАС would either silently misinterpret or reject mid-operation, leaving a
/// half-applied profile behind (test G09: the failure must happen before any CAD call).
/// </summary>
public static class SketchValidation
{
    /// <summary>Anything below this is a zero-length segment in engineering terms.</summary>
    public const double MinimumExtentMm = 1e-6;

    public const double MaximumExtentMm = 1e7;

    public static void Validate(SketchEntityDto entity)
    {
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
                RequireFinite(entity.StartDeg, "start_deg");
                RequireFinite(entity.SweepDeg, "sweep_deg");
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

            default:
                throw Bad("kind", $"Примитив {entity.Kind} не поддерживается контрактом v1.");
        }
    }

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
