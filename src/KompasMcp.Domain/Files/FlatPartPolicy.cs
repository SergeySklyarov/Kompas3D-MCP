using KompasMcp.Domain.Geometry;

namespace KompasMcp.Domain.Files;

/// <summary>One face as the plane check reads it: area, surface type, normal and a point of the surface.</summary>
/// <remarks>Every field is what an existing read of a face already gives (area, <c>GetSurface().GetNormal</c>
/// at the middle parameters, <c>GetPoint</c> at the same parameters); the check adds no new COM member.
/// A <c>null</c> normal or point means «not read» and is never treated as a zero vector.</remarks>
public sealed record FlatPartFace(
    string FaceRef,
    string SurfaceType,
    double AreaMm2,
    double[]? Normal,
    double[]? PointMm,
    double[]? AxisMm = null);

/// <summary>What the plane check concluded about a body.</summary>
public enum FlatPartVerdict
{
    /// <summary>Two parallel flat faces carry the body and everything else is perpendicular to them.</summary>
    Flat = 0,

    /// <summary>A step, a pocket or a draft: the body is not of one constant thickness.</summary>
    NotConstantThickness = 1,

    /// <summary>The faces do not name one pair: a cube has three, a bare cylinder has none.</summary>
    Ambiguous = 2,
}

/// <summary>The plane and thickness of a flat part, or the reason it has none.</summary>
public sealed record FlatPartPlan(
    FlatPartVerdict Verdict,
    double[]? Normal,
    double? ThicknessMm,
    IReadOnlyList<string> OffendingFaceRefs,
    IReadOnlyList<string> CandidateFaceRefs,
    string Reason);

/// <summary>Decides whether a body is a flat part of constant thickness, from its faces alone.</summary>
/// <remarks>INVARIANT: the two largest parallel flat faces are the flat sides; every other face must be
/// perpendicular to their normal (a wall) or lie in one of their two planes. A third parallel plane is a
/// step or a pocket floor, an inclined face is a draft — both refuse the export, because the projection of
/// such a body is not one closed contour.
/// MEASURED: a step shows up as a third parallel plane, not as a bigger area.
/// LIMIT: a cone, a sphere and a torus are refused even when they are a countersink; upgrade on demand.
/// History: docs/decisions/flat-part.md#plane-check</remarks>
public static class FlatPartPolicy
{
    /// <summary>Allowed angle between two normals that must be parallel or anti-parallel, in degrees.</summary>
    public const double NormalToleranceDeg = 0.5d;

    /// <summary>Allowed distance between a face's plane and the flat side's plane, in millimetres.</summary>
    public const double PlaneToleranceMm = 0.01d;

    /// <summary>Two candidate pairs closer in area than this factor are equally plausible.</summary>
    public const double PairAreaShareRatio = 0.98d;

    /// <summary>Read the body's faces and name its flat pattern, or why it has none.</summary>
    public static FlatPartPlan Analyze(IReadOnlyList<FlatPartFace> faces)
    {
        var planar = faces
            .Where(face => string.Equals(face.SurfaceType, "plane", StringComparison.OrdinalIgnoreCase))
            .Where(face => face.Normal is { Length: 3 } && face.PointMm is { Length: 3 })
            .Where(face => double.IsFinite(face.AreaMm2))
            .ToArray();

        var pairs = CandidatePairs(planar);
        if (pairs.Count == 0)
        {
            return new FlatPartPlan(
                FlatPartVerdict.Ambiguous,
                null,
                null,
                Array.Empty<string>(),
                planar.Select(face => face.FaceRef).ToArray(),
                "Плоских граней с прочитанной нормалью меньше двух, параллельной пары нет: нормаль "
                + "плоской детали не определена.");
        }

        var best = pairs[0];
        var rivals = pairs.Skip(1).Where(pair => pair.MinArea >= best.MinArea * PairAreaShareRatio).ToArray();
        if (rivals.Length > 0)
        {
            return new FlatPartPlan(
                FlatPartVerdict.Ambiguous,
                null,
                null,
                Array.Empty<string>(),
                pairs.SelectMany(pair => new[] { pair.First.FaceRef, pair.Second.FaceRef }).ToArray(),
                $"Равноправных пар граней {rivals.Length + 1} с площадями {best.MinArea:0.###} и "
                + $"{rivals[0].MinArea:0.###} мм2: какая пара задаёт плоскость детали, не решается.");
        }

        var normal = Unit(best.First.Normal!);
        var offsetFirst = RigidFrame.Dot(normal, best.First.PointMm!);
        var offsetSecond = RigidFrame.Dot(normal, best.Second.PointMm!);
        var thickness = Math.Abs(offsetFirst - offsetSecond);

        var offending = new List<string>();
        var deviations = new List<string>();
        foreach (var face in faces)
        {
            if (face.FaceRef == best.First.FaceRef || face.FaceRef == best.Second.FaceRef)
            {
                continue;
            }

            var (ok, deviation) = Conforms(face, normal, offsetFirst, offsetSecond);
            if (!ok)
            {
                offending.Add(face.FaceRef);
                deviations.Add($"{face.FaceRef}: {deviation}");
            }
        }

        if (offending.Count > 0)
        {
            return new FlatPartPlan(
                FlatPartVerdict.NotConstantThickness,
                normal,
                thickness,
                offending,
                new[] { best.First.FaceRef, best.Second.FaceRef },
                "Грани не перпендикулярны нормали плоской пары и не лежат в её плоскостях: "
                + string.Join("; ", deviations.Take(5)) + ".");
        }

        return new FlatPartPlan(
            FlatPartVerdict.Flat,
            normal,
            thickness,
            Array.Empty<string>(),
            new[] { best.First.FaceRef, best.Second.FaceRef },
            $"Плоская пара {best.First.FaceRef}/{best.Second.FaceRef}, толщина "
            + $"{thickness:0.###} мм, прочие грани перпендикулярны нормали или лежат в её плоскостях.");
    }

    /// <summary>Pairs of parallel flat faces, the one with the larger smaller area first.</summary>
    private static List<(FlatPartFace First, FlatPartFace Second, double MinArea)> CandidatePairs(
        IReadOnlyList<FlatPartFace> planar)
    {
        var pairs = new List<(FlatPartFace First, FlatPartFace Second, double MinArea)>();
        for (var i = 0; i < planar.Count; i++)
        {
            for (var j = i + 1; j < planar.Count; j++)
            {
                if (!IsParallel(planar[i].Normal!, planar[j].Normal!))
                {
                    continue;
                }

                pairs.Add((planar[i], planar[j], Math.Min(planar[i].AreaMm2, planar[j].AreaMm2)));
            }
        }

        return pairs
            .OrderByDescending(pair => pair.MinArea)
            .ThenByDescending(pair => pair.First.AreaMm2 + pair.Second.AreaMm2)
            .ToList();
    }

    /// <summary>Whether one face can belong to a body of constant thickness across the flat pair.</summary>
    /// <remarks>INVARIANT: a wall is either flat and perpendicular, or cylindrical/conical with its axis
    /// along the normal — a hole or a rounded corner. Anything else is a step, a pocket or a draft.
    /// </remarks>
    private static (bool Ok, string Deviation) Conforms(
        FlatPartFace face,
        double[] normal,
        double offsetFirst,
        double offsetSecond)
    {
        if (string.Equals(face.SurfaceType, "plane", StringComparison.OrdinalIgnoreCase))
        {
            if (face.Normal is not { Length: 3 } || face.PointMm is not { Length: 3 })
            {
                return (false, "нормаль или точка плоскости не прочитаны");
            }

            var angle = AngleDeg(face.Normal, normal);
            if (angle <= NormalToleranceDeg || Math.Abs(angle - 180d) <= NormalToleranceDeg)
            {
                var offset = RigidFrame.Dot(normal, face.PointMm);
                var distance = Math.Min(
                    Math.Abs(offset - offsetFirst), Math.Abs(offset - offsetSecond));
                return distance <= PlaneToleranceMm
                    ? (true, string.Empty)
                    : (false, $"параллельна плоскости детали, но смещена на {distance:0.###} мм");
            }

            var fromPerpendicular = Math.Abs(angle - 90d);
            return fromPerpendicular <= NormalToleranceDeg
                ? (true, string.Empty)
                : (false, $"наклонена к нормали на {angle:0.###}° вместо 90°");
        }

        if (face.SurfaceType is "cylinder" or "cone")
        {
            if (face.AxisMm is not { Length: 3 })
            {
                return (false, $"ось {face.SurfaceType} не прочитана");
            }

            var angle = AngleDeg(face.AxisMm, normal);
            var deviation = Math.Min(angle, Math.Abs(angle - 180d));
            return deviation <= NormalToleranceDeg
                ? (true, string.Empty)
                : (false, $"ось {face.SurfaceType} отклонена от нормали на {deviation:0.###}°");
        }

        return (false, $"грань типа {face.SurfaceType} не является ни плоскостью, ни стенкой отверстия");
    }

    private static bool IsParallel(double[] first, double[] second)
    {
        var angle = AngleDeg(first, second);
        return angle <= NormalToleranceDeg || Math.Abs(angle - 180d) <= NormalToleranceDeg;
    }

    /// <summary>Angle between two vectors in degrees, or 0 when one of them has no length.</summary>
    private static double AngleDeg(double[] first, double[] second)
    {
        var a = Unit(first);
        var b = Unit(second);
        if (RigidFrame.Norm(a) <= double.Epsilon || RigidFrame.Norm(b) <= double.Epsilon)
        {
            return 0d;
        }

        return Math.Acos(Math.Clamp(RigidFrame.Dot(a, b), -1d, 1d)) * (180d / Math.PI);
    }

    private static double[] Unit(double[] vector)
    {
        var norm = RigidFrame.Norm(vector);
        return norm <= double.Epsilon
            ? new[] { 0d, 0d, 0d }
            : new[] { vector[0] / norm, vector[1] / norm, vector[2] / norm };
    }
}
