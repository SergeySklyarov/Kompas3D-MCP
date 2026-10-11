using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The four kinds of driving sketch dimension, as the RULE layer sees them.</summary>
public enum SketchDimensionKind
{
    Linear,
    Diametral,
    Radial,
    Angular,
}

/// <summary>The shape of sketch object a kind measures, in the vocabulary of the entity list.</summary>
public enum SketchObjectShape
{
    Segment,
    Circle,
    Arc,
}

/// <summary>The PURE part of <c>kompas_create_sketch_dimension</c>: which fields a kind takes, what its
/// value means and which sketch object it measures.</summary>
/// <remarks>INVARIANT: a call that contradicts its own <c>kind</c> is refused BEFORE COM. A field of a
/// foreign kind silently ignored would let a client believe it set a diameter while the value went to a
/// length, and the acceptance run would then measure the caller's mistake instead of the server.
/// DOC: the measured object of each kind is the one its help page names — the diametral and radial
/// dimensions take a circle or an arc («в качестве опорного объекта может быть использована окружность
/// или дуга окружности», <c>idiametraldimension_baseobject.html</c> and
/// <c>iradialdimension_baseobject.html</c>), and the angular dimension takes two segments («в качестве
/// базовых объектов могут использоваться отрезки», <c>iangledimension_baseobject1.html</c>).
/// LIMIT: the diametral kind accepts a CIRCLE ONLY, narrower than the help's «окружность или дуга
/// окружности»; the order names a circle for it and an arc or a circle for the radial kind. A subset of
/// a documented latitude does not contradict it, and the narrowing is named in the tool description.
/// History: docs/decisions/adapter-sketch.md#sketch-dimension-kinds</remarks>
public static class SketchDimensionRule
{
    /// <summary>Resolves the <c>kind</c> field. An omitted field is the DEFAULT kind — the behaviour
    /// the tool had before the field existed — and an unknown name is named back with the accepted
    /// list, never coerced to the default.</summary>
    public static bool TryResolve(string? name, out SketchDimensionKind kind, out string? problem)
    {
        kind = SketchDimensionKind.Linear;
        problem = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        switch (name)
        {
            case SketchDimensionKinds.Linear:
                kind = SketchDimensionKind.Linear;
                return true;
            case SketchDimensionKinds.Diametral:
                kind = SketchDimensionKind.Diametral;
                return true;
            case SketchDimensionKinds.Radial:
                kind = SketchDimensionKind.Radial;
                return true;
            case SketchDimensionKinds.Angular:
                kind = SketchDimensionKind.Angular;
                return true;
            default:
                problem = $"kind '{name}' не объявлен; принимаются "
                          + string.Join(", ", SketchDimensionKinds.All)
                          + $". Отсутствие поля означает '{SketchDimensionKinds.Default}'.";
                return false;
        }
    }

    public static string Name(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral => SketchDimensionKinds.Diametral,
        SketchDimensionKind.Radial => SketchDimensionKinds.Radial,
        SketchDimensionKind.Angular => SketchDimensionKinds.Angular,
        _ => SketchDimensionKinds.Linear,
    };

    /// <summary>The sketch object the kind measures.</summary>
    public static IReadOnlyList<SketchObjectShape> AcceptedShapes(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral => new[] { SketchObjectShape.Circle },
        SketchDimensionKind.Radial => new[] { SketchObjectShape.Arc, SketchObjectShape.Circle },
        SketchDimensionKind.Angular => new[] { SketchObjectShape.Segment },
        _ => new[] { SketchObjectShape.Segment },
    };

    /// <summary>Whether the kind needs the second measured object (<c>second_object_ref</c>).</summary>
    public static bool NeedsSecondObject(SketchDimensionKind kind) => kind == SketchDimensionKind.Angular;

    /// <summary>The measured object named the way the refusal names it.</summary>
    public static string RequiredShapeText(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral => "окружность",
        SketchDimensionKind.Radial => "дуга или окружность",
        SketchDimensionKind.Angular => "два отрезка",
        _ => "отрезок",
    };

    public static string ShapeText(SketchObjectShape shape) => shape switch
    {
        SketchObjectShape.Circle => "окружность",
        SketchObjectShape.Arc => "дуга",
        _ => "отрезок",
    };

    /// <summary>The name of the value member a kind takes: the DIAMETER for a diametral dimension, the
    /// RADIUS for a radial one, the ANGLE for an angular one and the LENGTH for a linear one. DOC:
    /// <c>idimensiontext_nominalvalue.html</c> and the per-kind remarks on
    /// <c>idiametraldimension.html</c> / <c>iangledimension.html</c>; MEASURED for the radial kind, whose
    /// page repeats the diametral wording (see the API notes).</summary>
    public static string GeometryName(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral => "diameter_mm",
        SketchDimensionKind.Radial => "radius_mm",
        SketchDimensionKind.Angular => "angle_deg",
        _ => "length_mm",
    };

    /// <summary>What the requested value means for the kind, in the refusal's words.</summary>
    public static string ValueMeaning(SketchDimensionKind kind) => kind switch
    {
        SketchDimensionKind.Diametral => "value_mm задаёт ДИАМЕТР, а не радиус",
        SketchDimensionKind.Radial => "value_mm задаёт РАДИУС",
        SketchDimensionKind.Angular => "value_deg задаёт УГОЛ в градусах",
        _ => "value_mm задаёт ДЛИНУ отрезка",
    };

    /// <summary>The fields a kind does NOT take, each named. Empty means every supplied field belongs to
    /// the kind.</summary>
    /// <remarks>The three fields are passed as "was it supplied" flags, not as values, because the
    /// decision is about the CALL and not about the numbers: <c>value_deg = 0</c> is still a field of a
    /// foreign kind.</remarks>
    public static IReadOnlyList<string> ForeignFields(
        SketchDimensionKind kind,
        bool secondObjectRef,
        bool valueMm,
        bool valueDeg,
        bool offsetMm)
    {
        var foreign = new List<string>();

        if (secondObjectRef && !NeedsSecondObject(kind))
        {
            foreign.Add("second_object_ref");
        }

        switch (kind)
        {
            case SketchDimensionKind.Angular:
                if (valueMm)
                {
                    foreign.Add("value_mm");
                }

                break;
            default:
                if (valueDeg)
                {
                    foreign.Add("value_deg");
                }

                break;
        }

        // The dimension line's offset is the LINEAR route's member: the linear dimension is placed by
        // three points and a shelf, while the other kinds place themselves from their base object and
        // carry their own position members (Angle, Radius, Xc/Yc — see the per-kind property pages).
        // Accepting offset_mm there would promise a placement this server does not perform.
        if (offsetMm && kind != SketchDimensionKind.Linear)
        {
            foreign.Add("offset_mm");
        }

        return foreign;
    }
}
