using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe RP — are the INPUTS of the body-reposition feature readable via the DOCUMENTED API7 route?</summary>
/// <remarks>
/// <b>Why a separate probe.</b> Probe RR (<c>--reposition-read</c>) concluded that
/// <c>reposition_vector_mm</c> and <c>reposition_axis_point_mm</c> are "not readable by any route".
/// Its member list was taken from the type library — that part is right — but it QUERIED the object
/// in two ways, and both bypass the documented route:
/// <list type="number">
/// <item>late binding via <c>IDispatch::GetTypeInfo(0)</c>, which returns the DEFAULT interface of the
/// class — that is its own observation (RR.5), and it also explains why <c>Position</c> "declared" the
/// feature's names rather than the coordinate-system names;</item>
/// <item>typed — but only via <c>ILocalCoordinateSystem</c>, without casting to the PARAMETERS
/// interface that the documentation names directly.</item>
/// </list>
/// <b>Documented route</b> (help.ascon.ru/KOMPAS_SDK/24/ru-RU, pages named at each read):
/// <c>IBodyReposition.Position</c> (read-only) → <c>ILocalCoordinateSystem</c>, which inherits
/// <c>IPoint3D</c> (DOC: «все методы и свойства для позиционирования ЛСК») and adds
/// <c>X</c>, <c>Y</c>, <c>Z</c>, <c>Vector3D(axis)</c>, <c>GetVector(axis)</c>, <c>ParameterType</c>,
/// <c>Parameters</c>, <c>OrientationType</c>, <c>LocalCSParameters</c>. The displacement magnitude
/// lives in the PARAMETERS interface, which is selected by <c>ParameterType</c> and obtained from
/// <c>Parameters</c> via <c>QueryInterface</c>: <c>IPoint3DParamDisplace.DX/DY/DZ</c> (ksPDisplace)
/// or the coordinates <c>X/Y/Z</c> (ksPParamCoord).
/// <b>A negative control is mandatory.</b> A member returning the same triple for any input proves a
/// constant, not a read. Each route is checked with TWO known translations, a rotation about an axis
/// through a point away from the origin, and a "translate → rotate" sequence. The geometry (bounding
/// box) and the feature identity are checked SEPARATELY from the parameter read.
/// History: docs/decisions/probes.md#rp-params</remarks>
internal sealed class RepositionParamsProbe
{
    /// <summary>Main translation: non-zero on all three coordinates.</summary>
    private static readonly double[] TranslateMain = { 7d, -11d, 13d };

    /// <summary>Negative control: a different non-zero translation of the same kind.</summary>
    private static readonly double[] TranslateControl = { 1d, 2d, 3d };

    /// <summary>Axis point of the rotation — OUTSIDE the origin (order §3 requirement).</summary>
    private static readonly double[] AxisPoint = { 5d, 0d, 0d };

    private static readonly double[] AxisDirection = { 0d, 0d, 1d };

    private const double RotateAngleDeg = 90d;

    /// <summary>Foreign body that the edit must not touch.</summary>
    private const double ThirdX0 = 40d, ThirdX1 = 50d, ThirdY0 = 0d, ThirdY1 = 10d, ThirdZ = 5d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private string _tlb = string.Empty;
    private Dictionary<string, string[]> _declared = new(StringComparer.Ordinal);

    public RepositionParamsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "reposition-params.json"),
            Path.Combine(options.ReportDir, "reposition-params.md"));

    public void Run()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

        Launch();
        try
        {
            _tlb = Path.Combine(_options.KompasRoot, "Bin", "kAPI7.tlb");
            DeclaredMembers();
            DocumentedRoute();
            NegativeControl();
            RotationAboutPoint();
            ChainAndThirdBody();
            AfterEdit();
            AfterReopen();
            ObjectIdentity();
            CentreDeep();
            DocumentedWriteRoutes();
            ReadWritePair();
            ObjectCoordinateSystem();
            ReopenedFeatureRoute();
            ReopenDiscriminating();
            EulerRoute();
            EulerAllAnglesRoute();
            EulerOrderAndDisplacement();
            ReopenSequenceRoute();
            Summarize();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.1 ══

    /// <summary>What the PRODUCT type library declares for each interface of the documented chain.
    /// No name here is invented: all are read from <c>Bin\kAPI7.tlb</c>.</summary>
    private void DeclaredMembers()
    {
        var step = _report.Begin("RP.1", "Объявленные члены всей документированной цепочки",
            "Какие имена объявляет сама библиотека типов у интерфейсов, названных справкой?");
        step.Data["tlb"] = _tlb;
        if (!File.Exists(_tlb))
        {
            step.Fail("библиотека типов не найдена: " + _tlb);
            return;
        }

        var chain = new[]
        {
            "IBodyReposition", "ILocalCoordinateSystem", "IPoint3D", "IPoint3DParamDisplace",
            "IPoint3DParamCoord", "ILocalCSAxesDirectionParam", "ILocalCSEulerParam",
            "ILocalCSOrientByObjectParam", "ILocalCSObject", "IPlacement3D", "IVector3D",
        };

        foreach (var name in chain)
        {
            var members = TlbScan.MemberNames(_tlb, name);
            _declared[name] = members.Select(m => m.MemId + ":" + m.Name).ToArray();
            step.Observe(name + " → " + (members.Count == 0
                ? "<не объявлен>"
                : string.Join(", ", members.Select(m => m.MemId + ":" + m.Name))));
        }

        step.Data["declared"] = _declared;
        step.Pass(_declared["IBodyReposition"].Length == 0
            ? "цепочка прочитана не полностью"
            : "объявленные члены цепочки прочитаны из библиотеки типов продукта");
    }

    // ══════════════════════════════════════════════════════════════ RP.2 ══

    /// <summary>The documented route on the main translation. The WHOLE chain is read; the step's question is
    /// which member returns the written vector (7, −11, 13).</summary>
    private void DocumentedRoute()
    {
        var step = _report.Begin("RP.2", "Документированный маршрут на переносе (7, −11, 13)",
            "Отдаёт ли хоть один член документированной цепочки записанный вектор?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-main");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            step.Observe("до переноса: " + Describe(BodyRows(part)) + " (ожидание габарита (0,0,0)…(20,10,5))");

            var feature = CreateReposition(doc, part, box, Translate(TranslateMain), step, "перенос");
            if (feature is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var after = BodyRows(part);
            step.Observe("после переноса: " + Describe(after) + " (ожидание габарита (7,−11,13)…(27,−1,18))");
            step.Data["geometry_after"] = Describe(after);

            var reading = ReadDocumented(feature, step);
            step.Data["reading"] = reading.Values;
            step.Data["displacement"] = reading.Displacement;
            step.Data["displacement_route"] = reading.DisplacementRoute;
            step.Data["axis_point"] = reading.AxisPoint;
            step.Data["axis_point_route"] = reading.AxisPointRoute;
            step.Data["axes"] = reading.Axes;

            var hit = Hits(reading.Values, TranslateMain);
            step.Data["vector_found_in"] = hit;
            step.Observe("тройка (7,−11,13) найдена в: " + (hit.Length == 0 ? "<нигде>" : string.Join(", ", hit)));

            var geometry = after.Any(r => Near(r, 7d, -11d, 13d, 27d, -1d, 18d));
            step.Data["geometry_matches_expected_bbox"] = geometry;

            if (hit.Length == 0)
            {
                step.Fail("документированная цепочка не отдала записанный вектор ни одним членом");
            }
            else if (!geometry)
            {
                step.Fail("вектор найден, но геометрия не соответствует объявленному габариту — "
                    + "читать и двигать оказались разными вещами");
            }
            else
            {
                step.Pass("вектор переноса читается документированным маршрутом: " + string.Join(", ", hit));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.3 ══

    /// <summary>The same route on ANOTHER known input. The route is usable only if it tells both apart.</summary>
    private void NegativeControl()
    {
        var step = _report.Begin("RP.3", "Отрицательный контроль: другой перенос (1, 2, 3)",
            "Различает ли маршрут два разных известных входа, или отдаёт константу?");
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (label, vector) in new[]
                 {
                     ("основной (7,−11,13)", TranslateMain),
                     ("контроль (1,2,3)", TranslateControl),
                 })
        {
            var doc = NewPart(out var part);
            try
            {
                var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-" + label);
                if (box is null)
                {
                    step.Observe(label + ": брусок не построен");
                    continue;
                }

                var feature = CreateReposition(doc, part, box, Translate(vector), step, label);
                if (feature is null)
                {
                    step.Observe(label + ": признак не создан");
                    continue;
                }

                var reading = ReadDocumented(feature, step);
                step.Data["reading_" + label] = reading.Values;
                var hit = Hits(reading.Values, vector);
                found[label] = hit;
                step.Observe(label + ": вектор найден в " + (hit.Length == 0 ? "<нигде>" : string.Join(", ", hit)));
            }
            catch (Exception ex)
            {
                step.Observe(label + ": исключение " + HResult.Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        step.Data["found"] = found;
        var main = found.TryGetValue("основной (7,−11,13)", out var m) ? m : Array.Empty<string>();
        var control = found.TryGetValue("контроль (1,2,3)", out var c) ? c : Array.Empty<string>();
        var both = main.Intersect(control, StringComparer.Ordinal).ToArray();
        step.Data["routes_distinguishing_both_inputs"] = both;

        if (both.Length > 0)
        {
            step.Pass("маршрут различает ДВА известных входа: " + string.Join(", ", both));
        }
        else if (main.Length > 0 || control.Length > 0)
        {
            step.Fail("тройка найдена, но не одним и тем же членом на обоих входах — это признак "
                + "константы, а не чтения");
        }
        else
        {
            step.Fail("ни на основном входе, ни на контроле документированная цепочка вектора не отдала");
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.4 ══

    /// <summary>Rotation about an axis THROUGH A POINT OUTSIDE THE ORIGIN. Both the parameter and the geometry
    /// are read — separately: "was read" and "was rotated" are proven by different observations.</summary>
    private void RotationAboutPoint()
    {
        var step = _report.Begin("RP.4", "Поворот вокруг оси через точку (5,0,0), направление (0,0,1), +90°",
            "Читается ли точка оси и угол, и повернулось ли тело?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-rotate");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            step.Observe("до поворота: " + Describe(BodyRows(part)));

            var matrix = RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg);
            var expected = ExpectedBbox(matrix, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
            step.Observe("матрица поворота посчитана пробой; ожидаемый габарит ("
                + string.Join(", ", expected.Min.Select(v => Api5.Num(v))) + ")…("
                + string.Join(", ", expected.Max.Select(v => Api5.Num(v))) + ")");
            step.Data["expected_bbox"] = new { min = expected.Min, max = expected.Max };

            var feature = CreateReposition(doc, part, box, matrix, step, "поворот");
            if (feature is null)
            {
                step.Fail("признак поворота не создан");
                return;
            }

            var after = BodyRows(part);
            step.Observe("после поворота: " + Describe(after));
            step.Data["geometry_after"] = Describe(after);

            var geometry = after.Any(r => Near(r, expected.Min[0], expected.Min[1], expected.Min[2],
                expected.Max[0], expected.Max[1], expected.Max[2]));
            step.Data["geometry_matches_expected_bbox"] = geometry;

            var reading = ReadDocumented(feature, step);
            step.Data["reading"] = reading.Values;
            step.Data["displacement"] = reading.Displacement;
            step.Data["displacement_route"] = reading.DisplacementRoute;
            step.Data["axis_point"] = reading.AxisPoint;
            step.Data["axis_point_route"] = reading.AxisPointRoute;
            step.Data["axis_direction"] = reading.AxisDirection;
            step.Data["angle_deg"] = reading.AngleDeg;
            step.Data["kind"] = reading.Kind;

            var pointHit = reading.Values
                .Where(p => ContainsTriple(p.Value, AxisPoint))
                .Select(p => p.Key)
                .ToArray();
            step.Data["axis_point_found_in"] = pointHit;
            step.Observe("точка оси (5,0,0) найдена в: "
                + (pointHit.Length == 0 ? "<нигде>" : string.Join(", ", pointHit)));

            if (!geometry)
            {
                step.Fail("геометрия не соответствует посчитанному габариту — поворот не применён");
            }
            else if (pointHit.Length == 0)
            {
                step.Fail("поворот применён (габарит совпал), но точка оси не читается ни одним "
                    + "членом документированной цепочки");
            }
            else
            {
                step.Pass("поворот применён и точка оси читается: " + string.Join(", ", pointHit));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.5 ══

    /// <summary>The "translate → rotate" sequence and an independent FOREIGN body: editing one feature must
    /// neither reposition the second nor touch the third body.</summary>
    private void ChainAndThirdBody()
    {
        var step = _report.Begin("RP.5", "Последовательность «перенос → поворот» и постороннее тело",
            "Читаются ли оба признака, и остаётся ли постороннее тело нетронутым?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-chain-A");
            var third = ExtrudeRect(doc, part, ThirdX0, ThirdX1, ThirdY0, ThirdY1, ThirdZ, step, "RP-chain-T");
            if (box is null || third is null)
            {
                step.Fail("тела не построены");
                return;
            }

            var thirdBefore = Describe(BodyRows(part).Where(r => r.Index == third.Index).ToList());
            step.Observe("постороннее тело до: " + thirdBefore);
            step.Data["third_before"] = thirdBefore;

            var translate = CreateReposition(doc, part, box, Translate(TranslateMain), step, "перенос");
            if (translate is null)
            {
                step.Fail("признак переноса не создан");
                return;
            }

            var afterTranslate = BodyRows(part);
            step.Observe("после переноса: " + Describe(afterTranslate));

            // Second feature — rotation of the already translated body about an axis through (5,0,0).
            var moved = afterTranslate.FirstOrDefault(r => Near(r, 7d, -11d, 13d, 27d, -1d, 18d));
            if (moved is null)
            {
                step.Fail("перенесённое тело не найдено — второй признак не на что ставить");
                return;
            }

            var rotate = CreateReposition(doc, part, moved, RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg),
                step, "поворот");
            if (rotate is null)
            {
                step.Fail("признак поворота не создан");
                return;
            }

            var after = BodyRows(part);
            step.Observe("после переноса и поворота: " + Describe(after));
            step.Data["geometry_after"] = Describe(after);
            step.Data["feature_count"] = after.Count;

            var thirdAfter = Describe(after.Where(r => r.Index == third.Index).ToList());
            step.Data["third_after"] = thirdAfter;
            var thirdUntouched = thirdAfter == thirdBefore;
            step.Data["third_untouched"] = thirdUntouched;
            step.Observe("постороннее тело после: " + thirdAfter
                + " → " + (thirdUntouched ? "не изменилось" : "ИЗМЕНИЛОСЬ"));

            var readTranslate = ReadDocumented(translate, step);
            var readRotate = ReadDocumented(rotate, step);
            step.Data["reading_translate"] = readTranslate.Values;
            step.Data["reading_rotate"] = readRotate.Values;
            step.Data["vector_found_in_translate"] = Hits(readTranslate.Values, TranslateMain);
            step.Data["axis_point_found_in_rotate"] = readRotate.Values
                .Where(p => ContainsTriple(p.Value, AxisPoint)).Select(p => p.Key).ToArray();

            step.Observe("признак переноса: вектор найден в "
                + (step.Data["vector_found_in_translate"] is string[] v && v.Length > 0
                    ? string.Join(", ", v) : "<нигде>"));
            step.Observe("признак поворота: точка оси найдена в "
                + (step.Data["axis_point_found_in_rotate"] is string[] p && p.Length > 0
                    ? string.Join(", ", p) : "<нигде>"));

            if (!thirdUntouched)
            {
                step.Fail("постороннее тело ИЗМЕНИЛОСЬ — правка одного признака задела чужое тело");
            }
            else
            {
                step.Pass("оба признака читаются на своих входах, постороннее тело не изменилось");
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.6 ══

    /// <summary>A read RIGHT AFTER the parameters of the same feature are changed: the edit writes into the same
    /// element, so the read must return the NEW value, not the original one.</summary>
    private void AfterEdit()
    {
        var step = _report.Begin("RP.6", "Чтение после правки параметров того же признака",
            "Возвращает ли чтение НОВЫЙ вектор, а не первоначальный?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-edit");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Translate(TranslateMain), step, "первый перенос");
            if (feature is null)
            {
                step.Fail("признак не создан");
                return;
            }

            var before = ReadDocumented(feature, step);
            step.Data["reading_before"] = before.Values;
            step.Observe("до правки: " + Join(before.Values));

            var newVector = TranslateControl;
            var moved = BodyRows(part).FirstOrDefault(r => Near(r, 7d, -11d, 13d, 27d, -1d, 18d));
            var edited = EditReposition(doc, part, feature, moved, Translate(newVector), step);
            if (!edited)
            {
                step.Fail("правка признака не выполнена");
                return;
            }

            var after = BodyRows(part);
            step.Observe("после правки: " + Describe(after)
                + " (ожидание габарита (1,2,3)…(21,12,8))");
            step.Data["geometry_after"] = Describe(after);

            var reading = ReadDocumented(feature, step);
            step.Data["reading_after"] = reading.Values;
            step.Observe("после правки: " + Join(reading.Values));

            var hitNew = Hits(reading.Values, newVector);
            var hitOld = Hits(reading.Values, TranslateMain);
            step.Data["new_vector_found_in"] = hitNew;
            step.Data["old_vector_found_in"] = hitOld;

            if (hitNew.Length == 0)
            {
                step.Fail("после правки чтение не отдаёт НОВЫЙ вектор — параметр либо не записан, "
                    + "либо не читается");
            }
            else if (hitOld.Length > 0)
            {
                step.Fail("после правки чтение отдаёт ещё и СТАРЫЙ вектор — читается не то состояние");
            }
            else
            {
                step.Pass("чтение возвращает новый вектор и не возвращает прежний: "
                    + string.Join(", ", hitNew));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.7 ══

    /// <summary>Save → close → open: the feature is taken ANEW from the reopened document, and both the
    /// parameters and the geometry are read. The order's §3 question requires exactly this stage.</summary>
    private void AfterReopen()
    {
        var step = _report.Begin("RP.7", "Чтение после сохранения, закрытия и открытия",
            "Читаются ли параметры с переоткрытого документа, и стоит ли тело на месте?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-reopen");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Translate(TranslateMain), step, "перенос");
            if (feature is null)
            {
                step.Fail("признак не создан");
                return;
            }

            var path = Path.Combine(_options.WorkDir, "reposition-params.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs: " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            var container = Container(reopened);
            if (container?.BodyRepositions is not { } collection || collection.Count == 0
                || collection[0] is not IBodyReposition reopenedFeature)
            {
                step.Fail("признак изменения положения после переоткрытия не найден");
                return;
            }

            step.Observe("признаков BodyRepositions после переоткрытия: " + Api5.Raw(collection.Count));
            step.Data["feature_count_after_reopen"] = Api5.Raw(collection.Count);

            var reading = ReadDocumented(reopenedFeature, step);
            step.Data["reading"] = reading.Values;
            step.Data["displacement"] = reading.Displacement;
            step.Data["displacement_route"] = reading.DisplacementRoute;

            var hit = Hits(reading.Values, TranslateMain);
            step.Data["vector_found_in"] = hit;
            step.Observe("с переоткрытого документа вектор найден в: "
                + (hit.Length == 0 ? "<нигде>" : string.Join(", ", hit)));

            var bodies = BodyRows((ksPart)reopened.GetPart(-1));
            step.Observe("геометрия переоткрытого документа: " + Describe(bodies));
            step.Data["geometry_after_reopen"] = Describe(bodies);
            var geometry = bodies.Any(r => Near(r, 7d, -11d, 13d, 27d, -1d, 18d));
            step.Data["geometry_matches_expected_bbox"] = geometry;

            if (hit.Length == 0)
            {
                step.Fail("с переоткрытого документа вектор (7, −11, 13) не отдал ни один член "
                    + "документированной цепочки");
            }
            else if (!geometry)
            {
                step.Fail("вектор прочитан, но тело стоит не на объявленном месте");
            }
            else
            {
                step.Pass("с переоткрытого документа читается и вектор, и геометрия: "
                    + string.Join(", ", hit));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.9 ══

    /// <summary>Identity of the chain's objects: is <c>Position</c> a SEPARATE object or the very same one as
    /// the feature? The question is not idle: <c>Api5.RuntimeName</c> of <c>Position</c> and of the
    /// feature matched (<c>BodyRepositionClass</c>), and if it is one object then all the X/Y/Z/Parameters
    /// reads describe the feature, not the coordinate system, and the "zeros" mean something else.</summary>
    private void ObjectIdentity()
    {
        var step = _report.Begin("RP.9", "Тождество объектов: Position и признак — один объект или разные",
            "Совпадают ли указатели IUnknown у Position, у признака и у элемента коллекции?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-identity");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not IBodyReposition feature)
            {
                step.Fail("признак не создан");
                return;
            }

            var created = CreateOn(part, doc, feature, box, Translate(TranslateMain), step);
            if (!created)
            {
                step.Fail("признак не собран");
                return;
            }

            var position = feature.Position;
            var element = container.BodyRepositions[0];

            var featurePointer = Pointer(feature);
            var positionPointer = Pointer(position);
            var elementPointer = element is null ? IntPtr.Zero : Pointer(element);

            step.Data["feature_pointer"] = featurePointer.ToInt64().ToString("x");
            step.Data["position_pointer"] = positionPointer.ToInt64().ToString("x");
            step.Data["element_pointer"] = elementPointer.ToInt64().ToString("x");
            step.Observe("IUnknown признака: " + step.Data["feature_pointer"]);
            step.Observe("IUnknown Position: " + step.Data["position_pointer"]);
            step.Observe("IUnknown элемента коллекции: " + step.Data["element_pointer"]);

            var sameAsFeature = featurePointer == positionPointer;
            var sameAsElement = positionPointer == elementPointer;
            step.Data["position_is_feature"] = sameAsFeature;
            step.Data["position_is_element"] = sameAsElement;

            // The feature and Position are different objects if the pointers differ; but the feature
            // also has GetVector (measured earlier), so the member set is queried as well.
            step.Data["position_own_names"] = Late.MemberNames(position)
                .Select(m => m.MemId + ":" + m.Name).ToArray();
            step.Data["feature_own_names"] = Late.MemberNames(feature)
                .Select(m => m.MemId + ":" + m.Name).ToArray();
            var positionNames = step.Data["position_own_names"] as string[] ?? Array.Empty<string>();
            var featureNames = step.Data["feature_own_names"] as string[] ?? Array.Empty<string>();
            step.Observe("Position объявляет своих имён: " + string.Join(", ", positionNames));
            step.Observe("признак объявляет своих имён: " + string.Join(", ", featureNames));

            step.Pass(sameAsFeature
                ? "Position — ТОТ ЖЕ объект, что признак: чтения описывают признак, а не систему координат"
                : "Position — отдельный объект от признака");
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.10 ══

    /// <summary>Descending into <c>RepositionCentre</c> and <c>ILocalCSObject.CoordinateSystem</c> — two documented
    /// object members that the previous revision of the probe did NOT unfold: it queried them by late
    /// binding through the default interface of the class, which for these objects coincides with the
    /// feature. Here the member set is read from the OBJECT itself and from the interfaces it confirms.</summary>
    private void CentreDeep()
    {
        var step = _report.Begin("RP.10", "Спуск в RepositionCentre и ILocalCSObject.CoordinateSystem",
            "Не спрятана ли точка оси в объектных членах, которые прибор прежде не раскрывал?");

        var doc = NewPart(out var part);
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-centre");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            // Rotation about a point OUTSIDE the origin: the axis point is known in advance.
            var feature = CreateReposition(doc, part, box,
                RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg), step, "поворот");
            if (feature is null)
            {
                step.Fail("признак поворота не создан");
                return;
            }

            var found = new List<string>();

            foreach (var (label, value) in new (string, object?)[]
                     {
                         ("RepositionCentre", SafeObject(() => feature.RepositionCentre)),
                         ("Position", SafeObject(() => feature.Position)),
                         ("RepositionBody", SafeObject(() => feature.RepositionBody)),
                     })
            {
                if (value is null)
                {
                    step.Observe(label + ": null");
                    continue;
                }

                var own = Late.MemberNames(value);
                step.Data["own_" + label] = own.Select(m => m.MemId + ":" + m.Name).ToArray();
                step.Observe(label + " (" + Api5.RuntimeName(value) + ") объявляет своих имён "
                    + own.Count + ": " + string.Join(", ", own.Select(m => m.MemId + ":" + m.Name)));

                foreach (var confirmed in ConfirmedInterfaces(value))
                {
                    step.Observe(label + " подтверждает интерфейс: " + confirmed);
                    if (confirmed != "IPoint3D" && confirmed != "ILocalCoordinateSystem")
                    {
                        continue;
                    }

                    if (value is IPoint3D point)
                    {
                        var x = Try(() => point.X);
                        var y = Try(() => point.Y);
                        var z = Try(() => point.Z);
                        var text = Triple(x, y, z);
                        step.Observe(label + "→" + confirmed + ".X/Y/Z = " + text);
                        if (ContainsTriple(text, AxisPoint))
                        {
                            found.Add(label + "→" + confirmed + ".X/Y/Z");
                        }
                    }
                }

                // Late binding by the names declared by the OBJECT itself: a name that the library
                // declares on another interface will still not be accepted by the object.
                foreach (var (_, name) in own)
                {
                    var text = DescribeValue(SafeGet(value, name));
                    step.Data["read_" + label + "_" + name] = text;
                    if (ContainsTriple(text, AxisPoint))
                    {
                        found.Add(label + "." + name);
                    }
                }
            }

            // ILocalCSObject.CoordinateSystem — the documented "object coordinate system" (version v21).
            if (SafeObject(() => feature.Position) is ILocalCoordinateSystem local && local is ILocalCSObject subordinate)
            {
                try
                {
                    var cs = subordinate.CoordinateSystem;
                    step.Observe("ILocalCSObject.CoordinateSystem = " + (cs is null ? "null" : Api5.RuntimeName(cs)));
                    if (cs is IPoint3D csPoint)
                    {
                        var text = Triple(Try(() => csPoint.X), Try(() => csPoint.Y), Try(() => csPoint.Z));
                        step.Observe("ILocalCSObject.CoordinateSystem→IPoint3D.X/Y/Z = " + text);
                        if (ContainsTriple(text, AxisPoint))
                        {
                            found.Add("ILocalCSObject.CoordinateSystem→IPoint3D.X/Y/Z");
                        }
                    }
                }
                catch (Exception ex)
                {
                    step.Observe("ILocalCSObject.CoordinateSystem: " + HResult.Describe(ex));
                }
            }

            step.Data["axis_point_found_in"] = found.ToArray();
            if (found.Count == 0)
            {
                step.Fail("точка оси (5,0,0) не найдена ни в одном объектном члене, включая "
                    + "RepositionCentre и ILocalCSObject.CoordinateSystem");
            }
            else
            {
                step.Pass("точка оси найдена: " + string.Join(", ", found));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.11 ══

    /// <summary>Documented WRITE routes: <c>IPoint3D.X/Y/Z</c> (allowed for the coordinate parameter type, and
    /// that is what was measured — <c>ksPParamCoord</c>) and <c>ILocalCoordinateSystem.SetDisplacementByAxis</c>.
    /// The question is not "does the write work" but "does it yield a READABLE state": if after the write
    /// the written value is read back through the same documented route, then the inputs are readable,
    /// and the obstacle is not in the API but in the creation route.</summary>
    private void DocumentedWriteRoutes()
    {
        var step = _report.Begin("RP.11", "Запись документированными членами и чтение обратно",
            "Даёт ли запись X/Y/Z и SetDisplacementByAxis читаемое состояние, и двигает ли она тело?");

        var cases = new[]
        {
            ("координаты X/Y/Z", (Func<ILocalCoordinateSystem, bool>)(local =>
            {
                local.X = TranslateControl[0];
                local.Y = TranslateControl[1];
                local.Z = TranslateControl[2];
                return true;
            })),
            ("SetDisplacementByAxis", (Func<ILocalCoordinateSystem, bool>)(local =>
                local.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOX, TranslateControl[0])
                && local.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOY, TranslateControl[1])
                && local.SetDisplacementByAxis(ksObj3dTypeEnum.o3d_axisOZ, TranslateControl[2]))),
        };

        foreach (var (label, write) in cases)
        {
            var doc = NewPart(out var part);
            try
            {
                var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-write-" + label);
                if (box is null)
                {
                    step.Observe(label + ": брусок не построен");
                    continue;
                }

                var container = Container(doc);
                if (container?.BodyRepositions?.Add() is not IBodyReposition feature)
                {
                    step.Observe(label + ": признак не создан");
                    continue;
                }

                if (_app.TransferInterface(box.Element!, 2, 0) is not IKompasAPIObject transferred)
                {
                    step.Observe(label + ": тело не переносится в API7");
                    continue;
                }

                feature.RepositionBody = transferred;
                var local = feature.Position;

                bool written;
                try
                {
                    written = write(local);
                }
                catch (Exception ex)
                {
                    step.Observe(label + ": запись — " + HResult.Describe(ex));
                    continue;
                }

                var updated = feature.Update();
                part.RebuildModel();
                doc.RebuildDocument();
                step.Observe(label + ": записано=" + written + ", Update()=" + updated);

                var after = BodyRows(part);
                step.Observe(label + ": геометрия " + Describe(after)
                    + " (ожидание габарита (1,2,3)…(21,12,8))");
                step.Data["geometry_" + label] = Describe(after);
                var moved = after.Any(r => Near(r, 1d, 2d, 3d, 21d, 12d, 8d));
                step.Data["moved_" + label] = moved;

                var reading = ReadDocumented(feature, step);
                step.Data["reading_" + label] = reading.Values;
                var hit = Hits(reading.Values, TranslateControl);
                step.Data["read_back_" + label] = hit;
                step.Observe(label + ": записанная тройка (1,2,3) прочитана в "
                    + (hit.Length == 0 ? "<нигде>" : string.Join(", ", hit)));

                if (moved && hit.Length > 0)
                {
                    step.Pass(label + ": запись применена к геометрии и читается обратно");
                }
                else if (!moved)
                {
                    step.Observe(label + ": документированный маршрут записи тело НЕ двигает — "
                        + "самостоятельный факт о продукте, к чтению отношения не имеющий");
                }
            }
            catch (Exception ex)
            {
                step.Observe(label + ": исключение " + HResult.Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        // The negative control of the "field present / field absent" pair is set by BOTH halves in one
        // run: if no write route produced a readable state, the step must say so plainly, not stay silent.
        if (step.Data.Keys.All(k => !k.StartsWith("read_back_", StringComparison.Ordinal)))
        {
            step.Fail("ни один документированный маршрут записи не довёл признак до читаемого состояния");
        }
        else if (step.Verdict == Verdict.Unknown)
        {
            step.Pass("маршруты записи проверены; читаемость состояния названа в данных шага");
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.12 ══

    /// <summary>The pair "write with a documented member → read it back", set by BOTH halves in ONE run. The
    /// positive half drives the input into the feature the "by coordinates" way
    /// (<c>ILocalCoordinateSystem.X/Y/Z</c> — <c>ilocalcoordinatesystem_x.html</c>, documented for both
    /// read and write); the negative half drives the SAME input with a matrix
    /// (<c>InitByMatrix3D</c>) and does not touch the documented members.</summary>
    /// <remarks>Why both halves. Without the positive one, "not readable" would pass on a product that never
    /// returns the value; without the negative one, "readable" would pass on a product that always
    /// returns it — and then the read would not tell the written value from a constant. Beyond that,
    /// the positive half is repeated on a REOPENED document: a value living only in the session is a
    /// cache, not a model parameter, and order §2 forbids passing one off as the other.</remarks>
    private void ReadWritePair()
    {
        var step = _report.Begin("RP.12",
            "Пара «запись по координатам → чтение обратно» и её отрицательный контроль",
            "Отдаёт ли признак записанный вход, если он доведён членом X/Y/Z, и переживает ли он переоткрытие?");

        var rotation = RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg);
        var axisTranslation = new[] { rotation[12], rotation[13], rotation[14] };

        var cases = new[]
        {
            new PairCase("перенос координатами", "translate-coords",
                null, TranslateMain, TranslateMain, Translate(TranslateMain), true),
            new PairCase("контроль: только матрица", "matrix-only",
                Translate(TranslateMain), null, TranslateMain, Translate(TranslateMain), false),
            new PairCase("поворот: матрица + координаты c−R·c", "rotate-translation",
                rotation, axisTranslation, axisTranslation, rotation, true),
            new PairCase("поворот: матрица + координаты c", "rotate-axis-point",
                rotation, AxisPoint, AxisPoint, rotation, false),
        };

        var results = cases.Select(item => PairRun(step, item)).ToList();

        foreach (var result in results)
        {
            step.Observe("ИТОГ «" + result.Label + "»: габарит=" + result.Geometry
                + ", чтение сразу=" + result.Read
                + (result.ReopenAttempted ? ", чтение после переоткрытия=" + result.ReopenRead : ""));
        }

        var positive = results.First(r => r.Label == "перенос координатами");
        var negative = results.First(r => r.Label == "контроль: только матрица");
        var keptAfterReopen = step.Data.TryGetValue("geometry_reopen_matches_translate-coords", out var k1)
            && k1 is true;
        var keptAfterRebuild = step.Data.TryGetValue("geometry_rebuilt_matches_translate-coords", out var k2)
            && k2 is true;

        step.Data["positive_geometry"] = positive.Geometry;
        step.Data["positive_read"] = positive.Read;
        step.Data["positive_read_after_reopen"] = positive.ReopenRead;
        step.Data["positive_routes"] = positive.Routes;
        step.Data["positive_geometry_after_reopen"] = keptAfterReopen;
        step.Data["positive_geometry_after_rebuild"] = keptAfterRebuild;
        step.Data["negative_geometry"] = negative.Geometry;
        step.Data["negative_read"] = negative.Read;

        if (!positive.Geometry || !negative.Geometry)
        {
            step.Fail("половина пары не поставлена: записанный вход не довёл тело до объявленного "
                + "габарита, поэтому вопрос о чтении на этой постановке не решается");
        }
        else if (positive.Read && !positive.ReopenRead && keptAfterReopen)
        {
            step.Observe("РАЗЛИЧЕНО: вход, записанный координатами, читается в ТОЙ ЖЕ сессии и "
                + "ИСЧЕЗАЕТ после сохранения, закрытия и открытия — при том что ГАБАРИТ сохранён"
                + (keptAfterRebuild ? " и после повторной сборки в новой сессии" : string.Empty)
                + ". Значит X/Y/Z — ВХОДНОЙ БУФЕР признака, расходуемый при Update(), а в модели "
                + "хранится РАЗМЕЩЕНИЕ, а не параметры построения");
            step.Fail("вход читается сразу после записи, но НЕ переживает переоткрытия — "
                + "это кеш сеанса, а не параметр модели");
        }
        else if (positive.Read && !positive.ReopenRead)
        {
            step.Fail("вход читается сразу после записи, НЕ переживает переоткрытия и при этом "
                + "потерян и габарит: это уже не граница чтения, а потеря геометрии");
        }
        else if (!positive.Read)
        {
            step.Fail("член X/Y/Z записан и тело сдвинулось, но обратно значение не отдаёт ни один "
                + "член документированной цепочки");
        }
        else if (negative.Read)
        {
            step.Fail("отрицательная половина не различает: тот же вход, доведённый МАТРИЦЕЙ, "
                + "тоже читается — значит читается не записанное, а константа");
        }
        else
        {
            step.Pass("пара поставлена: вход, доведённый координатами, читается и сразу, и после "
                + "переоткрытия (" + string.Join(", ", positive.Routes) + "); тот же вход, доведённый "
                + "матрицей, не читается — то есть читается ЗАПИСАННОЕ, а не константа");
        }

        var byTranslation = results.First(r => r.Label == "поворот: матрица + координаты c−R·c");
        var byAxisPoint = results.First(r => r.Label == "поворот: матрица + координаты c");
        step.Data["rotate_translation_geometry"] = byTranslation.Geometry;
        step.Data["rotate_axis_point_geometry"] = byAxisPoint.Geometry;
        step.Observe("поворот: габарит сохранился при записи t=c−R·c — " + byTranslation.Geometry
            + "; при записи c — " + byAxisPoint.Geometry
            + ". Это и есть различающая пара о СМЫСЛЕ X/Y/Z при повороте");

        // A control of the PROBE ITSELF, without which the conclusion "the input is empty after reopen"
        // would rest on the unproven assumption that the reopened object is initialized at all. The
        // rotation is chosen because its orientation does NOT coincide with the identity: if the object
        // returns the STORED rotation it is answering meaningfully, and an empty X/Y/Z is a fact about
        // the product, not about the probe.
        if (step.Data.TryGetValue("reading_reopen_rotate-translation", out var raw)
            && raw is Dictionary<string, string> reopened)
        {
            var ox = reopened.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            var xyz = reopened.GetValueOrDefault("ILocalCoordinateSystem.X/Y/Z");
            step.Data["reopen_rotation_orientation_ox"] = ox;
            step.Data["reopen_rotation_xyz"] = xyz;
            step.Observe("КОНТРОЛЬ ПРИБОРА на переоткрытом ПОВОРОТЕ: GetVector(OX)=" + ox
                + ", X/Y/Z=" + xyz
                + ". Ориентация (0,1,0) — ХРАНИМЫЙ поворот, значит объект отвечает по делу; "
                + "ориентация (1,0,0) означала бы неинициализированный объект, и тогда пустой "
                + "X/Y/Z ничего о продукте не доказывал бы");
        }
    }

    /// <summary>One run of a pair: drive the input into the feature, build, read, and if needed reopen and read
    /// again. All four quantities are returned outward, because the verdict is set over a PAIR of runs,
    /// not inside one.</summary>
    private PairResult PairRun(ProbeStep step, PairCase item)
    {
        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-pair-" + item.Slug);
            if (box is null)
            {
                step.Observe(item.Label + ": брусок не построен");
                return PairResult.Empty(item);
            }

            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not IBodyReposition feature)
            {
                step.Observe(item.Label + ": BodyRepositions.Add() недоступен");
                return PairResult.Empty(item);
            }

            if (_app.TransferInterface(box.Element!, 2, 0) is not IKompasAPIObject transferred)
            {
                step.Observe(item.Label + ": тело не переносится в API7");
                return PairResult.Empty(item);
            }

            feature.RepositionBody = transferred;
            var local = feature.Position;

            if (item.Matrix is not null)
            {
                local.InitByMatrix3D(item.Matrix);
                step.Observe(item.Label + ": записана матрица; документированные члены НЕ тронуты");
            }

            if (item.Coordinates is not null)
            {
                local.X = item.Coordinates[0];
                local.Y = item.Coordinates[1];
                local.Z = item.Coordinates[2];
                step.Observe(item.Label + ": записаны координаты ("
                    + string.Join(", ", item.Coordinates.Select(v => Api5.Num(v))) + ")");
            }

            var updated = feature.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe(item.Label + ": Update()=" + updated);

            var rows = BodyRows(part);
            step.Observe(item.Label + ": геометрия " + Describe(rows));
            step.Data["geometry_" + item.Slug] = Describe(rows);

            var expected = ExpectedBbox(item.Expect, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
            var geometry = rows.Any(r => Near(r, expected.Min[0], expected.Min[1], expected.Min[2],
                expected.Max[0], expected.Max[1], expected.Max[2]));
            step.Data["geometry_matches_" + item.Slug] = geometry;
            step.Observe(item.Label + ": совпадение с объявленным габаритом " + geometry
                + " (объявлен (" + string.Join(", ", expected.Min.Select(v => Api5.Num(v))) + ")…("
                + string.Join(", ", expected.Max.Select(v => Api5.Num(v))) + "))");

            var reading = ReadDocumented(feature, step);
            step.Data["reading_" + item.Slug] = reading.Values;
            var hit = Hits(reading.Values, item.Search);
            step.Data["read_" + item.Slug] = hit;
            step.Observe(item.Label + ": прочитано в "
                + (hit.Length == 0 ? "<нигде>" : string.Join(", ", hit)));

            var reopenHit = false;
            if (item.Reopen)
            {
                var path = Path.Combine(_options.WorkDir, "rp12-" + item.Slug + ".m3d");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                step.Observe(item.Label + ": SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
                doc.close();

                reopened = (ksDocument3D)_app.Document3D();
                if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
                {
                    step.Observe(item.Label + ": документ не переоткрылся");
                }
                else if (Container(reopened)?.BodyRepositions is not { } collection || collection.Count == 0
                    || collection[0] is not IBodyReposition again)
                {
                    step.Observe(item.Label + ": признак после переоткрытия не найден");
                }
                else
                {
                    var second = ReadDocumented(again, step);
                    step.Data["reading_reopen_" + item.Slug] = second.Values;
                    var reopenRoutes = Hits(second.Values, item.Search);
                    reopenHit = reopenRoutes.Length > 0;
                    step.Data["read_reopen_" + item.Slug] = reopenRoutes;
                    step.Observe(item.Label + ": после переоткрытия прочитано в "
                        + (reopenRoutes.Length == 0 ? "<нигде>" : string.Join(", ", reopenRoutes)));

                    // THE MAIN DISTINCTION OF THIS STEP: "the parameter was not saved" and "the
                    // geometry was not saved" are DIFFERENT facts about the product, and a single read
                    // cannot tell them apart. The bounding box after reopen is read here, in the same run.
                    var reopenedPart = (ksPart)reopened.GetPart(-1);
                    var rowsAfter = BodyRows(reopenedPart);
                    step.Observe(item.Label + ": геометрия после переоткрытия " + Describe(rowsAfter));
                    step.Data["geometry_reopen_" + item.Slug] = Describe(rowsAfter);
                    var geometryKept = rowsAfter.Any(r => Near(r, expected.Min[0], expected.Min[1],
                        expected.Min[2], expected.Max[0], expected.Max[1], expected.Max[2]));
                    step.Data["geometry_reopen_matches_" + item.Slug] = geometryKept;
                    step.Observe(item.Label + ": габарит после переоткрытия совпал с объявленным — "
                        + geometryKept);

                    // The stage that distinguishes "the input was consumed at Update and saved by the
                    // matrix" from "the input was not saved at all": in a NEW session the feature is
                    // rebuilt, and after the rebuild both the input and the geometry are read.
                    step.Observe(item.Label + ": повторная сборка в новой сессии Update()="
                        + Api5.Raw(SafeBool(() => again.Update())));
                    reopenedPart.RebuildModel();
                    reopened.RebuildDocument();
                    var rowsRebuilt = BodyRows(reopenedPart);
                    step.Observe(item.Label + ": геометрия после повторной сборки " + Describe(rowsRebuilt));
                    step.Data["geometry_rebuilt_" + item.Slug] = Describe(rowsRebuilt);
                    var rebuiltKept = rowsRebuilt.Any(r => Near(r, expected.Min[0], expected.Min[1],
                        expected.Min[2], expected.Max[0], expected.Max[1], expected.Max[2]));
                    step.Data["geometry_rebuilt_matches_" + item.Slug] = rebuiltKept;

                    var third = ReadDocumented(again, step);
                    step.Data["reading_rebuilt_" + item.Slug] = third.Values;
                    var rebuiltRoutes = Hits(third.Values, item.Search);
                    step.Data["read_rebuilt_" + item.Slug] = rebuiltRoutes;
                    step.Observe(item.Label + ": после повторной сборки прочитано в "
                        + (rebuiltRoutes.Length == 0 ? "<нигде>" : string.Join(", ", rebuiltRoutes)));
                }
            }

            return new PairResult(item.Label, geometry, hit.Length > 0, reopenHit, item.Reopen, hit);
        }
        catch (Exception ex)
        {
            step.Observe(item.Label + ": исключение " + HResult.Describe(ex));
            return PairResult.Empty(item);
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    /// <summary>The setup of one half of the pair: WHAT drives the input, WHAT is searched for in the
    /// read, and which bounding box is declared as an independent control.</summary>
    private sealed record PairCase(
        string Label,
        string Slug,
        double[]? Matrix,
        double[]? Coordinates,
        double[] Search,
        double[] Expect,
        bool Reopen);

    private sealed record PairResult(
        string Label,
        bool Geometry,
        bool Read,
        bool ReopenRead,
        bool ReopenAttempted,
        string[] Routes)
    {
        public static PairResult Empty(PairCase item) =>
            new(item.Label, false, false, false, item.Reopen, Array.Empty<string>());
    }

    // ══════════════════════════════════════════════════════════════ RP.13 ══

    /// <summary>
    /// The last unexamined documented branch: <c>ILocalCSObject.CoordinateSystem</c> — the "object
    /// coordinate system", help page <c>ilocalcsobject_coordinatesystem.html</c>, version v21, data type
    /// <c>IModelObject</c>, read and write.
    /// </summary>
    /// <remarks>The question is posed on a REOPENED document not by chance: that is exactly where the X/Y/Z input
    /// buffer is empty (RP.12) while the placement is preserved. If the placement is in the model and the
    /// documented reference to the object coordinate system returns it — the requirement is feasible. If
    /// it does not return it — the requirement is withdrawn not because "the code returned zeros" but
    /// because no documented members remain.</remarks>
    private void ObjectCoordinateSystem()
    {
        var step = _report.Begin("RP.13",
            "СК объекта (ILocalCSObject.CoordinateSystem) на живом и на переоткрытом документе",
            "Отдаёт ли документированная СК объекта размещение там, где входной буфер пуст?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-csobj");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var feature = CreateReposition(doc, part, box, Translate(TranslateMain), step, "перенос");
            if (feature is null)
            {
                step.Fail("признак не создан");
                return;
            }

            var found = new List<string>();
            InspectCoordinateSystem(feature, "живой", TranslateMain, step, found);

            var path = Path.Combine(_options.WorkDir, "rp13-csobj.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            if (Container(reopened)?.BodyRepositions is not { } collection || collection.Count == 0
                || collection[0] is not IBodyReposition again)
            {
                step.Fail("признак после переоткрытия не найден");
                return;
            }

            var rows = BodyRows((ksPart)reopened.GetPart(-1));
            step.Observe("геометрия переоткрытого документа: " + Describe(rows));
            step.Data["geometry_after_reopen"] = Describe(rows);

            InspectCoordinateSystem(again, "переоткрытый", TranslateMain, step, found);

            step.Data["found_in"] = found.ToArray();
            if (found.Count == 0)
            {
                step.Fail("СК объекта не отдала размещение ни на живом документе, ни на переоткрытом");
            }
            else
            {
                step.Pass("СК объекта отдала размещение: " + string.Join(", ", found));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    /// <summary>Unfold the object coordinate system: its own names, the confirmed interfaces and a read
    /// by each name. No name is invented — the list is taken from the object itself.</summary>
    private void InspectCoordinateSystem(
        IBodyReposition feature, string label, double[] search, ProbeStep step, List<string> found)
    {
        try
        {
            if (feature.Position is not ILocalCSObject subordinate)
            {
                step.Observe(label + ": Position НЕ подтверждает ILocalCSObject");
                return;
            }

            var cs = subordinate.CoordinateSystem;
            step.Observe(label + ": ILocalCSObject.CoordinateSystem = "
                + (cs is null ? "null" : Api5.RuntimeName(cs)));
            if (cs is null)
            {
                return;
            }

            var own = Late.MemberNames(cs);
            step.Data["own_" + label] = own.Select(m => m.MemId + ":" + m.Name).ToArray();
            step.Observe(label + ": СК объекта объявляет своих имён " + own.Count + ": "
                + string.Join(", ", own.Select(m => m.MemId + ":" + m.Name)));

            foreach (var confirmed in ConfirmedInterfaces(cs))
            {
                step.Observe(label + ": СК объекта подтверждает интерфейс " + confirmed);
            }

            if (cs is ILocalCoordinateSystem inner)
            {
                var xyz = Triple(Try(() => inner.X), Try(() => inner.Y), Try(() => inner.Z));
                step.Observe(label + ": СК объекта→ILocalCoordinateSystem.X/Y/Z = " + xyz);
                step.Data["xyz_" + label] = xyz;
                if (ContainsTriple(xyz, search))
                {
                    found.Add(label + ": СК объекта→ILocalCoordinateSystem.X/Y/Z");
                }

                foreach (var (axisLabel, axis) in new[]
                         {
                             ("OX", ksObj3dTypeEnum.o3d_axisOX),
                             ("OY", ksObj3dTypeEnum.o3d_axisOY),
                             ("OZ", ksObj3dTypeEnum.o3d_axisOZ),
                         })
                {
                    try
                    {
                        var text = inner.GetVector(axis, out var vx, out var vy, out var vz)
                            ? Triple(vx, vy, vz)
                            : "False";
                        step.Observe(label + ": СК объекта.GetVector(" + axisLabel + ") = " + text);
                        step.Data["vector_" + label + "_" + axisLabel] = text;
                        if (ContainsTriple(text, search))
                        {
                            found.Add(label + ": СК объекта.GetVector(" + axisLabel + ")");
                        }
                    }
                    catch (Exception ex)
                    {
                        step.Observe(label + ": СК объекта.GetVector(" + axisLabel + ") — "
                            + HResult.Describe(ex));
                    }
                }
            }
            else
            {
                step.Observe(label + ": СК объекта НЕ подтверждает ILocalCoordinateSystem — "
                    + "по справке тип данных IModelObject, координат у него нет");
            }

            foreach (var (_, name) in own)
            {
                var text = DescribeValue(SafeGet(cs, name));
                step.Data["read_" + label + "_" + name] = text;
                if (ContainsTriple(text, search))
                {
                    found.Add(label + ": СК объекта." + name);
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe(label + ": " + HResult.Describe(ex));
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.14 ══

    /// <summary>The reopened feature: the same object or one obtained anew?</summary>
    /// <remarks>Why this is needed. RP.12 showed that on a reopened document <c>GetVector</c> returns an IDENTITY
    /// orientation while the bounding box confirms a rotation. From this alone it must NOT be concluded
    /// that the parameters are not stored: an object obtained from the cache before the document finished
    /// rebuilding the model would look exactly the same. Here both versions are told apart: the same
    /// feature is read right after opening and AGAIN OBTAINED after the rebuild, and both the pointers
    /// and the orientation are compared. Without this step the conclusion "the placement is not readable
    /// after reopen" would describe the probe, not the product — the same class as the <c>4/tan</c> case.</remarks>
    private void ReopenedFeatureRoute()
    {
        var step = _report.Begin("RP.14", "Переоткрытый признак: кэш объекта или свежее получение",
            "Теряется ли размещение потому, что объект признака на переоткрытом документе взят из кэша?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var box = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP-route");
            if (box is null)
            {
                step.Fail("брусок не построен");
                return;
            }

            var rotation = RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg);
            var feature = CreateReposition(doc, part, box, rotation, step, "поворот");
            if (feature is null)
            {
                step.Fail("признак не создан");
                return;
            }

            var live = ReadDocumented(feature, step);
            var liveOx = live.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            var liveXyz = live.Values.GetValueOrDefault("ILocalCoordinateSystem.X/Y/Z");
            step.Data["live_get_vector_ox"] = liveOx;
            step.Data["live_xyz"] = liveXyz;
            step.Observe("живой признак: GetVector(OX)=" + liveOx + ", X/Y/Z=" + liveXyz
                + " (ожидание поворота: OX=(0,1,0), X/Y/Z=(5,−5,0))");

            var path = Path.Combine(_options.WorkDir, "rp14-route.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            var part2 = (ksPart)reopened.GetPart(-1);

            // Half 1: the object is taken right after opening, before any rebuild.
            var first = Fetch(reopened, out var ptrFirst);
            var oxFirst = first is null
                ? "<признак не найден>"
                : ReadDocumented(first, step).Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            step.Data["reopen_first_pointer"] = ptrFirst;
            step.Data["reopen_first_get_vector_ox"] = oxFirst;
            step.Observe("сразу после открытия: IUnknown=" + ptrFirst + ", GetVector(OX)=" + oxFirst);

            // The rebuild in the new session is the stage after which the parameters could materialize.
            part2.RebuildModel();
            reopened.RebuildDocument();
            var rows = BodyRows(part2);
            step.Observe("геометрия после сборки: " + Describe(rows));
            step.Data["geometry_after_rebuild"] = Describe(rows);

            // Half 2: the feature is OBTAINED ANEW from the collection, not a reused reference.
            var second = Fetch(reopened, out var ptrSecond);
            var secondReading = second is null ? null : ReadDocumented(second, step);
            var oxSecond = secondReading?.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            var xyzSecond = secondReading?.Values.GetValueOrDefault("ILocalCoordinateSystem.X/Y/Z");
            step.Data["reopen_second_pointer"] = ptrSecond;
            step.Data["reopen_second_get_vector_ox"] = oxSecond;
            step.Data["reopen_second_xyz"] = xyzSecond;
            step.Data["same_object"] = ptrFirst == ptrSecond;
            step.Observe("после сборки, признак получен заново: IUnknown=" + ptrSecond
                + ", GetVector(OX)=" + oxSecond + ", X/Y/Z=" + xyzSecond
                + "; тот же объект, что и сразу после открытия: " + (ptrFirst == ptrSecond));

            // The verdict is set by the ORIENTATION, not by emptiness: an identity axis on a rotated
            // body is either a lost placement or an unusable object, and both cases must be named,
            // not smoothed over.
            var storedRotation = new[] { 0d, 1d, 0d };
            if (oxSecond is null)
            {
                step.Fail("признак на переоткрытом документе не получен заново");
            }
            else if (ContainsTriple(oxSecond, storedRotation))
            {
                step.Pass("размещение читается с переоткрытого документа, если признак получен ЗАНОВО "
                    + "после сборки: GetVector(OX)=" + oxSecond + " — то есть прежнее чтение описывало "
                    + "кэш объекта, а не продукт");
            }
            else
            {
                step.Fail("размещение не отдаёт ни объект сразу после открытия (GetVector(OX)=" + oxFirst
                    + "), ни признак, полученный заново после сборки (GetVector(OX)=" + oxSecond
                    + "), — при габарите, который подтверждает поворот: " + Describe(rows));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    /// <summary>Obtain the body-reposition feature anew — from a fresh container and a fresh collection
    /// element, not from a previously saved reference.</summary>
    /// <remarks>Index access is documented (<c>ibodyrepositions_bodyreposition.html</c>, DOC: «Возвращает
    /// элемент, заданный по индексу», <c>VARIANT Index</c> — DOC: «индекс или имя элемента»). The
    /// element count is PRINTED together with the pointer: without it "the first was taken" is an
    /// assumption that it is the only one, not a measurement. Step RP.16 goes further and identifies
    /// the element BY BODY.</remarks>
    private IBodyReposition? Fetch(ksDocument3D doc, out string pointer)
    {
        try
        {
            var container = Container(doc);
            if (container?.BodyRepositions is not { } collection || collection.Count == 0
                || collection[0] is not IBodyReposition feature)
            {
                pointer = "<признак не найден>";
                return null;
            }

            pointer = Pointer(feature).ToString("x") + " (из " + collection.Count + ")";
            return feature;
        }
        catch (Exception ex)
        {
            pointer = HResult.Describe(ex);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════ RP.15–RP.20 ══
    //
    // Order 19.09.2026 "fix the wrong read of transformations after reopen":
    //   §2 — verify the implementation strictly against the SDK documentation (interface, object,
    //        signature, result handling, sequence of working with the document);
    //   §4 — discriminating checks on an asymmetric 20×10×5 body; the first read after opening
    //        BEFORE any write; a positive control of a REAL translation; a foreign body;
    //        geometry, feature identity and parameter correctness — SEPARATELY.
    //
    // The probe must be clean BEFORE a measurement becomes a fact about the product. The former Fetch
    // took collection[0] and did not enumerate the collection at all; here the enumeration, the name and
    // the body of each element are mandatory, and the content of the OBJECT ITSELF is captured by the
    // documented Position.WriteToFile — otherwise "not readable" would describe the probe, not the
    // product (class 4/tan).

    /// <summary>Reopen: preparing the discriminating cases, the first read BEFORE any write, a positive control
    /// of a real translation, the content of the object itself, the geometry and the reproducibility.</summary>
    private void ReopenDiscriminating()
    {
        var setup = _report.Begin("RP.15", "Переоткрытие: подготовка различающих случаев",
            "Записаны ли различающие преобразования так, что их различает ГЕОМЕТРИЯ, а не только чтение?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            // Body A — rotation +90° about Z through a point OUTSIDE the origin (5,0,0).
            // Body B — a REAL translation: the positive control without which banning the "translate"
            //          value would pass as a fix.
            // Body S — foreign: the edit does not touch it.
            var a = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, setup, "RP15-A");
            var b = ExtrudeRect(doc, part, 200d, 220d, 0d, 10d, 5d, setup, "RP15-B");
            var s = ExtrudeRect(doc, part, 100d, 110d, 0d, 10d, 10d, setup, "RP15-S");
            if (a is null || b is null || s is null)
            {
                setup.Fail("тела не построены");
                return;
            }

            var matrixRot = RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg);
            var matrixTr = Translate(TranslateMain);
            var expectRot = ExpectedBbox(matrixRot, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
            var expectTr = ExpectedBbox(matrixTr, new[] { 200d, 0d, 0d }, new[] { 220d, 10d, 5d });

            var rotate = CreateReposition(doc, part, a, matrixRot, setup,
                "A: поворот +90° вокруг Z через (5,0,0)");
            var translate = CreateReposition(doc, part, b, matrixTr, setup,
                "B: перенос (7,−11,13) — положительный контроль");
            if (rotate is null || translate is null)
            {
                setup.Fail("признаки не созданы");
                return;
            }

            var live = BodyRows(part);
            setup.Observe("живая геометрия: " + Describe(live));
            setup.Data["live_geometry"] = Describe(live);
            setup.Data["expected_rotate"] = Box(expectRot.Min, expectRot.Max);
            setup.Data["expected_translate"] = Box(expectTr.Min, expectTr.Max);

            var rotateOk = FindByBbox(live, expectRot.Min, expectRot.Max) is not null;
            var translateOk = FindByBbox(live, expectTr.Min, expectTr.Max) is not null;
            var foreignOk = live.Any(r => Near(r, 100d, 0d, 0d, 110d, 10d, 10d));
            setup.Data["rotate_geometry_matches"] = rotateOk;
            setup.Data["translate_geometry_matches"] = translateOk;
            setup.Data["foreign_untouched"] = foreignOk;
            if (!rotateOk || !translateOk || !foreignOk)
            {
                setup.Fail("живая геометрия не совпала с независимым расчётом: поворот=" + rotateOk
                    + ", перенос=" + translateOk + ", постороннее=" + foreignOk);
                return;
            }

            // The live read and the CONTENT OF THE OBJECT ITSELF — the reference for comparison with the reopened one.
            var liveRot = ReadDocumented(rotate, setup);
            var liveTr = ReadDocumented(translate, setup);
            var liveRotOx = liveRot.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            var liveTrOx = liveTr.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            setup.Data["live_rotate_get_vector_ox"] = liveRotOx;
            setup.Data["live_translate_get_vector_ox"] = liveTrOx;
            setup.Observe("живой поворот: GetVector(OX)=" + liveRotOx + " (ожидание (0,1,0))");
            setup.Observe("живой перенос: GetVector(OX)=" + liveTrOx
                + " (у настоящего переноса ориентация ЕДИНИЧНАЯ — это ожидание, а не дефект)");

            var liveRotFile = DumpPosition(rotate, Path.Combine(_options.WorkDir, "rp15-live-rotate.txt"),
                setup, "живой поворот");
            var liveTrFile = DumpPosition(translate, Path.Combine(_options.WorkDir, "rp15-live-translate.txt"),
                setup, "живой перенос");
            setup.Data["live_rotate_serialized"] = liveRotFile;
            setup.Data["live_translate_serialized"] = liveTrFile;

            setup.Pass("различающие преобразования записаны, габариты совпали с независимым расчётом, "
                + "постороннее тело не изменилось");

            var path = Path.Combine(_options.WorkDir, "rp15-discriminating.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            setup.Observe("SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                setup.Fail("документ не переоткрылся");
                return;
            }

            var part2 = (ksPart)reopened.GetPart(-1);

            // ───────────────────────────────────────────────────────── RP.16 ──
            var first = _report.Begin("RP.16", "Переоткрытие: первое чтение ДО записи — записанный поворот",
                "Отдаёт ли документированный маршрут ЗАПИСАННЫЙ поворот, если признак найден перечислением "
                + "и прочитан ДО сборки и ДО любой записи?");

            var elements = Enumerate(reopened, first);
            first.Data["count"] = elements.Count;

            // FIRST read: before the rebuild, before the edit, before anything that could restore the correct value.
            var before = new List<(string Who, Reading Reading)>();
            foreach (var (index, name, feature) in elements)
            {
                before.Add(("#" + index + " " + name, ReadDocumented(feature, first)));
            }

            first.Observe("сразу после открытия, ДО сборки:");
            for (var i = 0; i < before.Count; i++)
            {
                var reading = before[i].Reading;
                first.Observe("  " + before[i].Who
                    + ": GetVector(OX)=" + reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)")
                    + ", X/Y/Z=" + reading.Values.GetValueOrDefault("ILocalCoordinateSystem.X/Y/Z")
                    + ", Valid=" + reading.Values.GetValueOrDefault("ILocalCoordinateSystem.Valid")
                    + ", OrientationType="
                    + reading.Values.GetValueOrDefault("ILocalCoordinateSystem.OrientationType"));
            }

            part2.RebuildModel();
            reopened.RebuildDocument();

            var after = new List<(string Who, Reading Reading)>();
            foreach (var (index, name, feature) in elements)
            {
                after.Add(("#" + index + " " + name, ReadDocumented(feature, first)));
            }

            var rowsAfter = BodyRows(part2);
            first.Observe("геометрия после сборки: " + Describe(rowsAfter));
            first.Data["geometry_after_rebuild"] = Describe(rowsAfter);

            var rotatedRow = FindByBbox(rowsAfter, expectRot.Min, expectRot.Max);
            var translatedRow = FindByBbox(rowsAfter, expectTr.Min, expectTr.Max);
            first.Data["rotated_body_found"] = rotatedRow?.Describe() ?? "<нет>";
            first.Data["translated_body_found"] = translatedRow?.Describe() ?? "<нет>";

            // The feature is identified by the BODY it moves, WITHIN THE SAME document: all features
            // share one name, the order is an assumption, and RepositionBody is documented.
            var rotateIndex = rotatedRow is null ? -1 : IndexOfBody(elements, rotatedRow, first, "поворот");
            var translateIndex = translatedRow is null ? -1 : IndexOfBody(elements, translatedRow, first, "перенос");
            first.Data["rotate_element"] = rotateIndex;
            first.Data["translate_element"] = translateIndex;

            if (rotatedRow is null)
            {
                first.Fail("повёрнутое тело не найдено после переоткрытия — измерять нечего");
            }
            else if (rotateIndex < 0 || rotateIndex >= after.Count)
            {
                first.Fail("признак поворота не сопоставлен с телом после переоткрытия — "
                    + "вопрос остаётся открытым, а не решается по порядку");
            }
            else
            {
                var ox = after[rotateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
                first.Data["reopened_rotate_get_vector_ox"] = ox;
                first.Data["reopened_rotate_xyz"] =
                    after[rotateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.X/Y/Z");
                // A full map of the documented read of the reopened feature — so that the record keeps
                // EVERYTHING that was tried, not only the member by which the verdict is judged.
                first.Data["reopened_rotate_full_reading"] = Join(after[rotateIndex].Reading.Values);
                first.Data["reopened_rotate_valid"] =
                    after[rotateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.Valid");
                first.Data["live_rotate_valid"] =
                    liveRot.Values.GetValueOrDefault("ILocalCoordinateSystem.Valid");
                first.Observe("полное документированное чтение переоткрытого признака поворота: "
                    + Join(after[rotateIndex].Reading.Values));
                if (ContainsTriple(ox ?? string.Empty, new[] { 0d, 1d, 0d }))
                {
                    first.Pass("записанный поворот читается с переоткрытого документа: GetVector(OX)=" + ox);
                }
                else
                {
                    first.Fail("на переоткрытом документе записанный поворот +90° вокруг Z НЕ читается: "
                        + "GetVector(OX)=" + ox + " вместо (0, 1, 0) — при геометрии "
                        + rotatedRow.Describe() + ", которая поворот подтверждает");
                }
            }

            // ───────────────────────────────────────────────────────── RP.17 ──
            var control = _report.Begin("RP.17", "Переоткрытие: положительный контроль настоящего переноса",
                "Остаётся ли значение «translate» законным там, где поворота НЕ записывали?");

            control.Data["translated_body_found"] = translatedRow?.Describe() ?? "<нет>";
            if (translatedRow is null)
            {
                control.Fail("перенесённое тело не найдено после переоткрытия — контроль не поставлен");
            }
            else if (translateIndex < 0 || translateIndex >= after.Count)
            {
                control.Fail("признак переноса не сопоставлен с телом после переоткрытия");
            }
            else
            {
                var ox = after[translateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
                var oy = after[translateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OY)");
                var oz = after[translateIndex].Reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OZ)");
                control.Data["reopened_translate_get_vector"] = new[] { ox, oy, oz };
                var identity = ContainsTriple(ox ?? string.Empty, new[] { 1d, 0d, 0d })
                    && ContainsTriple(oy ?? string.Empty, new[] { 0d, 1d, 0d })
                    && ContainsTriple(oz ?? string.Empty, new[] { 0d, 0d, 1d });
                if (identity)
                {
                    control.Pass("у признака, которому поворота не записывали, оси ЕДИНИЧНЫ, и тело стоит "
                        + "ровно на записанном переносе (" + translatedRow.Describe()
                        + ") — значит единичная ориентация сама по себе не запрещена и значение "
                        + "«translate» остаётся законным");
                }
                else
                {
                    control.Fail("у признака БЕЗ поворота оси не единичны (" + ox + " / " + oy + " / " + oz
                        + ") — положительный контроль не поставлен, и различать случаи нечем");
                }
            }

            // ───────────────────────────────────────────────────────── RP.18 ──
            var content = _report.Begin("RP.18", "Переоткрытие: содержимое САМОГО объекта",
                "Лежит ли в объекте признака на переоткрытом документе записанная ориентация, или объект пуст?");

            // The content is captured for ALL elements, not only the matched one: otherwise a matching
            // failure would hide the very answer this step was written for (lesson of step SP.6).
            var reopenedFiles = new List<string>();
            for (var i = 0; i < elements.Count; i++)
            {
                reopenedFiles.Add(DumpPosition(elements[i].Feature,
                    Path.Combine(_options.WorkDir, "rp15-reopened-" + i + ".txt"), content,
                    "переоткрытый элемент " + i));
            }

            var reopenedRotFile = rotateIndex >= 0 && rotateIndex < reopenedFiles.Count
                ? reopenedFiles[rotateIndex]
                : "<признак поворота не сопоставлен>";
            var reopenedTrFile = translateIndex >= 0 && translateIndex < reopenedFiles.Count
                ? reopenedFiles[translateIndex]
                : "<признак переноса не сопоставлен>";

            content.Data["live_rotate_serialized"] = liveRotFile;
            content.Data["reopened_rotate_serialized"] = reopenedRotFile;
            content.Data["live_translate_serialized"] = liveTrFile;
            content.Data["reopened_translate_serialized"] = reopenedTrFile;
            content.Data["reopened_all_serialized"] = reopenedFiles.ToArray();

            if (liveRotFile == reopenedRotFile)
            {
                content.Pass("содержимое объекта на переоткрытом документе совпало с живым — объект "
                    + "ЗАПОЛНЕН, и отказ чтения описывает продукт, а не пустой объект: " + reopenedRotFile);
            }
            else
            {
                content.Fail("содержимое объекта на переоткрытом документе ОТЛИЧАЕТСЯ от живого: живой «"
                    + liveRotFile + "», переоткрытый «" + reopenedRotFile
                    + "» — документированное чтение переоткрытого признака описывает пустой объект");
            }

            // ───────────────────────────────────────────────────────── RP.19 ──
            var identityStep = _report.Begin("RP.19", "Переоткрытие: геометрия, тождество признака, постороннее тело",
                "Сохранились ли геометрия, тождество признака и постороннее тело — отдельно от чтения параметров?");

            var foreignAfter = rowsAfter.Any(r => Near(r, 100d, 0d, 0d, 110d, 10d, 10d));
            var pointersBefore = elements.Select(e => Pointer(e.Feature).ToString("x")).ToArray();
            var pointersAfter = Enumerate(reopened, identityStep).Select(e => Pointer(e.Feature).ToString("x")).ToArray();
            var sameObjects = pointersBefore.SequenceEqual(pointersAfter, StringComparer.Ordinal);
            identityStep.Data["geometry_after_reopen"] = Describe(rowsAfter);
            identityStep.Data["rotated_body_found"] = rotatedRow?.Describe() ?? "<нет>";
            identityStep.Data["translated_body_found"] = translatedRow?.Describe() ?? "<нет>";
            identityStep.Data["foreign_untouched"] = foreignAfter;
            identityStep.Data["feature_count"] = elements.Count;
            identityStep.Data["rotate_element"] = rotateIndex;
            identityStep.Data["translate_element"] = translateIndex;
            identityStep.Data["same_objects_after_rebuild"] = sameObjects;
            identityStep.Observe("указатели признаков до и после сборки: " + string.Join(", ", pointersBefore)
                + " / " + string.Join(", ", pointersAfter));
            if (rotatedRow is not null && translatedRow is not null && foreignAfter
                && rotateIndex >= 0 && translateIndex >= 0)
            {
                identityStep.Pass("геометрия пережила переоткрытие (поворот " + rotatedRow.Describe()
                    + ", перенос " + translatedRow.Describe() + "), постороннее тело не изменилось, "
                    + "признаков в коллекции " + elements.Count + ", и каждый опознан ПО ТЕЛУ, "
                    + "которое перемещает (тот же объект до и после сборки: " + sameObjects + ")");
            }
            else
            {
                identityStep.Fail("после переоткрытия геометрия, постороннее тело или тождество признаков "
                    + "не совпали: поворот=" + (rotatedRow?.Describe() ?? "<нет>") + ", перенос="
                    + (translatedRow?.Describe() ?? "<нет>") + ", постороннее=" + foreignAfter
                    + ", элемент поворота=" + rotateIndex + ", элемент переноса=" + translateIndex);
            }

            // ───────────────────────────────────────────────────────── RP.21 ──
            // The last documented route to a LCS: IAuxiliaryGeomContainer.LocalCoordinateSystems
            // (iauxiliarygeomcontainer.html — DOC: «Позволяет получить коллекции объектов вспомогательной
            // геометрии (ЛСК, сплайн и т.д)», obtained from IPart7 via QueryInterface). Checked BEFORE
            // any write: if the placement lives there, it must be readable here.
            var auxRoute = _report.Begin("RP.21", "Переоткрытие: документированная коллекция ЛСК",
                "Держит ли записанное размещение документированная коллекция локальных систем координат?");

            try
            {
                if (_app.TransferInterface(part2, 2, 0) is not IModelObject part7)
                {
                    auxRoute.Unknown("деталь не переносится в API7 как IModelObject");
                }
                else if (part7 is not IAuxiliaryGeomContainer auxiliary)
                {
                    auxRoute.Unknown("деталь не отвечает QI(IAuxiliaryGeomContainer) — маршрут недостижим");
                }
                else if (auxiliary.LocalCoordinateSystems is not { } systems)
                {
                    auxRoute.Fail("IAuxiliaryGeomContainer.LocalCoordinateSystems → null: "
                        + "документированная коллекция ЛСК пуста");
                }
                else
                {
                    auxRoute.Observe("элементов ЛСК в переоткрытом документе: " + Api5.Raw(systems.Count));
                    var carrying = 0;
                    for (var i = 0; i < systems.Count; i++)
                    {
                        if (systems[i] is not ILocalCoordinateSystem system)
                        {
                            auxRoute.Observe("ЛСК " + i + ": элемент не ILocalCoordinateSystem");
                            continue;
                        }

                        var ox = ReadAxisOf(system, ksObj3dTypeEnum.o3d_axisOX);
                        auxRoute.Observe("ЛСК " + i + ": GetVector(OX)=" + ox
                            + ", X/Y/Z=" + Triple(Try(() => system.X), Try(() => system.Y), Try(() => system.Z))
                            + ", WriteToFile " + DumpLocalSystem(system,
                                Path.Combine(_options.WorkDir, "rp15-lcs-" + i + ".txt")));
                        if (ContainsTriple(ox ?? string.Empty, new[] { 0d, 1d, 0d }))
                        {
                            carrying++;
                        }
                    }

                    auxRoute.Data["count"] = systems.Count;
                    auxRoute.Data["carrying_rotation"] = carrying;
                    if (carrying > 0)
                    {
                        auxRoute.Pass("размещение читается документированной коллекцией ЛСК: записанная "
                            + "ориентация найдена в " + carrying + " элементе(ах)");
                    }
                    else
                    {
                        auxRoute.Fail("документированная коллекция ЛСК размещения не несёт: элементов "
                            + systems.Count + ", и ни один не отдал записанную ориентацию");
                    }
                }
            }
            catch (Exception ex)
            {
                auxRoute.Unknown("коллекция ЛСК: " + HResult.Describe(ex));
            }

            // ───────────────────────────────────────────────────────── RP.20 ──
            var durability = _report.Begin("RP.20", "Переоткрытие: переживает ли верное чтение следующее открытие",
                "Держится ли верное чтение после записи, или оно живёт только в сессии записи?");

            if (rotateIndex < 0 || rotateIndex >= elements.Count)
            {
                durability.Fail("признак поворота не сопоставлен — вопрос о записи не поставлен");
            }
            else
            {
                var target = elements[rotateIndex].Feature;

                // Stage 1: the documented Update() on the reopened object — it does not read, it APPLIES.
                var beforeUpdate = BodyRows(part2);
                var updated = SafeBool(() => target.Update());
                part2.RebuildModel();
                reopened.RebuildDocument();
                var afterUpdate = BodyRows(part2);
                durability.Observe("Update() на переоткрытом объекте=" + Api5.Raw(updated)
                    + "; геометрия до " + Describe(beforeUpdate) + ", после " + Describe(afterUpdate));
                durability.Data["geometry_before_update"] = Describe(beforeUpdate);
                durability.Data["geometry_after_update"] = Describe(afterUpdate);
                durability.Data["update_result"] = Api5.Raw(updated);

                // Stage 2: writing the same rotation — does it restore the correct read.
                var rewrote = EditReposition(reopened, part2, target, null, matrixRot, durability);
                var rowsAfterWrite = BodyRows(part2);
                durability.Data["geometry_after_write"] = Describe(rowsAfterWrite);
                durability.Observe("после повторной записи геометрия: " + Describe(rowsAfterWrite));
                var afterWrite = ReadDocumented(target, durability);
                var oxAfterWrite = afterWrite.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
                durability.Data["get_vector_ox_after_write"] = oxAfterWrite;
                durability.Observe("после записи того же поворота: GetVector(OX)=" + oxAfterWrite
                    + " (Update()=" + rewrote + ")");
                var writeRestores = ContainsTriple(oxAfterWrite ?? string.Empty, new[] { 0d, 1d, 0d });

                // Stage 3: save, close, open AGAIN — does the correct read hold.
                var path2 = Path.Combine(_options.WorkDir, "rp15-discriminating-2.m3d");
                if (File.Exists(path2))
                {
                    File.Delete(path2);
                }

                durability.Observe("SaveAs " + Api5.Raw(SafeBool(() => reopened.SaveAs(path2))));
                TryClose(reopened);
                var again = (ksDocument3D)_app.Document3D();
                try
                {
                    if (Api5.SafeBool(() => again.Open(path2, true)) != true)
                    {
                        durability.Fail("документ не переоткрылся во второй раз");
                    }
                    else
                    {
                        var part3 = (ksPart)again.GetPart(-1);
                        var rowsAgain = BodyRows(part3);
                        durability.Data["geometry_second_reopen"] = Describe(rowsAgain);
                        var elementsAgain = Enumerate(again, durability);
                        var rotatedAgain = FindByBbox(rowsAgain, expectRot.Min, expectRot.Max);
                        var againIndex = rotatedAgain is null ? -1
                            : IndexOfBody(elementsAgain, rotatedAgain, durability,
                                "поворот (второе переоткрытие)");
                        if (againIndex < 0 || againIndex >= elementsAgain.Count)
                        {
                            durability.Fail("признак поворота не сопоставлен после второго переоткрытия");
                        }
                        else
                        {
                            var reading = ReadDocumented(elementsAgain[againIndex].Feature, durability);
                            var ox = reading.Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
                            durability.Data["get_vector_ox_second_reopen"] = ox;
                            durability.Observe("после второго переоткрытия: GetVector(OX)=" + ox);
                            if (writeRestores && !ContainsTriple(ox ?? string.Empty, new[] { 0d, 1d, 0d }))
                            {
                                durability.Fail("верное чтение живёт ТОЛЬКО в сессии записи: сразу после записи "
                                    + "GetVector(OX)=" + oxAfterWrite + ", а после сохранения, закрытия и "
                                    + "открытия снова " + ox + " — публиковать такое значение значило бы "
                                    + "подменить чтение кэшем сеанса");
                            }
                            else if (writeRestores)
                            {
                                durability.Pass("верное чтение пережило сохранение, закрытие и открытие: "
                                    + "GetVector(OX)=" + ox);
                            }
                            else
                            {
                                durability.Fail("запись НЕ вернула верное чтение: сразу после записи "
                                    + "GetVector(OX)=" + oxAfterWrite);
                            }
                        }
                    }
                }
                finally
                {
                    TryClose(again);
                }
            }
        }
        catch (Exception ex)
        {
            _report.Note("RP.15", "исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    /// <summary>
    /// Enumerate ALL elements of the feature collection and name each. The former <c>Fetch</c> took
    /// <c>collection[0]</c>: index access is documented
    /// (<c>ibodyrepositions_bodyreposition.html</c> — DOC: «Возвращает элемент, заданный по индексу»,
    /// <c>VARIANT Index</c> — DOC: «индекс или имя элемента»), but WITHOUT the enumeration neither the
    /// element count nor the fact that the right one was read is visible.
    /// </summary>
    private List<(int Index, string Name, IBodyReposition Feature)> Enumerate(ksDocument3D doc, ProbeStep step)
    {
        var found = new List<(int, string, IBodyReposition)>();
        try
        {
            if (Container(doc)?.BodyRepositions is not { } collection)
            {
                step.Observe("коллекция BodyRepositions недоступна");
                return found;
            }

            step.Observe("BodyRepositions.Count = " + Api5.Raw(collection.Count));
            for (var i = 0; i < collection.Count; i++)
            {
                if (collection[i] is not IBodyReposition feature)
                {
                    step.Observe("элемент " + i + ": не IBodyReposition");
                    continue;
                }

                var name = SafeGet(feature, "Name") as string ?? "<без имени>";
                found.Add((i, name, feature));
                step.Observe("элемент " + i + ": IUnknown=" + Pointer(feature).ToString("x") + ", Name=" + name);
            }
        }
        catch (Exception ex)
        {
            step.Observe("перечисление: " + HResult.Describe(ex));
        }

        return found;
    }

    /// <summary>The index of the element that moves the given body of THE SAME document. Identification goes by
    /// the DOCUMENTED <c>IBodyReposition.RepositionBody</c>, not by order and not by name: in one
    /// document all features share a name, and order is an assumption, not a measurement.</summary>
    /// <remarks>Comparing <c>RepositionBody</c> of a live feature with reopened elements must NOT be done: the
    /// live feature belongs to an already closed document, and its body is an object of ANOTHER document.
    /// The first revision of this step did exactly that, and a matching failure looked like a product
    /// failure. The body is taken from THE SAME document whose elements are enumerated.</remarks>
    private int IndexOfBody(
        List<(int Index, string Name, IBodyReposition Feature)> elements, BodyRow target,
        ProbeStep step, string label)
    {
        try
        {
            if (target.Element is null)
            {
                step.Observe(label + ": у тела нет элемента дерева — сопоставление невозможно");
                return -1;
            }

            var pointer = Pointer(_app.TransferInterface(target.Element, 2, 0));
            var seen = new List<string>();
            for (var i = 0; i < elements.Count; i++)
            {
                var moved = elements[i].Feature.RepositionBody;
                seen.Add(moved is null ? "<пусто>" : Pointer(moved).ToString("x"));
                if (moved is not null && Pointer(moved) == pointer)
                {
                    step.Observe(label + ": элемент " + i + " перемещает тело " + target.Describe());
                    return i;
                }
            }

            step.Observe(label + ": ни один из " + elements.Count + " элементов не назвал тело "
                + target.Describe() + "; искали IUnknown=" + pointer.ToString("x")
                + ", элементы назвали: " + string.Join(", ", seen));
            return -1;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": сопоставление по телу — " + HResult.Describe(ex));
            return -1;
        }
    }

    /// <summary>
    /// The content of the feature OBJECT itself, written by the documented
    /// <c>ILocalCoordinateSystem.WriteToFile</c> (<c>ilocalcoordinatesystem_writetofile.html</c>).
    /// This is what lies IN THE OBJECT, not what the probe thinks about it: the distinction "object
    /// empty" versus "object filled but read incorrectly" is unresolvable without this stage.
    /// </summary>
    private string DumpPosition(IBodyReposition feature, string path, ProbeStep step, string label)
    {
        try
        {
            var local = feature.Position;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var written = SafeBool(() => local.WriteToFile(path));
            var content = File.Exists(path)
                ? File.ReadAllText(path).Replace("\r", " ").Replace("\n", " | ").Trim()
                : "<файл не создан>";
            step.Observe(label + ": WriteToFile=" + Api5.Raw(written) + " → " + content);
            return content;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": WriteToFile — " + HResult.Describe(ex));
            return "<ошибка: " + HResult.Describe(ex) + ">";
        }
    }

    private static BodyRow? FindByBbox(List<BodyRow> rows, double[] min, double[] max) =>
        rows.FirstOrDefault(row => Near(row, min[0], min[1], min[2], max[0], max[1], max[2]));

    /// <summary>A LCS axis via the documented <c>GetVector</c>; a failure is named, not hidden.</summary>
    private static string ReadAxisOf(ILocalCoordinateSystem system, ksObj3dTypeEnum axis)
    {
        try
        {
            return system.GetVector(axis, out var x, out var y, out var z) ? Triple(x, y, z) : "False";
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>The LCS content written by its own <c>WriteToFile</c>.</summary>
    private static string DumpLocalSystem(ILocalCoordinateSystem system, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var written = SafeBool(() => system.WriteToFile(path));
            var content = File.Exists(path)
                ? File.ReadAllText(path).Replace("\r", " ").Replace("\n", " | ").Trim()
                : "<файл не создан>";
            return Api5.Raw(written) + ": " + content;
        }
        catch (Exception ex)
        {
            return "<ошибка: " + HResult.Describe(ex) + ">";
        }
    }

    private static string Box(double[] min, double[] max) =>
        "(" + string.Join(", ", min.Select(v => Api5.Num(v))) + ")…("
            + string.Join(", ", max.Select(v => Api5.Num(v))) + ")";

    // ══════════════════════════════════════════════════════════════ RP.22 ══

    /// <summary>Reopen: the documented Euler-angle mode.</summary>
    /// <remarks>
    /// <c>ILocalCSEulerParam.RotationAngle</c> is documented as READ AND WRITE (DOC: «Свойство
    /// позволяет устанавливать и получать угол вращения», <c>ilocalcseulerparam_rotationangle.html</c>),
    /// and it is the only member of the whole chain with such access to an ANGLE. The previous revision
    /// dismissed it CONDITIONALLY — "it is a DIFFERENT orientation mode, and product features have
    /// <c>OrientationType = 0</c>" — i.e. by a state created by OUR OWN write route, not by the help
    /// page. Here the mode is set explicitly, by the documented member <c>OrientationType</c>, and it is
    /// checked directly: does the angle survive a reopen.
    /// </remarks>
    private void EulerRoute()
    {
        var step = _report.Begin("RP.22", "Переоткрытие: документированный режим углов Эйлера",
            "Переживает ли переоткрытие угол, записанный документированным "
            + "ILocalCSEulerParam.RotationAngle, и РАЗЛИЧАЕТ ли чтение два разных угла?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var body = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP22-E");
            if (body is null)
            {
                step.Fail("тело не построено");
                return;
            }

            if (Container(doc)?.BodyRepositions?.Add() is not IBodyReposition feature)
            {
                step.Fail("BodyRepositions.Add() недоступен");
                return;
            }

            if (_app.TransferInterface(body.Element!, 2, 0) is not IKompasAPIObject moved)
            {
                step.Fail("тело не переносится в API7");
                return;
            }

            feature.RepositionBody = moved;
            var local = feature.Position;

            // Documented order: first the orientation mode, then the parameter interface of THAT mode
            // (ilocalcoordinatesystem_localcsparameters.html — DOC: «В зависимости от типа
            // OrientationType интерфейс параметров должен приводиться к…»).
            local.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
            step.Observe("OrientationType := ksEulerCorners, прочитано обратно: "
                + EnumName(() => (int)local.OrientationType, "ksOrientationTypeEnum"));

            if (local.LocalCSParameters is not ILocalCSEulerParam euler)
            {
                step.Fail("при OrientationType=ksEulerCorners LocalCSParameters не подтверждает "
                    + "ILocalCSEulerParam — документированный интерфейс параметров недостижим");
                return;
            }

            // The FIRST angle is chosen so that it does NOT coincide with the second: without two
            // different values "90 was read" would not differ from "the member returns a constant".
            const double firstAngle = 30d;
            const double secondAngle = 90d;
            euler.RotationAngle = firstAngle;
            step.Observe("RotationAngle := " + Api5.Num(firstAngle) + ", прочитано обратно до Update(): "
                + Num(() => euler.RotationAngle));

            var updated = feature.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("Update()=" + updated);
            var liveAngle = Num(() => euler.RotationAngle);
            var liveOx = ReadAxisOf(local, ksObj3dTypeEnum.o3d_axisOX);
            var liveValid = Api5.Raw(SafeBool(() => local.Valid));
            step.Observe("живой: OrientationType=" + EnumName(() => (int)local.OrientationType, "ksOrientationTypeEnum")
                + ", RotationAngle=" + liveAngle + ", GetVector(OX)=" + liveOx + ", Valid=" + liveValid);
            var liveGeometry = Describe(BodyRows(part));
            step.Observe("живая геометрия: " + liveGeometry);
            step.Data["live_geometry"] = liveGeometry;
            step.Data["live_rotation_angle"] = liveAngle;
            step.Data["live_get_vector_ox"] = liveOx;
            step.Data["live_valid"] = liveValid;
            step.Data["live_orientation_type"] =
                EnumName(() => (int)local.OrientationType, "ksOrientationTypeEnum");

            // ── session 2: the first read BEFORE the rebuild and BEFORE any write ──
            var path = Path.Combine(_options.WorkDir, "rp22-euler.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            var (firstRead, firstElement) = ReadEulerAngle(reopened, step, "первое переоткрытие");
            if (firstRead is null || firstElement is null)
            {
                step.Fail("на переоткрытом документе признак не найден — измерять нечего");
                return;
            }

            var angleAfterFirst = Try(() => ((ILocalCSEulerParam)firstRead.LocalCSParameters).RotationAngle);
            step.Data["angle_after_first_reopen"] = Num(
                () => ((ILocalCSEulerParam)firstRead.LocalCSParameters).RotationAngle);
            step.Data["orientation_after_first_reopen"] =
                EnumName(() => (int)firstRead.OrientationType, "ksOrientationTypeEnum");
            step.Data["get_vector_ox_after_first_reopen"] =
                ReadAxisOf(firstRead, ksObj3dTypeEnum.o3d_axisOX);
            step.Data["valid_after_first_reopen"] = Api5.Raw(SafeBool(() => firstRead.Valid));

            // ── session 3: a SECOND, different angle — the discriminating half of the pair ──
            var part2 = (ksPart)reopened.GetPart(-1);
            part2.RebuildModel();
            reopened.RebuildDocument();
            step.Observe("геометрия после сборки: " + Describe(BodyRows(part2)));

            var secondFeature = firstElement!;
            var secondLocal = secondFeature.Position;
            secondLocal.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
            if (secondLocal.LocalCSParameters is not ILocalCSEulerParam secondEuler)
            {
                step.Fail("на переоткрытом документе LocalCSParameters не подтверждает ILocalCSEulerParam");
                return;
            }

            secondEuler.RotationAngle = secondAngle;
            step.Observe("в переоткрытой сессии RotationAngle := " + Api5.Num(secondAngle)
                + ", Update()=" + secondFeature.Update());
            part2.RebuildModel();
            reopened.RebuildDocument();
            step.Observe("геометрия после второй записи: " + Describe(BodyRows(part2)));
            step.Observe("SaveAs " + Api5.Raw(SafeBool(() => reopened.SaveAs(path))));
            reopened.close();

            var third = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => third.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся во второй раз");
                return;
            }

            try
            {
                var (secondRead, _) = ReadEulerAngle(third, step, "второе переоткрытие");
                if (secondRead is null)
                {
                    step.Fail("во второй переоткрытой сессии признак не найден — измерять нечего");
                    return;
                }

                var angleAfterSecond = Try(
                    () => ((ILocalCSEulerParam)secondRead.LocalCSParameters).RotationAngle);
                step.Data["angle_after_second_reopen"] = Num(
                    () => ((ILocalCSEulerParam)secondRead.LocalCSParameters).RotationAngle);

                var firstOk = angleAfterFirst is { } f && Math.Abs(f - firstAngle) < 1e-6;
                var secondOk = angleAfterSecond is { } s && Math.Abs(s - secondAngle) < 1e-6;
                step.Data["first_angle_matches"] = firstOk;
                step.Data["second_angle_matches"] = secondOk;

                if (firstOk && secondOk)
                {
                    step.Pass("угол, записанный документированным ILocalCSEulerParam.RotationAngle, "
                        + "переживает переоткрытие И чтение различает два разных угла: после первого "
                        + "переоткрытия " + Api5.Num(firstAngle) + " (ожидание " + Api5.Num(firstAngle)
                        + "), после второго " + Api5.Num(secondAngle) + " (ожидание "
                        + Api5.Num(secondAngle) + ") — то есть член читает, а не отдаёт постоянное");
                }
                else
                {
                    step.Fail("угол режима Эйлера переоткрытие НЕ переживает: после первого "
                        + "переоткрытия " + (angleAfterFirst is { } fv ? Api5.Num(fv) : "<не прочитан>")
                        + " (ожидание " + Api5.Num(firstAngle) + "), после второго "
                        + (angleAfterSecond is { } sv ? Api5.Num(sv) : "<не прочитан>")
                        + " (ожидание " + Api5.Num(secondAngle) + ")");
                }
            }
            finally
            {
                TryClose(third);
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.24 ══

    /// <summary>The documented PARAMETRIC route as a whole: three Euler angles, an arbitrary axis and the LCS
    /// origin — which of these survives a reopen.</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> RP.22 measured ONE angle (<c>RotationAngle</c>). That is not enough
    /// for the read requirement, for two reasons. First: the product can rotate about an ARBITRARY axis,
    /// and if the parametric route expresses only coordinate axes it does not close the requirement.
    /// Second: the LCS has an ORIGIN (<c>X/Y/Z</c>), and in RP.22 it was not readable after a reopen —
    /// i.e. "the parameters survive a reopen" referred to the angle, not to the placement as a whole.
    /// Here THREE angles are set at once — <c>RotationAngle</c>, <c>NutationAngle</c>,
    /// <c>PrecessionAngle</c> (<c>ilocalcseulerparam_props.html</c>: all three documented as read AND
    /// write, type <c>double</c>) — and with three DIFFERENT magnitudes.
    /// <b>The angle multiplication order is NOT guessed.</b> The help page specifies it with a figure
    /// (<c>rotation_pict.html</c>), not text, so the probe does not build the expected rotation from the
    /// angles. It reads the LIVE matrix (<c>GetVector</c> along three axes) and requires the body's
    /// bounding box to match THAT matrix: geometry and matrix must say the same thing. The rotation axis
    /// is computed FROM THE MEASURED matrix, not assumed — and the question "does the route express an
    /// arbitrary axis" is settled by measurement, not by argument.
    /// <b>The discriminating pair.</b> The second document is set with a DIFFERENT angle triple: the
    /// read must return to each document ITS triple. Without that "30 was read" would not differ from
    /// "what the probe itself put in came back".
    /// </remarks>
    private void EulerAllAnglesRoute()
    {
        var step = _report.Begin("RP.24", "Параметрический маршрут: три угла, произвольная ось и начало ЛСК",
            "Переживает ли переоткрытие тройка углов Эйлера целиком, выражает ли маршрут "
            + "произвольную ось, и сохраняется ли читаемость начала ЛСК?");

        // The third case is MANDATORY and was added after the first revision of this step. In it the LCS
        // origin was not written at all, and "after reopen X/Y/Z = (0, 0, 0)" was declared the limit of
        // the translation — even though zero was never going to go anywhere. An observation without a
        // discriminating half is not a measurement: for the question "does the ORIGIN survive a reopen"
        // to be settled, the origin must FIRST be written non-zero, and only then read.
        var cases = new[]
        {
            (Label: "A", Rotation: 30d, Nutation: 40d, Precession: 50d, Origin: (double[]?)null),
            (Label: "B", Rotation: 10d, Nutation: 20d, Precession: 35d, Origin: (double[]?)null),
            (Label: "C", Rotation: 30d, Nutation: 40d, Precession: 50d,
                Origin: (double[]?)new[] { 7d, -11d, 13d }),
        };

        var matched = new List<string>();
        var failed = new List<string>();

        foreach (var item in cases)
        {
            var doc = NewPart(out var part);
            ksDocument3D? reopened = null;
            try
            {
                var body = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP24-" + item.Label);
                if (body is null)
                {
                    step.Observe(item.Label + ": тело не построено");
                    continue;
                }

                if (Container(doc)?.BodyRepositions?.Add() is not IBodyReposition feature)
                {
                    step.Observe(item.Label + ": BodyRepositions.Add() недоступен");
                    continue;
                }

                if (_app.TransferInterface(body.Element!, 2, 0) is not IKompasAPIObject moved)
                {
                    step.Observe(item.Label + ": тело не переносится в API7");
                    continue;
                }

                feature.RepositionBody = moved;
                var local = feature.Position;

                // Documented order: first the orientation mode, then the parameter interface of THAT
                // mode (ilocalcoordinatesystem_localcsparameters.html).
                local.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
                if (local.LocalCSParameters is not ILocalCSEulerParam euler)
                {
                    step.Observe(item.Label + ": LocalCSParameters не подтверждает ILocalCSEulerParam");
                    continue;
                }

                euler.RotationAngle = item.Rotation;
                euler.NutationAngle = item.Nutation;
                euler.PrecessionAngle = item.Precession;
                if (item.Origin is { } originWrite)
                {
                    // The LCS origin by the same documented member as in RP.11/RP.12
                    // (ilocalcoordinatesystem_x.html — read and write), but WITHOUT a matrix: the whole
                    // parametric route is checked, not only the angles.
                    local.X = originWrite[0];
                    local.Y = originWrite[1];
                    local.Z = originWrite[2];
                    step.Observe(item.Label + ": начало ЛСК записано " + Triple(originWrite[0], originWrite[1], originWrite[2]));
                }

                var updated = feature.Update();
                part.RebuildModel();
                doc.RebuildDocument();

                var liveAxes = AxesMatrix(local);
                var liveMatrix = (double[])liveAxes.Clone();
                if (item.Origin is { } originExpected)
                {
                    liveMatrix[12] = originExpected[0];
                    liveMatrix[13] = originExpected[1];
                    liveMatrix[14] = originExpected[2];
                }

                var liveRows = BodyRows(part);
                var expected = ExpectedBbox(liveMatrix, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
                var agrees = liveRows.Count == 1
                    && Near(liveRows[0], expected.Min[0], expected.Min[1], expected.Min[2],
                        expected.Max[0], expected.Max[1], expected.Max[2]);
                var (axis, angleDeg) = RotationOf(liveAxes);
                var coordinateAxis = IsCoordinateAxis(axis);
                var liveGeometry = Describe(liveRows);

                step.Observe(item.Label + ": Update()=" + updated + ", живой габарит " + liveGeometry
                    + ", ожидание по ЖИВОЙ матрице " + Box(expected.Min, expected.Max)
                    + ", совпадение=" + agrees);
                step.Observe(item.Label + ": ось поворота ИЗ ИЗМЕРЕННОЙ матрицы "
                    + Triple(axis[0], axis[1], axis[2]) + " на " + Api5.Num(angleDeg)
                    + "°; координатная ось=" + coordinateAxis);
                step.Data["live_geometry_" + item.Label] = liveGeometry;
                step.Data["live_angles_" + item.Label] =
                    Triple(item.Rotation, item.Nutation, item.Precession);
                step.Data["live_axis_" + item.Label] = Triple(axis[0], axis[1], axis[2]);
                step.Data["live_axis_is_coordinate_" + item.Label] = coordinateAxis;
                step.Data["live_angle_deg_" + item.Label] = angleDeg;
                step.Data["geometry_agrees_with_matrix_" + item.Label] = agrees;

                if (!agrees)
                {
                    failed.Add(item.Label + ": габарит не совпал с живой матрицей — прибор описывает "
                        + "себя, а не продукт");
                    continue;
                }

                var path = Path.Combine(_options.WorkDir, "rp24-" + item.Label + ".m3d");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                step.Observe(item.Label + ": SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
                doc.close();

                reopened = (ksDocument3D)_app.Document3D();
                if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
                {
                    step.Observe(item.Label + ": документ не переоткрылся");
                    continue;
                }

                // ── FIRST read — BEFORE the rebuild and BEFORE any write ──
                var elements = Enumerate(reopened, step);
                if (elements.Count == 0)
                {
                    step.Observe(item.Label + ": на переоткрытом документе признак не найден");
                    continue;
                }

                var first = elements[0].Feature.Position;
                var firstAngles = first.LocalCSParameters as ILocalCSEulerParam;
                var afterAngles = firstAngles is null
                    ? new double?[] { null, null, null }
                    : new double?[]
                    {
                        Try(() => firstAngles.RotationAngle),
                        Try(() => firstAngles.NutationAngle),
                        Try(() => firstAngles.PrecessionAngle),
                    };
                var originAfter = ReadXyz(first);
                var orientationAfter =
                    EnumName(() => (int)first.OrientationType, "ksOrientationTypeEnum");
                var vectorAfter = ReadAxisOf(first, ksObj3dTypeEnum.o3d_axisOX);

                step.Observe(item.Label + ": переоткрытие, элемент " + elements[0].Index
                    + " «" + elements[0].Name + "» ДО сборки и ДО записи: OrientationType="
                    + orientationAfter + ", углы " + Triple(afterAngles[0], afterAngles[1], afterAngles[2])
                    + ", X/Y/Z=" + Triple(originAfter[0], originAfter[1], originAfter[2])
                    + ", GetVector(OX)=" + vectorAfter);

                var part2 = (ksPart)reopened.GetPart(-1);
                part2.RebuildModel();
                reopened.RebuildDocument();
                var rebuiltGeometry = Describe(BodyRows(part2));

                step.Observe(item.Label + ": геометрия после переоткрытия и сборки в новой сессии "
                    + rebuiltGeometry + " (живая была " + liveGeometry + ")");

                var anglesKept = afterAngles[0] is { } r && Math.Abs(r - item.Rotation) < 0.01
                    && afterAngles[1] is { } n && Math.Abs(n - item.Nutation) < 0.01
                    && afterAngles[2] is { } p && Math.Abs(p - item.Precession) < 0.01;
                var geometryKept = string.Equals(rebuiltGeometry, liveGeometry, StringComparison.Ordinal);
                // The discriminating half for the LCS ORIGIN: comparison with what was WRITTEN, not with
                // zero. If the origin was not written, the question is unmeasured — and it is called
                // "not measured", not "lost".
                bool? originKept = item.Origin is { } originExpectedAfter
                    ? originAfter[0] is { } ox && originAfter[1] is { } oy && originAfter[2] is { } oz
                      && Math.Abs(ox - originExpectedAfter[0]) < 0.01
                      && Math.Abs(oy - originExpectedAfter[1]) < 0.01
                      && Math.Abs(oz - originExpectedAfter[2]) < 0.01
                    : null;

                step.Data["angles_after_reopen_" + item.Label] =
                    Triple(afterAngles[0], afterAngles[1], afterAngles[2]);
                step.Data["angles_kept_" + item.Label] = anglesKept;
                step.Data["geometry_kept_" + item.Label] = geometryKept;
                step.Data["orientation_after_reopen_" + item.Label] = orientationAfter;
                step.Data["origin_after_reopen_" + item.Label] =
                    Triple(originAfter[0], originAfter[1], originAfter[2]);
                step.Data["origin_was_written_" + item.Label] = item.Origin is not null;
                step.Data["origin_kept_" + item.Label] =
                    originKept is null ? "не измерено" : originKept.Value.ToString();
                step.Data["get_vector_ox_after_reopen_" + item.Label] = vectorAfter;
                if (item.Origin is not null && !originKept.GetValueOrDefault())
                {
                    step.Observe(item.Label + ": НАЧАЛО ЛСК записано " + Triple(item.Origin[0], item.Origin[1], item.Origin[2])
                        + ", после переоткрытия прочитано "
                        + Triple(originAfter[0], originAfter[1], originAfter[2])
                        + " — при габарите, который перенос подтверждает");
                }

                if (anglesKept && geometryKept)
                {
                    matched.Add(item.Label);
                }
                else
                {
                    failed.Add(item.Label + ": тройка углов после переоткрытия "
                        + Triple(afterAngles[0], afterAngles[1], afterAngles[2]) + " (записана "
                        + Triple(item.Rotation, item.Nutation, item.Precession) + "), геометрия сохранена="
                        + geometryKept);
                }
            }
            catch (Exception ex)
            {
                step.Observe(item.Label + ": исключение " + HResult.Describe(ex));
                failed.Add(item.Label + ": исключение");
            }
            finally
            {
                if (reopened is not null)
                {
                    TryClose(reopened);
                }

                TryClose(doc);
            }
        }

        step.Data["cases_matched"] = matched.ToArray();
        step.Data["cases_failed"] = failed.ToArray();

        if (failed.Count > 0)
        {
            step.Fail("параметрический маршрут НЕ подтверждён: " + string.Join(" · ", failed));
        }
        else if (matched.Count != cases.Length)
        {
            step.Fail("ни одна постановка не доведена до конца: " + string.Join(" · ", failed));
        }
        else
        {
            // Only the measured is said about the LCS origin. The half "the origin was not written" yields
            // "not measured", not "lost": a zero that was never going anywhere proves nothing, and calling
            // it the limit of the translation would pass the absence of an experiment off as a result.
            var originCases = cases.Where(c => c.Origin is not null).ToArray();
            var originKept = originCases
                .Where(c => step.Data.TryGetValue("origin_kept_" + c.Label, out var v)
                    && v as string == "True")
                .Select(c => c.Label).ToArray();
            var originLost = originCases
                .Where(c => step.Data.TryGetValue("origin_kept_" + c.Label, out var v)
                    && v as string == "False")
                .Select(c => c.Label).ToArray();

            var originVerdict = originCases.Length == 0
                ? "Начало ЛСК не записывалось ни в одной постановке, поэтому вопрос о переносе этим "
                  + "маршрутом здесь НЕ измерен."
                : originLost.Length == 0
                    ? "Начало ЛСК переоткрытие ПЕРЕЖИВАЕТ: постановки " + string.Join(", ", originKept)
                      + " вернули ЗАПИСАННУЮ тройку — значит параметрический маршрут несёт и перенос."
                    : "Начало ЛСК переоткрытие НЕ переживает: постановка " + string.Join(", ", originLost)
                      + " записана ненулевой, а прочитана (0, 0, 0) при габарите, который перенос "
                      + "подтверждает, — то есть ПЕРЕНОС этим маршрутом не покрыт"
                      + (originKept.Length > 0
                          ? "; постановки " + string.Join(", ", originKept) + " начало сохранили"
                          : string.Empty);

            step.Pass("параметрический маршрут подтверждён на " + matched.Count + " постановках: тройка "
                + "углов переживает переоткрытие целиком, геометрия сохранена, а различающая пара "
                + "(A и B) вернула каждой постановке ЕЁ тройку — то есть чтение не является "
                + "константой. Ось поворота, ВЫЧИСЛЕННАЯ из измеренной матрицы, координатной не "
                + "является ни в одной постановке: маршрут выражает ПРОИЗВОЛЬНУЮ ось. " + originVerdict);
        }
    }

    // ══════════════════════════════════════════════════════════════ RP.25 ══

    /// <summary>
    /// The order and units of the Euler angles — BY MEASUREMENT, and the displacement route
    /// <c>ParameterType = ksPDisplace</c> + <c>IPoint3DParamDisplace.DX/DY/DZ</c>.
    /// </summary>
    /// <remarks>
    /// <b>Why a separate step.</b> RP.24 measured that the angle triple survives a reopen, but left two
    /// questions open, and both decide whether the route suits the product.
    /// <list type="number">
    /// <item><b>The angle multiplication order.</b> The help page specifies it with a FIGURE
    /// (<c>rotation_pict.html</c> → <c>images/_praezession.jpg</c>: rotation R — about the body's own
    /// axis, precession P — about the vertical, nutation N — the tilt). The figure names the AXES and
    /// the MEANING, but not the order of the product, so the order is measured here rather than fitted
    /// to a single example: each angle is set ALONE (90°, the rest zero), the rotation axis is computed
    /// from the LIVE matrix, and then three combined setups are compared against all six products of
    /// these three matrices. The order is the one that matched in ALL three setups.</item>
    /// <item><b>Units.</b> If an angle of 90 gives a quarter turn and 30 gives a third, the angles are in
    /// DEGREES; this is checked by reading the angle FROM THE MEASURED matrix, not by a single number
    /// matching.</item>
    /// <item><b>The translation part.</b> The help page gives it a separate documented route:
    /// <c>ILocalCoordinateSystem.ParameterType</c> (read and write, <c>ksPoint3DTypeEnum</c>,
    /// <c>ilocalcoordinatesystem_parametertype.html</c>) with the value <c>ksPDisplace = 2</c>
    /// DOC: «По смещению от опорного объекта», after which <c>Parameters</c> (read-only) is cast to
    /// <c>IPoint3DParamDisplace</c> with <c>DX/DY/DZ</c> — DOC: «Смещение по X/Y/Z», read AND write
    /// (<c>ipoint3dparamdisplace_dx.html</c>). The previous steps checked ONLY <c>ksPParamCoord = 1</c>
    /// and got <c>Parameters = null</c>; <c>ksPDisplace</c> was never checked. That is the concrete new
    /// reason why the route is set here rather than a previously checked one being repeated.</item>
    /// </list>
    /// <b>A discriminating pair and a negative control are mandatory.</b> Two DIFFERENT non-zero
    /// displacements with the same angle triple tell a read from a constant. The negative control is a
    /// setup in which the displacement was NOT WRITTEN at all: its triple must not equal any of the
    /// written ones, otherwise "it was read" would mean "what the probe itself put in came back".
    /// <b>The first read is BEFORE ANY WRITE.</b> A write temporarily restores the correct read (RP.20),
    /// so the feature is read right after opening, before the rebuild and before any <c>Update()</c>.
    /// <b>The "axis + angle ↔ Euler angles" transformation is checked by MATRIX EQUIVALENCE.</b>
    /// Angle parametrization is ambiguous, so it is not numbers that are compared but products: the
    /// matrix assembled from the READ angles in the MEASURED order must match the matrix of the rotation
    /// by the given angle about the given axis. The check runs on an arbitrary axis, at 90° and 180°, and
    /// on an axis through a point outside the origin — i.e. on the very contract the product promises.
    /// </remarks>
    private void EulerOrderAndDisplacement()
    {
        var step = _report.Begin("RP.25", "Порядок углов Эйлера, единицы и маршрут смещения",
            "Каким порядком спрягаются три угла, в каких единицах они заданы, и переживает ли "
            + "переоткрытие документированный маршрут смещения ParameterType = ksPDisplace + "
            + "IPoint3DParamDisplace.DX/DY/DZ?");

        var failed = new List<string>();

        // ── A. the axes of the three angles: one angle at a time ────────────────────────────────
        var single = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var singleAxes = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (var item in new[]
                 {
                     (Label: "R", Rotation: 90d, Nutation: 0d, Precession: 0d),
                     (Label: "N", Rotation: 0d, Nutation: 90d, Precession: 0d),
                     (Label: "P", Rotation: 0d, Nutation: 0d, Precession: 90d),
                 })
        {
            var run = RunEulerCase(step, "A-" + item.Label, item.Rotation, item.Nutation, item.Precession,
                displacement: null, writeDisplacement: false, keep: false);
            if (run.LiveMatrix is null)
            {
                failed.Add("A/" + item.Label + ": живая матрица не измерена");
                continue;
            }

            single[item.Label] = Mat3(run.LiveMatrix);
            var (axis, angle) = RotationOf(run.LiveMatrix);
            // The factor's axis is taken from the MEASURED matrix (the eigenvector with eigenvalue 1),
            // NOT from its column: a column is the image of a basis vector, and for a rotation about X
            // the third column equals (0,−1,0), i.e. is not an axis. The first revision of the step took
            // exactly the column and so assembled not a single product.
            singleAxes[item.Label] = axis;
            step.Observe("A: только " + item.Label + " = 90° — ось поворота ИЗ ИЗМЕРЕННОЙ матрицы "
                + Triple(axis[0], axis[1], axis[2]) + " на " + Api5.Num(angle) + "°");
            step.Data["axis_" + item.Label] = Triple(axis[0], axis[1], axis[2]);
            step.Data["angle_deg_" + item.Label] = angle;
        }

        if (single.Count != 3)
        {
            step.Fail("оси трёх углов не измерены: без них порядок не устанавливается");
            return;
        }

        // Units: 30 rather than 90 — if the angle reads as a third of a turn, the angles are in degrees.
        var units = RunEulerCase(step, "A-units", 30d, 0d, 0d, displacement: null,
            writeDisplacement: false, keep: false);
        if (units.LiveMatrix is not null)
        {
            var (_, unitAngle) = RotationOf(units.LiveMatrix);
            step.Data["angle_deg_for_30"] = unitAngle;
            step.Observe("A: угол R = 30 дал поворот на " + Api5.Num(unitAngle)
                + "° — единицы " + (Math.Abs(unitAngle - 30d) < 0.01 ? "ГРАДУСЫ" : "НЕ градусы"));
        }

        // ── A2. order: three combined setups against all six products ──────────────────────────
        var combined = new List<(string Label, double[] Matrix, string Of)>();
        foreach (var item in new[]
                 {
                     (Label: "K1", Rotation: 90d, Nutation: 90d, Precession: 0d),
                     (Label: "K2", Rotation: 0d, Nutation: 90d, Precession: 90d),
                     (Label: "K3", Rotation: 90d, Nutation: 0d, Precession: 90d),
                 })
        {
            var run = RunEulerCase(step, "A-" + item.Label, item.Rotation, item.Nutation, item.Precession,
                displacement: null, writeDisplacement: false, keep: false);
            if (run.LiveMatrix is null)
            {
                failed.Add("A/" + item.Label + ": живая матрица не измерена");
                continue;
            }

            combined.Add((item.Label, Mat3(run.LiveMatrix),
                "R=" + Api5.Num(item.Rotation) + ", N=" + Api5.Num(item.Nutation)
                + ", P=" + Api5.Num(item.Precession)));
        }

        var orders = new List<string>();
        var orderKeys = new List<int[]>();
        var factorAxes = new[] { singleAxes["P"], singleAxes["N"], singleAxes["R"] };
        foreach (var permutation in Permutations(new[] { 0, 1, 2 }))
        {
            var ok = true;
            var worst = 0d;
            foreach (var probeCase in combined)
            {
                var angles = AnglesOf(probeCase.Of);
                var built = ComposeEuler(factorAxes, permutation, angles);
                var diff = MaxDiff(built, probeCase.Matrix);
                worst = Math.Max(worst, diff);
                if (diff > 1e-6)
                {
                    ok = false;
                    break;
                }
            }

            var name = Name(permutation);
            step.Data["order_" + name + "_max_diff"] = worst;
            if (ok)
            {
                orders.Add(name);
                orderKeys.Add(permutation);
            }
        }

        step.Observe("A: порядок проверен на " + combined.Count + " составных постановках; совпали "
            + (orders.Count == 0 ? "ни одного произведения" : string.Join(", ", orders)));
        if (orders.Count != 1)
        {
            step.Fail("порядок перемножения углов не установлен однозначно: совпало произведений "
                + orders.Count + " (" + string.Join(", ", orders) + ")");
            return;
        }

        var order = orderKeys[0];
        step.Data["euler_order"] = orders[0];
        step.Data["factor_axes"] = "P=" + Triple(singleAxes["P"][0], singleAxes["P"][1], singleAxes["P"][2])
            + "; N=" + Triple(singleAxes["N"][0], singleAxes["N"][1], singleAxes["N"][2])
            + "; R=" + Triple(singleAxes["R"][0], singleAxes["R"][1], singleAxes["R"][2]);

        // ── B. the displacement route: ksPDisplace + IPoint3DParamDisplace.DX/DY/DZ ────────────
        var written = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var kept = new List<string>();
        var lost = new List<string>();
        foreach (var item in new[]
                 {
                     (Label: "D1", Angles: new[] { 30d, 40d, 50d }, Displacement: (double[]?)new[] { 7d, -11d, 13d }),
                     (Label: "D2", Angles: new[] { 30d, 40d, 50d }, Displacement: (double[]?)new[] { 1d, 2d, 3d }),
                     (Label: "D0", Angles: new[] { 30d, 40d, 50d }, Displacement: (double[]?)null),
                 })
        {
            var run = RunEulerCase(step, "B-" + item.Label, item.Angles[0], item.Angles[1], item.Angles[2],
                item.Displacement, writeDisplacement: item.Displacement is not null, keep: true);

            step.Data["live_matrix_" + item.Label] = DescribeMatrix(run.LiveMatrix);
            step.Data["read_angles_" + item.Label] = Triple(run.ReadAngles[0], run.ReadAngles[1], run.ReadAngles[2]);
            step.Data["orientation_after_" + item.Label] = run.OrientationTypeAfter ?? "не прочитано";
            step.Data["parameter_type_after_" + item.Label] = run.ParameterTypeAfter ?? "не прочитано";
            step.Data["displacement_after_" + item.Label] =
                Triple(run.DisplacementAfter[0], run.DisplacementAfter[1], run.DisplacementAfter[2]);
            step.Data["xyz_after_" + item.Label] = Triple(run.XyzAfter[0], run.XyzAfter[1], run.XyzAfter[2]);
            step.Data["get_vector_ox_after_" + item.Label] = run.GetVectorAfter ?? "не прочитано";
            step.Data["geometry_live_" + item.Label] = run.LiveGeometry;
            step.Data["geometry_after_reopen_" + item.Label] = run.GeometryAfterReopen;

            if (item.Displacement is { } want)
            {
                written[item.Label] = want;
            }

            var anglesKept = Near3(run.ReadAngles, item.Angles);
            step.Data["angles_kept_" + item.Label] = anglesKept;

            if (item.Displacement is { } expected)
            {
                var displacementKept = Near3(run.DisplacementAfter, expected);
                step.Data["displacement_kept_" + item.Label] = displacementKept;
                if (anglesKept && displacementKept && run.GeometryKept)
                {
                    kept.Add(item.Label);
                }
                else
                {
                    lost.Add(item.Label + " (углы " + Triple(run.ReadAngles[0], run.ReadAngles[1], run.ReadAngles[2])
                        + " против " + Triple(expected[0], expected[1], expected[2])
                        + ", смещение " + Triple(run.DisplacementAfter[0], run.DisplacementAfter[1],
                            run.DisplacementAfter[2]) + " против "
                        + Triple(expected[0], expected[1], expected[2])
                        + ", геометрия сохранена=" + run.GeometryKept + ")");
                }
            }
            else
            {
                // Negative control: the displacement was not written. Its read triple must differ from
                // EVERY written one — otherwise the read would have returned an imposed value.
                var collides = written.Values.Any(v => Near3(run.DisplacementAfter, v));
                step.Data["negative_control_collides"] = collides;
                step.Observe("B: отрицательный контроль D0 (смещение не записывалось) прочитал "
                    + Triple(run.DisplacementAfter[0], run.DisplacementAfter[1], run.DisplacementAfter[2])
                    + "; совпадение с записанными: " + collides);
                if (collides)
                {
                    failed.Add("B/D0: отрицательный контроль совпал с записанным смещением — "
                        + "прибор вернул навязанное значение");
                }
            }
        }

        // ── C. axis + angle ↔ Euler angles: MATRIX equivalence ─────────────────────────────────
        var axesForCompose = new[] { singleAxes["P"], singleAxes["N"], singleAxes["R"] };
        var equivalence = new List<string>();
        foreach (var item in new[]
                 {
                     (Label: "C1", Point: new[] { 5d, 0d, 0d }, Direction: new[] { 0d, 0d, 1d }, Angle: 90d),
                     (Label: "C2", Point: new[] { 5d, -3d, 7d }, Direction: new[] { 1d, 1d, 1d }, Angle: 120d),
                     (Label: "C3", Point: new[] { 5d, 0d, 0d }, Direction: new[] { 0d, 0d, 1d }, Angle: 180d),
                 })
        {
            var target = Mat3(RotateAboutAxis(item.Point, item.Direction, item.Angle));
            var solved = SolveEuler(axesForCompose, order, target);
            if (solved.Residual > 1e-6)
            {
                failed.Add("C/" + item.Label + ": углы для заданной оси и угла не найдены "
                    + "(невязка " + Api5.Num(solved.Residual) + ")");
                continue;
            }

            // The translation part is the same placement as in the matrix route: t = c − R·c. That very
            // equality was measured by the discriminating pair RP.12, so it is taken as an expectation
            // rather than postulated anew.
            var translation = TranslationOf(target, item.Point);

            // The angles are written into three DIFFERENT model fields: rotation, nutation, precession.
            var run = RunEulerCase(step, "C-" + item.Label,
                solved.Angles[2], solved.Angles[1], solved.Angles[0],
                translation, writeDisplacement: true, keep: true);

            var live = run.LiveMatrix is null ? null : Mat3(run.LiveMatrix);
            var liveDiff = live is null ? double.NaN : MaxDiff(live, target);
            var readBack = ComposeEuler(axesForCompose, order, ByFactor(run.ReadAngles));
            var readDiff = MaxDiff(readBack, target);
            var displacementKept = Near3(run.DisplacementAfter, translation);

            step.Observe("C: " + item.Label + " — ось " + Triple(item.Direction[0], item.Direction[1], item.Direction[2])
                + " на " + Api5.Num(item.Angle) + "° через " + Triple(item.Point[0], item.Point[1], item.Point[2])
                + ": углы Эйлера " + Triple(solved.Angles[0], solved.Angles[1], solved.Angles[2])
                + " (невязка подбора " + Api5.Num(solved.Residual) + "), живая матрица против заданной "
                + Api5.Num(liveDiff) + ", прочитанные углы против заданной " + Api5.Num(readDiff)
                + ", перенос записан " + Triple(translation[0], translation[1], translation[2])
                + " и прочитан " + Triple(run.DisplacementAfter[0], run.DisplacementAfter[1], run.DisplacementAfter[2])
                + ", геометрия сохранена=" + run.GeometryKept);

            step.Data["euler_angles_" + item.Label] = Triple(solved.Angles[0], solved.Angles[1], solved.Angles[2]);
            step.Data["matrix_diff_live_" + item.Label] = liveDiff;
            step.Data["matrix_diff_read_" + item.Label] = readDiff;
            step.Data["displacement_written_" + item.Label] = Triple(translation[0], translation[1], translation[2]);
            step.Data["displacement_read_" + item.Label] =
                Triple(run.DisplacementAfter[0], run.DisplacementAfter[1], run.DisplacementAfter[2]);

            if (liveDiff < 1e-6 && readDiff < 1e-6 && displacementKept && run.GeometryKept)
            {
                equivalence.Add(item.Label);
            }
            else
            {
                failed.Add("C/" + item.Label + ": эквивалентность матриц не подтверждена (живая "
                    + Api5.Num(liveDiff) + ", прочитанная " + Api5.Num(readDiff)
                    + ", перенос сохранён=" + displacementKept + ", геометрия сохранена=" + run.GeometryKept + ")");
            }
        }

        // ── summary ─────────────────────────────────────────────────────────────────────────────
        if (failed.Count > 0)
        {
            step.Fail("не подтверждено: " + string.Join("; ", failed));
            return;
        }

        if (kept.Count == 0)
        {
            step.Fail("маршрут смещения записан, но НИ ОДНА постановка не вернула записанное "
                + "смещение после переоткрытия: " + string.Join("; ", lost));
            return;
        }

        // The route is named and confirmed by a discriminating pair, not by "seems to work": both halves
        // of the placement (orientation and translation) were read from the REOPENED document, and the
        // matrix equivalence was confirmed on independent setups. The axis point is not read by a separate
        // member — it is DERIVED from the "orientation + translation" pair, and this is stated here plainly
        // so that the route's name does not promise more than what was measured.
        step.Data["routes_distinguishing_both_inputs"] = new List<string>
        {
            "RP.25 Position.OrientationType=ksEulerCorners + LocalCSParameters→ILocalCSEulerParam "
            + "(порядок " + orders[0] + ") и Position.ParameterType=ksPDisplace + "
            + "Parameters→IPoint3DParamDisplace.DX/DY/DZ: перенос прочитан с переоткрытого документа "
            + "на " + kept.Count + " постановках различающей пары, ориентация — на всех; точка оси "
            + "ВЫВОДИТСЯ из пары «ориентация + перенос», отдельного члена для неё по-прежнему нет",
        };

        step.Pass("порядок углов установлен измерением: " + orders[0] + "; единицы — градусы; "
            + "тройка углов переоткрытие переживает; маршрут смещения ksPDisplace + "
            + "IPoint3DParamDisplace.DX/DY/DZ переоткрытие " + (lost.Count == 0
                ? "ПЕРЕЖИВАЕТ на всех " + kept.Count + " постановках"
                : "переживает на " + kept.Count + " постановках и НЕ переживает на: "
                  + string.Join("; ", lost))
            + "; эквивалентность «ось + угол ↔ углы Эйлера» подтверждена матрично на "
            + equivalence.Count + " постановках");
    }

    /// <summary>The full lifecycle of one setup: create → write → live measurement → save → close → NEW session →
    /// open → read BEFORE ANY WRITE → geometry after the rebuild.</summary>
    private EulerLifecycle RunEulerCase(
        ProbeStep step, string label, double rotation, double nutation, double precession,
        double[]? displacement, bool writeDisplacement, bool keep)
    {
        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var body = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP25-" + label);
            if (body is null)
            {
                step.Observe(label + ": тело не построено");
                return EulerLifecycle.Empty(label);
            }

            if (Container(doc)?.BodyRepositions?.Add() is not IBodyReposition feature)
            {
                step.Observe(label + ": BodyRepositions.Add() недоступен");
                return EulerLifecycle.Empty(label);
            }

            if (_app.TransferInterface(body.Element!, 2, 0) is not IKompasAPIObject moved)
            {
                step.Observe(label + ": тело не переносится в API7");
                return EulerLifecycle.Empty(label);
            }

            feature.RepositionBody = moved;
            var local = feature.Position;

            // Documented order: first the orientation mode, then the parameter interface of THAT mode
            // (ilocalcoordinatesystem_localcsparameters.html).
            local.OrientationType = ksOrientationTypeEnum.ksEulerCorners;
            if (local.LocalCSParameters is not ILocalCSEulerParam euler)
            {
                step.Observe(label + ": LocalCSParameters не подтверждает ILocalCSEulerParam");
                return EulerLifecycle.Empty(label);
            }

            euler.RotationAngle = rotation;
            euler.NutationAngle = nutation;
            euler.PrecessionAngle = precession;

            if (writeDisplacement && displacement is { } value)
            {
                // The documented order is the same: first the point parameter type, then the interface of
                // THAT type on Parameters (ilocalcoordinatesystem_parametertype.html).
                local.ParameterType = ksPoint3DTypeEnum.ksPDisplace;
                if (local.Parameters is not IPoint3DParamDisplace displace)
                {
                    step.Observe(label + ": Parameters не подтверждает IPoint3DParamDisplace при "
                        + "ParameterType = ksPDisplace");
                    return EulerLifecycle.Empty(label);
                }

                displace.DX = value[0];
                displace.DY = value[1];
                displace.DZ = value[2];
                step.Observe(label + ": смещение записано маршрутом ksPDisplace + DX/DY/DZ = "
                    + Triple(value[0], value[1], value[2]));
            }

            feature.Update();
            part.RebuildModel();
            doc.RebuildDocument();

            var liveAxes = AxesMatrix(local);
            var liveMatrix = (double[])liveAxes.Clone();
            if (displacement is { } liveTranslation)
            {
                liveMatrix[12] = liveTranslation[0];
                liveMatrix[13] = liveTranslation[1];
                liveMatrix[14] = liveTranslation[2];
            }

            var liveRows = BodyRows(part);
            var liveGeometry = Describe(liveRows);
            var expected = ExpectedBbox(liveMatrix, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
            var agrees = liveRows.Count == 1
                && Near(liveRows[0], expected.Min[0], expected.Min[1], expected.Min[2],
                    expected.Max[0], expected.Max[1], expected.Max[2]);
            step.Observe(label + ": живой габарит " + liveGeometry + ", ожидание по ЖИВОЙ матрице "
                + Box(expected.Min, expected.Max) + ", совпадение=" + agrees);
            if (!agrees)
            {
                // The probe is describing itself, not the product: the matrix it read does not explain
                // the geometry. Continuing on such a basis is not allowed.
                step.Observe(label + ": габарит не совпал с живой матрицей — постановка не измерена");
                return EulerLifecycle.Empty(label) with { LiveGeometry = liveGeometry };
            }

            if (!keep)
            {
                return EulerLifecycle.Empty(label) with
                {
                    LiveMatrix = liveAxes,
                    LiveGeometry = liveGeometry,
                    LiveAgrees = true,
                };
            }

            var path = Path.Combine(_options.WorkDir, "rp25-" + label + ".m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe(label + ": SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Observe(label + ": документ не переоткрылся");
                return EulerLifecycle.Empty(label) with { LiveMatrix = liveAxes, LiveGeometry = liveGeometry };
            }

            // ── FIRST read — BEFORE the rebuild and BEFORE any write ──
            var elements = Enumerate(reopened, step);
            if (elements.Count == 0)
            {
                step.Observe(label + ": на переоткрытом документе признак не найден");
                return EulerLifecycle.Empty(label) with { LiveMatrix = liveAxes, LiveGeometry = liveGeometry };
            }

            var first = elements[0].Feature.Position;
            var firstEuler = first.LocalCSParameters as ILocalCSEulerParam;
            var readAngles = firstEuler is null
                ? new double?[] { null, null, null }
                : new double?[]
                {
                    Try(() => firstEuler.RotationAngle),
                    Try(() => firstEuler.NutationAngle),
                    Try(() => firstEuler.PrecessionAngle),
                };
            var orientationAfter = EnumName(() => (int)first.OrientationType, "ksOrientationTypeEnum");
            var parameterTypeAfter = EnumName(() => (int)first.ParameterType, "ksPoint3DTypeEnum");
            var displacementAfter = ReadDisplacement(first);
            var xyzAfter = ReadXyz(first);
            var vectorAfter = ReadAxisOf(first, ksObj3dTypeEnum.o3d_axisOX);

            step.Observe(label + ": переоткрытие, элемент " + elements[0].Index + " «" + elements[0].Name
                + "» ДО сборки и ДО записи: OrientationType=" + orientationAfter
                + ", углы " + Triple(readAngles[0], readAngles[1], readAngles[2])
                + ", ParameterType=" + parameterTypeAfter
                + ", DX/DY/DZ=" + Triple(displacementAfter[0], displacementAfter[1], displacementAfter[2])
                + ", X/Y/Z=" + Triple(xyzAfter[0], xyzAfter[1], xyzAfter[2])
                + ", GetVector(OX)=" + vectorAfter);

            var part2 = (ksPart)reopened.GetPart(-1);
            part2.RebuildModel();
            reopened.RebuildDocument();
            var rebuilt = Describe(BodyRows(part2));
            step.Observe(label + ": геометрия после переоткрытия и сборки " + rebuilt
                + " (живая была " + liveGeometry + ")");

            return new EulerLifecycle(
                label, liveAxes, liveGeometry, true,
                readAngles, orientationAfter, parameterTypeAfter, displacementAfter, xyzAfter, vectorAfter,
                rebuilt,
                string.Equals(rebuilt, liveGeometry, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            step.Observe(label + ": исключение " + HResult.Describe(ex));
            return EulerLifecycle.Empty(label);
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }
        }
    }

    /// <summary>The displacement from the documented <c>ksPDisplace</c> route, without substitutions.</summary>
    private static double?[] ReadDisplacement(ILocalCoordinateSystem system)
    {
        try
        {
            if (system.Parameters is not IPoint3DParamDisplace displace)
            {
                return new double?[] { null, null, null };
            }

            return new double?[] { Try(() => displace.DX), Try(() => displace.DY), Try(() => displace.DZ) };
        }
        catch (Exception)
        {
            return new double?[] { null, null, null };
        }
    }

    private static double[] AnglesOf(string of)
    {
        var numbers = Numbers(of);
        return new[] { numbers[2], numbers[1], numbers[0] };
    }

    private static IEnumerable<int[]> Permutations(int[] source)
    {
        if (source.Length <= 1)
        {
            yield return source;
            yield break;
        }

        for (var i = 0; i < source.Length; i++)
        {
            var rest = source.Where((_, index) => index != i).ToArray();
            foreach (var tail in Permutations(rest))
            {
                yield return new[] { source[i] }.Concat(tail).ToArray();
            }
        }
    }

    private static string Name(int[] order) => string.Concat(order.Select(i => "PNR"[i]));

    /// <summary>A 3×3 matrix (rows) from a 4×4 assembled by columns (layout measured by RP.2).</summary>
    private static double[] Mat3(double[] m16)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                result[(row * 3) + column] = m16[(column * 4) + row];
            }
        }

        return result;
    }

    private static double[] Identity3() => new[] { 1d, 0d, 0d, 0d, 1d, 0d, 0d, 0d, 1d };

    private static double[] Mul3(double[] a, double[] b)
    {
        var result = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var sum = 0d;
                for (var k = 0; k < 3; k++)
                {
                    sum += a[(row * 3) + k] * b[(k * 3) + column];
                }

                result[(row * 3) + column] = sum;
            }
        }

        return result;
    }

    /// <summary>Rotation by an angle about a given axis. The axis arrives MEASURED (the eigenvector of the
    /// single-angle matrix), not assumed to be a coordinate axis.</summary>
    private static double[] Rot3(double[] axis, double angleDeg)
    {
        var norm = Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));
        var k = norm < 1e-12 ? new[] { 0d, 0d, 1d } : new[] { axis[0] / norm, axis[1] / norm, axis[2] / norm };
        var angle = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var oneMinusCos = 1d - cos;
        return new[]
        {
            cos + (oneMinusCos * k[0] * k[0]), (oneMinusCos * k[0] * k[1]) - (sin * k[2]), (oneMinusCos * k[0] * k[2]) + (sin * k[1]),
            (oneMinusCos * k[1] * k[0]) + (sin * k[2]), cos + (oneMinusCos * k[1] * k[1]), (oneMinusCos * k[1] * k[2]) - (sin * k[0]),
            (oneMinusCos * k[2] * k[0]) - (sin * k[1]), (oneMinusCos * k[2] * k[1]) + (sin * k[0]), cos + (oneMinusCos * k[2] * k[2]),
        };
    }

    /// <summary>The product of three rotations in the MEASURED order. <paramref name="angles"/> is indexed by the
    /// FACTOR identifier (0 — precession P, 1 — nutation N, 2 — rotation R), and <paramref name="order"/>
    /// names which factor stands at which position of the product.</summary>
    private static double[] ComposeEuler(double[][] axes, int[] order, double[] angles)
    {
        var result = Identity3();
        for (var i = 0; i < 3; i++)
        {
            result = Mul3(result, Rot3(axes[order[i]], angles[order[i]]));
        }

        return result;
    }

    /// <summary>A triple from the model (rotation, nutation, precession) → indexing by factor (P, N, R).</summary>
    private static double[] ByFactor(double?[] rotationNutationPrecession) => new[]
    {
        rotationNutationPrecession[2] ?? double.NaN,
        rotationNutationPrecession[1] ?? double.NaN,
        rotationNutationPrecession[0] ?? double.NaN,
    };

    private static double MaxDiff(double[] a, double[] b)
    {
        var worst = 0d;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        }

        return worst;
    }

    /// <summary>The Euler angles for a given matrix — by NUMERICAL search at the MEASURED order. The order is not
    /// fitted: it was established by step A and passed in here; only the angles are fitted, and the
    /// validity of the fit is proven by matrix equivalence on INDEPENDENT setups (step C).</summary>
    private static (double[] Angles, double Residual) SolveEuler(double[][] axes, int[] order, double[] target)
    {
        var best = double.MaxValue;
        var bestAngles = new[] { 0d, 0d, 0d };
        for (var a = 0; a < 360; a += 10)
        {
            for (var b = 0; b < 360; b += 10)
            {
                for (var c = 0; c < 360; c += 10)
                {
                    var diff = MaxDiff(ComposeEuler(axes, order, new[] { (double)a, b, c }), target);
                    if (diff < best)
                    {
                        best = diff;
                        bestAngles = new[] { (double)a, b, c };
                    }
                }
            }
        }

        for (var size = 1d; size > 1e-7; size /= 10d)
        {
            var improved = true;
            while (improved)
            {
                improved = false;
                foreach (var da in new[] { -size, 0d, size })
                {
                    foreach (var db in new[] { -size, 0d, size })
                    {
                        foreach (var dc in new[] { -size, 0d, size })
                        {
                            var candidate = new[] { bestAngles[0] + da, bestAngles[1] + db, bestAngles[2] + dc };
                            var diff = MaxDiff(ComposeEuler(axes, order, candidate), target);
                            if (diff < best - 1e-13)
                            {
                                best = diff;
                                bestAngles = candidate;
                                improved = true;
                            }
                        }
                    }
                }
            }
        }

        return (bestAngles, best);
    }

    private static double[] TranslationOf(double[] r, double[] point) => new[]
    {
        point[0] - ((r[0] * point[0]) + (r[1] * point[1]) + (r[2] * point[2])),
        point[1] - ((r[3] * point[0]) + (r[4] * point[1]) + (r[5] * point[2])),
        point[2] - ((r[6] * point[0]) + (r[7] * point[1]) + (r[8] * point[2])),
    };

    private static bool Near3(double?[] read, double[] expected) =>
        read.Length == 3
        && read[0] is { } a && Math.Abs(a - expected[0]) < 0.01
        && read[1] is { } b && Math.Abs(b - expected[1]) < 0.01
        && read[2] is { } c && Math.Abs(c - expected[2]) < 0.01;

    private static string DescribeMatrix(double[]? m) => m is null
        ? "не измерена"
        : string.Join(" | ", Enumerable.Range(0, 3).Select(row =>
            string.Join(", ", Enumerable.Range(0, 3).Select(column =>
                Api5.Num(m[(column * 4) + row])))));

    private sealed record EulerLifecycle(
        string Label,
        double[]? LiveMatrix,
        string LiveGeometry,
        bool LiveAgrees,
        double?[] ReadAngles,
        string? OrientationTypeAfter,
        string? ParameterTypeAfter,
        double?[] DisplacementAfter,
        double?[] XyzAfter,
        string? GetVectorAfter,
        string GeometryAfterReopen,
        bool GeometryKept)
    {
        public static EulerLifecycle Empty(string label) => new(
            label, null, "<не измерено>", false,
            new double?[] { null, null, null }, null, null,
            new double?[] { null, null, null }, new double?[] { null, null, null }, null,
            "<не измерено>", false);
    }

    /// <summary>A 4×4 matrix assembled from the THREE axes read by <c>GetVector</c> (layout measured by RP.2:
    /// 3×3 by columns in 0…10, translation in 12…14). If the read fails it stays identity, and that is
    /// visible in the bounding box rather than hidden.</summary>
    private static double[] AxesMatrix(ILocalCoordinateSystem system)
    {
        var matrix = new double[16];
        matrix[0] = 1d;
        matrix[5] = 1d;
        matrix[10] = 1d;
        var axes = new[]
        {
            ksObj3dTypeEnum.o3d_axisOX, ksObj3dTypeEnum.o3d_axisOY, ksObj3dTypeEnum.o3d_axisOZ,
        };
        for (var column = 0; column < 3; column++)
        {
            if (!system.GetVector(axes[column], out var x, out var y, out var z))
            {
                return matrix;
            }

            matrix[column * 4] = x;
            matrix[(column * 4) + 1] = y;
            matrix[(column * 4) + 2] = z;
        }

        return matrix;
    }

    /// <summary>The axis and angle of rotation COMPUTED from the measured matrix, not assumed.</summary>
    private static (double[] Axis, double AngleDeg) RotationOf(double[] m)
    {
        double Cell(int row, int column) => m[(column * 4) + row];
        var trace = Cell(0, 0) + Cell(1, 1) + Cell(2, 2);
        var cos = Math.Clamp((trace - 1d) / 2d, -1d, 1d);
        var angleDeg = Math.Acos(cos) * 180d / Math.PI;
        var axis = new[]
        {
            Cell(2, 1) - Cell(1, 2), Cell(0, 2) - Cell(2, 0), Cell(1, 0) - Cell(0, 1),
        };
        var norm = Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));
        if (norm > 1e-9)
        {
            for (var i = 0; i < 3; i++)
            {
                axis[i] /= norm;
            }
        }

        return (axis, angleDeg);
    }

    private static bool IsCoordinateAxis(double[] axis)
    {
        var norm = Math.Sqrt((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]));
        if (norm < 1e-9)
        {
            return true;
        }

        for (var i = 0; i < 3; i++)
        {
            var unit = new double[3];
            unit[i] = 1d;
            var dot = Math.Abs(((axis[0] * unit[0]) + (axis[1] * unit[1]) + (axis[2] * unit[2])) / norm);
            if (dot > 0.9999)
            {
                return true;
            }
        }

        return false;
    }

    private static double?[] ReadXyz(ILocalCoordinateSystem system)
    {
        try
        {
            return new double?[] { system.X, system.Y, system.Z };
        }
        catch (Exception)
        {
            return new double?[] { null, null, null };
        }
    }

    /// <summary>Reopen: does the documented SEQUENCE of working with the document restore the read (RP.23).</summary>
    /// <remarks>
    /// <b>Why a separate step.</b> Order §2 requires checking not only the interface and the signature but
    /// also the "sequence of working with the document". The previous steps read the reopened feature
    /// IMMEDIATELY (RP.14, RP.16) and AFTER a write (RP.20), while the stage "documented <c>Update()</c> —
    /// and then read" remained unmeasured: RP.20 called <c>Update()</c> but read only after a REPEAT WRITE,
    /// so "Update() itself restored the read" and "the write restored the read" are indistinguishable there.
    /// <b>Why this is a decisive question, not pedantry.</b> If a documented call makes the matrix form of
    /// the placement current, then the read defect is cured by the SEQUENCE, and the product is obliged to
    /// follow it. If no documented call does so, then publishing the derived transformation is forbidden
    /// under any conditions — and then the correct answer of the product is an explicit unreadable state.
    /// <b>The first read is before any call.</b> A parameter write restores the read (RP.20), and that is
    /// exactly why the first read is taken BEFORE all the stages; the stages are compared against it, not
    /// against one another.
    /// </remarks>
    private void ReopenSequenceRoute()
    {
        var step = _report.Begin("RP.23", "Переоткрытие: восстанавливает ли чтение документированная последовательность",
            "Возвращает ли записанную ориентацию документированный вызов Update() — признака или его "
            + "системы координат, — если прочитать признак ПОСЛЕ него?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            var body = ExtrudeRect(doc, part, 0d, 20d, 0d, 10d, 5d, step, "RP23-A");
            if (body is null)
            {
                step.Fail("тело не построено");
                return;
            }

            var matrix = RotateAboutAxis(AxisPoint, AxisDirection, RotateAngleDeg);
            var expect = ExpectedBbox(matrix, new[] { 0d, 0d, 0d }, new[] { 20d, 10d, 5d });
            if (CreateReposition(doc, part, body, matrix, step, "поворот +90° вокруг Z через (5,0,0)")
                is not { } feature)
            {
                step.Fail("признак не создан");
                return;
            }

            var live = BodyRows(part);
            step.Observe("живая геометрия: " + Describe(live));
            var liveOx = ReadDocumented(feature, step).Values
                .GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            step.Data["live_get_vector_ox"] = liveOx;
            step.Observe("живой GetVector(OX)=" + liveOx + " (ожидание (0, 1, 0))");

            var path = Path.Combine(_options.WorkDir, "rp23-sequence.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            step.Observe("SaveAs " + Api5.Raw(SafeBool(() => doc.SaveAs(path))));
            doc.close();

            reopened = (ksDocument3D)_app.Document3D();
            if (Api5.SafeBool(() => reopened.Open(path, true)) != true)
            {
                step.Fail("документ не переоткрылся");
                return;
            }

            var part2 = (ksPart)reopened.GetPart(-1);
            var elements = Enumerate(reopened, step);
            if (elements.Count == 0)
            {
                step.Fail("признаков в переоткрытом документе нет — измерять нечего");
                return;
            }

            var target = elements[0].Feature;
            string Ox() => ReadDocumented(target, step).Values
                .GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)") ?? "<нет>";

            // Stage A — the first read BEFORE any call: this is the reference point.
            var a = Ox();
            step.Observe("A. сразу после открытия, ДО любого вызова: GetVector(OX)=" + a);

            // Stage B — the documented IModelObject.Update() on the feature itself.
            var updated = SafeBool(() => target.Update());
            var b = Ox();
            step.Observe("B. после IModelObject.Update() признака (" + Api5.Raw(updated)
                + "): GetVector(OX)=" + b);

            // Stage C — the documented ILocalCoordinateSystem.Update() on the coordinate system.
            bool? localUpdated = null;
            var c = "<не прочитано>";
            try
            {
                var local = target.Position;
                localUpdated = SafeBool(() => local.Update());
                c = ReadAxisOf(local, ksObj3dTypeEnum.o3d_axisOX);
            }
            catch (Exception ex)
            {
                c = HResult.Describe(ex);
            }

            step.Observe("C. после ILocalCoordinateSystem.Update() (" + Api5.Raw(localUpdated)
                + "): GetVector(OX)=" + c);

            // Stage D — the documented rebuild of the document.
            part2.RebuildModel();
            reopened.RebuildDocument();
            var d = Ox();
            step.Observe("D. после RebuildModel + RebuildDocument: GetVector(OX)=" + d);
            step.Observe("геометрия после всех ступеней: " + Describe(BodyRows(part2)));
            step.Data["before_any_call"] = a;
            step.Data["after_feature_update"] = b;
            step.Data["after_local_update"] = c;
            step.Data["after_rebuild"] = d;
            step.Data["geometry_matches_rotation"] =
                FindByBbox(BodyRows(part2), expect.Min, expect.Max) is not null;

            var wanted = new[] { 0d, 1d, 0d };
            var restored = new List<string>();
            if (ContainsTriple(a, wanted))
            {
                restored.Add("A (сразу после открытия)");
            }

            if (ContainsTriple(b, wanted))
            {
                restored.Add("B (после IModelObject.Update признака)");
            }

            if (ContainsTriple(c, wanted))
            {
                restored.Add("C (после ILocalCoordinateSystem.Update)");
            }

            if (ContainsTriple(d, wanted))
            {
                restored.Add("D (после сборки документа)");
            }

            step.Data["restoring_steps"] = restored;
            if (restored.Count == 0)
            {
                step.Fail("записанную ориентацию не вернула НИ ОДНА документированная ступень: "
                    + "сразу после открытия " + a + ", после Update() признака " + b + ", после Update() "
                    + "системы координат " + c + ", после сборки " + d + " — ожидание (0, 1, 0) при "
                    + "габарите, который поворот подтверждает");
            }
            else
            {
                step.Pass("чтение восстанавливает документированная ступень: "
                    + string.Join("; ", restored));
            }
        }
        catch (Exception ex)
        {
            step.Fail("исключение: " + HResult.Describe(ex));
        }
        finally
        {
            if (reopened is not null)
            {
                TryClose(reopened);
            }

            TryClose(doc);
        }
    }

    /// <summary>The first element of the reopened document in the Euler-angle mode: enumeration, identification by
    /// body, then the READ — before the rebuild and before any write.</summary>
    private (ILocalCoordinateSystem? Read, IBodyReposition? Element) ReadEulerAngle(
        ksDocument3D doc, ProbeStep step, string when)
    {
        var elements = Enumerate(doc, step);
        if (elements.Count == 0)
        {
            return (null, null);
        }

        var (index, name, feature) = elements[0];
        var local = feature.Position;
        var angles = local.LocalCSParameters as ILocalCSEulerParam;
        step.Observe(when + ", элемент " + index + " «" + name + "» (ДО сборки и ДО записи): "
            + "OrientationType=" + EnumName(() => (int)local.OrientationType, "ksOrientationTypeEnum")
            + ", RotationAngle=" + (angles is null ? "<не ILocalCSEulerParam>" : Num(() => angles.RotationAngle))
            + ", PrecessionAngle=" + (angles is null ? "-" : Num(() => angles.PrecessionAngle))
            + ", NutationAngle=" + (angles is null ? "-" : Num(() => angles.NutationAngle))
            + ", GetVector(OX)=" + ReadAxisOf(local, ksObj3dTypeEnum.o3d_axisOX)
            + ", Valid=" + Api5.Raw(SafeBool(() => local.Valid))
            + ", WriteToFile " + DumpPosition(feature,
                Path.Combine(_options.WorkDir, "rp22-" + when.Replace(' ', '-') + ".txt"), step,
                when + " элемент " + index));
        return (local, feature);
    }

    // ══════════════════════════════════════════════════════════════ RP.8 ══

    /// <summary>The probe's summary in one statement: either a route is named or a limit is named.</summary>
    private void Summarize()
    {
        var step = _report.Begin("RP.8", "Итог: назван ли документированный маршрут чтения входов",
            "Есть ли член, отдающий записанные входы и подтверждённый более чем одним известным входом?");

        var routes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var previous in _report.Steps)
        {
            foreach (var key in new[] { "vector_found_in", "axis_point_found_in", "routes_distinguishing_both_inputs" })
            {
                if (!previous.Data.TryGetValue(key, out var value) || value is not IEnumerable<string> list)
                {
                    continue;
                }

                foreach (var item in list)
                {
                    routes.Add(previous.Id + " " + key + ": " + item);
                }
            }
        }

        step.Data["routes"] = routes.ToArray();
        if (routes.Count == 0)
        {
            step.Fail("документированный маршрут чтения входов НЕ подтверждён: ни один член цепочки "
                + "не отдал записанные входы");
            return;
        }

        step.Pass("маршрут назван: " + string.Join(" · ", routes));
    }

    // ══════════════════════════════════════════════════════════════ reading ══

    /// <summary>A read of the WHOLE documented chain. Each member is called in a typed way through the interface
    /// the help page names directly; a failure is recorded by name and with the HRESULT, not by silence.</summary>
    /// <remarks>
    /// The "parameter → help page" mapping:
    /// <list type="bullet">
    /// <item><c>IBodyReposition.Position</c>, <c>.RepositionCentre</c> — <c>ibodyreposition_propers.html</c>;</item>
    /// <item><c>ILocalCoordinateSystem.X/Y/Z</c>, <c>.Vector3D</c>, <c>.GetVector</c>,
    /// <c>.ParameterType</c>, <c>.Parameters</c>, <c>.OrientationType</c>,
    /// <c>.LocalCSParameters</c> — <c>ilocalcoordinatesystem_props.html</c> and
    /// <c>ilocalcoordinatesystem_methods.html</c>;</item>
    /// <item><c>IPoint3DParamDisplace.DX/DY/DZ</c> — <c>ipoint3dparamdisplace_props.html</c>;</item>
    /// <item><c>ILocalCSAxesDirectionParam.AngleByOwnAxis</c> (write only),
    /// <c>.DirectingObject</c> — <c>ilocalcsaxesdirectionparam_props.html</c>;</item>
    /// <item><c>ILocalCSEulerParam.RotationAngle</c> — <c>ilocalcseulerparam_props.html</c>.</item>
    /// </list>
    /// </remarks>
    private Reading ReadDocumented(IBodyReposition reposition, ProbeStep step)
    {
        var reading = new Reading();

        ILocalCoordinateSystem? local = null;
        try
        {
            local = reposition.Position;
            reading.Values["IBodyReposition.Position"] = Api5.RuntimeName(local);
        }
        catch (Exception ex)
        {
            reading.Values["IBodyReposition.Position"] = HResult.Describe(ex);
        }

        // Valid is documented as DOC: «Возвращает признак невырожденности объекта», BOOL, read-only
        // (ilocalcoordinatesystem_valid.html). It is read SEPARATELY because it is the last documented
        // member of the chain by which a reader could tell a live placement object from one whose
        // placement was not restored on load.
        if (local is not null)
        {
            reading.Values["ILocalCoordinateSystem.Valid"] = Api5.Raw(SafeBool(() => local.Valid));
        }

        // The displacement centre point — a separate property of the feature (documented as IModelObject).
        try
        {
            var centre = reposition.RepositionCentre;
            reading.Values["IBodyReposition.RepositionCentre"] = Api5.RuntimeName(centre);
            if (centre is IPoint3D centrePoint)
            {
                var x = Try(() => centrePoint.X);
                var y = Try(() => centrePoint.Y);
                var z = Try(() => centrePoint.Z);
                reading.Values["RepositionCentre→IPoint3D.X/Y/Z"] = Triple(x, y, z);
                if (x is not null && y is not null && z is not null)
                {
                    reading.AxisPoint = new[] { x.Value, y.Value, z.Value };
                    reading.AxisPointRoute = "RepositionCentre→IPoint3D.X/Y/Z";
                }
            }
            else
            {
                reading.Values["RepositionCentre→IPoint3D"] = "объект НЕ подтверждает IPoint3D";
            }
        }
        catch (Exception ex)
        {
            reading.Values["IBodyReposition.RepositionCentre"] = HResult.Describe(ex);
        }

        // RepositionObjects — DOC: «Перемещаемые объекты (тела, поверхности, кривые, точки)», type
        // VARIANT, version v24 (ibodyreposition_repositionobjects.html). It is read because it is a
        // DOCUMENTED member of the feature, not because a vector is expected in it: the expectation is
        // checked, not assumed. This exhausts the list of documented members of the feature — none
        // remains unread.
        //
        // It is read by LATE BINDING not out of carelessness: the product type library declares it
        // (RP.1: 805:RepositionObjects), while the interop the client is compiled against does NOT.
        // A typed access to it does not compile, and that is a separate measured fact about the
        // divergence of the wrapper and the type library, not a workaround.
        try
        {
            var objects = SafeGet(reposition, "RepositionObjects");
            if (objects is Array array)
            {
                var items = array.Cast<object?>()
                    .Select(item => item is null ? "null" : Api5.RuntimeName(item))
                    .ToArray();
                reading.Values["IBodyReposition.RepositionObjects"] =
                    "[" + items.Length + "] " + string.Join(", ", items);
            }
            else
            {
                reading.Values["IBodyReposition.RepositionObjects"] = DescribeValue(objects);
            }
        }
        catch (Exception ex)
        {
            reading.Values["IBodyReposition.RepositionObjects"] = HResult.Describe(ex);
        }

        if (local is null)
        {
            return reading;
        }

        reading.Values["ILocalCoordinateSystem.ParameterType"] =
            EnumName(() => (int)local.ParameterType, "ksPoint3DTypeEnum");
        reading.Values["ILocalCoordinateSystem.OrientationType"] =
            EnumName(() => (int)local.OrientationType, "ksOrientationTypeEnum");
        reading.Values["ILocalCoordinateSystem.X/Y/Z"] =
            Triple(Try(() => local.X), Try(() => local.Y), Try(() => local.Z));

        // The anchor object: documented as DOC: «Получить опорный объект», read-only. With the coordinate
        // method it is exactly what X/Y/Z are measured FROM, so its absence changes the meaning of the
        // zeros: "zero relative to nothing" is not the same as "zero relative to the body".
        try
        {
            var anchor = local.AssociationObject;
            reading.Values["ILocalCoordinateSystem.AssociationObject"] = anchor is null
                ? "null"
                : Api5.RuntimeName(anchor);
        }
        catch (Exception ex)
        {
            reading.Values["ILocalCoordinateSystem.AssociationObject"] = HResult.Describe(ex);
        }

        var xyz = new[] { Try(() => local.X), Try(() => local.Y), Try(() => local.Z) };
        if (xyz.All(v => v is not null))
        {
            reading.Coordinates = new[] { xyz[0]!.Value, xyz[1]!.Value, xyz[2]!.Value };
            reading.CoordinatesRoute = "ILocalCoordinateSystem.X/Y/Z";
        }

        foreach (var (label, axis) in new[]
                 {
                     ("OX", ksObj3dTypeEnum.o3d_axisOX),
                     ("OY", ksObj3dTypeEnum.o3d_axisOY),
                     ("OZ", ksObj3dTypeEnum.o3d_axisOZ),
                 })
        {
            try
            {
                if (local.GetVector(axis, out var vx, out var vy, out var vz))
                {
                    reading.Values["ILocalCoordinateSystem.GetVector(" + label + ")"] =
                        Triple(vx, vy, vz);
                }
                else
                {
                    reading.Values["ILocalCoordinateSystem.GetVector(" + label + ")"] = "False";
                }
            }
            catch (Exception ex)
            {
                reading.Values["ILocalCoordinateSystem.GetVector(" + label + ")"] = HResult.Describe(ex);
            }

            // Vector3D(axis) — documented as DOC: «Вектор, задающий направление оси», read-only, type
            // IVector3D. It is checked separately from GetVector: they are different members.
            try
            {
                var vector = local.Vector3D[axis];
                reading.Values["ILocalCoordinateSystem.Vector3D(" + label + ")"] = vector is null
                    ? "null"
                    : Api5.RuntimeName(vector) + " → " + Triple(
                        LateNumber(vector, "X"), LateNumber(vector, "Y"), LateNumber(vector, "Z"));
            }
            catch (Exception ex)
            {
                reading.Values["ILocalCoordinateSystem.Vector3D(" + label + ")"] = HResult.Describe(ex);
            }
        }

        // The PARAMETERS interface is selected by ParameterType and obtained from Parameters via QI.
        try
        {
            var parameters = local.Parameters;
            reading.Values["ILocalCoordinateSystem.Parameters"] = Api5.RuntimeName(parameters);
            if (parameters is IPoint3DParamDisplace displace)
            {
                var dx = Try(() => displace.DX);
                var dy = Try(() => displace.DY);
                var dz = Try(() => displace.DZ);
                reading.Values["IPoint3DParamDisplace.DX/DY/DZ"] = Triple(dx, dy, dz);
                reading.Values["IPoint3DParamDisplace.Distance"] = Num(() => displace.Distance);
                if (dx is not null && dy is not null && dz is not null)
                {
                    reading.Displacement = new[] { dx.Value, dy.Value, dz.Value };
                    reading.DisplacementRoute = "Position.Parameters→IPoint3DParamDisplace.DX/DY/DZ";
                }

                try
                {
                    var direction = displace.Vector3D;
                    reading.Values["IPoint3DParamDisplace.Vector3D"] = Api5.RuntimeName(direction);
                    if (direction is not null)
                    {
                        // The member set of IVector3D is taken from the type library (RP.1), not guessed:
                        // here the declared ones are read, and the list itself goes into the report.
                        reading.Values["IPoint3DParamDisplace.Vector3D.X/Y/Z"] = Triple(
                            LateNumber(direction, "X"), LateNumber(direction, "Y"), LateNumber(direction, "Z"));
                    }
                }
                catch (Exception ex)
                {
                    reading.Values["IPoint3DParamDisplace.Vector3D"] = HResult.Describe(ex);
                }

                try
                {
                    var position = displace.PositionObject;
                    reading.Values["IPoint3DParamDisplace.PositionObject"] = Api5.RuntimeName(position);
                    if (position is IPoint3D anchor)
                    {
                        var ax = Try(() => anchor.X);
                        var ay = Try(() => anchor.Y);
                        var az = Try(() => anchor.Z);
                        reading.Values["PositionObject→IPoint3D.X/Y/Z"] = Triple(ax, ay, az);
                        if (reading.AxisPoint is null && ax is not null && ay is not null && az is not null)
                        {
                            reading.AxisPoint = new[] { ax.Value, ay.Value, az.Value };
                            reading.AxisPointRoute = "Position.Parameters→IPoint3DParamDisplace.PositionObject→IPoint3D";
                        }
                    }
                }
                catch (Exception ex)
                {
                    reading.Values["IPoint3DParamDisplace.PositionObject"] = HResult.Describe(ex);
                }
            }
            else
            {
                reading.Values["Parameters→IPoint3DParamDisplace"] =
                    "объект параметров НЕ подтверждает IPoint3DParamDisplace (ParameterType="
                    + reading.Values.GetValueOrDefault("ILocalCoordinateSystem.ParameterType") + ")";
            }
        }
        catch (Exception ex)
        {
            reading.Values["ILocalCoordinateSystem.Parameters"] = HResult.Describe(ex);
        }

        // ORIENTATION parameters: the interface is selected by OrientationType.
        try
        {
            var orientation = local.LocalCSParameters;
            reading.Values["ILocalCoordinateSystem.LocalCSParameters"] = Api5.RuntimeName(orientation);
            if (orientation is ILocalCSAxesDirectionParam axesParam)
            {
                try
                {
                    var lead = axesParam.LeadAxis;
                    reading.Values["ILocalCSAxesDirectionParam.LeadAxis"] = Api5.Raw(lead);
                }
                catch (Exception ex)
                {
                    reading.Values["ILocalCSAxesDirectionParam.LeadAxis"] = HResult.Describe(ex);
                }

                // AngleByOwnAxis is documented as DOC: «доступно только для записи» — it is NOT read by
                // construction, and this is recorded rather than passed off as a route failure.
                reading.Values["ILocalCSAxesDirectionParam.AngleByOwnAxis"] =
                    "справка: свойство доступно только для записи (ilocalcsaxesdirectionparam_anglebyownaxis.html)";
            }
            else if (orientation is ILocalCSEulerParam euler)
            {
                reading.Values["ILocalCSEulerParam.RotationAngle"] = Num(() => euler.RotationAngle);
                reading.Values["ILocalCSEulerParam.PrecessionAngle"] = Num(() => euler.PrecessionAngle);
                reading.Values["ILocalCSEulerParam.NutationAngle"] = Num(() => euler.NutationAngle);
                reading.AngleDeg = Try(() => euler.RotationAngle);
            }
            else
            {
                reading.Values["LocalCSParameters→интерфейс ориентации"] =
                    "объект параметров ориентации не подтвердил ни ILocalCSAxesDirectionParam, "
                    + "ни ILocalCSEulerParam";
            }
        }
        catch (Exception ex)
        {
            reading.Values["ILocalCoordinateSystem.LocalCSParameters"] = HResult.Describe(ex);
        }

        // ILocalCSObject — the subordinate object of the LCS, documented as obtained via QueryInterface.
        try
        {
            if (local is ILocalCSObject localObject)
            {
                reading.Values["ILocalCSObject.ModelObjectParamType"] =
                    EnumName(() => (int)localObject.ModelObjectParamType, "ksModelObjectParamTypeEnum");
            }
            else
            {
                reading.Values["ILocalCSObject"] = "объект НЕ подтверждает ILocalCSObject";
            }
        }
        catch (Exception ex)
        {
            reading.Values["ILocalCSObject"] = HResult.Describe(ex);
        }

        return reading;
    }

    private sealed class Reading
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public double[]? Displacement { get; set; }

        public string? DisplacementRoute { get; set; }

        public double[]? AxisPoint { get; set; }

        public string? AxisPointRoute { get; set; }

        public double[]? AxisDirection { get; set; }

        public double[]? Coordinates { get; set; }

        public string? CoordinatesRoute { get; set; }

        public double? AngleDeg { get; set; }

        public string? Kind { get; set; }

        public double[][]? Axes { get; set; }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    /// <summary>Find the written triple in the read values. NUMBERS are compared, not substrings: "17" contains
    /// "7", and a substring search would declare garbage a hit.</summary>
    private static string[] Hits(Dictionary<string, string> values, double[] triple) =>
        values.Where(p => ContainsTriple(p.Value, triple)).Select(p => p.Key).ToArray();

    private static bool ContainsTriple(string text, double[] triple)
    {
        var numbers = Numbers(text);
        for (var i = 0; i + 2 < numbers.Count; i++)
        {
            if (Math.Abs(numbers[i] - triple[0]) < 1e-6
                && Math.Abs(numbers[i + 1] - triple[1]) < 1e-6
                && Math.Abs(numbers[i + 2] - triple[2]) < 1e-6)
            {
                return true;
            }
        }

        return false;
    }

    private static List<double> Numbers(string text)
    {
        var found = new List<double>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"))
        {
            if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                found.Add(value);
            }
        }

        return found;
    }

    private static string Join(Dictionary<string, string> values) =>
        string.Join("; ", values.Select(p => p.Key + "=" + p.Value));

    private static string Triple(double? x, double? y, double? z) =>
        "(" + (x is null ? "?" : Api5.Num(x.Value))
            + ", " + (y is null ? "?" : Api5.Num(y.Value))
            + ", " + (z is null ? "?" : Api5.Num(z.Value)) + ")";

    private static string Num(Func<double> call)
    {
        try
        {
            return Api5.Num(call());
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    private static string EnumName(Func<int> call, string enumName)
    {
        try
        {
            return call() + " (" + enumName + ")";
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    private static double? Try(Func<double> call)
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

    /// <summary>A number read from a live object by late binding. Only for objects whose member the wrapper does
    /// not declare: <c>IVector3D</c> comes from <c>IPoint3DParamDisplace</c> and its member set is read
    /// from the type library (step RP.1).</summary>
    private static double? LateNumber(object comObject, string name)
    {
        try
        {
            return Late.Get(comObject, name) switch
            {
                double number => number,
                float small => small,
                int whole => whole,
                short tiny => tiny,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Launch()
    {
        var step = _report.Begin("RP.Z0", "Свой невидимый сеанс КОМПАС-3D v24", "Сеанс поднимается сам?");
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        step.Observe("свой процесс: " + Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid)));
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("RP.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit: " + HResult.Describe(ex));
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 15000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var alive = NewProcesses();
        step.Data["orphans"] = alive;
        step.Observe("своих процессов после Quit: " + alive.Count + ", ожидание " + waited + " мс");
        if (alive.Count == 0)
        {
            step.Pass("сеанс освобождён");
        }
        else
        {
            step.Unknown("процесс остался");
        }
    }

    private List<int> NewProcesses()
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            var pid = process.Id;
            process.Dispose();
            if (!_pidsBefore.Contains(pid))
            {
                found.Add(pid);
            }
        }

        return found;
    }

    private ksDocument3D NewPart(out ksPart part)
    {
        var doc = (ksDocument3D)_app.Document3D();
        doc.Create(true, true);
        if (doc.GetPart(-1) is not ksPart created)
        {
            throw new InvalidOperationException("деталь не получена");
        }

        part = created;
        return doc;
    }

    private static void TryClose(ksDocument3D? doc)
    {
        try
        {
            doc?.close();
        }
        catch (Exception)
        {
            // Not the subject of this step.
        }
    }

    private IModelContainer? Container(ksDocument3D doc)
    {
        try
        {
            return _app.TransferInterface(doc, 2, 0) is IKompasDocument3D document7
                && document7.TopPart is IModelContainer container
                ? container
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creating a body-reposition feature with a given 4×4 matrix.</summary>
    private IBodyReposition? CreateReposition(
        ksDocument3D doc, ksPart part, BodyRow target, double[] matrix, ProbeStep step, string label)
    {
        try
        {
            var container = Container(doc);
            if (container?.BodyRepositions?.Add() is not IBodyReposition reposition)
            {
                step.Observe(label + ": BodyRepositions.Add() недоступен");
                return null;
            }

            if (_app.TransferInterface(target.Element!, 2, 0) is not IKompasAPIObject transferred)
            {
                step.Observe(label + ": тело не переносится в API7");
                return null;
            }

            reposition.RepositionBody = transferred;
            reposition.Position.InitByMatrix3D(matrix);
            var updated = reposition.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe(label + ": Update()=" + updated);
            return updated ? reposition : null;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": создание признака — " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>Editing an EXISTING feature in place: the same matrix is rewritten into the same element, no new
    /// feature is created. Otherwise the "edit" would accumulate a second displacement.</summary>
    private bool EditReposition(
        ksDocument3D doc, ksPart part, IBodyReposition reposition, BodyRow? target, double[] matrix, ProbeStep step)
    {
        try
        {
            if (target is not null
                && _app.TransferInterface(target.Element!, 2, 0) is IKompasAPIObject transferred)
            {
                reposition.RepositionBody = transferred;
            }

            reposition.Position.InitByMatrix3D(matrix);
            var updated = reposition.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("правка: Update()=" + updated);
            return updated;
        }
        catch (Exception ex)
        {
            step.Observe("правка: " + HResult.Describe(ex));
            return false;
        }
    }

    private static bool? SafeBool(Func<bool> call)
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

    /// <summary>Builds an already created feature: body + matrix + Update(). Separate from
    /// <see cref="CreateReposition"/> because step RP.9 needs the feature OBJECT itself before the build.</summary>
    private bool CreateOn(ksPart part, ksDocument3D doc, IBodyReposition feature, BodyRow target,
        double[] matrix, ProbeStep step)
    {
        try
        {
            if (_app.TransferInterface(target.Element!, 2, 0) is not IKompasAPIObject transferred)
            {
                step.Observe("тело не переносится в API7");
                return false;
            }

            feature.RepositionBody = transferred;
            feature.Position.InitByMatrix3D(matrix);
            var updated = feature.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            step.Observe("сборка признака: Update()=" + updated);
            return updated;
        }
        catch (Exception ex)
        {
            step.Observe("сборка признака: " + HResult.Describe(ex));
            return false;
        }
    }

    /// <summary>The IUnknown pointer — the only way to tell "the same object" from "another object of the
    /// same class": for both, <c>Api5.RuntimeName</c> is the same.</summary>
    private static IntPtr Pointer(object comObject)
    {
        try
        {
            return Marshal.GetIUnknownForObject(comObject);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Which interfaces a live object confirms. A cast in C# is a QueryInterface, so
    /// "does not confirm" is the object's answer, not the probe's conclusion.</summary>
    private static List<string> ConfirmedInterfaces(object value)
    {
        var found = new List<string>();
        if (value is IPoint3D)
        {
            found.Add("IPoint3D");
        }

        if (value is ILocalCoordinateSystem)
        {
            found.Add("ILocalCoordinateSystem");
        }

        if (value is ILocalCSObject)
        {
            found.Add("ILocalCSObject");
        }

        if (value is IModelObject)
        {
            found.Add("IModelObject");
        }

        if (value is IPlacement3D)
        {
            found.Add("IPlacement3D");
        }

        if (value is IPoint3DParamDisplace)
        {
            found.Add("IPoint3DParamDisplace");
        }

        return found;
    }

    private static T? SafeObject<T>(Func<T> call) where T : class
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

    private static object? SafeGet(object target, string name)
    {
        try
        {
            return Late.Get(target, name);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribeValue(object? value) => value switch
    {
        null => "null",
        double number => Api5.Num(number),
        string text => text,
        _ => Api5.Raw(value),
    };

    private static BodyRow? ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return null;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() = false");
            return null;
        }

        return BodyRows(part).FirstOrDefault(r => Near(r, u0, v0, 0d, u1, v1, thickness));
    }

    private static ksEntity ProfileSketchOn(
        ksDocument3D doc, string name, short planeType, double u0, double u1, double v0, double v1)
    {
        var part = (ksPart)doc.GetPart(-1);
        if (part.GetDefaultEntity(planeType) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости " + planeType + " нет");
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
        }

        definition.EndEdit();
        return sketch;
    }

    private static List<BodyRow> BodyRows(ksPart part)
    {
        var rows = new List<BodyRow>();
        try
        {
            if (part.BodyCollection() is not ksBodyCollection bodies)
            {
                return rows;
            }

            bodies.refresh();
            var count = bodies.GetCount();
            for (var i = 0; i < count; i++)
            {
                if (bodies.GetByIndex(i) is not { } element)
                {
                    continue;
                }

                double[]? min = null;
                double[]? max = null;
                if (element is ksBody body)
                {
                    (double[] Min, double[] Max)? box = null;
                    if (Api5.SafeBool(() =>
                        {
                            var ok = body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                            if (ok)
                            {
                                box = (new[] { x1, y1, z1 }, new[] { x2, y2, z2 });
                            }

                            return ok;
                        }) == true
                        && box is { } value)
                    {
                        min = value.Min;
                        max = value.Max;
                    }
                }

                rows.Add(new BodyRow(i, element, Api5.BodyVolume(element), min, max));
            }
        }
        catch (Exception)
        {
            // What was read is returned.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 0.01 && Math.Abs(min[1] - y0) < 0.01 && Math.Abs(min[2] - z0) < 0.01
        && Math.Abs(max[0] - x1) < 0.01 && Math.Abs(max[1] - y1) < 0.01 && Math.Abs(max[2] - z1) < 0.01;

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    /// <summary>A 4×4 translation matrix in the layout measured by probe <c>--reposition</c> (step RP.2).</summary>
    private static double[] Translate(double[] vector) => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        vector[0], vector[1], vector[2], 1d,
    };

    /// <summary>
    /// The rotation matrix by an angle about an axis through a point. Rodrigues' formula; the translation
    /// <c>c − R·c</c> keeps the axis point in place. The 3×3 is laid out BY COLUMNS — the same layout as in
    /// <c>RepositionMatrix</c> (measured by steps RP.2/RP.4).
    /// </summary>
    private static double[] RotateAboutAxis(double[] axisPoint, double[] axisDirection, double angleDeg)
    {
        var length = Math.Sqrt(axisDirection[0] * axisDirection[0]
            + axisDirection[1] * axisDirection[1] + axisDirection[2] * axisDirection[2]);
        var k = new[] { axisDirection[0] / length, axisDirection[1] / length, axisDirection[2] / length };
        var angle = angleDeg * Math.PI / 180d;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var oneMinusCos = 1d - cos;

        var r = new[,]
        {
            { cos + (oneMinusCos * k[0] * k[0]), (oneMinusCos * k[0] * k[1]) - (sin * k[2]), (oneMinusCos * k[0] * k[2]) + (sin * k[1]) },
            { (oneMinusCos * k[1] * k[0]) + (sin * k[2]), cos + (oneMinusCos * k[1] * k[1]), (oneMinusCos * k[1] * k[2]) - (sin * k[0]) },
            { (oneMinusCos * k[2] * k[0]) - (sin * k[1]), (oneMinusCos * k[2] * k[1]) + (sin * k[0]), cos + (oneMinusCos * k[2] * k[2]) },
        };

        var c = axisPoint;
        var t = new[]
        {
            c[0] - ((r[0, 0] * c[0]) + (r[0, 1] * c[1]) + (r[0, 2] * c[2])),
            c[1] - ((r[1, 0] * c[0]) + (r[1, 1] * c[1]) + (r[1, 2] * c[2])),
            c[2] - ((r[2, 0] * c[0]) + (r[2, 1] * c[1]) + (r[2, 2] * c[2])),
        };

        return new[]
        {
            r[0, 0], r[1, 0], r[2, 0], 0d,
            r[0, 1], r[1, 1], r[2, 1], 0d,
            r[0, 2], r[1, 2], r[2, 2], 0d,
            t[0], t[1], t[2], 1d,
        };
    }

    /// <summary>The bounding box of the image of a box — a control independent of KOMPAS.</summary>
    private static (double[] Min, double[] Max) ExpectedBbox(double[] matrix, double[] min, double[] max)
    {
        var corners = new List<double[]>();
        foreach (var x in new[] { min[0], max[0] })
        {
            foreach (var y in new[] { min[1], max[1] })
            {
                foreach (var z in new[] { min[2], max[2] })
                {
                    corners.Add(new[]
                    {
                        (matrix[0] * x) + (matrix[4] * y) + (matrix[8] * z) + matrix[12],
                        (matrix[1] * x) + (matrix[5] * y) + (matrix[9] * z) + matrix[13],
                        (matrix[2] * x) + (matrix[6] * y) + (matrix[10] * z) + matrix[14],
                    });
                }
            }
        }

        return (
            new[] { corners.Min(p => p[0]), corners.Min(p => p[1]), corners.Min(p => p[2]) },
            new[] { corners.Max(p => p[0]), corners.Max(p => p[1]), corners.Max(p => p[2]) });
    }

    private sealed record BodyRow(int Index, object? Element, double? Volume, double[]? Min, double[]? Max)
    {
        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")");
    }
}
