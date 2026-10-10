using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;

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
/// <remarks>DOC: <c>IDrawingObject1::NewConstraint()</c> (<c>idrawingobject1_newconstraint.html</c>) →
/// <c>IParametriticConstraint</c> → <c>Create()</c> (<c>iparametriticconstraint_create.html</c>);
/// the object comes from <c>IDrawingContainer::GetObjects</c> inside
/// <c>ISketch.BeginEdit/BeginEditEx</c>, and the list from <c>IDrawingObject1::Constraints</c>
/// (<c>idrawingobject1_constraints.html</c>).
/// MEASURED (the instrument's own trap, found on the first run): an <c>IDrawingObject1</c> and its
/// <c>Constraints</c> are alive ONLY INSIDE the edit session. Read after <c>EndEdit()</c> the very same
/// object answers <c>Constraints → null</c> and zero coordinates, WITHOUT an exception. So every read
/// and every confirmation happens inside the session, and a value read outside it is a defect of the
/// instrument, not a property of КОМПАС.
/// INVARIANT: the confirmation is the RE-READ LIST, never the return of <c>Create()</c>. MEASURED: the
/// template object returned by <c>NewConstraint()</c> keeps its defaults (<c>Value</c> 0,
/// <c>Expression</c> empty) after a successful creation, so reading it back would report a created
/// dimension as empty.
/// History: docs/decisions/adapter-api7.md#sketch-constraints</remarks>
internal static class Api7SketchConstraints
{
    /// <summary>The write route, named once so the response and the acceptance evidence cannot
    /// drift apart.</summary>
    public const string Route =
        "ISketch.BeginEdit() → IFragmentDocument.ViewsAndLayersManager.Views → IView(QI IDrawingContainer)" +
        ".GetObjects(ksAllObj) → QI(IDrawingObject1) → NewConstraint() → set → Create() → " +
        "IDrawingObject1.Constraints (внутри сеанса) → ISketch.EndEdit()";

    /// <summary>The read route: the same enumeration, entered READ-ONLY.</summary>
    public const string ReadRoute =
        "ISketch.BeginEditEx(true) → IFragmentDocument.ViewsAndLayersManager.Views → " +
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

    /// <summary>Enumerates the objects of a sketch with their addresses, inside ONE session that the
    /// caller owns.</summary>
    public static (IReadOnlyList<SketchConstraintObject> Objects, string? Failure) EnumerateInSession(
        IFragmentDocument fragment, List<string> notes)
    {
        var rows = new List<SketchConstraintObject>();

        IViews? views;
        try
        {
            views = fragment.ViewsAndLayersManager?.Views;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (rows, "IFragmentDocument.ViewsAndLayersManager.Views: " + ex.GetType().Name);
        }

        if (views is null)
        {
            return (rows, "IFragmentDocument.ViewsAndLayersManager.Views → null");
        }

        // The fragment document issues the address: under BeginEdit the current document IS the sketch
        // fragment, and the entity belongs to it.
        var addressDocument = fragment as IKompasDocument1;
        if (addressDocument is null)
        {
            notes.Add("документ фрагмента не отвечает QI(IKompasDocument1): адреса не выдаются");
        }

        var viewCount = SafeInt(() => views.Count) ?? 0;
        for (var v = 0; v < viewCount; v++)
        {
            IView? view;
            try
            {
                view = views.get_View(v);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add($"вид {v}: не получен ({ex.GetType().Name})");
                continue;
            }

            if (view is not IDrawingContainer container)
            {
                notes.Add($"вид {v}: не отвечает QI(IDrawingContainer)");
                continue;
            }

            object? raw;
            try
            {
                raw = container.get_Objects(new[] { (int)DrawingObjectTypeEnum.ksAllObj });
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                notes.Add($"вид {v}: GetObjects бросил {ex.GetType().Name}");
                continue;
            }

            if (raw is not Array array)
            {
                notes.Add($"вид {v}: GetObjects вернул не массив");
                continue;
            }

            foreach (var item in array)
            {
                if (item is not IDrawingObject drawing)
                {
                    notes.Add($"элемент {rows.Count}: не отвечает IDrawingObject");
                    continue;
                }

                rows.Add(new SketchConstraintObject(
                    rows.Count,
                    AddressOf(drawing, addressDocument, notes, rows.Count),
                    KindOf(drawing),
                    drawing,
                    drawing as IDrawingObject1));
            }
        }

        return (rows, null);
    }

    private static string? AddressOf(IDrawingObject drawing, IKompasDocument1? document, List<string> notes,
        int index)
    {
        if (document is null || drawing is not IKompasAPIObject apiObject)
        {
            return null;
        }

        try
        {
            // DOC (ksapi_ikompasdocument_getobjectid.html): GetObjectId(object, parent), «parent —
            // родительский документ объекта (nullptr — текущий документ)». Under BeginEdit the current
            // document is the sketch fragment, where the entity lives; a fragment receiver yields a
            // non-empty address, a part receiver yields "" WITHOUT an exception.
            return document.GetObjectId(apiObject, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"объект {index}: адрес не выдан ({ex.GetType().Name})");
            return null;
        }
    }

    private static string? KindOf(IDrawingObject drawing)
    {
        try
        {
            return drawing.DrawingObjectType.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
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

    private static int? SafeInt(Func<int?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
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
