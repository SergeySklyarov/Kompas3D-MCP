using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Scenario E: a component array along a CURVE through the documented
/// <c>IModelContainer.FeaturePatterns.Add(o3d_curvePartArray=40)</c> → <c>IPathPattern</c> route.</summary>
/// <remarks>DOC: <c>copytype.html</c> (type 40 → <c>IPathPattern</c>), <c>ipathpattern_props.html</c>,
/// <c>ipathpattern_curves.html</c> (one curve is <c>VT_DISPATCH</c>, several are <c>VT_ARRAY|VT_DISPATCH</c>),
/// <c>ipathpattern_count.html</c>, <c>ipathpattern_bystep.html</c>, <c>ipathpattern_step.html</c>,
/// <c>ipathpattern_reversedirection.html</c>, <c>ipathpattern_saveinitialorientation.html</c>; the API5
/// sibling <c>kscurvepartarraydefinition_curvearray.html</c> types the trajectory as a <c>ksEntityCollection</c>,
/// and <c>obj3dtype.html</c> names the combined kind <c>o3d_curveElement=111</c> «Кривые (пространственные и
/// ребра)» — so a trajectory is a CURVE ENTITY or an EDGE, never a sketch.
/// LIMIT: no page names which object kinds the trajectory accepts, so the probe offers the candidates the
/// product could address and records the kernel's answer instead of guessing.
/// History: docs/decisions/assembly-ext.md#path-pattern</remarks>
internal sealed partial class AssemblyExtProbe
{
    /// <summary>Length of the fixture part along X, and of the trajectory the fixture offers.</summary>
    private const double PathLength = 100;

    /// <summary>What the fixture offers as the trajectory.</summary>
    private enum PathCurve
    {
        /// <summary>A sketch on XOY — the entity a part pattern is usually given.</summary>
        Sketch,

        /// <summary>The same sketch as a one-element <c>VT_ARRAY|VT_DISPATCH</c>.</summary>
        SketchArray,

        /// <summary>A long edge of the component's body, transferred to API7.</summary>
        ComponentEdge,

        /// <summary>An element of <c>ksPart.EntityCollection(o3d_curveElement=111)</c>.</summary>
        CurveEntity,
    }

    /// <summary>What one measured run gave: the component count read back and the instance origins.</summary>
    private sealed record PathOutcome(int? Count, List<string> Origins);

    private void PathPatternScenario()
    {
        var source = PartFile(Path.Combine(_work, "path-part.m3d"), PartH, PathLength);

        PathRun("path-sketch", source, PathCurve.Sketch, byStep: true, step: 30, count: 3);
        PathRun("path-sketch-array", source, PathCurve.SketchArray, byStep: true, step: 30, count: 3);
        PathRun("path-edge", source, PathCurve.ComponentEdge, byStep: true, step: 30, count: 3);
        PathRun("path-entity", source, PathCurve.CurveEntity, byStep: true, step: 30, count: 3);
        PathRun("path-edge-full", source, PathCurve.ComponentEdge, byStep: false, step: 0, count: 3);

        // The component is placed AWAY from the origin so the two frames differ: if the copies land at
        // placement + k·step the kernel applied the component matrix to the curve, and if they land at
        // k·step it did not. The same run tells whether the edge's own vertices are local or document.
        PathRun("path-edge-offset", source, PathCurve.ComponentEdge, byStep: true, step: 30, count: 3,
            Place(10, 20, 0));

        // The two remaining settings of the contract, each measured alone so a change in the instance set is
        // attributable to it.
        PathRun("path-edge-reverse", source, PathCurve.ComponentEdge, byStep: true, step: 30, count: 3,
            reverse: true);
        PathRun("path-edge-turn", source, PathCurve.ComponentEdge, byStep: true, step: 30, count: 3,
            saveInitialOrientation: false);
    }

    /// <summary>One measured run: a fresh assembly, one component, the offered trajectory, the pattern,
    /// then save and reopen.</summary>
    /// <remarks>INVARIANT: the verdict of the trajectory row is the READ-BACK, not the absence of a COM fault —
    /// MEASURED that writing a sketch to <c>Curves</c> raises nothing and reads back as null.</remarks>
    private PathOutcome PathRun(
        string name, string partPath, PathCurve kind, bool byStep, double step, int count,
        double[]? placement = null, bool reverse = false, bool saveInitialOrientation = true)
    {
        var (doc5, _, top) = NewDocument(isPart: false);
        var assemblyPath = Path.Combine(_work, name + ".m3d");
        var component = Insert(top, partPath, placement);
        doc5.RebuildDocument();
        var before = Part5Count();
        Record(name + ".components_before", "компонентов до массива", before.ToString(),
            before == 1 ? "PASS" : "FAIL", "1");

        var (curve, description) = BuildCurve(kind);
        if (curve is null)
        {
            Record(name + ".curve_build", "траектория построена", description, "FAIL");
            return new PathOutcome(null, []);
        }

        Record(name + ".curve_build", "траектория построена", description, "PASS");

        if (top is not IModelContainer container || container.FeaturePatterns is not { } patterns)
        {
            Record(name + ".factory", "IModelContainer.FeaturePatterns доступен", "недоступен", "FAIL");
            return new PathOutcome(null, []);
        }

        IFeaturePattern? created;
        try
        {
            created = patterns.Add(ksObj3dTypeEnum.o3d_curvePartArray);
        }
        catch (COMException ex)
        {
            Record(name + ".add", "FeaturePatterns.Add(o3d_curvePartArray=40)",
                $"COMException: {ex.Message}", "FAIL");
            return new PathOutcome(null, []);
        }

        if (created is not IPathPattern path)
        {
            Record(name + ".add", "FeaturePatterns.Add(o3d_curvePartArray=40) отвечает QI(IPathPattern)",
                created is null ? "null" : $"объект {created.GetType().Name} интерфейс не подтверждает",
                "FAIL", "IPathPattern, ModelObjectType = o3d_curvePartArray (40)");
            return new PathOutcome(null, []);
        }

        Record(name + ".add", "FeaturePatterns.Add(o3d_curvePartArray=40) отвечает QI(IPathPattern)",
            $"ModelObjectType = {path.ModelObjectType} ({(int)path.ModelObjectType})", "PASS",
            "IPathPattern, ModelObjectType = o3d_curvePartArray (40)");

        string? writeFailure = null;
        try
        {
            path.Curves = curve;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            writeFailure = ex.GetType().Name + ": " + ex.Message;
        }

        var afterWrite = DescribeCurve(SafeCurve(path));
        Record(name + ".curves_write", "IPathPattern.Curves = <траектория>; чтение сразу после записи",
            writeFailure ?? afterWrite, writeFailure is null && CurveStored(kind, afterWrite) ? "PASS" : "FAIL",
            "принято и читается обратно");
        if (writeFailure is not null)
        {
            return new PathOutcome(null, []);
        }

        var componentModel = component as IModelObject;
        var added = componentModel is not null && SafeBool(() => path.AddInitialObjects(componentModel));
        Record(name + ".sources", "AddInitialObjects(компонент) принял исходный объект",
            $"{added}", added ? "PASS" : "FAIL", "true");

        path.Count = count;
        path.ByStep = byStep;
        path.Step = byStep ? step : 0;
        path.ReverseDirection = reverse;
        path.SaveInitialOrientation = saveInitialOrientation;
        var updated = SafeBool(path.Update);
        doc5.RebuildDocument();
        var valid = SafeBool(() => path.Valid);
        Record(name + ".update", "Update() построил массив; Valid подтверждает",
            $"Update()={updated}, Valid={valid}", valid ? "PASS" : "FAIL", "Valid = true");
        Record(name + ".curves_after_update", "Curves, прочитанные после Update()",
            DescribeCurve(SafeCurve(path)), "измерено", "траектория читается");

        var countsRead = path.GetExemplarsCounts(out var count1, out var count2);
        Record(name + ".exemplars_counts", "GetExemplarsCounts: экземпляров по двум направлениям",
            $"прочитано={countsRead}, Count1={count1}, Count2={count2}",
            countsRead && count1 == count && count2 == 1 ? "PASS" : "FAIL", $"{count} и 1");
        Record(name + ".params_read_back", "ByStep и шаг, прочитанные из признака",
            $"ByStep={SafeBool(() => path.ByStep)}, Step={SafeDouble(() => path.Step)}", "измерено",
            byStep ? $"ByStep=true, Step={step}" : "ByStep=false, шаг вычислен моделью");

        var after = Part5Count();
        var origins = ComponentOrigins(top);
        Record(name + ".components_after", "компонентов после массива", after.ToString(),
            after == count ? "PASS" : "FAIL", count.ToString());
        Record(name + ".origins", "начала компонентов после массива",
            string.Join("; ", origins), "измерено", "начала лежат на траектории");

        // The same numbers must survive the file: an array that lives only in memory is not one a client
        // can rely on.
        var saved = doc5.SaveAs(assemblyPath);
        Record(name + ".save", "SaveAs сборки с массивом вдоль кривой", $"{saved}",
            saved ? "PASS" : "FAIL", "true");
        CloseCurrent();
        if (!Reopen(assemblyPath, out _) || _doc7?.TopPart is not IPart7 reopenTop)
        {
            Record(name + ".reopen", "сборка с массивом открывается повторно", "не открылась", "FAIL");
            return new PathOutcome(after, origins);
        }

        var reopenOrigins = Api7Origins(reopenTop);
        Record(name + ".reopen.components", "экземпляров после переоткрытия (IPart7.Parts)",
            reopenOrigins.Count.ToString(), reopenOrigins.Count == count ? "PASS" : "FAIL", count.ToString());
        Record(name + ".reopen.origins", "начала экземпляров после переоткрытия",
            string.Join("; ", reopenOrigins), "измерено", "те же начала");
        var reopenPatterns = Api7PatternCount(reopenTop);
        Record(name + ".reopen.patterns", "массив читается как признак после переоткрытия",
            $"{reopenPatterns}", reopenPatterns == 1 ? "PASS" : "FAIL", "1");

        Fact(name, assemblyPath);
        return new PathOutcome(after, origins);
    }

    /// <summary>Builds the offered trajectory and describes it for the report.</summary>
    private (object? Curve, string Description) BuildCurve(PathCurve kind)
    {
        switch (kind)
        {
            case PathCurve.Sketch:
            {
                var sketch = SketchLine(PathLength);
                return (sketch, sketch is null ? "не построен" : $"эскиз-линия 0…{PathLength} на XOY сборки");
            }

            case PathCurve.SketchArray:
            {
                var sketch = SketchLine(PathLength);
                return (sketch is null ? null : new object[] { sketch },
                    sketch is null ? "не построен" : $"тот же эскиз как массив из одного элемента");
            }

            case PathCurve.ComponentEdge:
                return ComponentEdge();

            case PathCurve.CurveEntity:
                return CurveEntity();

            default:
                return (null, "вид траектории не назван");
        }
    }

    /// <summary>A long edge of the component's body: <c>ksPart.BodyCollection()</c> →
    /// <c>ksBody.FaceCollection()</c> → <c>ksFaceDefinition.EdgeCollection()</c>, transferred to API7.</summary>
    /// <remarks>DOC: <c>kspart_bodycollection.html</c>, <c>ksbody_facecollection.html</c>,
    /// <c>ksface_edgecollection.html</c>. MEASURED: the transfer answer is what is offered — an edge that does
    /// not transfer is named, not replaced by a neighbour.</remarks>
    private (object? Curve, string Description) ComponentEdge()
    {
        try
        {
            if (Part5At(0) is not { } component || component.BodyCollection() is not ksBodyCollection bodies
                || bodies.GetCount() == 0 || bodies.GetByIndex(0) is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces)
            {
                return (null, "тело компонента не прочитано");
            }

            for (var f = 0; f < faces.GetCount(); f++)
            {
                if (faces.GetByIndex(f) is not ksFaceDefinition face
                    || face.EdgeCollection() is not ksEdgeCollection edges)
                {
                    continue;
                }

                for (var e = 0; e < edges.GetCount(); e++)
                {
                    if (edges.GetByIndex(e) is not ksEdgeDefinition edge
                        || EdgeLength(edge) is not double length || Math.Abs(length - PathLength) > 1e-6)
                    {
                        continue;
                    }

                    var transferred = _app5?.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
                    return (transferred ?? edge,
                        $"ребро компонента длиной {length:0.###} мм "
                        + $"{Point3Value(VertexPoint(edge, true))}…{Point3Value(VertexPoint(edge, false))}, "
                        + "перенос в API7: "
                        + (transferred is null ? "не удался, подан объект API5" : "удался"));
                }
            }

            return (null, "ребра нужной длины у компонента не найдено");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "тело компонента не прочитано: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>An element of <c>ksPart.EntityCollection(o3d_curveElement=111)</c> — the documented combined
    /// kind «Кривые (пространственные и ребра)».</summary>
    private (object? Curve, string Description) CurveEntity()
    {
        try
        {
            if (_doc5?.GetPart(-1) is not ksPart part
                || part.EntityCollection((short)KompasObjectTypesOfCurveElement) is not ksEntityCollection collection)
            {
                return (null, "EntityCollection(o3d_curveElement=111) не получена");
            }

            var count = collection.GetCount();
            if (count == 0)
            {
                return (null, "EntityCollection(o3d_curveElement=111) пуста");
            }

            var element = collection.GetByIndex(0);
            return (element, $"элемент 0 из EntityCollection(111), всего {count}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return (null, "EntityCollection(111) прервалась: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The combined type number <c>o3d_curveElement</c>, «Кривые (пространственные и ребра)».</summary>
    private const short KompasObjectTypesOfCurveElement = 111;

    /// <summary>Length of the straight span between an edge's two vertices. A box has only straight edges, so
    /// the fixture's edges are compared by this length; an edge whose vertices do not read gives null.</summary>
    private static double? EdgeLength(ksEdgeDefinition edge)
    {
        try
        {
            var start = VertexPoint(edge, true);
            var end = VertexPoint(edge, false);
            if (start is null || end is null)
            {
                return null;
            }

            var dx = end[0] - start[0];
            var dy = end[1] - start[1];
            var dz = end[2] - start[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>An edge vertex through <c>ksEdgeDefinition.GetVertex(start)</c> →
    /// <c>ksVertexDefinition.GetPoint</c>; both are documented API5 members.</summary>
    private static double[]? VertexPoint(ksEdgeDefinition edge, bool start)
    {
        try
        {
            return edge.GetVertex(start) is ksVertexDefinition vertex
                && vertex.GetPoint(out var x, out var y, out var z)
                    ? [x, y, z]
                    : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMethodException)
        {
            return null;
        }
    }

    /// <summary>A straight sketch on XOY of the assembly's own part — the trajectory a part pattern is
    /// usually given. DOC: <c>kspart_newentity.html</c> (<c>o3d_sketch = 5</c>),
    /// <c>kssketchdefinition_setplane.html</c>, <c>kssketchdefinition_beginedit.html</c>.</summary>
    private ksEntity? SketchLine(double length)
    {
        try
        {
            if (_doc5?.GetPart(-1) is not ksPart part || part.GetDefaultEntity(1) is not ksEntity plane)
            {
                return null;
            }

            if (part.NewEntity(5) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                return null;
            }

            definition.SetPlane(plane);
            sketch.Create();
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                return null;
            }

            editor.ksLineSeg(0, 0, length, 0, 1);
            definition.EndEdit();
            part.Update();
            _doc5.RebuildDocument();
            return sketch;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static object? SafeCurve(IPathPattern path)
    {        try
        {
            return path.Curves;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Whether the read-back means the write was STORED. An array must come back as an array; a
    /// single curve must come back as a dispatch and not as null.</summary>
    private static bool CurveStored(PathCurve kind, string readBack) =>
        kind == PathCurve.SketchArray ? readBack.StartsWith("VT_ARRAY", StringComparison.Ordinal) : readBack != "null";

    private static string DescribeCurve(object? curve) => curve switch
    {
        null => "null",
        Array array => $"VT_ARRAY|VT_DISPATCH из {array.Length} элементов",
        _ => curve.GetType().Name,
    };

    /// <summary>A point as the report spells it, invariant culture.</summary>
    private static string Point3Value(double[]? point) => point is null
        ? "не читается"
        : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"({point[0]:0.###}, {point[1]:0.###}, {point[2]:0.###})");

    private static int? Api7PatternCount(IPart7 top)
    {
        try
        {
            return (top as IModelContainer)?.FeaturePatterns?.Count;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> read)
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
}
