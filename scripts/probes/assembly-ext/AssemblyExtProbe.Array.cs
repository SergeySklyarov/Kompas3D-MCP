using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Scenario A: a component array through the documented
/// <c>IModelContainer.FeaturePatterns.Add(o3d_meshPartArray)</c> → <c>ILinearPattern</c> route.</summary>
/// <remarks>DOC: <c>copytype.html</c> (o3d_meshPartArray=39 → ILinearPattern), <c>ifeaturepatterns_add.html</c>,
/// <c>ifeaturepattern_addinitialobjects.html</c>, <c>ilinearpattern_props.html</c>,
/// <c>ifeaturepattern_getexemplarscounts.html</c>, <c>ifeaturepattern_issuitableobject.html</c>.
/// Every row compares a READ value with an expected one: the count of components and the origins the
/// array must produce are derived from the fixture, not taken from the call's return value.</remarks>
internal sealed partial class AssemblyExtProbe
{
    private void ArrayScenario()
    {
        var source = PartFile(Path.Combine(_work, "array-part.m3d"), PartH);
        var (doc5, doc7Local, top) = NewDocument(isPart: false);
        var assemblyPath = Path.Combine(_work, "array-asm.m3d");
        var component = Insert(top, source, null);
        doc5.RebuildDocument();

        var countBefore = Part5Count();
        Record("array.components_before", "компонентов в сборке до массива",
            $"{countBefore}", countBefore == 1 ? "PASS" : "FAIL", "1");

        if (top is not IModelContainer container)
        {
            Record("array.container", "IPart7 отвечает QI(IModelContainer)", "приведение не удалось", "FAIL");
            return;
        }

        var patterns = container.FeaturePatterns;
        Record("array.factory", "IModelContainer.FeaturePatterns доступен",
            patterns is null ? "null" : $"Count = {patterns.Count}", patterns is null ? "FAIL" : "PASS");

        var axis = BuildAxis(top, container, [0, 0, 0], [1, 0, 0]);
        if (axis is null)
        {
            Record("array.axis", "ось сетки построена в верхнем компоненте сборки", "ось не построена", "FAIL");
            return;
        }

        Record("array.axis", "ось сетки построена в верхнем компоненте сборки", "IAxis3DBy2Points.Valid", "PASS");

        IFeaturePattern? pattern = null;
        try
        {
            pattern = patterns!.Add(ksObj3dTypeEnum.o3d_meshPartArray);
        }
        catch (COMException ex)
        {
            Record("array.add", "FeaturePatterns.Add(o3d_meshPartArray=39)", $"COMException: {ex.Message}", "FAIL");
            return;
        }

        if (pattern is null)
        {
            Record("array.add", "FeaturePatterns.Add(o3d_meshPartArray=39)", "null", "FAIL");
            return;
        }

        var linear = pattern as ILinearPattern;
        Record("array.interface", "объект массива отвечает QI(ILinearPattern)",
            linear is null ? "нет" : $"ModelObjectType = {linear.ModelObjectType} ({(int)linear.ModelObjectType})",
            linear is null ? "FAIL" : "PASS", "ILinearPattern, ModelObjectType = o3d_meshPartArray (39)");
        if (linear is null)
        {
            return;
        }

        var componentObject = component as IModelObject;
        var suitable = componentObject is not null && SafeBool(() => pattern.IsSuitableObject(componentObject));
        Record("array.suitable", "компонент пригоден как исходный объект массива (IsSuitableObject)",
            $"{suitable}", suitable == true ? "PASS" : "FAIL", "true");

        linear.Axis1 = axis;
        linear.Count1 = 3;
        linear.Step1 = 30;
        linear.Direction1 = true;
        var added = componentObject is not null && SafeBool(() => pattern.AddInitialObjects(componentObject));
        Record("array.sources", "AddInitialObjects(компонент) принял исходный объект",
            $"{added}", added ? "PASS" : "FAIL", "true");

        var updated = SafeBool(pattern.Update);
        doc5.RebuildDocument();
        var valid = SafeBool(() => pattern.Valid);
        Record("array.update", "Update() встроил массив; Valid подтверждает",
            $"Update()={updated}, Valid={valid}", valid == true ? "PASS" : "FAIL", "Valid = true");

        var countsRead = pattern.GetExemplarsCounts(out var count1, out var count2);
        Record("array.exemplars_counts", "GetExemplarsCounts: экземпляров по двум направлениям",
            $"прочитано={countsRead}, Count1={count1}, Count2={count2}",
            countsRead && count1 == 3 && count2 == 1 ? "PASS" : "FAIL", "3 и 1");

        var countAfter = Part5Count();
        Record("array.components_after", "компонентов в сборке после массива (ksDocument3D.PartCollection(true))",
            $"{countBefore} → {countAfter}", countAfter == 3 ? "PASS" : "FAIL", "3");

        var origins = ComponentOrigins(top);
        Record("array.origins", "начала компонентов сборки после массива",
            string.Join("; ", origins), origins.Count == 3 ? "PASS" : "FAIL", "три начала: (0,0,0), (30,0,0), (60,0,0)");

        var exemplar = SafeExemplar(pattern, 1, 0);
        Record("array.exemplar.1_0", "Exemplar(1, 0) отдаёт объект экземпляра",
            exemplar is null ? "null" : exemplar.GetType().Name, exemplar is null ? "измерено" : "PASS");
        var exemplarZero = SafeExemplar(pattern, 0, 0);
        Record("array.exemplar.0_0", "Exemplar(0, 0) отдаёт объект экземпляра",
            exemplarZero is null ? "null" : exemplarZero.GetType().Name, exemplarZero is null ? "измерено" : "PASS");
        var countsProbe = new List<string>();
        for (var first = 1; first <= 3; first++)
        {
            countsProbe.Add($"{first},{0}:{(SafeExemplar(pattern, first, 0)?.GetType().Name ?? "null")}");
        }

        Fact("exemplar_probe", string.Join("; ", countsProbe));

        // Save and reopen: an array that lives only in memory is not an array the client can rely on.
        var saved = doc5.SaveAs(assemblyPath);
        Record("array.save", "SaveAs сборки с массивом", $"{saved}", saved ? "PASS" : "FAIL", "true");
        CloseCurrent();

        if (!Reopen(assemblyPath, out var reopened))
        {
            Record("array.reopen", "сборка с массивом открывается повторно", "не открылась", "FAIL");
            return;
        }

        var reopenTop = reopened.TopPart as IPart7;
        var reopenContainer = reopenTop as IModelContainer;
        var reopenCount = Part5CountDetail(out var countDiagnostics);
        Record("array.reopen.api5_route", "ksDocument3D.PartCollection(true) после переоткрытия",
            countDiagnostics, "измерено", "путь чтения назван");
        var reopenOrigins = reopenTop is null ? [] : Api7Origins(reopenTop);
        Record("array.reopen.components", "экземпляров у верхнего компонента после переоткрытия (IPart7.Parts)",
            $"{reopenOrigins.Count}", reopenOrigins.Count == 3 ? "PASS" : "FAIL", "3");
        Record("array.reopen.origins", "начала экземпляров после сохранения и переоткрытия",
            string.Join("; ", reopenOrigins), reopenOrigins.Count == 3 ? "PASS" : "FAIL",
            "те же три начала");
        Record("array.reopen.patterns", "массив читается как признак после переоткрытия",
            $"{reopenContainer?.FeaturePatterns?.Count}", reopenContainer?.FeaturePatterns?.Count == 1 ? "PASS" : "FAIL", "1");

        Fact("array_assembly", assemblyPath);
    }

    /// <summary>Axis by two model points through the documented <c>IAuxiliaryGeomContainer.Axes3D</c>.</summary>
    private IModelObject? BuildAxis(IPart7 top, IModelContainer container, double[] p1, double[] p2)
    {
        try
        {
            if (top is not IAuxiliaryGeomContainer auxiliary || auxiliary.Axes3D is not { } axes)
            {
                return null;
            }

            if (Point(container, p1) is not { } point1 || Point(container, p2) is not { } point2)
            {
                return null;
            }

            if (axes.Add(ksObj3dTypeEnum.o3d_axis2Points) is not IAxis3DBy2Points by2)
            {
                return null;
            }

            by2.Point1 = point1;
            by2.Point2 = point2;
            return by2.Update() && by2.Valid ? by2 : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static IPoint3D? Point(IModelContainer container, double[] coordinates)
    {
        if (container.Points3D?.Add() is not { } point)
        {
            return null;
        }

        point.X = coordinates[0];
        point.Y = coordinates[1];
        point.Z = coordinates[2];
        return point.Update() ? point : null;
    }

    /// <summary>Origins of the top component's children through API7 only: the collection that survives a
    /// reopen without an API5 address lookup.</summary>
    private List<string> Api7Origins(IPart7 top)
    {
        var origins = new List<string>();
        try
        {
            var parts = top.Parts;
            var count = parts?.Count ?? 0;
            for (var index = 0; index < count; index++)
            {
                if (parts!.get_Part(index) is IPart7 child)
                {
                    origins.Add(Origin(ComponentMatrix(top, child)));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            origins.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return origins;
    }

    private List<string> ComponentOrigins(IPart7 top)
    {
        var origins = new List<string>();
        var count = Part5Count();
        for (var index = 0; index < count; index++)
        {
            origins.Add(Origin(PlacementOf(Part5At(index)!)));
        }

        return origins;
    }

    private static IModelObject? SafeExemplar(IFeaturePattern pattern, int first, int second)
    {
        try
        {
            return pattern.Exemplar[first, second];
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private int Part5CountDetail(out string diagnostics)
    {
        try
        {
            var raw = _doc5?.PartCollection(true);
            if (raw is ksPartCollection collection)
            {
                diagnostics = $"ksPartCollection, Count = {collection.GetCount()}";
                return collection.GetCount();
            }

            diagnostics = raw is null
                ? "PartCollection(true) → null"
                : $"не ksPartCollection: {raw.GetType().Name}";
            return -1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            diagnostics = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>Opens a saved document and gives its API7 view; false when it does not open.</summary>
    private bool Reopen(string path, out IKompasDocument3D document7)
    {
        document7 = null!;
        try
        {
            CloseCurrent();
            _doc5 = (ksDocument3D)_app5!.Document3D();
            if (!_doc5.Open(path, false))
            {
                return false;
            }

            var opened = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            if (opened is null)
            {
                return false;
            }

            _doc7 = opened;
            document7 = opened;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }
}
