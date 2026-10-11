namespace KompasMcp.Domain.Geometry;

/// <summary>The kind of sketch object a relation rule reads, in the vocabulary of the entity list.</summary>
public enum SketchShapeKind
{
    Point,
    Segment,
    Circle,
    Arc,
}

/// <summary>Geometry of ONE sketch object as a relation rule sees it, in sketch-local millimetres. A
/// member that was not read is <c>null</c> — never zero, because zero is a legitimate coordinate.</summary>
/// <remarks>INVARIANT: the fields carry only what the object's own interface publishes
/// (<c>ILineSegment.X1/Y1/X2/Y2</c>, <c>ICircle.Xc/Yc/Radius</c>, <c>IArc.Xc/Yc/Radius/X1/Y1/X2/Y2/X3/Y3</c>,
/// <c>IPoint.X/Y</c>). A value that could not be read leaves the relation UNVERIFIABLE rather than
/// guessed. History: docs/decisions/geometry.md#constraint-geometry</remarks>
public sealed record SketchShape(
    SketchShapeKind Kind,
    double? X = null,
    double? Y = null,
    double? X1 = null,
    double? Y1 = null,
    double? X2 = null,
    double? Y2 = null,
    double? CenterX = null,
    double? CenterY = null,
    double? Radius = null,
    double? MidX = null,
    double? MidY = null)
{
    /// <summary>The point an INDEX addresses on this object, or <c>null</c> when the index has no
    /// meaning for the kind. DOC: <c>iparametriticconstraint_index.html</c> — «Индекс точки на объекте
    /// (начинается с 0, у дуги и окружности 0-центр)».</summary>
    public (double X, double Y)? PointAt(int? index)
    {
        var i = index ?? 0;
        return Kind switch
        {
            SketchShapeKind.Point when i == 0 && X is { } px && Y is { } py => (px, py),
            SketchShapeKind.Segment when i == 0 && X1 is { } ax && Y1 is { } ay => (ax, ay),
            SketchShapeKind.Segment when i == 1 && X2 is { } bx && Y2 is { } by => (bx, by),
            SketchShapeKind.Circle or SketchShapeKind.Arc when i == 0 && CenterX is { } cx && CenterY is { } cy
                => (cx, cy),
            _ => null,
        };
    }

    public bool IsCurve => Kind is SketchShapeKind.Segment or SketchShapeKind.Circle or SketchShapeKind.Arc;

    public bool IsCircular => Kind is SketchShapeKind.Circle or SketchShapeKind.Arc;
}

/// <summary>How a relation rule ended: the relation HOLDS, it is measurable but does NOT hold, or it
/// cannot be expressed as a number at all.</summary>
public enum ConstraintRelationOutcome
{
    Satisfied,
    NotSatisfied,
    NotExpressible,
}

/// <summary>The verdict of ONE relation check, with the numbers the refusal must carry.</summary>
public sealed record ConstraintRelationVerdict(
    ConstraintRelationOutcome Outcome,
    string Relation,
    double? Measured,
    double? Expected,
    double Tolerance,
    string? Reason = null)
{
    public bool Holds => Outcome == ConstraintRelationOutcome.Satisfied;

    /// <summary>Whether the relation was reducible to a checked number. A type that is not reducible is
    /// NAMED, never silently reported as satisfied.</summary>
    public bool Expresses => Outcome != ConstraintRelationOutcome.NotExpressible;

    public static ConstraintRelationVerdict Satisfied(string relation, double measured, double expected,
        double tolerance) => new(ConstraintRelationOutcome.Satisfied, relation, measured, expected, tolerance);

    public static ConstraintRelationVerdict NotSatisfied(string relation, double measured, double expected,
        double tolerance) => new(ConstraintRelationOutcome.NotSatisfied, relation, measured, expected, tolerance);

    public static ConstraintRelationVerdict NotExpressible(string relation, string reason) =>
        new(ConstraintRelationOutcome.NotExpressible, relation, null, null, 0d, reason);
}

/// <summary>The PURE part of <c>kompas_create_sketch_constraint</c>: whether the geometry re-read after a
/// constraint was written actually obeys the relation that constraint must establish.</summary>
/// <remarks>WHY. «Успех — только подтверждённый перечитыванием»: a record in the object's list proves the
/// constraint EXISTS, not that it BINDS the geometry. MEASURED: the kernel moves geometry when it accepts
/// a geometric constraint, so the relation is checkable on the re-read geometry; a type whose relation is
/// not a number is named in the answer's unverified aspects rather than passed off as verified.
/// LIMIT: the rules read the geometry AFTER the write only — they do not re-run the edit that would prove
/// a temporal property (<c>ksCFixedPoint</c> holds a point across a LATER edit of a neighbour), so those
/// types are NotExpressible here and are proved by the acceptance rows instead.
/// History: docs/decisions/geometry.md#constraint-geometry</remarks>
public static class SketchConstraintGeometryRule
{
    /// <summary>How far a length-like relation may miss, mm. The project's geometry delta class
    /// (<c>tolerance_classes.geometry_delta_mm</c>): measured constraint deltas are 6.25 and 30, orders
    /// above this, so the threshold separates "the kernel moved it" from "it did not".</summary>
    public const double LengthToleranceMm = 1e-6;

    /// <summary>How far an angle-like relation may miss, degrees. Same order as the length class, and far
    /// below the residual the kernel leaves behind when it settles an angle on its target.</summary>
    public const double AngleToleranceDeg = 1e-6;

    private const double DegreesPerRadian = 180d / Math.PI;

    /// <summary>Checks the relation the type must establish, from the geometry of the named objects.</summary>
    public static ConstraintRelationVerdict Evaluate(
        string constraintType,
        SketchShape? target,
        int? targetIndex,
        SketchShape? partner,
        int? partnerIndex,
        SketchShape? axis)
    {
        switch (constraintType)
        {
            case "ksCHorizontal":
                return DirectionDeviation(target, horizontal: true, "горизонталь");
            case "ksCVertical":
                return DirectionDeviation(target, horizontal: false, "вертикаль");
            case "ksCParallel":
                return TwoSegmentAngles(target, partner, expectedDeg: 0d, "параллельность");
            case "ksCPerpendicular":
                return TwoSegmentAngles(target, partner, expectedDeg: 90d, "перпендикулярность");
            case "ksCCollinear":
                return Collinear(target, partner);
            case "ksCEqualLength":
                return EqualLength(target, partner);
            case "ksCEqualRadius":
                return EqualRadius(target, partner);
            case "ksCHAlignPoints":
                return AlignedPoints(target, targetIndex, partner, partnerIndex, byY: true, "выравнивание по горизонтали");
            case "ksCVAlignPoints":
                return AlignedPoints(target, targetIndex, partner, partnerIndex, byY: false, "выравнивание по вертикали");
            case "ksCMergePoints":
                return MergePoints(target, targetIndex, partner, partnerIndex);
            case "ksCConcentricity":
                return Concentricity(target, partner);
            case "ksCPointOnCurve":
                return PointOnCurve(target, targetIndex, partner, middle: false);
            case "ksCPointOnCurveMiddle":
                return PointOnCurve(target, targetIndex, partner, middle: true);
            case "ksCTangentTwoCurves":
                return TangentTwoCurves(target, partner);
            case "ksCSymmetryTwoPoints":
                return SymmetryTwoPoints(target, targetIndex, partner, partnerIndex, axis);
            case "ksCBisector":
                return Bisector(target, partner, axis);
            default:
                return NotExpressible(constraintType);
        }
    }

    /// <summary>The types whose relation is not a number read right after the write, each with the reason
    /// it is not. Named here so the adapter never has to decide silently.</summary>
    private static ConstraintRelationVerdict NotExpressible(string type) => type switch
    {
        "ksCFixedPoint" => ConstraintRelationVerdict.NotExpressible(type,
            "«точка закреплена» проверяется ПРАВКОЙ СОСЕДНЕГО объекта: сразу после создания координаты "
            + "совпадают с исходными у любого ограничения, и число не различает закрепление"),
        "ksCFixedAngle" => ConstraintRelationVerdict.NotExpressible(type,
            "«угол зафиксирован» не задаёт числа: величина замораживается на текущей, и сразу после "
            + "создания проверять нечего"),
        "ksCFixedLenght" => ConstraintRelationVerdict.NotExpressible(type,
            "«длина зафиксирована» не задаёт числа: величина замораживается на текущей, и сразу после "
            + "создания проверять нечего"),
        "ksCAssociation" or "ksCFixedDim" or "ksCDimWithVariable" => ConstraintRelationVerdict.NotExpressible(
            type,
            "тип принадлежит ОБЪЕКТУ-РАЗМЕРУ и создаётся инструментом kompas_create_sketch_dimension, "
            + "где подтверждается перечитанным значением и геометрией"),
        _ => ConstraintRelationVerdict.NotExpressible(type,
            "для типа не объявлено проверяемого отношения"),
    };

    // ─────────────────────────────────────────────────────────────────── rules ──

    private static ConstraintRelationVerdict DirectionDeviation(SketchShape? shape, bool horizontal,
        string relation)
    {
        if (shape is not { Kind: SketchShapeKind.Segment } segment)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "объект не отрезок: направление не читается");
        }

        if (!TryDirection(segment, out var angle))
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "направление отрезка не прочитано");
        }

        // Both 0° and 180° are horizontal; the deviation is the distance to the nearer of the two.
        var deviation = horizontal ? FoldToAcute(angle) : Math.Abs(angle - 90d);
        return deviation <= AngleToleranceDeg
            ? ConstraintRelationVerdict.Satisfied(relation, deviation, 0d, AngleToleranceDeg)
            : ConstraintRelationVerdict.NotSatisfied(relation, deviation, 0d, AngleToleranceDeg);
    }

    private static ConstraintRelationVerdict TwoSegmentAngles(SketchShape? target, SketchShape? partner,
        double expectedDeg, string relation)
    {
        if (target is not { Kind: SketchShapeKind.Segment } a || partner is not { Kind: SketchShapeKind.Segment } b)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "нужны два отрезка: угла между ними не измерить");
        }

        if (!TryDirection(a, out var da) || !TryDirection(b, out var db))
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "направление отрезка не прочитано");
        }

        // Directions are folded into [0,180): the acute angle between the two lines is in [0,90].
        var diff = Math.Abs(da - db);
        var acute = Math.Min(diff, 180d - diff);
        var deviation = Math.Abs(acute - expectedDeg);
        return deviation <= AngleToleranceDeg
            ? ConstraintRelationVerdict.Satisfied(relation, acute, expectedDeg, AngleToleranceDeg)
            : ConstraintRelationVerdict.NotSatisfied(relation, acute, expectedDeg, AngleToleranceDeg);
    }

    private static ConstraintRelationVerdict Collinear(SketchShape? target, SketchShape? partner)
    {
        const string relation = "коллинеарность";
        if (target is not { Kind: SketchShapeKind.Segment } a || partner is not { Kind: SketchShapeKind.Segment } b)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "нужны два отрезка");
        }

        if (!TryDirection(a, out var da) || !TryDirection(b, out var db)
            || a.X1 is not { } ax || a.Y1 is not { } ay || a.X2 is not { } ax2 || a.Y2 is not { } ay2
            || b.X1 is not { } bx || b.Y1 is not { } by)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты отрезка не прочитаны");
        }

        var diff = Math.Abs(da - db);
        var parallelMiss = Math.Min(diff, 180d - diff);
        // Distance from b's start to a's INFINITE line, in mm — the cross product with a's unit direction.
        var len = Math.Sqrt(((ax2 - ax) * (ax2 - ax)) + ((ay2 - ay) * (ay2 - ay)));
        var distance = len > 0 ? Math.Abs(((bx - ax) * (ay2 - ay) - (by - ay) * (ax2 - ax)) / len) : double.NaN;
        var measured = Math.Max(parallelMiss, distance);
        return parallelMiss <= AngleToleranceDeg && distance <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict EqualLength(SketchShape? target, SketchShape? partner)
    {
        const string relation = "равенство длин";
        var la = SegmentLength(target);
        var lb = SegmentLength(partner);
        if (la is null || lb is null)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "длины двух отрезков не прочитаны");
        }

        var measured = Math.Abs(la.Value - lb.Value);
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict EqualRadius(SketchShape? target, SketchShape? partner)
    {
        const string relation = "равенство радиусов";
        if (target is not { IsCircular: true } a || partner is not { IsCircular: true } b
            || a.Radius is not { } ra || b.Radius is not { } rb)
        {
            return ConstraintRelationVerdict.NotExpressible(relation,
                "нужны две окружности или дуги с прочитанными радиусами");
        }

        var measured = Math.Abs(ra - rb);
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict AlignedPoints(SketchShape? target, int? targetIndex,
        SketchShape? partner, int? partnerIndex, bool byY, string relation)
    {
        var pa = target?.PointAt(targetIndex);
        var pb = partner?.PointAt(partnerIndex);
        if (pa is not { } a || pb is not { } b)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты двух точек не прочитаны");
        }

        var measured = byY ? Math.Abs(a.Y - b.Y) : Math.Abs(a.X - b.X);
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict MergePoints(SketchShape? target, int? targetIndex,
        SketchShape? partner, int? partnerIndex)
    {
        const string relation = "совпадение точек";
        var pa = target?.PointAt(targetIndex);
        var pb = partner?.PointAt(partnerIndex);
        if (pa is not { } a || pb is not { } b)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты двух точек не прочитаны");
        }

        var measured = Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict Concentricity(SketchShape? target, SketchShape? partner)
    {
        const string relation = "совпадение центров";
        if (target?.CenterX is not { } ax || target.CenterY is not { } ay
            || partner?.CenterX is not { } bx || partner.CenterY is not { } by)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты центров не прочитаны");
        }

        var measured = Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict PointOnCurve(SketchShape? target, int? targetIndex,
        SketchShape? curve, bool middle)
    {
        var relation = middle ? "точка на середине кривой" : "точка на кривой";
        if (target is not { Kind: SketchShapeKind.Point })
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "объект ограничения не точка");
        }

        if (curve is not { IsCurve: true })
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "партнёр не кривая");
        }

        if (target.PointAt(targetIndex) is not { } p)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты точки не прочитаны");
        }

        if (middle)
        {
            if (Midpoint(curve) is not { } mid)
            {
                return ConstraintRelationVerdict.NotExpressible(relation, "середина кривой не определена");
            }

            var dm = Math.Sqrt(((p.X - mid.X) * (p.X - mid.X)) + ((p.Y - mid.Y) * (p.Y - mid.Y)));
            return dm <= LengthToleranceMm
                ? ConstraintRelationVerdict.Satisfied(relation, dm, 0d, LengthToleranceMm)
                : ConstraintRelationVerdict.NotSatisfied(relation, dm, 0d, LengthToleranceMm);
        }

        var distance = DistanceToCurve(p, curve);
        if (distance is null)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "расстояние до кривой не вычислено");
        }

        return distance <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, distance.Value, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, distance.Value, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict TangentTwoCurves(SketchShape? target, SketchShape? partner)
    {
        const string relation = "касание";
        if (target is null || partner is null)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "геометрия объектов не прочитана");
        }

        // A straight line touches a circle or an arc when the centre is one radius from the line.
        if (target.Kind == SketchShapeKind.Segment && partner.IsCircular)
        {
            return LineToCircle(target, partner, relation);
        }

        if (partner.Kind == SketchShapeKind.Segment && target.IsCircular)
        {
            return LineToCircle(partner, target, relation);
        }

        if (target.IsCircular && partner.IsCircular)
        {
            if (target.CenterX is not { } ax || target.CenterY is not { } ay || target.Radius is not { } ra
                || partner.CenterX is not { } bx || partner.CenterY is not { } by || partner.Radius is not { } rb)
            {
                return ConstraintRelationVerdict.NotExpressible(relation, "центры или радиусы не прочитаны");
            }

            var d = Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
            var external = Math.Abs(d - (ra + rb));
            var internalGap = Math.Abs(d - Math.Abs(ra - rb));
            var best = Math.Min(external, internalGap);
            return best <= LengthToleranceMm
                ? ConstraintRelationVerdict.Satisfied(relation, best, 0d, LengthToleranceMm)
                : ConstraintRelationVerdict.NotSatisfied(relation, best, 0d, LengthToleranceMm);
        }

        return ConstraintRelationVerdict.NotExpressible(relation,
            "касание проверяется для прямой и окружности/дуги или для двух окружностей/дуг");
    }

    private static ConstraintRelationVerdict LineToCircle(SketchShape line, SketchShape circle, string relation)
    {
        if (line.X1 is not { } x1 || line.Y1 is not { } y1 || line.X2 is not { } x2 || line.Y2 is not { } y2
            || circle.CenterX is not { } cx || circle.CenterY is not { } cy || circle.Radius is not { } r)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты прямой или окружности не прочитаны");
        }

        var len = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
        if (len <= 0)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "отрезок нулевой длины");
        }

        var distance = Math.Abs(((cx - x1) * (y2 - y1) - (cy - y1) * (x2 - x1)) / len);
        var measured = Math.Abs(distance - r);
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    private static ConstraintRelationVerdict SymmetryTwoPoints(SketchShape? target, int? targetIndex,
        SketchShape? partner, int? partnerIndex, SketchShape? axis)
    {
        const string relation = "симметрия точек";
        var pa = target?.PointAt(targetIndex);
        var pb = partner?.PointAt(partnerIndex);
        if (pa is not { } a || pb is not { } b)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "координаты двух точек не прочитаны");
        }

        if (axis is not { Kind: SketchShapeKind.Segment } line
            || line.X1 is not { } x1 || line.Y1 is not { } y1 || line.X2 is not { } x2 || line.Y2 is not { } y2)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "ось симметрии не отрезок");
        }

        var len = Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
        if (len <= 0)
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "ось нулевой длины");
        }

        // The midpoint of the two points must lie ON the axis, and the segment between them must be
        // PERPENDICULAR to it. Both are read as one number: the larger of the two misses.
        var mx = (a.X + b.X) / 2d;
        var my = (a.Y + b.Y) / 2d;
        var distance = Math.Abs(((mx - x1) * (y2 - y1) - (my - y1) * (x2 - x1)) / len);
        var ux = (x2 - x1) / len;
        var uy = (y2 - y1) / len;
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var vlen = Math.Sqrt((vx * vx) + (vy * vy));
        var dot = vlen > 0 ? Math.Abs(((vx / vlen) * ux) + ((vy / vlen) * uy)) : 1d;
        var measured = Math.Max(distance, dot);
        return measured <= LengthToleranceMm
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, LengthToleranceMm)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, LengthToleranceMm);
    }

    /// <summary>The object segment is the bisector of the angle between the partner and the axis: the
    /// acute angle it makes with each of them is the same. Either of the two bisectors of a pair of lines
    /// satisfies this, so both are accepted.</summary>
    /// <remarks>MEASURED: the placement — object is the bisector itself, partner and axis are the two
    /// lines — is not described by the v24 help (the <c>BisectorVariant</c> page names only "two
    /// straight lines"), so the rule checks the RELATION rather than trusting the accepted write.
    /// History: docs/decisions/geometry.md#constraint-geometry</remarks>
    private static ConstraintRelationVerdict Bisector(SketchShape? target, SketchShape? partner,
        SketchShape? axis)
    {
        const string relation = "биссектриса";
        if (target is not { Kind: SketchShapeKind.Segment } o
            || partner is not { Kind: SketchShapeKind.Segment } p
            || axis is not { Kind: SketchShapeKind.Segment } a)
        {
            return ConstraintRelationVerdict.NotExpressible(relation,
                "нужны три отрезка: биссектриса, партнёр и ось");
        }

        if (!TryDirection(o, out var dO) || !TryDirection(p, out var dP) || !TryDirection(a, out var dA))
        {
            return ConstraintRelationVerdict.NotExpressible(relation, "направление отрезка не прочитано");
        }

        var measured = Math.Abs(Acute(dO, dP) - Acute(dO, dA));
        return measured <= AngleToleranceDeg
            ? ConstraintRelationVerdict.Satisfied(relation, measured, 0d, AngleToleranceDeg)
            : ConstraintRelationVerdict.NotSatisfied(relation, measured, 0d, AngleToleranceDeg);
    }

    // ─────────────────────────────────────────────────────────────── primitives ──

    /// <summary>The acute angle between two DIRECTIONS in degrees, both folded into [0,180).</summary>
    private static double Acute(double a, double b)
    {
        var d = Math.Abs(a - b) % 180d;
        return Math.Min(d, 180d - d);
    }

    private static bool TryDirection(SketchShape segment, out double angleDeg)
    {
        angleDeg = 0d;
        if (segment.X1 is not { } x1 || segment.Y1 is not { } y1
            || segment.X2 is not { } x2 || segment.Y2 is not { } y2)
        {
            return false;
        }

        var dx = x2 - x1;
        var dy = y2 - y1;
        if (Math.Abs(dx) < double.Epsilon && Math.Abs(dy) < double.Epsilon)
        {
            return false;
        }

        var deg = Math.Atan2(dy, dx) * DegreesPerRadian;
        deg %= 180d;
        if (deg < 0)
        {
            deg += 180d;
        }

        angleDeg = deg;
        return true;
    }

    private static double FoldToAcute(double angleDeg)
    {
        var a = Math.Abs(angleDeg) % 180d;
        return Math.Min(a, 180d - a);
    }

    private static double? SegmentLength(SketchShape? shape)
    {
        if (shape is not { Kind: SketchShapeKind.Segment } s
            || s.X1 is not { } x1 || s.Y1 is not { } y1 || s.X2 is not { } x2 || s.Y2 is not { } y2)
        {
            return null;
        }

        return Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));
    }

    private static (double X, double Y)? Midpoint(SketchShape curve) => curve.Kind switch
    {
        SketchShapeKind.Segment when curve.X1 is { } x1 && curve.Y1 is { } y1
                                     && curve.X2 is { } x2 && curve.Y2 is { } y2
            => ((x1 + x2) / 2d, (y1 + y2) / 2d),
        SketchShapeKind.Arc when curve.MidX is { } mx && curve.MidY is { } my => (mx, my),
        _ => null,
    };

    /// <summary>Distance from a point to a curve, mm. For a segment it is the distance to the SEGMENT
    /// (clamped to the nearer end), for a circle the radial miss, and for an arc the radial miss when the
    /// foot lies inside the arc's span and the distance to the nearer end otherwise.</summary>
    private static double? DistanceToCurve((double X, double Y) p, SketchShape curve)
    {
        switch (curve.Kind)
        {
            case SketchShapeKind.Segment when curve.X1 is { } x1 && curve.Y1 is { } y1
                                             && curve.X2 is { } x2 && curve.Y2 is { } y2:
            {
                var dx = x2 - x1;
                var dy = y2 - y1;
                var len2 = (dx * dx) + (dy * dy);
                if (len2 <= 0)
                {
                    return Math.Sqrt(((p.X - x1) * (p.X - x1)) + ((p.Y - y1) * (p.Y - y1)));
                }

                var t = (((p.X - x1) * dx) + ((p.Y - y1) * dy)) / len2;
                t = Math.Max(0d, Math.Min(1d, t));
                var fx = x1 + (t * dx);
                var fy = y1 + (t * dy);
                return Math.Sqrt(((p.X - fx) * (p.X - fx)) + ((p.Y - fy) * (p.Y - fy)));
            }

            case SketchShapeKind.Circle or SketchShapeKind.Arc when curve.CenterX is { } cx
                && curve.CenterY is { } cy && curve.Radius is { } r:
            {
                var d = Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)));
                return Math.Abs(d - r);
            }

            default:
                return null;
        }
    }
}
