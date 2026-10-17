using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe SR — the section-view route: does a cut line plus a <c>vt_Section</c> view really build
/// an associative section on this installation, and in which coordinate system does its trace live?</summary>
/// <remarks>
/// DOC: <c>isymbols2dcontainer_cutlines.html</c> (the cut-line container is taken from the view, as the
/// dimension container is), <c>icutlines_add.html</c>, <c>icutline_points.html</c> («x0, y0, ... xn, yn»),
/// <c>icutline_arrowpos.html</c>, <c>iviews_add.html</c> (<c>vt_Section = 7</c>),
/// <c>iassociationview_baseobject.html</c> («Вид разрез/сечение - требуется объект Линия разреза»),
/// <c>iassociationview_section.html</c>, <c>iassociationview_getprojectionpoint.html</c>,
/// <c>iviewdesignation_designation.html</c>, <c>iassociationview.html</c> (<c>HatchParam</c>).
/// INVARIANT: every answer is RE-READ from the object after the write; a boolean returned by
/// <c>Update()</c> is recorded as a check, never as the verdict (docs/decisions/drawings.md#view-edit).
/// LIMIT: the probe reads the picture through object counts, designation and hatch parameters; the DXF
/// contour check belongs to the acceptance group, which runs the product route.
/// History: docs/decisions/drawings.md#section-route
/// </remarks>
internal sealed class SectionRouteProbe
{
    private const string ModelRelative =
        "docs/acceptance/evidence/20261015-hole-false-success-ca9d444/defect/Stress_Plate_Holes_SideFailure.m3d";

    private const double PlateT = 32d;

    /// <summary>Trace of the order §0 in MODEL coordinates on the top face (Z = 32), stepped.</summary>
    private static readonly double[,] SteppedTrace =
    {
        { -10d, 30d }, { 45d, 30d }, { 45d, 60d }, { 75d, 60d }, { 75d, 50d }, { 130d, 50d },
    };

    private readonly ProbeReport _report;
    private readonly Options _options;

    private KompasObject _app = null!;
    private ksDocument2D _drawing5 = null!;
    private IApplication _application7 = null!;
    private IViews _views = null!;
    private IView? _baseView;
    private string _modelPath = string.Empty;

    public SectionRouteProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public void Run()
    {
        Launch();
        try
        {
            OpenModelAndDrawing();
            PickBaseView();
            MeasureProjectionSystem();
            CreateCutLineAndSection();
            SteppedSection();
            FailureStates();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("SR.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        var clock = Stopwatch.StartNew();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        clock.Stop();
        step.Observe("экземпляр API5 создан за " + clock.ElapsedMilliseconds + " мс");
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("SR.9", "Завершение сеанса", "Остался ли процесс КОМПАС после пробы?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        var left = Process.GetProcessesByName("KOMPAS").Length;
        step.Observe("процессов KOMPAS после Quit(): " + left);
        if (left == 0)
        {
            step.Pass("сеанс завершён");
        }
        else
        {
            step.Unknown("процесс остался — разбирать отдельно");
        }
    }

    // ═══════════════════════════════════════════════════════════ документы ══

    private void OpenModelAndDrawing()
    {
        var step = _report.Begin("SR.1", "Модель и чертёж со стандартными видами",
            "Открывается ли копия плиты и появляются ли ассоциативные виды по файлу модели?");

        var source = Path.Combine(_options.ProjectRoot, ModelRelative);
        if (!File.Exists(source))
        {
            step.Fail("исходной модели нет: " + source);
            throw new InvalidOperationException("нет модели");
        }

        var directory = Path.Combine(_options.WorkDir, "section-route");
        Directory.CreateDirectory(directory);
        _modelPath = Path.Combine(directory, "Stress_Plate_Holes_SideFailure.m3d");
        File.Copy(source, _modelPath, overwrite: true);
        step.Observe("копия модели: " + _modelPath);

        var model = (ksDocument3D)_app.Document3D();
        var opened = model.Open(_modelPath, false);
        step.Observe("Open(модель) = " + opened);
        if (!opened)
        {
            step.Fail("модель не открылась");
            throw new InvalidOperationException("модель не открылась");
        }

        var param = _app.GetParamStruct(35) as ksDocumentParam;
        if (param is null)
        {
            step.Fail("GetParamStruct(ko_DocumentParam=35) вернул null");
            throw new InvalidOperationException("нет параметров документа");
        }

        param.Init();
        param.type = 1;
        param.regime = 1;
        _drawing5 = (ksDocument2D)_app.Document2D();
        var created = _drawing5.ksCreateDocument(param);
        step.Observe("ksCreateDocument(чертёж) = " + created);
        if (!created)
        {
            step.Fail("чертёж не создан");
            throw new InvalidOperationException("чертёж не создан");
        }

        _application7 = (_app.ksGetApplication7() as IApplication)!;
        if (_application7 is null)
        {
            step.Fail("ksGetApplication7() не дал IApplication");
            throw new InvalidOperationException("нет API7");
        }

        var document = ActiveDocument();
        if (document is not IKompasDocument2D document2D)
        {
            step.Fail("активный документ не IKompasDocument2D: " + Api5.RuntimeName(document));
            throw new InvalidOperationException("нет 2D-документа");
        }

        _views = (document2D.ViewsAndLayersManager as IViewsAndLayersManager)?.Views as IViews
            ?? throw new InvalidOperationException("нет коллекции видов");

        // Projections: front + top + left + isometric, so a view whose GetProjectionPoint collapses Z
        // exists whichever projection the kernel names it by.
        object[] types = { 1, 3, 5, 7 };
        var accepted = _views.AddStandartViews(_modelPath, "Спереди", types, 100d, 150d, 1d, 30d, 30d);
        step.Observe("AddStandartViews = " + accepted + ", видов в коллекции: " + SafeInt(() => _views.Count));

        if (!accepted || SafeInt(() => _views.Count) is null or 0)
        {
            step.Fail("стандартные виды не построены");
            throw new InvalidOperationException("нет видов");
        }

        foreach (var row in ViewRows())
        {
            step.Observe("вид: " + row);
        }

        step.Pass("виды построены: " + SafeInt(() => _views.Count));
    }

    private object? ActiveDocument()
    {
        try
        {
            return _application7.ActiveDocument;
        }
        catch (Exception ex)
        {
            return Late.Get(_application7, "ActiveDocument") ?? throw new InvalidOperationException(
                "ActiveDocument недоступен: " + ex.GetType().Name);
        }
    }

    private List<string> ViewRows()
    {
        var rows = new List<string>();
        var count = SafeInt(() => _views.Count) ?? 0;
        for (var index = 0; index < count; index++)
        {
            IView? view = null;
            try
            {
                view = _views.get_View(index) as IView;
            }
            catch (Exception ex)
            {
                rows.Add("View(" + index + ") бросил: " + ex.GetType().Name);
                continue;
            }

            if (view is null)
            {
                rows.Add("View(" + index + ") = null");
                continue;
            }

            var association = view as IAssociationView;
            rows.Add(string.Format(
                CultureInfo.InvariantCulture,
                "#{0} type={1} scale={2} x={3} y={4} objects={5} projection={6} source={7}",
                index,
                Api5.Raw(SafeObject(() => view.ViewType)),
                Api5.Raw(SafeObject(() => view.Scale)),
                Api5.Raw(SafeObject(() => view.X)),
                Api5.Raw(SafeObject(() => view.Y)),
                Api5.Raw(SafeObject(() => view.ObjectCount)),
                association is null ? "нет" : Api5.Raw(SafeObject(() => association.ProjectionName)),
                association is null ? "нет" : Api5.Raw(SafeObject(() => association.SourceFileName))));
        }

        return rows;
    }

    // ═════════════════════════════════════════════════════ опорный вид ══

    private void PickBaseView()
    {
        var step = _report.Begin("SR.2", "Опорный вид по измерению",
            "Какой вид вырождает ось Z модели (отверстия видны окружностями)? Выбирается по "
            + "GetProjectionPoint, а не по имени проекции.");

        var count = SafeInt(() => _views.Count) ?? 0;
        var best = -1;
        var bestDelta = double.MaxValue;
        for (var index = 0; index < count; index++)
        {
            var view = _views.get_View(index) as IView;
            if (view is not IAssociationView association)
            {
                step.Observe("#" + index + ": не ассоциативный вид");
                continue;
            }

            var delta = ProjectionDelta(association);
            step.Observe("#" + index + ": смещение точки (0,0," + PlateT + ") относительно (0,0,0) на виде = "
                + Api5.Num(delta));
            if (delta is double value && Math.Abs(value) < bestDelta)
            {
                best = index;
                bestDelta = Math.Abs(value);
                _baseView = view;
            }
        }

        if (_baseView is null)
        {
            step.Fail("ни один вид не отдал GetProjectionPoint");
            return;
        }

        step.Observe("опорный вид: #" + best + ", |Δ| = " + Api5.Num(bestDelta));
        step.Pass("опорный вид выбран измерением");
    }

    /// <summary>Distance between the projections of (0,0,0) and (0,0,PlateT): zero on the view whose
    /// projection collapses Z.</summary>
    private static double? ProjectionDelta(IAssociationView view)
    {
        try
        {
            view.GetProjectionPoint(0d, 0d, 0d, out var x0, out var y0);
            view.GetProjectionPoint(0d, 0d, PlateT, out var x1, out var y1);
            return Math.Sqrt(((x1 - x0) * (x1 - x0)) + ((y1 - y0) * (y1 - y0)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════ система координат ══

    private void MeasureProjectionSystem()
    {
        var step = _report.Begin("SR.3", "Система координат трассы",
            "В какой системе лежат ICutLine.Points и совпадают ли они с GetProjectionPoint при масштабе 1:1 и 1:2?");

        if (_baseView is not IAssociationView association)
        {
            step.Fail("опорного вида нет");
            return;
        }

        foreach (var scale in new[] { 1d, 0.5d })
        {
            _baseView.Scale = scale;
            ((IDrawingObject)_baseView).Update();
            step.Observe("масштаб вида выставлен: " + Api5.Raw(SafeObject(() => _baseView.Scale)));

            foreach (var point in new[] { (0d, 0d), (45d, 30d), (75d, 50d) })
            {
                try
                {
                    association.GetProjectionPoint(point.Item1, point.Item2, PlateT, out var xv, out var yv);
                    step.Observe(string.Format(
                        CultureInfo.InvariantCulture,
                        "scale={0} модель ({1}, {2}) -> вид ({3}, {4})",
                        Api5.Num(scale), Api5.Num(point.Item1), Api5.Num(point.Item2),
                        Api5.Num(xv), Api5.Num(yv)));
                }
                catch (Exception ex)
                {
                    step.Observe("GetProjectionPoint бросил: " + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        step.Pass("проекция прочитана в двух масштабах");
    }

    // ═══════════════════════════════════════ линия разреза и вид-разрез ══

    private void CreateCutLineAndSection()
    {
        var step = _report.Begin("SR.4", "Линия разреза и вид-разрез",
            "Строится ли разрез по линии через документированные члены и какое значение Section даёт разрез?");

        if (_baseView is not IAssociationView association)
        {
            step.Fail("опорного вида нет");
            return;
        }

        _baseView.Scale = 1d;
        ((IDrawingObject)_baseView).Update();

        var trace = new List<double>();
        for (var index = 0; index < SteppedTrace.GetLength(0); index++)
        {
            association.GetProjectionPoint(
                SteppedTrace[index, 0], SteppedTrace[index, 1], PlateT, out var xv, out var yv);
            trace.Add(xv);
            trace.Add(yv);
        }

        step.Observe("трасса в координатах вида: " + string.Join(", ", trace.Select(v => Api5.Num(v))));

        object? container;
        try
        {
            container = Late.Get(_baseView, "CutLines");
        }
        catch (Exception ex)
        {
            step.Fail("ISymbols2DContainer.CutLines недоступен: " + ex.GetType().Name + " " + ex.Message);
            return;
        }

        step.Observe("контейнер линий разреза: " + Api5.RuntimeName(container));
        if (container is null)
        {
            step.Fail("CutLines = null");
            return;
        }

        object? cutLine;
        try
        {
            cutLine = Late.Call(container, "Add");
        }
        catch (Exception ex)
        {
            step.Fail("ICutLines.Add бросил: " + ex.GetType().Name + " " + ex.Message);
            return;
        }

        step.Observe("линия создана: " + Api5.RuntimeName(cutLine));
        if (cutLine is null)
        {
            step.Fail("ICutLines.Add вернул null");
            return;
        }

        var written = TrySet(cutLine, "Points", trace.ToArray());
        step.Observe("запись Points (double[]): " + written);
        step.Observe("Points после неё: " + DescribeArray(SafeObject(() => Late.Get(cutLine, "Points"))));

        // A SAFEARRAY property of a dispinterface can be put-by-value or put-by-ref; the help names the
        // member but not the marshalling, so the forms are tried and the one that READS BACK is kept.
        foreach (var (label, value, byRef) in new (string, object, bool)[]
                 {
                     ("double[] putref", trace.ToArray(), true),
                     ("object[] byval", trace.Cast<object>().ToArray(), false),
                     ("object[] putref", trace.Cast<object>().ToArray(), true),
                 })
        {
            if (DescribeArray(SafeObject(() => Late.Get(cutLine, "Points"))) != "null")
            {
                break;
            }

            step.Observe("запись Points (" + label + "): " + TrySet(cutLine, "Points", value, byRef));
            step.Observe("Points после неё: " + DescribeArray(SafeObject(() => Late.Get(cutLine, "Points"))));
        }

        step.Observe("объявленные члены линии: " + string.Join(", ",
            SafeObject(() => Late.MemberNames(cutLine!)) is List<(int MemId, string Name)> names
                ? names.Select(n => n.Name)
                : Array.Empty<string>()));

        step.Observe("запись ArrowPos(true): " + TrySet(cutLine, "ArrowPos", true));
        step.Observe("Update линии: " + TryCall(cutLine, "Update"));
        step.Observe("Points после записи: " + DescribeArray(SafeObject(() => Late.Get(cutLine, "Points"))));
        step.Observe("AutoSorted: " + Api5.Raw(SafeObject(() => Late.Get(cutLine, "AutoSorted"))));
        step.Observe("ArrowPos: " + Api5.Raw(SafeObject(() => Late.Get(cutLine, "ArrowPos"))));
        step.Observe("Text: " + DescribeText(SafeObject(() => Late.Get(cutLine, "Text"))));
        foreach (var name in new[] { "X1", "Y1", "X2", "Y2" })
        {
            step.Observe(name + ": " + Api5.Raw(SafeObject(() => Late.Get(cutLine, name))));
        }

        var before = SafeInt(() => _views.Count) ?? 0;
        object? section;
        try
        {
            section = Late.Call(_views, "Add", 7);
        }
        catch (Exception ex)
        {
            step.Fail("IViews.Add(vt_Section=7) бросил: " + ex.GetType().Name + " " + ex.Message);
            return;
        }

        step.Observe("вид-разрез создан: " + Api5.RuntimeName(section));
        if (section is null)
        {
            step.Fail("IViews.Add вернул null");
            return;
        }

        var after = SafeInt(() => _views.Count) ?? 0;
        step.Observe("видов до/после Add: " + before + " / " + after);

        step.Observe("BaseObject: " + TrySet(section, "BaseObject", cutLine, putRef: true));
        step.Observe("Section = TRUE: " + TrySet(section, "Section", true));
        step.Observe("Update вида: " + TryCall(section, "Update"));
        step.Observe("видов после Update: " + SafeInt(() => _views.Count));
        ReadSection(section, "Section=TRUE");

        step.Observe("Section = FALSE: " + TrySet(section, "Section", false));
        step.Observe("Update вида: " + TryCall(section, "Update"));
        ReadSection(section, "Section=FALSE");

        step.Pass("вид-разрез построен по линии; значения Section измерены");
    }

    private void ReadSection(object? section, string label)
    {
        var step = _report.Steps[^1];
        step.Observe(label + ": объектов в виде " + Api5.Raw(SafeObject(() => Late.Get(section!, "ObjectCount"))));
        step.Observe(label + ": тип " + Api5.Raw(SafeObject(() => Late.Get(section!, "ViewType"))));
        step.Observe(label + ": designation " + Api5.Raw(SafeObject(() => Late.Get(section!, "Designation"))));
        step.Observe(label + ": источник " + Api5.Raw(SafeObject(() => Late.Get(section!, "SourceFileName"))));
        var hatch = SafeObject(() => Late.Get(section!, "HatchParam"));
        step.Observe(label + ": HatchParam " + Api5.RuntimeName(hatch));
        if (hatch is not null)
        {
            step.Observe(label + ": шаг " + Api5.Raw(SafeObject(() => Late.Get(hatch, "Step")))
                + ", угол " + Api5.Raw(SafeObject(() => Late.Get(hatch, "Angle")))
                + ", стиль " + Api5.Raw(SafeObject(() => Late.Get(hatch, "Style"))));
        }
    }

    // ════════════════════════════════════════════════════ ступенчатый ══

    private void SteppedSection()
    {
        var step = _report.Begin("SR.5", "Ступенчатый разрез по линии с перегибами",
            "Строит ли КОМПАС ступенчатый разрез по трассе из шести точек (секущие плоскости совмещены)?");

        var trace = new List<double>();
        if (_baseView is not IAssociationView association)
        {
            step.Fail("опорного вида нет");
            return;
        }

        for (var index = 0; index < SteppedTrace.GetLength(0); index++)
        {
            association.GetProjectionPoint(
                SteppedTrace[index, 0], SteppedTrace[index, 1], PlateT, out var xv, out var yv);
            trace.Add(xv);
            trace.Add(yv);
        }

        step.Observe("точек в трассе: " + (trace.Count / 2) + " (перегибов: " + ((trace.Count / 2) - 2) + ")");

        object? container = SafeObject(() => Late.Get(_baseView, "CutLines"));
        if (container is null)
        {
            step.Fail("CutLines недоступен");
            return;
        }

        var cutLine = SafeObject(() => Late.Call(container, "Add"));
        if (cutLine is null)
        {
            step.Fail("вторая линия не создана");
            return;
        }

        step.Observe("Points: " + TrySet(cutLine, "Points", trace.ToArray()));
        step.Observe("ArrowPos(true): " + TrySet(cutLine, "ArrowPos", true));
        step.Observe("Update линии: " + TryCall(cutLine, "Update"));

        var viewsBefore = SafeInt(() => _views.Count) ?? 0;
        var view = SafeObject(() => Late.Call(_views, "Add", 7));
        if (view is null)
        {
            step.Fail("вид под ступенчатый разрез не создан");
            return;
        }

        step.Observe("BaseObject: " + TrySet(view, "BaseObject", cutLine, putRef: true));
        step.Observe("Section=TRUE: " + TrySet(view, "Section", true));
        step.Observe("Update: " + TryCall(view, "Update"));
        step.Observe("видов до/после: " + viewsBefore + " / " + SafeInt(() => _views.Count));
        ReadSection(view, "ступенчатый");
        step.Pass("ступенчатый разрез построен по линии с перегибами");
    }

    // ══════════════════════════════════════════════════════════ отказы ══

    private void FailureStates()
    {
        var step = _report.Begin("SR.6", "Что остаётся при отказе и при удалении",
            "Удаляется ли линия вместе с видом и что остаётся, если вид не создан?");

        object? container = SafeObject(() => Late.Get(_baseView!, "CutLines"));
        step.Observe("линий в контейнере: " + Api5.Raw(SafeObject(() => Late.Get(container!, "Count"))));

        var view = SafeObject(() => _views.get_View(SafeInt(() => _views.Count) - 1));
        step.Observe("последний вид: " + Api5.Raw(SafeObject(() => Late.Get(view!, "ViewType"))));
        step.Observe("Delete вида: " + TryCall(view, "Delete"));
        step.Observe("видов после Delete: " + SafeInt(() => _views.Count));
        step.Observe("линий после Delete вида: "
            + Api5.Raw(SafeObject(() => Late.Get(container!, "Count"))));

        // A trace that misses the body: the view is created but shows nothing. Recorded, not judged.
        var outside = SafeObject(() => Late.Call(container, "Add"));
        if (outside is not null)
        {
            step.Observe("Points вне тела: " + TrySet(outside, "Points", new[] { 500d, 500d, 600d, 600d }));
            step.Observe("Update: " + TryCall(outside, "Update"));
            var empty = SafeObject(() => Late.Call(_views, "Add", 7));
            if (empty is not null)
            {
                step.Observe("BaseObject: " + TrySet(empty, "BaseObject", outside, putRef: true));
                step.Observe("Section=TRUE: " + TrySet(empty, "Section", true));
                step.Observe("Update: " + TryCall(empty, "Update"));
                ReadSection(empty, "мимо тела");
            }
        }

        step.Pass("состояния отказа записаны");
    }

    // ═════════════════════════════════════════════════════════ помощники ══

    private static string TrySet(object target, string name, object? value, bool putRef = false)
    {
        try
        {
            Late.Set(target, name, value, putRef);
            return "записано";
        }
        catch (Exception ex)
        {
            return "отказ: " + Describe(ex);
        }
    }

    private static string TryCall(object target, string name, params object?[] arguments)
    {
        try
        {
            var answer = Late.Call(target, name, arguments);
            return "вернул " + Api5.Raw(answer);
        }
        catch (Exception ex)
        {
            return "отказ: " + Describe(ex);
        }
    }

    private static string Describe(Exception ex) =>
        ex is COMException com ? "COM 0x" + com.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + com.Message
            : ex.GetType().Name + " " + ex.Message;

    private static string DescribeArray(object? value) => value switch
    {
        null => "null",
        Array array => string.Join(", ", array.Cast<object?>().Select(Api5.Raw)),
        _ => Api5.Raw(value),
    };

    private static string DescribeText(object? value) =>
        value is null ? "null" : Api5.Raw(SafeObject(() => Late.Get(value, "Str")));

    private static int? SafeInt(Func<int> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? SafeObject(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
