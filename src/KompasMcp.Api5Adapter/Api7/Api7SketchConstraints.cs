using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>One constraint of an object, as READ BACK from the model. An unread member is
/// <c>null</c> and named in <see cref="Notes"/>; it is never reported as zero or false.</summary>
internal sealed record SketchConstraintRow(
    int Index,
    string? Type,
    int? TypeCode,
    int? PointIndex,
    int? PartnerIndex,
    int? SegmentIndex,
    int? PartnerSegmentIndex,
    bool? Valid,
    double? Value,
    string? Expression,
    string? Variable,
    bool? PartnerPresent,
    IReadOnlyList<string> Notes);

/// <summary>An object of a sketch with its stable address and its constraint host.</summary>
internal sealed record SketchConstraintObject(
    int Index,
    string? Address,
    string? Kind,
    IDrawingObject Drawing,
    IDrawingObject1? Host);

/// <summary>Sketch constraints through the documented API7 route (block G6).</summary>
/// <remarks>DOC: <c>IDrawingObject1::NewConstraint()</c> → <c>IParametriticConstraint</c> → <c>Create()</c>.
/// The object comes from <c>IDrawingContainer.GetObjects</c> inside <c>ISketch.BeginEdit</c>, the
/// list from <c>IDrawingObject1::Constraints</c>.
/// MEASURED: an object and its <c>Constraints</c> are alive ONLY INSIDE the edit session — after
/// <c>EndEdit()</c> the same object answers <c>Constraints → null</c> and zero coordinates.
/// INVARIANT: confirmation is the RE-READ LIST, never the return of <c>Create()</c>.
/// History: docs/decisions/adapter-api7.md#sketch-constraints</remarks>
internal static class Api7SketchConstraints
{
    /// <summary>The write route, named once so the response and the acceptance evidence cannot
    /// drift apart.</summary>
    public const string Route =
        "ISketch.BeginEdit() → IKompasDocument2D.ViewsAndLayersManager.Views → IView(QI IDrawingContainer)" +
        ".GetObjects(ksAllObj) → QI(IDrawingObject1) → NewConstraint() → set → Create() → " +
        "IDrawingObject1.Constraints (внутри сеанса) → ISketch.EndEdit()";

    /// <summary>The read route: the same enumeration, entered READ-ONLY.</summary>
    public const string ReadRoute =
        "ISketch.BeginEditEx(true) → IKompasDocument2D.ViewsAndLayersManager.Views → " +
        "IView(QI IDrawingContainer).GetObjects(ksAllObj) → QI(IDrawingObject1) → " +
        "IDrawingObject1.Constraints (внутри сеанса) → ISketch.EndEdit()";

    /// <summary>The type vocabulary, taken from the ONE assembly that declares it, so no number is
    /// duplicated as a literal anywhere in the server.</summary>
    public static IReadOnlyDictionary<string, ksConstraintTypeEnum> TypeVocabulary { get; } =
        Enum.GetNames<ksConstraintTypeEnum>()
            .Where(name => name != "value__")
            .ToDictionary(name => name, Enum.Parse<ksConstraintTypeEnum>, StringComparer.Ordinal);

    /// <summary>The names this adapter can resolve, compared against the list the published schema
    /// offers. Computed ONCE and published, so a name added to one side and not the other is a named
    /// refusal rather than a silent hole between the schema and the adapter.</summary>
    public static IReadOnlyList<string> VocabularyProblems { get; } =
        SketchConstraintTypes.AgreementProblems(TypeVocabulary.Keys);

    /// <summary>The objects of a sketch, enumerated inside ONE session that the caller owns.</summary>
    /// <remarks>The enumeration itself lives in <see cref="Api7SketchEntities.ObjectsInSession"/> — the
    /// module of sketch ENTITIES owns the route; this module only adds the constraint host.
    /// History: docs/decisions/adapter-api7.md#sketch-constraints</remarks>
    public static (IReadOnlyList<SketchConstraintObject> Objects, string? Failure) EnumerateInSession(
        IFragmentDocument fragment, List<string> notes)
    {
        var (objects, failure) = Api7SketchEntities.ObjectsInSession(fragment, notes);
        if (failure is not null)
        {
            return (Array.Empty<SketchConstraintObject>(), failure);
        }

        return (objects
            .Select(o => new SketchConstraintObject(
                o.Index, o.Address, o.Kind, o.Drawing, o.Drawing as IDrawingObject1))
            .ToList(), null);
    }

    /// <summary>Reads the constraint list of an object. MUST be called inside the session — see the
    /// class remarks.</summary>
    public static IReadOnlyList<SketchConstraintRow> ReadRows(IDrawingObject1 host, List<string> notes)
    {
        var rows = new List<SketchConstraintRow>();

        object? raw;
        try
        {
            raw = host.Constraints;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("Constraints не прочитан: " + ex.GetType().Name);
            return rows;
        }

        // DOC (idrawingobject1_constraints.html): the property is VARIANT; with ONE constraint the
        // variant is VT_DISPATCH, not an array. A single element is therefore a legitimate shape and is
        // unwrapped rather than reported as "not an array".
        if (raw is IParametriticConstraint single)
        {
            rows.Add(Describe(single, 0, notes));
            return rows;
        }

        if (raw is not Array array)
        {
            if (raw is not null)
            {
                notes.Add("Constraints вернул не массив и не одно ограничение: " + raw.GetType().Name);
            }

            return rows;
        }

        for (var i = 0; i < array.Length; i++)
        {
            if (array.GetValue(i) is IParametriticConstraint constraint)
            {
                rows.Add(Describe(constraint, i, notes));
            }
            else
            {
                notes.Add($"элемент списка [{i}] не отвечает IParametriticConstraint");
            }
        }

        return rows;
    }

    private static SketchConstraintRow Describe(IParametriticConstraint constraint, int index, List<string> notes)
    {
        var local = new List<string>();
        int? typeCode = ReadInt(() => (int)constraint.ConstraintType, local, "тип ограничения");
        var typeName = typeCode is not null && Enum.IsDefined(typeof(ksConstraintTypeEnum), typeCode.Value)
            ? ((ksConstraintTypeEnum)typeCode.Value).ToString()
            : null;

        bool? partnerPresent = null;
        try
        {
            var partner = constraint.Partner;
            partnerPresent = partner is not null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            local.Add("партнёр не прочитан: " + ex.GetType().Name);
        }

        return new SketchConstraintRow(
            index,
            typeName,
            typeCode,
            ReadInt(() => constraint.Index, local, "индекс точки"),
            ReadInt(() => constraint.PartnerIndex, local, "индекс точки партнёра"),
            ReadInt(() => constraint.SegmentIndex, local, "индекс сегмента"),
            ReadInt(() => constraint.PartnerSegmentIndex, local, "индекс сегмента партнёра"),
            ReadBool(() => constraint.Valid, local, "признак действительности"),
            ReadDouble(() => constraint.Value, local, "значение"),
            ReadString(() => constraint.Expression, local, "выражение"),
            ReadString(() => constraint.Variable, local, "имя переменной"),
            partnerPresent,
            local);
    }

    /// <summary>Result of one <c>NewConstraint()</c> → <c>Create()</c> pair.</summary>
    internal sealed record CreateOutcome(bool? Created, string? Failure);

    /// <summary>Writes one constraint on the object. The properties are set BEFORE <c>Create()</c>:
    /// the help is explicit that a constraint's parameters may only be changed at creation time and
    /// that an existing one must be deleted and re-created to be edited.</summary>
    public static CreateOutcome Create(
        IDrawingObject1 host,
        ksConstraintTypeEnum type,
        int? pointIndex,
        object? partner,
        int? partnerPointIndex,
        IDrawingObject? axis,
        double? value,
        string? expression,
        string? variable,
        List<string> notes)
    {
        IParametriticConstraint? constraint;
        try
        {
            constraint = host.NewConstraint();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new(null, "NewConstraint() отказал: " + ex.GetType().Name);
        }

        if (constraint is null)
        {
            return new(null, "NewConstraint() → null");
        }

        try
        {
            constraint.ConstraintType = type;
            if (pointIndex is { } index)
            {
                constraint.Index = index;
            }

            if (partner is not null)
            {
                constraint.Partner = partner;
            }

            if (partnerPointIndex is { } partnerIndex)
            {
                constraint.PartnerIndex = partnerIndex;
            }

            if (axis is not null)
            {
                constraint.Axis = axis;
            }

            if (value is { } wanted)
            {
                constraint.Value = wanted;
            }

            if (expression is { } text)
            {
                constraint.Expression = text;
            }

            if (variable is { } name)
            {
                constraint.Variable = name;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new(null, "запись свойств ограничения отказала: " + ex.GetType().Name);
        }

        try
        {
            return new(constraint.Create(), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return new(null, "Create() отказал: " + ex.GetType().Name);
        }
    }

    /// <summary>Deletes ONE constraint by its position in the list this server hands out.</summary>
    public static (bool? Result, string? Failure) DeleteOne(IDrawingObject1 host, int index,
        IReadOnlyList<SketchConstraintRow> rows, List<string> notes)
    {
        if (index < 0 || index >= rows.Count)
        {
            return (null, $"в списке ограничений объекта {rows.Count} записей, позиция {index} вне их");
        }

        object? raw;
        try
        {
            raw = host.Constraints;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "Constraints не прочитан: " + ex.GetType().Name);
        }

        var single = raw as IParametriticConstraint;
        var array = raw as Array;
        var target = single is not null && index == 0
            ? single
            : array is not null && index < array.Length
                ? array.GetValue(index) as IParametriticConstraint
                : null;

        if (target is null)
        {
            return (null, "запись по позиции не отвечает IParametriticConstraint");
        }

        try
        {
            return (target.Delete(), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "Delete() отказал: " + ex.GetType().Name);
        }
    }

    /// <summary>Deletes ALL constraints of the object.</summary>
    public static (bool? Result, string? Failure) DeleteAll(IDrawingObject1 host)
    {
        try
        {
            return (host.DeleteConstraints(), null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "DeleteConstraints() отказал: " + ex.GetType().Name);
        }
    }

    /// <summary>What a refused create left in the constraint list, and what was done about it.</summary>
    /// <remarks><see cref="Code"/> is one of <c>not_needed</c> (the list never grew), <c>removed</c> (the
    /// list is back to <c>before</c>) or <c>failed</c> (records remain, or the delete refused) — so the
    /// refusal can say what happened instead of claiming "nothing was changed".</remarks>
    internal sealed record CleanupOutcome(IReadOnlyList<SketchConstraintRow> Rows, string Code, string? Failure);

    /// <summary>Removes the record a REFUSED create left behind, through the same documented route
    /// <c>kompas_delete_sketch_constraint</c> uses (<c>IParametriticConstraint::Delete()</c>), and re-reads
    /// the list.</summary>
    /// <remarks>MEASURED: the kernel answers <c>Create() = FALSE</c> and still appends a record of that
    /// type with <c>Valid = false</c>. INVARIANT: only the RE-READ list may say "nothing was changed".
    /// DOC: <c>iparametriticconstraint_create.html</c> («TRUE — параметрическое ограничение создано,
    /// FALSE — в случае неудачи»). History: docs/decisions/adapter-api7.md#constraint-refusal-cleanup</remarks>
    public static CleanupOutcome RemoveAdded(IDrawingObject1 host,
        IReadOnlyList<SketchConstraintRow> before, IReadOnlyList<SketchConstraintRow> after, List<string> notes)
    {
        if (after.Count <= before.Count)
        {
            return new CleanupOutcome(after, "not_needed", null);
        }

        // The kernel APPENDS the record of a refused constraint, so the added ones are the tail positions;
        // deleting from the tail keeps the remaining positions valid.
        for (var index = after.Count - 1; index >= before.Count; index--)
        {
            var (result, failure) = DeleteOne(host, index, after, notes);
            if (failure is not null || result == false)
            {
                return new CleanupOutcome(ReadRows(host, notes), "failed",
                    failure ?? $"Delete() по позиции {index} вернул false");
            }
        }

        var reread = ReadRows(host, notes);
        return reread.Count == before.Count
            ? new CleanupOutcome(reread, "removed", null)
            : new CleanupOutcome(reread, "failed",
                $"после удаления в списке {reread.Count} записей, до вызова было {before.Count}");
    }

    /// <summary>Reads the parametric state of ONE object
    /// (<c>ksObjectConstraintsStateEnum</c>, <c>constraintsstate.html</c>).</summary>
    public static (int? Raw, string? Name) ObjectState(IDrawingObject1 host, List<string> notes)
    {
        try
        {
            var raw = (int)host.ConstraintsState;
            var name = Enum.IsDefined(typeof(ksObjectConstraintsStateEnum), raw)
                ? ((ksObjectConstraintsStateEnum)raw).ToString()
                : null;
            return (raw, name);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("состояние объекта не прочитано: " + ex.GetType().Name);
            return (null, null);
        }
    }

    /// <summary>Reads the geometry of one drawing object for a relation check. MUST be called inside the
    /// session — see the class remarks: outside it the same object answers zero coordinates silently.
    /// A shape whose members did not answer is <c>null</c> with the reason named, never zeros.</summary>
    /// <remarks>DOC: <c>ilinesegment_props.html</c> (X1/Y1/X2/Y2/Length/Angle),
    /// <c>icircle_props.html</c> (Xc/Yc/Radius/X/Y) and <c>iarc_props.html</c>
    /// (Xc/Yc/Radius/X1/Y1/X2/Y2/X3/Y3) and <c>ipoint_props.html</c> (X/Y). The ARC is checked before the
    /// CIRCLE: an arc carries a radius too, and naming it a circle would lose its endpoints.
    /// History: docs/decisions/adapter-api7.md#constraint-geometry</remarks>
    public static SketchShape? ReadShape(IDrawingObject drawing, List<string> notes)
    {
        try
        {
            switch (drawing)
            {
                case ILineSegment segment:
                    return new SketchShape(SketchShapeKind.Segment,
                        X1: segment.X1, Y1: segment.Y1, X2: segment.X2, Y2: segment.Y2);
                case IArc arc:
                    return new SketchShape(SketchShapeKind.Arc,
                        X1: arc.X1, Y1: arc.Y1, X2: arc.X2, Y2: arc.Y2,
                        CenterX: arc.Xc, CenterY: arc.Yc, Radius: arc.Radius, MidX: arc.X3, MidY: arc.Y3);
                case ICircle circle:
                    return new SketchShape(SketchShapeKind.Circle,
                        CenterX: circle.Xc, CenterY: circle.Yc, Radius: circle.Radius);
                case IPoint point:
                    return new SketchShape(SketchShapeKind.Point, X: point.X, Y: point.Y);
                default:
                    notes.Add("геометрия объекта не прочитана: вид не отрезок, не дуга, не окружность и не точка");
                    return null;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add("геометрия объекта не прочитана: " + ex.GetType().Name);
            return null;
        }
    }

    private static int? ReadInt(Func<int> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитан: {ex.GetType().Name}");
            return null;
        }
    }


    private static bool? ReadBool(Func<bool> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитан: {ex.GetType().Name}");
            return null;
        }
    }

    private static double? ReadDouble(Func<double> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитано: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ReadString(Func<string> read, List<string> notes, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"{what} не прочитано: {ex.GetType().Name}");
            return null;
        }
    }
}
