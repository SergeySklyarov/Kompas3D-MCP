using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;

namespace KompasMcp.Api5Adapter.Api7;

/// <summary>A sketch entity as an OBJECT with an ADDRESS. An empty field means "not read", not zero;
/// the reason is named in <see cref="Notes"/>.</summary>
/// <remarks>INVARIANT: <see cref="Address"/> is neither "the N-th drawn by the server" nor a
/// coordinate — it is the string that <c>IKompasDocument1.GetObjectId</c> issues and
/// <c>IKompasDocument1.FindObjectById</c> accepts back. That is why the address is usable as an edit
/// input and a saved coordinate is not (acceptance requirement <c>dep.sketch.entities</c>).</remarks>
internal sealed record SketchEntityRow(
    int Index,
    string? Address,
    string? Kind,
    string? Name,
    int? TypeCode,
    IReadOnlyList<string> Notes);

/// <summary>Result of enumerating sketch entities: rows, route and per-collection counts.</summary>
internal sealed record SketchEntitiesRead(
    IReadOnlyList<SketchEntityRow> Rows,
    IReadOnlyDictionary<string, int?> CollectionCounts,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Sketch entities as OBJECTS with a stable address (<c>dep.sketch.entities</c>).</summary>
/// <remarks>DOC: the route is from the official v24 help, not a guess
/// (<c>DEPENDENCIES_PRODUCT_ROUTES_STEP0_REPORT_20260921.md</c> §6.4): <c>ISketch.BeginEdit</c> →
/// <c>IFragmentDocument</c> → <c>IViewsAndLayersManager.Views</c> → <c>IView</c> →
/// <c>IDrawingContainer.GetObjects</c>; the address is <c>IKompasDocument1.GetObjectId</c>; the exit
/// is <c>ISketch.EndEdit</c>. MEASURED (InteropScan): the help and the shipped interop diverge on
/// four points; a name is resolved by ENUMERATING the collection and comparing <c>Name</c> (<c>ByName</c> absent).
/// History: docs/decisions/adapter-api7.md#sketch-entities</remarks>
internal static class Api7SketchEntities
{
    /// <summary>Enumeration route. The string goes to the client response and to the acceptance
    /// evidence, so it is one for both: a divergence would make the records incomparable.</summary>
    public const string Route =
        "ISketch.BeginEditEx(true) → IFragmentDocument.ViewsAndLayersManager.Views → " +
        "IView(QI IDrawingContainer).GetObjects(ksAllObj) → IKompasDocument1.GetObjectId → " +
        "ISketch.EndEdit()";

    /// <summary>The document the object belongs to: walking up <c>Parent</c> to <c>IKompasDocument</c>.</summary>
    /// <remarks>Why not <c>IApplication.ActiveDocument</c>: "the active document" is window state, not
    /// a property of the object. A sketch in an inactive document would be read with a FOREIGN
    /// document's address — a silently wrong answer, not a refusal. The <c>Parent</c> walk starts from
    /// the object itself and so does not depend on window activity.</remarks>
    private static IKompasDocument? DocumentOf(IKompasAPIObject? start)
    {
        var node = start;
        for (var depth = 0; depth < 32 && node is not null; depth++)
        {
            if (node is IKompasDocument document)
            {
                return document;
            }

            node = SafeParent(node);
        }

        return null;
    }

    private static IKompasAPIObject? SafeParent(IKompasAPIObject node)
    {
        try
        {
            return node.Parent;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Enumerate the sketch entities with addresses. Returns either the rows or a NAMED reason
    /// why enumeration did not happen: an empty list and "not read" are different states.</summary>
    public static (SketchEntitiesRead? Read, string? Failure) Read(
        Api7Bridge bridge, object sketch5, int limit)
    {
        if (bridge.TransferTo7(sketch5) is not ISketch sketch)
        {
            return (null, "эскиз не переносится в API7 как ISketch");
        }

        // The DOCUMENT issues the address, so it is needed before entering edit mode.
        var document = DocumentOf(sketch as IKompasAPIObject);
        if (document is null)
        {
            return (null, "документ эскиза не найден подъёмом по IKompasAPIObject.Parent");
        }

        if (document is not IKompasDocument1 withIds)
        {
            return (null,
                "документ не отвечает QI(IKompasDocument1): GetObjectId объявлен именно на нём " +
                "(IID {58890FE8-E671-4561-994A-600DD29032E4}), у IKompasDocument его нет");
        }

        FragmentDocument? fragment;
        try
        {
            // BeginEditEx(true) is READ-ONLY. BeginEdit() would open the sketch for writing, giving a
            // read the right to change the model: these are different members, not overloads with a
            // default.
            fragment = sketch.BeginEditEx(true);
        }
        catch (COMException ex)
        {
            return (null, $"ISketch.BeginEditEx(true) отказал: HRESULT 0x{ex.HResult:X8}");
        }

        if (fragment is null)
        {
            return (null, "ISketch.BeginEditEx(true) → null: вход в эскиз не состоялся");
        }

        try
        {
            var fragmentDoc = fragment as IFragmentDocument;
            if (fragmentDoc is null)
            {
                return (null,
                    "BeginEditEx вернул объект, не отвечающий QI(IFragmentDocument): " +
                    "co-class FragmentDocument несёт ноль членов, члены живут на IFragmentDocument");
            }

            var views = fragmentDoc.ViewsAndLayersManager?.Views;
            if (views is null)
            {
                return (null, "IFragmentDocument.ViewsAndLayersManager.Views → null");
            }

            var counts = new Dictionary<string, int?>(StringComparer.Ordinal);
            var rows = new List<SketchEntityRow>();
            var notes = new List<string>();

            // THE FRAGMENT DOCUMENT ISSUES THE ADDRESS, NOT THE PART DOCUMENT. DOC: the help declares
            // parent as «родительский документ объекта (nullptr — текущий документ)», and under
            // BeginEditEx the current document is the sketch fragment, where the entity lives.
            // History: docs/decisions/adapter-api7.md#sketch-address
            var fragmentAsDocument = fragment as IKompasDocument1;
            notes.Add(fragmentAsDocument is not null
                ? "Адрес выдаёт документ фрагмента эскиза: он отвечает QI(IKompasDocument1), и " +
                  "сущность принадлежит именно ему."
                : "Документ фрагмента НЕ отвечает QI(IKompasDocument1): адрес взять негде, " +
                  "перечисление пойдёт без адресов.");

            var viewCount = SafeI(() => views.Count) ?? 0;
            notes.Add($"видов во фрагменте: {viewCount}");

            for (var v = 0; v < viewCount && rows.Count < limit; v++)
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

                if (view is null)
                {
                    notes.Add($"вид {v}: null");
                    continue;
                }

                // Per-type counts are read from the VIEW itself, because it is the container of
                // graphic objects; IView does not itself carry Objects — a QI is needed.
                var container = view as IDrawingContainer;
                if (container is null)
                {
                    notes.Add($"вид {v}: не отвечает QI(IDrawingContainer)");
                    continue;
                }

                counts[$"view{v}.object_count"] = SafeI(() => view.ObjectCount);
                counts[$"view{v}.line_segments"] = SafeI(() => container.LineSegments?.Count);
                counts[$"view{v}.circles"] = SafeI(() => container.Circles?.Count);
                counts[$"view{v}.arcs"] = SafeI(() => container.Arcs?.Count);
                counts[$"view{v}.poly_lines"] = SafeI(() => container.PolyLines2D?.Count);
                counts[$"view{v}.rectangles"] = SafeI(() => container.Rectangles?.Count);
                counts[$"view{v}.points"] = SafeI(() => container.Points?.Count);

                // DOC: ksAllObj = 0 means "all types" in the official DrawingObjectTypeEnum
                // (Interop.Kompas6Constants). It is passed AS AN ARRAY: in the interop the parameter
                // is declared Object (SAFEARRAY), not a single number.
                var objects = ReadObjects(container, notes, v);
                foreach (var item in objects)
                {
                    if (rows.Count >= limit)
                    {
                        notes.Add($"предел перечисления {limit} достигнут — список усечён");
                        break;
                    }

                    rows.Add(Describe(item, withIds, fragmentAsDocument, rows.Count, notes));
                }
            }

            return (new SketchEntitiesRead(rows, counts, Route, notes), null);
        }
        finally
        {
            // Leaving edit mode is mandatory on the failure path too: a fragment left open for
            // reading would keep the sketch in edit mode, and the next call would find the model busy.
            try
            {
                sketch.EndEdit();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A failed exit is no reason to return a false success, nor to lose what was read:
                // the reason stays in the call journal.
            }
        }
    }

    private static IReadOnlyList<object> ReadObjects(
        IDrawingContainer container, List<string> notes, int viewIndex)
    {
        try
        {
            var raw = container.get_Objects(new int[] { (int)DrawingObjectTypeEnum.ksAllObj });
            if (raw is Array array)
            {
                return array.Cast<object>().Where(o => o is not null).ToList();
            }

            if (raw is null)
            {
                notes.Add($"вид {viewIndex}: IDrawingContainer.GetObjects(ksAllObj) → null");
                return Array.Empty<object>();
            }

            notes.Add($"вид {viewIndex}: GetObjects вернул {raw.GetType().Name}, а не массив");
            return Array.Empty<object>();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"вид {viewIndex}: GetObjects бросил {ex.GetType().Name}");
            return Array.Empty<object>();
        }
    }

    private static SketchEntityRow Describe(
        object item, IKompasDocument1 document, IKompasDocument1? fragmentDocument, int index,
        List<string> notes)
    {
        var local = new List<string>();
        string? address = null;
        string? name = null;
        int? typeCode = null;
        string? kind = null;

        if (item is IKompasAPIObject apiObject)
        {
            try
            {
                // THE ADDRESS RECEIVER IS THE FRAGMENT DOCUMENT, NOT THE PART DOCUMENT — MEASURED.
                // DOC (ksapi_ikompasdocument_getobjectid.html): GetObjectId(object, parent), «parent —
                // родительский документ объекта (nullptr - текущий документ)». Under BeginEditEx(true)
                // the current document is the sketch fragment, and the entity belongs to it. A fragment
                // receiver yields a non-empty address, a part receiver yields "" (a SILENT refusal, no
                // exception). The second parameter is `null`, the ONLY expressible form: in the shipped
                // interop `IKompasDocument1` is NOT an `IKompasAPIObject`, so "the current document" is
                // expressed by the absence of a parent while the receiver document is named explicitly.
                // History: docs/decisions/adapter-api7.md#sketch-address
                var addressDocument = fragmentDocument ?? document;
                address = addressDocument.GetObjectId(apiObject, null);
                if (fragmentDocument is null)
                {
                    local.Add(
                        "адрес запрошен у документа ДЕТАЛИ: документ фрагмента не отвечает " +
                        "QI(IKompasDocument1), и адрес, скорее всего, будет пуст");
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                local.Add($"адрес не выдан: {ex.GetType().Name}");
            }

            // The model-object type is read from IKompasAPIObject — the only enumeration member EVERY
            // API7 object carries (IKompasAPIObject has just four: Application, Parent, Reference,
            // Type).
            typeCode = ReadInt(() => (int)apiObject.Type, local, "тип объекта модели");
            kind = ReadString(() => apiObject.Type.ToString(), local, "вид объекта модели");
        }
        else
        {
            local.Add("объект не отвечает IKompasAPIObject — адрес и тип недостижимы");
        }

        if (item is IDrawingObject drawing)
        {
            // The primitive kind comes from IDrawingObject.DrawingObjectType — the "segment / circle /
            // arc / polyline" of the acceptance type list. The type number is published AS A NUMBER
            // and is not replaced by a name if the value falls outside the declared enumeration.
            var drawingKind = ReadString(
                () => drawing.DrawingObjectType.ToString(), local, "вид графического объекта");
            var drawingCode = ReadInt(
                () => (int)drawing.DrawingObjectType, local, "номер типа графического объекта");
            if (drawingKind is not null)
            {
                kind = drawingKind;
            }

            if (drawingCode is not null)
            {
                typeCode = drawingCode;
            }
        }

        // MEASURED: a graphic object has NO NAME in the API — IDrawingObject's members are
        // Application, Delete, DrawingObjectParamType, DrawingObjectType, LayerNumber, Parent,
        // Reference, Temp, Type, Update, Valid, with no name member. An empty name here therefore
        // means "not published by the route", not "an object without a name": identity is carried by
        // the address.
        if (item is IModelObject model)
        {
            name = ReadString(() => model.Name, local, "имя объекта модели");
        }

        notes.AddRange(local.Select(n => $"объект {index}: {n}"));
        return new SketchEntityRow(index, address, kind, name, typeCode, local);
    }

    private static string? ReadString(Func<string?> read, List<string> notes, string what)
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

    private static int? SafeI(Func<int?> read)
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

    /// <summary>The datum plane of an EXISTING sketch: <c>ISketch.Plane</c>.</summary>
    /// <remarks>The route of actions <c>DEP.DPL.03.read</c> and <c>DEP.DPL.04.edit</c> (step-0 report
    /// §6.1 item 7): reading and changing the datum of an existing sketch.
    /// MEASURED: the help names members <c>GetPlane</c>/<c>SetPlane</c>, but the interop declares ONE
    /// property — <c>IModelObject get_Plane()</c> and <c>Void set_Plane(IModelObject)</c>; a C#
    /// assignment is exactly the call the page describes.
    /// History: docs/decisions/adapter-api7.md#sketch-plane</remarks>
    public static (string? Kind, string? Name, string? Failure) ReadPlane(ISketch sketch)
    {
        try
        {
            var plane = sketch.Plane;
            if (plane is null)
            {
                return (null, null, "ISketch.Plane → null: у эскиза нет опорной плоскости");
            }

            // The datum kind comes from the MODEL OBJECT TYPE (o3d_planeXOY=11 etc. from
            // obj3dtype.html), not from the name: a user can rename a plane, but not its type.
            var kind = SafeS(() => plane.ModelObjectType.ToString());
            var name = SafeS(() => plane.Name);
            return (kind, name, null);
        }
        catch (COMException ex)
        {
            return (null, null, $"ISketch.Plane отказал на чтении: HRESULT 0x{ex.HResult:X8}");
        }
        catch (InvalidCastException ex)
        {
            return (null, null, $"ISketch.Plane вернул неприводимое значение: {ex.GetType().Name}");
        }
    }

    /// <summary>Change the datum plane of an existing sketch: <c>ISketch.Plane = object</c>, then
    /// <c>Update()</c>. Returns <c>null</c> on success or a named reason for refusal.</summary>
    /// <remarks>INVARIANT: a successful return is not an applied edit — without <c>Update()</c> the
    /// assignment is accepted and the model does not change (MEASURED on neighbouring routes: no hole
    /// mode changes without <c>Update()</c>, <c>docs/acceptance/api7/hole-modes.md</c>). Confirmation
    /// is therefore taken SEPARATELY at the caller, by re-reading the bounding box and volume.
    /// History: docs/decisions/adapter-api7.md#sketch-plane</remarks>
    public static string? SetPlane(ISketch sketch, IModelObject plane)
    {
        try
        {
            sketch.Plane = plane;
            return SafeB(() => sketch.Update()) == true
                ? null
                : "ISketch.Update() не подтвердил смену опорной плоскости";
        }
        catch (COMException ex)
        {
            return $"ISketch.Plane отказал на записи: HRESULT 0x{ex.HResult:X8}";
        }
        catch (InvalidCastException ex)
        {
            return $"присваивание ISketch.Plane бросило {ex.GetType().Name}";
        }
    }

    private static string? SafeS(Func<string?> read)
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

    private static bool? SafeB(Func<bool> read)
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

    /// <summary>Resolve an address back to a model object. Returns the object or a named reason:
    /// "address not found" and "address not parsed" are different states.</summary>
    public static (IKompasAPIObject? Object, string? Failure) ResolveAddress(
        IKompasDocument1 document, string address)
    {
        try
        {
            var found = document.FindObjectById(address, null);
            return found is null
                ? (null, $"FindObjectById({address}) → null: адрес не разрешился")
                : (found, null);
        }
        catch (COMException ex)
        {
            return (null, $"FindObjectById отказал: HRESULT 0x{ex.HResult:X8}");
        }
        catch (InvalidCastException ex)
        {
            return (null, $"FindObjectById бросил {ex.GetType().Name}");
        }
    }
}
