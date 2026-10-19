using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe DR — «Уклон» от основания and «Уклон от базовой линии»: which documented routes
/// create the feature, what unit does the angle take and what does <c>direction</c> mean.</summary>
/// <remarks>
/// DOC: <c>ksinclinedefinition.html</c> (definition from <c>ksEntity::GetDefinition</c>),
/// <c>ksinclinedefinition_facearray.html</c> (<c>FaceArray()</c> → collection of faces),
/// <c>ksinclinedefinition_setplane.html</c> (<c>SetPlane</c>, «плоский объект»),
/// <c>ksinclinedefinition_angle.html</c> (<c>double</c>, unit NOT named),
/// <c>ksinclinedefinition_direction.html</c> («TRUE - наружу, FALSE - внутрь»),
/// <c>iinclines_add.html</c> and <c>idraftsfromedges_add.html</c> (Add → set properties → Update).
/// MEASURED TARGET: a 100×80×50 bar drafted 10° on the +X face gives 382367.30 (material removed) or
/// 417632.70 (added); radians would give 335163.92, so the volume separates degrees from radians.
/// INVARIANT: <c>Create()=true</c> proves nothing - the volume is read back and compared.
/// History: docs/decisions/probes.md#drafts
/// </remarks>
internal sealed class DraftsProbe
{
    private const short SketchType = 5;
    private const short PlaneXoy = 1;
    private const short BaseExtrusion = 24;
    private const short InclineType = 42;
    private const int LineStyle = 1;

    private const double BarX = 100d;
    private const double BarY = 80d;
    private const double BarZ = 50d;
    private const double AngleDeg = 10d;
    private const double VolumeTolerance = 0.5d;

    private static double VolumeV0 => BarX * BarY * BarZ;
    private static double VolumeInward => 382367.30d;
    private static double VolumeOutward => 417632.70d;
    private static double VolumeRadians => 335163.92d;

    private readonly ProbeReport _report;
    private readonly Options _options;

    private KompasObject _app = null!;

    public DraftsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        try
        {
            Launch();
            Api5Incline();
            Api7Incline();
            FaceBaseIncline(direction: true);
            FaceBaseIncline(direction: false);
            DraftFromEdges(reverse: false);
            DraftFromEdges(reverse: true);
            Lifecycle();
        }
        catch (Exception ex)
        {
            _report.Begin("DR.X", "Необработанное исключение пробы DR").Fail(ex.ToString());
        }
    }

    private void Launch()
    {
        var step = _report.Begin("DR.1", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        step.Pass("сеанс поднят");
    }

    /// <summary>DR.3/DR.4: the API5 route with the XOY plane as base, once per direction flag.</summary>
    private void Api5Incline()
    {
        foreach (var outward in new[] { true, false })
        {
            var id = outward ? "DR.3" : "DR.4";
            var step = _report.Begin(id, "API5: уклон 10°, direction=" + (outward ? "TRUE" : "FALSE"),
                "Создаётся ли маршрутом NewEntity(42), и что даёт флаг?");
            var document = BuildBar("DR.2", "Брус 100×80×50 выдавливанием");
            if (document is null)
            {
                step.Fail("брус не построен.");
                return;
            }

            var part = (ksPart)document.GetPart(-1);
            var face = PlusXFace(part);
            if (face is null)
            {
                step.Fail("грань с нормалью +X не найдена.");
                return;
            }

            if (part.NewEntity(InclineType) is not ksEntity entity
                || entity.GetDefinition() is not ksInclineDefinition incline)
            {
                step.Fail("NewEntity(o3d_incline=42) или его определение не получены.");
                return;
            }

            entity.name = "dr-incline-" + (outward ? "true" : "false");
            var faceArray = incline.FaceArray();
            step.Data["face_added"] = faceArray is null
                ? null
                : Late.Call(faceArray, "Add", face.Element);

            if (part.GetDefaultEntity(PlaneXoy) is not ksEntity basePlane)
            {
                step.Fail("Стандартная плоскость XOY не получена как объект основания.");
                return;
            }

            step.Data["set_plane"] = incline.SetPlane(basePlane);
            incline.angle = AngleDeg;
            incline.direction = outward;
            var created = Api5.SafeBool(entity.Create);
            step.Data["create"] = created;
            document.RebuildDocument();
            Report(step, part, created, "API5 direction=" + (outward ? "TRUE" : "FALSE"));
            CloseQuietly(document);
        }
    }

    /// <summary>DR.5: the same feature through API7 <c>IInclines.Add</c>, which the help page describes
    /// as «задать параметры и вызвать IModelObject::Update».</summary>
    private void Api7Incline()
    {
        var step = _report.Begin("DR.5", "API7: IInclines.Add → Faces/Plane/Angle/Direction → Update",
            "Повторяет ли API7 маршрут тот же объём?");
        var document = BuildBar("DR.2", "Брус 100×80×50 выдавливанием");
        if (document is null)
        {
            step.Fail("брус не построен.");
            return;
        }

        var part = (ksPart)document.GetPart(-1);
        var face = PlusXFace(part);
        if (face is null || part.GetDefaultEntity(PlaneXoy) is not ksEntity basePlane)
        {
            step.Fail("грань +X или плоскость XOY не получены.");
            return;
        }

        if (Container(part) is not { } container)
        {
            step.Fail("деталь не отвечает QI(IModelContainer).");
            return;
        }

        var face7 = ToApi7(face.Element);
        var plane7 = ToApi7(basePlane);
        if (face7 is null || plane7 is null)
        {
            step.Fail("грань или плоскость не перенесены в API7 как IModelObject.");
            return;
        }

        IIncline? incline = null;
        try
        {
            incline = container.Inclines?.Add() as IIncline;
        }
        catch (Exception ex)
        {
            step.Observe("IInclines.Add: " + HResult.Describe(ex));
        }

        if (incline is null)
        {
            step.Fail("IInclines.Add() не вернул IIncline - маршрут API7 недоступен.");
            return;
        }

        incline.Faces = new object[] { face7 };
        incline.Plane = plane7;
        incline.Angle = AngleDeg;
        incline.Direction = true;
        var updated = Api5.SafeBool(() => ((IModelObject)incline).Update());
        step.Data["update"] = updated;
        document.RebuildDocument();
        Report(step, part, updated, "API7 Direction=TRUE");
        CloseQuietly(document);
    }

    /// <summary>DR.6: the base is a flat FACE of the body, not a plane. DR-02 of the order asks for
    /// exactly that, while <c>SetPlane</c> is documented for a «плоский объект ksEntity».</summary>
    private void FaceBaseIncline(bool direction)
    {
        var step = _report.Begin(direction ? "DR.6" : "DR.6b",
            "Основание - нижняя грань тела, direction=" + (direction ? "TRUE" : "FALSE"),
            "Принимает ли SetPlane грань тела и в какую сторону она уклоняется?");
        var document = BuildBar("DR.2", "Брус 100×80×50 выдавливанием");
        if (document is null)
        {
            step.Fail("брус не построен.");
            return;
        }

        var part = (ksPart)document.GetPart(-1);
        var face = PlusXFace(part);
        var bottom = FaceHandles(part).FirstOrDefault(f => f.Normal is { } n
            && Math.Abs(n[0]) < 0.01 && Math.Abs(n[1]) < 0.01 && n[2] < -0.99);
        if (face is null || bottom is null)
        {
            step.Fail("грань +X или нижняя грань не найдены.");
            return;
        }

        if (part.NewEntity(InclineType) is not ksEntity entity
            || entity.GetDefinition() is not ksInclineDefinition incline
            || incline.FaceArray() is not { } faceArray)
        {
            step.Fail("Определение уклона или массив граней не получены.");
            return;
        }

        Late.Call(faceArray, "Add", face.Element);
        var accepted = Api5.SafeBool(() => incline.SetPlane(bottom.Element));
        step.Data["set_plane_face"] = accepted;
        incline.angle = AngleDeg;
        incline.direction = direction;
        var created = Api5.SafeBool(entity.Create);
        step.Data["create"] = created;
        document.RebuildDocument();

        if (accepted == true)
        {
            Report(step, part, created, "основание-грань, direction=" + (direction ? "TRUE" : "FALSE"));
        }
        else
        {
            step.Fail("SetPlane(грань тела) вернул " + Api5.Raw(accepted)
                + " - грань как основание этим маршрутом не принимается");
        }

        CloseQuietly(document);
    }

    /// <summary>DR.7/DR.8: «Уклон от базовой линии» — the only API7 family here. Base line is the top
    /// edge of the +X face (z = 50), direction is the XOY plane, side is the +X face.</summary>
    private void DraftFromEdges(bool reverse)
    {
        var id = reverse ? "DR.8" : "DR.7";
        var step = _report.Begin(id, "Уклон от базовой линии, reverse=" + (reverse ? "TRUE" : "FALSE"),
            "Создаётся ли семейство и какую пару объёмов даёт смена reverse?");
        var document = BuildBar("DR.2", "Брус 100×80×50 выдавливанием");
        if (document is null)
        {
            step.Fail("брус не построен.");
            return;
        }

        var part = (ksPart)document.GetPart(-1);
        var face = PlusXFace(part);
        if (face is null || part.GetDefaultEntity(PlaneXoy) is not ksEntity basePlane)
        {
            step.Fail("грань +X или плоскость XOY не получены.");
            return;
        }

        var topEdge = TopEdgeOf(face);
        step.Data["top_edge"] = topEdge?.Ends;
        if (topEdge is null)
        {
            step.Fail("верхнее ребро грани +X (z = 50) не найдено.");
            return;
        }

        if (Container(part) is not { } container)
        {
            step.Fail("деталь не отвечает QI(IModelContainer).");
            return;
        }

        var edge7 = ToApi7(topEdge.Element);
        var plane7 = ToApi7(basePlane);
        var face7 = ToApi7(face.Element);
        if (edge7 is null || plane7 is null || face7 is null)
        {
            step.Fail("ребро, плоскость или грань не перенесены в API7 как IModelObject.");
            return;
        }

        IDraftFromEdges? draft = null;
        try
        {
            draft = container.DraftsFromEdges?.Add() as IDraftFromEdges;
        }
        catch (Exception ex)
        {
            step.Observe("IDraftsFromEdges.Add: " + HResult.Describe(ex));
        }

        if (draft is null)
        {
            step.Fail("IDraftsFromEdges.Add() не вернул IDraftFromEdges - семейство недоступно.");
            return;
        }

        draft.BaseLine = new object[] { edge7 };
        draft.DirectionObject = plane7;
        draft.SideFace = face7;
        draft.Angle = AngleDeg;
        draft.Reverse = reverse;
        var updated = Api5.SafeBool(() => ((IModelObject)draft).Update());
        step.Data["update"] = updated;
        document.RebuildDocument();
        Report(step, part, updated, "базовая линия, reverse=" + (reverse ? "TRUE" : "FALSE"));
        CloseQuietly(document);
    }

    /// <summary>DR.9: the feature's own life - is it visible in the API5 feature tree, does it survive
    /// save → close → reopen, and are its parameters readable from the reopened file?</summary>
    /// <remarks>The tree walk is <c>EntityCollection(o3d_OperationElement)</c> by TYPE, not by name:
    /// the name is not an identifier (measured on the chamfer family). A feature the tree does not show
    /// cannot be read, suppressed or deleted by the existing tools, so this decides whether the adapter
    /// may register a reference to it at all.</remarks>
    private void Lifecycle()
    {
        var step = _report.Begin("DR.9", "Жизненный цикл: дерево API5, save → close → reopen",
            "Виден ли уклон в дереве и переживает ли он переоткрытие?");
        var document = BuildBar("DR.2", "Брус 100×80×50 выдавливанием");
        if (document is null)
        {
            step.Fail("брус не построен.");
            return;
        }

        var part = (ksPart)document.GetPart(-1);
        var face = PlusXFace(part);
        if (face is null || part.GetDefaultEntity(PlaneXoy) is not ksEntity basePlane)
        {
            step.Fail("грань +X или плоскость XOY не получены.");
            return;
        }

        if (part.NewEntity(InclineType) is not ksEntity entity
            || entity.GetDefinition() is not ksInclineDefinition incline
            || incline.FaceArray() is not { } faceArray)
        {
            step.Fail("Определение уклона или массив граней не получены.");
            return;
        }

        Late.Call(faceArray, "Add", face.Element);
        incline.SetPlane(basePlane);
        incline.angle = AngleDeg;
        incline.direction = true;
        if (Api5.SafeBool(entity.Create) != true)
        {
            step.Fail("Create() вернул false - жизненный цикл проверять не на чем.");
            return;
        }

        document.RebuildDocument();
        var volumeBuilt = Api5.Volume(part);
        step.Data["volume_built"] = volumeBuilt;
        step.Data["tree_before"] = TreeSummary(part);
        step.Observe("дерево после создания: " + step.Data["tree_before"]);

        var directory = Path.Combine(_options.WorkDir, "drafts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "draft-lifecycle.m3d");
        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true.");
            return;
        }

        document.close();
        var document3D = (ksDocument3D)_app.Document3D();
        if (Api5.SafeBool(() => document3D.Open(path)) != true)
        {
            step.Fail("переоткрытие " + path + " не удалось.");
            return;
        }

        // Open does not make the document active (measured in probe M): the active one is asked of the app.
        var reopened = Api5.SafeObject(() => _app.GetType().InvokeMember("ActiveDocument",
            System.Reflection.BindingFlags.GetProperty, null, _app, null)) as ksDocument3D ?? document3D;
        var reopenedPart = (ksPart)reopened.GetPart(-1);
        var volumeReopened = Api5.Volume(reopenedPart);
        var treeAfter = TreeSummary(reopenedPart);
        step.Data["volume_reopened"] = volumeReopened;
        step.Data["tree_after"] = treeAfter;
        step.Observe("дерево после переоткрытия: " + treeAfter);

        var found = FindIncline(reopenedPart);
        double? angleRead = null;
        bool? directionRead = null;
        if (found is not null && found.GetDefinition() is ksInclineDefinition reread)
        {
            angleRead = Api5.SafeDouble(() => reread.angle);
            directionRead = Api5.SafeBool(() => reread.direction);
        }

        step.Data["angle_read"] = angleRead;
        step.Data["direction_read"] = directionRead;
        step.Observe("перечитано из файла: angle=" + Api5.Num(angleRead)
            + " direction=" + Api5.Raw(directionRead));

        var volumeKept = volumeBuilt is double vb && volumeReopened is double vr
            && Math.Abs(vb - vr) <= VolumeTolerance;
        var angleKept = angleRead is double a && Math.Abs(a - AngleDeg) <= 1e-6;
        if (found is null)
        {
            step.Fail("признак type=42 не найден в дереве после переоткрытия: " + treeAfter);
        }
        else if (!volumeKept || !angleKept)
        {
            step.Fail("после переоткрытия объём " + Api5.Num(volumeReopened) + " (было "
                + Api5.Num(volumeBuilt) + "), угол " + Api5.Num(angleRead));
        }
        else
        {
            step.Pass("дерево видит type=42, после переоткрытия объём и угол те же");
        }

        CloseQuietly(reopened);
    }

    /// <summary>The last operation element of type <c>o3d_incline</c> in the API5 tree.</summary>
    private static ksEntity? FindIncline(ksPart part)
    {
        try
        {
            if (part.EntityCollection(Api5.OperationElement)
                is not ksEntityCollection collection)
            {
                return null;
            }

            ksEntity? found = null;
            for (var i = 0; i < (Api5.SafeInt(collection.GetCount) ?? 0); i++)
            {
                if (collection.GetByIndex(i) is ksEntity element && element.type == InclineType)
                {
                    found = element;
                }
            }

            return found;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Types of the operation elements in the tree, in walk order.</summary>
    private static string TreeSummary(ksPart part)
    {
        try
        {
            if (part.EntityCollection(Api5.OperationElement)
                is not ksEntityCollection collection)
            {
                return "<дерево недоступно>";
            }

            var types = new List<string>();
            for (var i = 0; i < (Api5.SafeInt(collection.GetCount) ?? 0); i++)
            {
                if (collection.GetByIndex(i) is ksEntity element)
                {
                    types.Add(element.type.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            return types.Count == 0 ? "<пусто>" : string.Join(", ", types);
        }
        catch (Exception)
        {
            return "<дерево не читается>";
        }
    }

    /// <summary>Volume after the mutation, the analytic pair it matches, and the verdict. The step
    /// records WHICH analytic volume came out, not what the help page promises.</summary>
    private void Report(ProbeStep step, ksPart part, bool? created, string label)
    {
        var volume = Api5.Volume(part);
        step.Data["volume_after"] = volume;
        step.Data["faces_after"] = Api5.FaceCount(part);
        step.Observe(label + ": объём " + Api5.Num(volume) + " мм³; эталоны 10°: убыль "
            + Api5.Num(VolumeInward) + ", прирост " + Api5.Num(VolumeOutward)
            + ", радианы " + Api5.Num(VolumeRadians));

        if (created != true)
        {
            step.Fail("Create/Update вернул " + Api5.Raw(created) + "; объём " + Api5.Num(volume));
        }
        else if (volume is double a && Math.Abs(a - VolumeInward) <= VolumeTolerance)
        {
            step.Data["material"] = "removed";
            step.Pass(label + " дал " + Api5.Num(volume) + " - материал УБЫЛ (градусы)");
        }
        else if (volume is double b && Math.Abs(b - VolumeOutward) <= VolumeTolerance)
        {
            step.Data["material"] = "added";
            step.Pass(label + " дал " + Api5.Num(volume) + " - материал ПРИБЫЛ (градусы)");
        }
        else
        {
            step.Fail("объём " + Api5.Num(volume) + " не совпал ни с одним эталоном 10° ("
                + Api5.Num(VolumeInward) + " / " + Api5.Num(VolumeOutward) + ", радианы "
                + Api5.Num(VolumeRadians) + ")");
        }
    }

    /// <summary>The acceptance reference bar: a 0..100 × 0..80 rectangle on XOY extruded up by 50.</summary>
    private ksDocument3D? BuildBar(string stepId, string title)
    {
        var step = _report.Begin(stepId, title, "V0 = 400000 мм³?");
        var document = (ksDocument3D)_app.Document3D();
        if (document.Create(true, true) != true)
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane
            || part.NewEntity(SketchType) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
        {
            step.Fail("Плоскость или эскиз не получены.");
            return null;
        }

        sketch.name = "dr-profile";
        sketchDefinition.SetPlane(plane);
        sketch.Create();
        if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
        {
            step.Fail("BeginEdit() вернул не ksDocument2D.");
            return null;
        }

        try
        {
            editor.ksLineSeg(0d, 0d, BarX, 0d, LineStyle);
            editor.ksLineSeg(BarX, 0d, BarX, BarY, LineStyle);
            editor.ksLineSeg(BarX, BarY, 0d, BarY, LineStyle);
            editor.ksLineSeg(0d, BarY, 0d, 0d, LineStyle);
        }
        finally
        {
            sketchDefinition.EndEdit();
        }

        if (part.NewEntity(BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Fail("Базовое выдавливание не создано.");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0 /* etBlind */, BarZ, 0d, false);
        if (Api5.SafeBool(extrusion.Create) != true)
        {
            step.Fail("Create() выдавливания вернул false.");
            return null;
        }

        document.RebuildDocument();
        var volume = Api5.Volume(part);
        step.Data["volume_mm3"] = volume;
        step.Data["faces"] = Api5.FaceCount(part);
        if (volume is double v && Math.Abs(v - VolumeV0) <= VolumeTolerance)
        {
            step.Pass("V0 = " + Api5.Num(volume) + " мм³, граней " + Api5.FaceCount(part));
            return document;
        }

        step.Fail("объём " + Api5.Num(volume) + " вместо 400000");
        return null;
    }

    private IModelContainer? Container(ksPart part) =>
        _app.TransferInterface(part, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelContainer;

    private IModelObject? ToApi7(object element) =>
        _app.TransferInterface(element, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IModelObject;

    private FaceHandle? PlusXFace(ksPart part) =>
        FaceHandles(part).FirstOrDefault(f => f.Normal is { } n
            && Math.Abs(n[0] - 1d) < 0.01 && Math.Abs(n[1]) < 0.01 && Math.Abs(n[2]) < 0.01);

    /// <summary>The edge of the +X face whose two vertices both sit at z = 50 - the top edge that the
    /// base line of a draft is taken from.</summary>
    private static EdgeHandle? TopEdgeOf(FaceHandle face)
    {
        try
        {
            if (face.Element is not ksFaceDefinition definition
                || definition.EdgeCollection() is not ksEdgeCollection edges)
            {
                return null;
            }

            for (var e = 0; e < (Api5.SafeInt(edges.GetCount) ?? 0); e++)
            {
                if (edges.GetByIndex(e) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                var ends = new List<double[]>();
                foreach (var flag in new[] { true, false })
                {
                    if (edge.GetVertex(flag) is ksVertexDefinition vertex
                        && vertex.GetPoint(out var x, out var y, out var z))
                    {
                        ends.Add([x, y, z]);
                    }
                }

                if (ends.Count == 2 && ends.All(p => Math.Abs(p[2] - BarZ) < 0.01))
                {
                    return new EdgeHandle(edge, ends);
                }
            }
        }
        catch (Exception)
        {
            // An edge collection that cannot be walked is reported by the caller as "not found".
        }

        return null;
    }

    private void CloseQuietly(ksDocument3D document)
    {
        try
        {
            document.close();
        }
        catch (Exception)
        {
            // A document that will not close is not what this probe measures.
        }
    }

    private sealed record FaceHandle(int Index, object Element, double[]? Normal);

    private sealed record EdgeHandle(ksEdgeDefinition Element, List<double[]> Ends);

    /// <summary>Live face elements with their plane normals: the normals come from
    /// <see cref="Api5.ReadFaces"/>, the elements from the same collection in the same order, because
    /// <c>FaceArray().Add</c> needs the live object and the reading keeps only a description.</summary>
    private static List<FaceHandle> FaceHandles(ksPart part)
    {
        var list = new List<FaceHandle>();
        try
        {
            var readings = Api5.ReadFaces(part, new ProbeStep { Id = "—", Title = "чтение граней" });
            if (part.GetMainBody() is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces)
            {
                return list;
            }

            var count = Api5.SafeInt(faces.GetCount) ?? 0;
            for (var i = 0; i < count && i < readings.Count; i++)
            {
                var element = faces.GetByIndex(i);
                if (element is not null)
                {
                    list.Add(new FaceHandle(i, element, readings[i].PlaneNormal));
                }
            }
        }
        catch (Exception)
        {
            // A face collection that cannot be walked is reported by the caller as "not found".
        }

        return list;
    }
}
