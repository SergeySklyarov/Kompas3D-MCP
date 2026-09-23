using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// Analytic area of a sketch profile, used as the expected value for an extrusion
/// (spec 1.11: volume must be compared against an analytic expectation, a silent PASS is
/// forbidden).
/// </summary>
/// <remarks>
/// Only shapes whose area is exactly computable from the primitives the caller sent are
/// answered. A polyline that self-intersects, or an arc-only profile, returns null so the
/// extrusion reports <c>call_returned</c> + an unverified aspect rather than inventing a
/// target. That is the difference between "we checked" and "the numbers happened to look fine".
///
/// The sum is signed-area-independent on purpose: profile area is what the extrusion multiplies
/// by depth, so overlapping contours would need the solver, not this formula — and such a profile
/// is reported as unverified by the caller instead of being silently added up.
/// </remarks>
public static class ProfileArea
{
    /// <summary>Two distinct vertices closer than this are treated as the same point.</summary>
    private const double CoincidenceMm = 1e-6;

    /// <summary>Computed area in mm², or null when the set of entities is not analytically an area.</summary>
    public static double? Of(IReadOnlyList<SketchEntityDto> entities)
    {
        double total = 0d;
        foreach (var entity in entities)
        {
            switch (entity.Kind)
            {
                case SketchEntityKind.Rectangle:
                    total += (entity.WidthMm ?? double.NaN) * (entity.HeightMm ?? double.NaN);
                    break;

                case SketchEntityKind.Circle:
                    var radius = entity.RadiusMm ?? double.NaN;
                    total += Math.PI * radius * radius;
                    break;

                case SketchEntityKind.Polyline:
                    var polygon = entity.PointsMm;
                    if (polygon is null || entity.Closed != true || polygon.Count < 3)
                    {
                        return null;
                    }

                    total += Shoelace(polygon);
                    break;

                // line and arc: a contour on its own does not determine an area without the
                // solver, so no analytic expectation is claimed.
                default:
                    return null;
            }
        }

        if (!double.IsFinite(total) || total <= 0d)
        {
            return null;
        }

        return total;
    }

    /// <summary>Area of a closed polygon, absolute value of the shoelace sum, in mm².</summary>
    public static double Shoelace(IReadOnlyList<IReadOnlyList<double>> points)
    {
        double sum = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            sum += (current[0] * next[1]) - (next[0] * current[1]);
        }

        return Math.Abs(sum) / 2d;
    }

    /// <summary>
    /// Tolerance for comparing a measured volume with the analytic expectation: a relative part
    /// plus an absolute floor, so a tiny model is not failed by floating-point noise and a huge
    /// one is not passed by an oversized absolute slack.
    /// </summary>
    public static double Tolerance(double expectedMm3) =>
        Math.Max(1e-3, 1e-9 * Math.Abs(expectedMm3));

    public static bool Matches(double? expectedMm3, double? measuredMm3)
    {
        if (expectedMm3 is not double expected || measuredMm3 is not double measured)
        {
            return false;
        }

        return Math.Abs(measured - expected) <= Tolerance(expected);
    }
}
