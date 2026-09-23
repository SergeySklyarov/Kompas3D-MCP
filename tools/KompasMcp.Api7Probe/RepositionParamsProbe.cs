using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба RP — читаются ли ВХОДЫ признака изменения положения по ДОКУМЕНТИРОВАННОМУ маршруту API7.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем отдельная проба.</b> Проба RR (<c>--reposition-read</c>) заключила, что
/// <c>reposition_vector_mm</c> и <c>reposition_axis_point_mm</c> «не читаются ни одним маршрутом».
/// Её перечень членов брался из библиотеки типов — это верно, — но ОПРАШИВАЛА она объект двумя
/// способами, и оба обходят документированный маршрут:
/// </para>
/// <list type="number">
/// <item>поздним связыванием по <c>IDispatch::GetTypeInfo(0)</c>, который отдаёт УМОЛЧАТЕЛЬНЫЙ
/// интерфейс класса, — это её собственное наблюдение (RR.5), и оно же объясняет, почему
/// <c>Position</c> «объявлял» имена признака, а не имена системы координат;</item>
/// <item>типизированно — но только по <c>ILocalCoordinateSystem</c>, без приведения к интерфейсу
/// ПАРАМЕТРОВ, который документация называет прямо.</item>
/// </list>
/// <para>
/// <b>Документированный маршрут</b> (help.ascon.ru/KOMPAS_SDK/24/ru-RU, страницы названы у каждого
/// чтения): <c>IBodyReposition.Position</c> (только для чтения) → <c>ILocalCoordinateSystem</c>,
/// который наследует <c>IPoint3D</c> («все методы и свойства для позиционирования ЛСК») и добавляет
/// <c>X</c>, <c>Y</c>, <c>Z</c>, <c>Vector3D(ось)</c>, <c>GetVector(ось)</c>, <c>ParameterType</c>,
/// <c>Parameters</c>, <c>OrientationType</c>, <c>LocalCSParameters</c>. Величина смещения лежит в
/// интерфейсе ПАРАМЕТРОВ, который выбирается по <c>ParameterType</c> и берётся у
/// <c>Parameters</c> через <c>QueryInterface</c>: <c>IPoint3DParamDisplace.DX/DY/DZ</c>
/// (ksPDisplace) либо координаты <c>X/Y/Z</c> (ksPParamCoord).
/// </para>
/// <para>
/// <b>Отрицательный контроль обязателен.</b> Член, отдающий одну и ту же тройку на любом входе,
/// доказывает не чтение, а константу. Каждый маршрут проверяется ДВУМЯ известными переносами,
/// поворотом вокруг оси через точку вне начала координат и последовательностью «перенос → поворот».
/// Геометрия (габарит) и тождество признака проверяются ОТДЕЛЬНО от чтения параметров.
/// </para>
/// </remarks>
internal sealed class RepositionParamsProbe
{
    /// <summary>Основной перенос: ненулевой по всем трём координатам.</summary>
    private static readonly double[] TranslateMain = { 7d, -11d, 13d };

    /// <summary>Отрицательный контроль: другой ненулевой перенос того же вида.</summary>
    private static readonly double[] TranslateControl = { 1d, 2d, 3d };

    /// <summary>Точка оси поворота — ВНЕ начала координат (требование §3 наряда).</summary>
    private static readonly double[] AxisPoint = { 5d, 0d, 0d };

    private static readonly double[] AxisDirection = { 0d, 0d, 1d };

    private const double RotateAngleDeg = 90d;

    /// <summary>Постороннее тело, которого правка касаться не должна.</summary>
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

    /// <summary>
    /// Что библиотека типов ПРОДУКТА объявляет у каждого интерфейса документированной цепочки.
    /// Ни одно имя здесь не придумано: все читаются из <c>Bin\kAPI7.tlb</c>.
    /// </summary>
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

    /// <summary>
    /// Документированный маршрут на основном переносе. Читается ВСЯ цепочка; вопрос шага — каким
    /// членом отдаётся записанный вектор (7, −11, 13).
    /// </summary>
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

    /// <summary>
    /// Тот же маршрут на ДРУГОМ известном входе. Маршрут годен только если различает оба.
    /// </summary>
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

    /// <summary>
    /// Поворот вокруг оси ЧЕРЕЗ ТОЧКУ ВНЕ НАЧАЛА КООРДИНАТ. Читается и параметр, и геометрия —
    /// отдельно: «прочиталось» и «повернулось» доказываются разными наблюдениями.
    /// </summary>
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

    /// <summary>
    /// Последовательность «перенос → поворот» и независимое ПОСТОРОННЕЕ тело: правка одного признака
    /// не должна ни переставить второй, ни тронуть третье тело.
    /// </summary>
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

            // Второй признак — поворот уже перенесённого тела вокруг оси через (5,0,0).
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

    /// <summary>
    /// Чтение СРАЗУ ПОСЛЕ ИЗМЕНЕНИЯ ПАРАМЕТРОВ того же признака: правка записывает в тот же элемент,
    /// поэтому чтение обязано вернуть НОВОЕ значение, а не первоначальное.
    /// </summary>
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

    /// <summary>
    /// Save → close → open: признак берётся ЗАНОВО из переоткрытого документа, читаются и параметры,
    /// и геометрия. Вопрос наряда §3 требует именно этой ступени.
    /// </summary>
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

    /// <summary>
    /// Тождество объектов цепочки: <c>Position</c> — это ОТДЕЛЬНЫЙ объект или тот же самый, что и
    /// признак? Вопрос не праздный: <c>Api5.RuntimeName</c> у <c>Position</c> и у признака совпал
    /// (<c>BodyRepositionClass</c>), а если это один объект, то все чтения X/Y/Z/Parameters
    /// описывают признак, а не систему координаты, и «нули» означают не то, что кажется.
    /// </summary>
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

            // Признак и Position — разные объекты, если указатели разошлись; но у признака тоже есть
            // GetVector (измерено ранее), поэтому спрашивается ещё и состав членов.
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

    /// <summary>
    /// Спуск в <c>RepositionCentre</c> и <c>ILocalCSObject.CoordinateSystem</c> — два документированных
    /// объектных члена, которые предыдущая редакция прибора НЕ раскрывала: она опрашивала их
    /// поздним связыванием по умолчательному интерфейсу класса, а он у этих объектов совпадает с
    /// признаком. Здесь состав имён читается у САМОГО объекта и у интерфейсов, которые он
    /// подтверждает.
    /// </summary>
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

            // Поворот вокруг точки ВНЕ начала координат: точка оси известна заранее.
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

                // Позднее связывание по именам, объявленным САМИМ объектом: имя, которое библиотека
                // объявляет у другого интерфейса, объект всё равно не примет.
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

            // ILocalCSObject.CoordinateSystem — документированный «СК объекта» (версия v21).
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

    /// <summary>
    /// Документированные маршруты ЗАПИСИ: <c>IPoint3D.X/Y/Z</c> (разрешены при координатном типе
    /// параметров, а он и измерен — <c>ksPParamCoord</c>) и <c>ILocalCoordinateSystem.SetDisplacementByAxis</c>.
    /// Вопрос не «работает ли запись», а «даёт ли она ЧИТАЕМОЕ состояние»: если после записи тем же
    /// документированным маршрутом читается записанное, то входы читаются, и препятствие —
    /// не в API, а в маршруте создания.
    /// </summary>
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

        // Отрицательный контроль пары «есть поле / нет поля» ставится ОБЕИМИ половинами в одной
        // постановке: если ни один маршрут записи не дал читаемого состояния, шаг обязан назвать это
        // прямо, а не молчать.
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

    /// <summary>
    /// Пара «запись документированным членом → чтение его обратно», поставленная ОБЕИМИ половинами в
    /// ОДНОЙ постановке. Положительная половина доводит вход до признака способом «по координатам»
    /// (<c>ILocalCoordinateSystem.X/Y/Z</c> — <c>ilocalcoordinatesystem_x.html</c>, документирован и на
    /// чтение, и на запись); отрицательная доводит ТОТ ЖЕ вход матрицей
    /// (<c>InitByMatrix3D</c>) и документированные члены не трогает.
    /// </summary>
    /// <remarks>
    /// Зачем обе половины. Без положительной «не читается» прошло бы на продукте, который не отдаёт
    /// значение никогда; без отрицательной «читается» прошло бы на продукте, который отдаёт его
    /// всегда, — а тогда чтение не отличало бы записанное от константы. Сверх этого положительная
    /// половина повторяется на ПЕРЕОТКРЫТОМ документе: значение, живущее только в сеансе, — это кеш,
    /// а не параметр модели, и наряд §2 запрещает выдавать одно за другое.
    /// </remarks>
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

        // Отрицательный контроль ПРИБОРА, без которого вывод «после переоткрытия вход пуст» стоял бы
        // на недоказанном допущении, что переоткрытый объект вообще инициализирован. Поворот выбран
        // потому, что его ориентация НЕ совпадает с единичной: если объект отдаёт ХРАНИМЫЙ поворот,
        // он отвечает по делу, и пустой X/Y/Z — факт о продукте, а не о приборе.
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

    /// <summary>
    /// Один прогон пары: довести вход до признака, собрать, прочитать, при необходимости переоткрыть и
    /// прочитать снова. Все четыре величины возвращаются наружу, потому что вердикт ставится по ПАРЕ
    /// прогонов, а не внутри одного.
    /// </summary>
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

                    // ГЛАВНОЕ РАЗЛИЧЕНИЕ ЭТОГО ШАГА: «параметр не сохранился» и «сохранилась не
                    // геометрия» — РАЗНЫЕ факты о продукте, и по одному чтению они неразличимы.
                    // Габарит после переоткрытия читается здесь же и той же постановкой.
                    var reopenedPart = (ksPart)reopened.GetPart(-1);
                    var rowsAfter = BodyRows(reopenedPart);
                    step.Observe(item.Label + ": геометрия после переоткрытия " + Describe(rowsAfter));
                    step.Data["geometry_reopen_" + item.Slug] = Describe(rowsAfter);
                    var geometryKept = rowsAfter.Any(r => Near(r, expected.Min[0], expected.Min[1],
                        expected.Min[2], expected.Max[0], expected.Max[1], expected.Max[2]));
                    step.Data["geometry_reopen_matches_" + item.Slug] = geometryKept;
                    step.Observe(item.Label + ": габарит после переоткрытия совпал с объявленным — "
                        + geometryKept);

                    // Ступень, отличающая «вход израсходован при Update и сохранён матрицей» от
                    // «вход не сохранён вовсе»: в НОВОЙ сессии признак собирается заново, и после
                    // сборки читаются и вход, и геометрия.
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

    /// <summary>Постановка одной половины пары: ЧЕМ доводится вход, ЧТО ищется в чтении и какой
    /// габарит объявлен независимым контролем.</summary>
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
    /// Последняя неразобранная документированная ветвь: <c>ILocalCSObject.CoordinateSystem</c> —
    /// «СК объекта», справка <c>ilocalcsobject_coordinatesystem.html</c>, версия v21, тип данных
    /// <c>IModelObject</c>, чтение и запись.
    /// </summary>
    /// <remarks>
    /// Вопрос ставится на ПЕРЕОТКРЫТОМ документе не случайно: именно там входной буфер X/Y/Z пуст
    /// (RP.12), а размещение сохранено. Если размещение в модели есть и документированная ссылка на
    /// систему координаты объекта его отдаёт — требование выполнимо. Если не отдаёт — оно снимается
    /// не потому, что «код вернул нули», а потому, что документированных членов больше не осталось.
    /// </remarks>
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

    /// <summary>Раскрыть СК объекта: собственные имена, подтверждённые интерфейсы и чтение по каждому
    /// имени. Ни одно имя не придумано — перечень берётся у самого объекта.</summary>
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

    /// <summary>
    /// Переоткрытый признак: тот же объект или заново полученный?
    /// </summary>
    /// <remarks>
    /// Зачем это нужно. RP.12 показал, что на переоткрытом документе <c>GetVector</c> отдаёт ЕДИНИЧНУЮ
    /// ориентацию при габарите, который подтверждает поворот. Из одного этого НЕЛЬЗЯ заключить, что
    /// параметры не хранятся: ровно так же выглядел бы объект, полученный из кэша до того, как
    /// документ досчитал модель. Здесь обе версии разводятся: один и тот же признак читается сразу
    /// после открытия и ЗАНОВО ПОЛУЧЕННЫЙ после сборки, и сравниваются и указатели, и ориентация.
    /// Без этого шага вывод «размещение не читается после переоткрытия» описывал бы прибор, а не
    /// продукт, — тот же класс, что случай <c>4/tan</c>.
    /// </remarks>
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

            // Половина 1: объект берётся сразу после открытия, до любой сборки.
            var first = Fetch(reopened, out var ptrFirst);
            var oxFirst = first is null
                ? "<признак не найден>"
                : ReadDocumented(first, step).Values.GetValueOrDefault("ILocalCoordinateSystem.GetVector(OX)");
            step.Data["reopen_first_pointer"] = ptrFirst;
            step.Data["reopen_first_get_vector_ox"] = oxFirst;
            step.Observe("сразу после открытия: IUnknown=" + ptrFirst + ", GetVector(OX)=" + oxFirst);

            // Сборка в новой сессии — ступень, после которой параметры могли бы материализоваться.
            part2.RebuildModel();
            reopened.RebuildDocument();
            var rows = BodyRows(part2);
            step.Observe("геометрия после сборки: " + Describe(rows));
            step.Data["geometry_after_rebuild"] = Describe(rows);

            // Половина 2: признак ПОЛУЧАЕТСЯ ЗАНОВО из коллекции, а не переиспользуется ссылка.
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

            // Вердикт ставится по ОРИЕНТАЦИИ, а не по пустоте: единичная ось при повёрнутом теле —
            // это либо потерянное размещение, либо непригодный объект, и оба случая обязаны быть
            // названы, а не сглажены.
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

    /// <summary>Получить признак изменения положения заново — из свежего контейнера и свежего
    /// элемента коллекции, а не из ранее сохранённой ссылки.</summary>
    /// <remarks>
    /// Доступ по индексу документирован (<c>ibodyrepositions_bodyreposition.html</c>, «Возвращает
    /// элемент, заданный по индексу», <c>VARIANT Index</c> — «индекс или имя элемента»). Число
    /// элементов при этом ПЕЧАТАЕТСЯ вместе с указателем: без него «взят первый» — предположение о
    /// том, что он единственный, а не измерение. Шаг RP.16 идёт дальше и опознаёт элемент ПО ТЕЛУ.
    /// </remarks>
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
    // Наряд 19.09.2026 «устранить неправильное чтение преобразований после переоткрытия»:
    //   §2 — проверить реализацию строго по документации SDK (интерфейс, объект, сигнатура,
    //        обработка результата, последовательность работы с документом);
    //   §4 — различающие проверки на несимметричном теле 20×10×5; первое чтение после открытия
    //        ДО любой записи; положительный контроль НАСТОЯЩЕГО переноса; постороннее тело;
    //        геометрия, тождество признака и правильность параметров — РАЗДЕЛЬНО.
    //
    // Прибор обязан быть чист ДО того, как измерение станет фактом о продукте. Прежний Fetch брал
    // collection[0] и не перечислял коллекцию вовсе; здесь перечисление, имя и тело каждого элемента
    // обязательны, а содержимое САМОГО объекта снимается документированным Position.WriteToFile —
    // иначе «не читается» описывало бы прибор, а не продукт (класс 4/tan).

    /// <summary>
    /// Переоткрытие: подготовка различающих случаев, первое чтение ДО записи, положительный контроль
    /// настоящего переноса, содержимое самого объекта, геометрия и воспроизводимость.
    /// </summary>
    private void ReopenDiscriminating()
    {
        var setup = _report.Begin("RP.15", "Переоткрытие: подготовка различающих случаев",
            "Записаны ли различающие преобразования так, что их различает ГЕОМЕТРИЯ, а не только чтение?");

        var doc = NewPart(out var part);
        ksDocument3D? reopened = null;
        try
        {
            // Тело A — поворот +90° вокруг Z через точку ВНЕ начала координат (5,0,0).
            // Тело B — НАСТОЯЩИЙ перенос: положительный контроль, без которого запрет значения
            //          «translate» прошёл бы как исправление.
            // Тело S — постороннее: правка его не касается.
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

            // Живое чтение и СОДЕРЖИМОЕ САМОГО ОБЪЕКТА — эталон для сравнения с переоткрытым.
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

            // ПЕРВОЕ чтение: до сборки, до правки, до всего, что могло бы вернуть верное значение.
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

            // Признак опознаётся по ТЕЛУ, которое он перемещает, ВНУТРИ ЭТОГО ЖЕ документа:
            // имя у всех признаков одно и то же, порядок — предположение, а RepositionBody документирован.
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
                // Полная карта документированного чтения переоткрытого признака — чтобы в записи
                // осталось ВСЁ, что было испробовано, а не только тот член, которым судят вердикт.
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

            // Содержимое снимается у ВСЕХ элементов, а не только у сопоставленного: без этого отказ
            // сопоставления прятал бы ответ, ради которого шаг и написан (урок шага SP.6).
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
            // Последний документированный маршрут к ЛСК: IAuxiliaryGeomContainer.LocalCoordinateSystems
            // (iauxiliarygeomcontainer.html — «Позволяет получить коллекции объектов вспомогательной
            // геометрии (ЛСК, сплайн и т.д)», берётся у IPart7 через QueryInterface). Проверяется ДО
            // любой записи: если размещение лежит там, оно обязано читаться здесь.
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

                // Ступень 1: документированный Update() на переоткрытом объекте — не читает, а ПРИМЕНЯЕТ.
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

                // Ступень 2: запись того же поворота — возвращает ли она верное чтение.
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

                // Ступень 3: сохранить, закрыть, открыть ЗАНОВО — держится ли верное чтение.
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
    /// Перечислить ВСЕ элементы коллекции признаков и назвать каждый. Прежний <c>Fetch</c> брал
    /// <c>collection[0]</c>: доступ по индексу документирован
    /// (<c>ibodyrepositions_bodyreposition.html</c> — «Возвращает элемент, заданный по индексу»,
    /// <c>VARIANT Index</c> — «индекс или имя элемента»), но БЕЗ перечисления не видно ни числа
    /// элементов, ни того, что прочитан тот самый.
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

    /// <summary>
    /// Номер элемента, перемещающего заданное тело ЭТОГО ЖЕ документа. Опознание идёт по
    /// ДОКУМЕНТИРОВАННОМУ <c>IBodyReposition.RepositionBody</c>, а не по порядку и не по имени: у
    /// всех признаков одного документа имя совпадает, а порядок — предположение, а не измерение.
    /// </summary>
    /// <remarks>
    /// Сравнивать <c>RepositionBody</c> живого признака с переоткрытыми элементами НЕЛЬЗЯ: живой
    /// признак принадлежит уже закрытому документу, и его тело — объект ДРУГОГО документа. Первая
    /// редакция этого шага так и делала, и отказ сопоставления выглядел как отказ продукта. Тело
    /// берётся из ТОГО ЖЕ документа, чьи элементы перечисляются.
    /// </remarks>
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
    /// Содержимое САМОГО объекта признака, записанное документированным
    /// <c>ILocalCoordinateSystem.WriteToFile</c> (<c>ilocalcoordinatesystem_writetofile.html</c>).
    /// Это то, что лежит В ОБЪЕКТЕ, а не то, что о нём думает прибор: различие «объект пуст» и
    /// «объект заполнен, но читается неверно» без этой ступени неразрешимо.
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

    /// <summary>Ось ЛСК документированным <c>GetVector</c>; отказ называется, а не прячется.</summary>
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

    /// <summary>Содержимое ЛСК, записанное её собственным <c>WriteToFile</c>.</summary>
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

    /// <summary>
    /// Переоткрытие: документированный режим углов Эйлера.
    /// </summary>
    /// <remarks>
    /// <c>ILocalCSEulerParam.RotationAngle</c> документирован как ЧТЕНИЕ И ЗАПИСЬ («Свойство
    /// позволяет устанавливать и получать угол вращения», <c>ilocalcseulerparam_rotationangle.html</c>),
    /// и это единственный член всей цепочки с таким доступом к УГЛУ. Прежняя редакция отвела его
    /// УСЛОВНО — «это ДРУГОЙ режим ориентации, а у признаков продукта <c>OrientationType = 0</c>», —
    /// то есть по состоянию, созданному НАШИМ ЖЕ маршрутом записи, а не по справке. Здесь режим
    /// ставится сам, документированным членом <c>OrientationType</c>, и проверяется прямо: переживает
    /// ли угол переоткрытие.
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

            // Документированный порядок: сначала режим ориентации, потом интерфейс параметров ЭТОГО
            // режима (ilocalcoordinatesystem_localcsparameters.html: «В зависимости от типа
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

            // ПЕРВЫЙ угол выбран так, чтобы он НЕ совпадал со вторым: без двух разных значений
            // «прочиталось 90» не отличалось бы от «член отдаёт постоянное».
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

            // ── сессия 2: первое чтение ДО сборки и ДО любой записи ──
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

            // ── сессия 3: ВТОРОЙ, другой угол — различающая половина пары ──
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

    /// <summary>
    /// Документированный ПАРАМЕТРИЧЕСКИЙ маршрут целиком: три угла Эйлера, произвольная ось и
    /// начало ЛСК — что из этого переживает переоткрытие.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> RP.22 измерил ОДИН угол (<c>RotationAngle</c>). Для требования
    /// чтения этого мало по двум причинам. Первая: продукт умеет поворот вокруг ПРОИЗВОЛЬНОЙ оси, и
    /// если параметрический маршрут выражает только координатные оси, он требования не закрывает.
    /// Вторая: у ЛСК есть НАЧАЛО (<c>X/Y/Z</c>), и в RP.22 оно не читалось после переоткрытия —
    /// то есть «параметры переживают переоткрытие» относилось к углу, а не к размещению целиком.
    /// Здесь ставятся ТРИ угла сразу — <c>RotationAngle</c>, <c>NutationAngle</c>,
    /// <c>PrecessionAngle</c> (<c>ilocalcseulerparam_props.html</c>: все три документированы как
    /// чтение И запись, тип <c>double</c>), причём тремя РАЗНЫМИ величинами.
    /// </para>
    /// <para>
    /// <b>Порядок перемножения углов НЕ угадывается.</b> Справка задаёт его рисунком
    /// (<c>rotation_pict.html</c>), а не текстом, поэтому прибор не строит ожидаемый поворот из
    /// углов. Он читает ЖИВУЮ матрицу (<c>GetVector</c> по трём осям) и требует, чтобы габарит тела
    /// совпал с ЭТОЙ матрицей: геометрия и матрица обязаны говорить одно и то же. Ось поворота
    /// считается ИЗ ИЗМЕРЕННОЙ матрицы, а не предполагается, — и вопрос «выражает ли маршрут
    /// произвольную ось» решается измерением, а не рассуждением.
    /// </para>
    /// <para>
    /// <b>Различающая пара.</b> Второй документ ставится с ДРУГОЙ тройкой углов: чтение обязано
    /// вернуть каждому документу ЕГО тройку. Без этого «прочиталось 30» не отличалось бы от
    /// «вернулось то, что прибор сам и положил».
    /// </para>
    /// </remarks>
    private void EulerAllAnglesRoute()
    {
        var step = _report.Begin("RP.24", "Параметрический маршрут: три угла, произвольная ось и начало ЛСК",
            "Переживает ли переоткрытие тройка углов Эйлера целиком, выражает ли маршрут "
            + "произвольную ось, и сохраняется ли читаемость начала ЛСК?");

        // Третий случай ОБЯЗАТЕЛЕН и добавлен после первой редакции этого шага. В ней начало ЛСК не
        // записывалось вовсе, и «после переоткрытия X/Y/Z = (0, 0, 0)» было объявлено границей
        // переноса — при том что ноль и не должен был никуда деться. Наблюдение без различающей
        // половины не является измерением: чтобы вопрос «переживает ли переоткрытие НАЧАЛО»
        // решался, начало надо СНАЧАЛА записать ненулевым, и только потом читать.
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

                // Документированный порядок: сначала режим ориентации, потом интерфейс параметров
                // ЭТОГО режима (ilocalcoordinatesystem_localcsparameters.html).
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
                    // Начало ЛСК тем же документированным членом, что и в RP.11/RP.12
                    // (ilocalcoordinatesystem_x.html — чтение и запись), но БЕЗ матрицы: проверяется
                    // параметрический маршрут целиком, а не только углы.
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

                // ── ПЕРВОЕ чтение — ДО сборки и ДО любой записи ──
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
                // Различающая половина НАЧАЛА ЛСК: сравнение с ЗАПИСАННЫМ, а не с нулём. Если начало
                // не записывалось, вопрос не измерен — и называется «не измерено», а не «потеряно».
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
            // Про начало ЛСК говорится ТОЛЬКО измеренное. Половина «начало не записывалось» даёт
            // «не измерено», а не «потеряно»: ноль, который никуда не девался, ничего не
            // доказывает, и объявлять его границей переноса значило бы выдать отсутствие опыта за
            // результат опыта.
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
    /// Порядок и единицы углов Эйлера — ИЗМЕРЕНИЕМ, и маршрут смещения
    /// <c>ParameterType = ksPDisplace</c> + <c>IPoint3DParamDisplace.DX/DY/DZ</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> RP.24 измерил, что тройка углов переоткрытие переживает, но
    /// оставил два вопроса открытыми, и оба решают, годится ли маршрут продукту.
    /// </para>
    /// <list type="number">
    /// <item><b>Порядок перемножения углов.</b> Справка задаёт его РИСУНКОМ
    /// (<c>rotation_pict.html</c> → <c>images/_praezession.jpg</c>: вращение R — вокруг
    /// собственной оси тела, прецессия P — вокруг вертикали, нутация N — наклон). Рисунок называет
    /// ОСИ и СМЫСЛ, но не порядок произведения, поэтому порядок здесь измеряется, а не подбирается
    /// под один пример: каждый угол ставится ОДИН (90°, остальные нули), ось поворота вычисляется из
    /// ЖИВОЙ матрицы, а затем три составных постановки сравниваются со всеми шестью произведениями
    /// этих трёх матриц. Порядок — тот, что совпал во ВСЕХ трёх постановках.</item>
    /// <item><b>Единицы.</b> Если угол 90 даёт четверть оборота, а 30 — третью часть, углы заданы в
    /// ГРАДУСАХ; проверяется чтением угла ИЗ ИЗМЕРЕННОЙ матрицы, а не совпадением одного числа.</item>
    /// <item><b>Переносная часть.</b> Справка даёт для неё отдельный документированный маршрут:
    /// <c>ILocalCoordinateSystem.ParameterType</c> (чтение и запись, <c>ksPoint3DTypeEnum</c>,
    /// <c>ilocalcoordinatesystem_parametertype.html</c>) со значением <c>ksPDisplace = 2</c>
    /// «По смещению от опорного объекта», после чего <c>Parameters</c> (только для чтения)
    /// приводится к <c>IPoint3DParamDisplace</c> с <c>DX/DY/DZ</c> — «Смещение по X/Y/Z», чтение И
    /// запись (<c>ipoint3dparamdisplace_dx.html</c>). Прежние шаги проверяли ТОЛЬКО
    /// <c>ksPParamCoord = 1</c> и получили <c>Parameters = null</c>; <c>ksPDisplace</c> не проверялся
    /// ни разу. Это и есть конкретная новая причина, по которой маршрут ставится здесь, а не
    /// повторяется прежде проверенный.</item>
    /// </list>
    /// <para>
    /// <b>Различающая пара и отрицательный контроль обязательны.</b> Два РАЗНЫХ ненулевых смещения
    /// при одной и той же тройке углов отличают чтение от константы. Отрицательный контроль —
    /// постановка, в которой смещение НЕ ЗАПИСЫВАЛОСЬ вовсе: её тройка не должна быть равна ни
    /// одной из записанных, иначе «прочиталось» означало бы «вернулось то, что прибор сам положил».
    /// </para>
    /// <para>
    /// <b>Первое чтение — ДО ЛЮБОЙ ЗАПИСИ.</b> Запись временно восстанавливает верное чтение
    /// (RP.20), поэтому признак читается сразу после открытия, до сборки и до любого <c>Update()</c>.
    /// </para>
    /// <para>
    /// <b>Преобразование «ось + угол ↔ углы Эйлера» проверяется ЭКВИВАЛЕНТНОСТЬЮ МАТРИЦ.</b>
    /// Параметризация углами неоднозначна, поэтому сверяются не числа, а произведения: матрица,
    /// собранная из ПРОЧИТАННЫХ углов в ИЗМЕРЕННОМ порядке, обязана совпасть с матрицей поворота на
    /// заданный угол вокруг заданной оси. Проверка идёт на произвольной оси, на 90° и 180° и на оси
    /// через точку вне начала координат — то есть на том контракте, который продукт и обещает.
    /// </para>
    /// </remarks>
    private void EulerOrderAndDisplacement()
    {
        var step = _report.Begin("RP.25", "Порядок углов Эйлера, единицы и маршрут смещения",
            "Каким порядком спрягаются три угла, в каких единицах они заданы, и переживает ли "
            + "переоткрытие документированный маршрут смещения ParameterType = ksPDisplace + "
            + "IPoint3DParamDisplace.DX/DY/DZ?");

        var failed = new List<string>();

        // ── A. оси трёх углов: по одному углу за раз ────────────────────────────────────────────
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
            // Ось фактора берётся из ИЗМЕРЕННОЙ матрицы (собственный вектор с собственным значением
            // 1), а НЕ из её столбца: столбец — это образ базисного вектора, и у поворота вокруг X
            // третий столбец равен (0,−1,0), то есть осью не является. Первая редакция шага брала
            // именно столбец и потому не собрала ни одного произведения.
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

        // Единицы: не 90, а 30 — если угол читается как треть оборота, углы заданы в градусах.
        var units = RunEulerCase(step, "A-units", 30d, 0d, 0d, displacement: null,
            writeDisplacement: false, keep: false);
        if (units.LiveMatrix is not null)
        {
            var (_, unitAngle) = RotationOf(units.LiveMatrix);
            step.Data["angle_deg_for_30"] = unitAngle;
            step.Observe("A: угол R = 30 дал поворот на " + Api5.Num(unitAngle)
                + "° — единицы " + (Math.Abs(unitAngle - 30d) < 0.01 ? "ГРАДУСЫ" : "НЕ градусы"));
        }

        // ── A2. порядок: три составных постановки против всех шести произведений ────────────────
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

        // ── B. маршрут смещения: ksPDisplace + IPoint3DParamDisplace.DX/DY/DZ ───────────────────
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
                // Отрицательный контроль: смещение не записывалось. Его прочитанная тройка обязана
                // отличаться от КАЖДОЙ записанной — иначе чтение вернуло бы навязанное значение.
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

        // ── C. ось + угол ↔ углы Эйлера: эквивалентность МАТРИЦ ─────────────────────────────────
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

            // Переносная часть — то же размещение, что у матричного маршрута: t = c − R·c. Именно это
            // равенство измерено различающей парой RP.12, поэтому оно берётся как ожидание, а не
            // постулируется заново.
            var translation = TranslationOf(target, item.Point);

            // Углы пишутся в трёх РАЗНЫХ полях модели: вращение, нутация, прецессия.
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

        // ── итог ────────────────────────────────────────────────────────────────────────────────
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

        // Маршрут назван ИМЕНЕМ и подтверждён различающей парой, а не «похоже, работает»: обе
        // половины размещения (ориентация и перенос) прочитаны с ПЕРЕОТКРЫТОГО документа, и
        // эквивалентность матриц подтверждена на независимых постановках. Точка оси при этом не
        // читается отдельным членом — она ВЫВОДИТСЯ из пары «ориентация + перенос», и это записано
        // здесь прямо, чтобы название маршрута не обещало больше измеренного.
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

    /// <summary>
    /// Полный жизненный цикл одной постановки: создание → запись → живое измерение → сохранение →
    /// закрытие → НОВАЯ сессия → открытие → чтение ДО ЛЮБОЙ ЗАПИСИ → геометрия после сборки.
    /// </summary>
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

            // Документированный порядок: сначала режим ориентации, потом интерфейс параметров ЭТОГО
            // режима (ilocalcoordinatesystem_localcsparameters.html).
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
                // Документированный порядок тот же: сначала тип параметров точки, потом интерфейс
                // ЭТОГО типа у Parameters (ilocalcoordinatesystem_parametertype.html).
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
                // Прибор описывает себя, а не продукт: матрица, которую он же и прочитал, не
                // объясняет геометрию. Продолжать на таком основании нельзя.
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

            // ── ПЕРВОЕ чтение — ДО сборки и ДО любой записи ──
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

    /// <summary>Смещение из документированного маршрута <c>ksPDisplace</c>, без подстановок.</summary>
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

    /// <summary>Матрица 3×3 (строки) из 4×4, собранной по столбцам (раскладка измерена RP.2).</summary>
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

    /// <summary>
    /// Поворот на угол вокруг заданной оси. Ось приходит ИЗМЕРЕННОЙ (собственный вектор матрицы
    /// единичного угла), а не предполагается координатной.
    /// </summary>
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

    /// <summary>
    /// Произведение трёх поворотов в ИЗМЕРЕННОМ порядке. <paramref name="angles"/> индексирован
    /// ИДЕНТИФИКАТОРОМ фактора (0 — прецессия P, 1 — нутация N, 2 — вращение R), а
    /// <paramref name="order"/> называет, какой фактор стоит на какой позиции произведения.
    /// </summary>
    private static double[] ComposeEuler(double[][] axes, int[] order, double[] angles)
    {
        var result = Identity3();
        for (var i = 0; i < 3; i++)
        {
            result = Mul3(result, Rot3(axes[order[i]], angles[order[i]]));
        }

        return result;
    }

    /// <summary>Тройка из модели (вращение, нутация, прецессия) → индексация по фактору (P, N, R).</summary>
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

    /// <summary>
    /// Углы Эйлера для заданной матрицы — ЧИСЛЕННЫМ поиском при ИЗМЕРЕННОМ порядке. Порядок не
    /// подбирается: он установлен шагом A и передан сюда; подбираются только углы, а годность
    /// подбора доказывает эквивалентность матриц на НЕЗАВИСИМЫХ постановках (шаг C).
    /// </summary>
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

    /// <summary>
    /// Матрица 4×4, собранная из ТРЁХ осей, прочитанных <c>GetVector</c> (раскладка измерена RP.2:
    /// 3×3 по столбцам в 0…10, перенос в 12…14). Не прочиталось — остаётся единичной, и это видно
    /// в габарите, а не прячется.
    /// </summary>
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

    /// <summary>Ось и угол поворота, ВЫЧИСЛЕННЫЕ из измеренной матрицы, а не предположенные.</summary>
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

    /// <summary>
    /// Переоткрытие: восстанавливает ли чтение документированная ПОСЛЕДОВАТЕЛЬНОСТЬ работы с
    /// документом (RP.23).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Наряд §2 требует проверить не только интерфейс и сигнатуру, но и
    /// «последовательность работы с документом». Прежние шаги читали переоткрытый признак СРАЗУ
    /// (RP.14, RP.16) и ПОСЛЕ записи (RP.20), а ступень «документированный <c>Update()</c> — и
    /// прочитать» осталась неизмеренной: RP.20 вызывал <c>Update()</c>, но читал только после
    /// ПОВТОРНОЙ ЗАПИСИ, поэтому «Update() сам восстановил чтение» и «чтение восстановила запись»
    /// там неразличимы.
    /// </para>
    /// <para>
    /// <b>Почему это решающий вопрос, а не педантизм.</b> Если документированный вызов делает
    /// матричный вид размещения актуальным, то дефект чтения лечится ПОСЛЕДОВАТЕЛЬНОСТЬЮ, и продукт
    /// обязан её соблюдать. Если ни один документированный вызов этого не делает, то публиковать
    /// выведенное преобразование нельзя ни при каких условиях — и тогда правильный ответ продукта
    /// есть явное состояние нечитаемости.
    /// </para>
    /// <para>
    /// <b>Первое чтение — до любого вызова.</b> Запись параметров восстанавливает чтение (RP.20), и
    /// именно поэтому первое чтение берётся ДО всех ступеней; ступени сравниваются с ним, а не друг
    /// с другом.
    /// </para>
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

            // Ступень A — первое чтение ДО любого вызова: это и есть точка отсчёта.
            var a = Ox();
            step.Observe("A. сразу после открытия, ДО любого вызова: GetVector(OX)=" + a);

            // Ступень B — документированный IModelObject.Update() на самом признаке.
            var updated = SafeBool(() => target.Update());
            var b = Ox();
            step.Observe("B. после IModelObject.Update() признака (" + Api5.Raw(updated)
                + "): GetVector(OX)=" + b);

            // Ступень C — документированный ILocalCoordinateSystem.Update() на системе координат.
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

            // Ступень D — документированная сборка документа.
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

    /// <summary>
    /// Первый элемент переоткрытого документа в режиме углов Эйлера: перечисление, опознание по
    /// телу, затем ЧТЕНИЕ — до сборки и до любой записи.
    /// </summary>
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

    /// <summary>Итог пробы одним утверждением: назван маршрут или назван предел.</summary>
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

    // ══════════════════════════════════════════════════════════════ чтение ══

    /// <summary>
    /// Чтение ВСЕЙ документированной цепочки. Каждый член вызывается типизированно по интерфейсу,
    /// который справка называет прямо; отказ записывается по имени и с HRESULT, а не молчанием.
    /// </summary>
    /// <remarks>
    /// Соответствие «параметр → страница справки»:
    /// <list type="bullet">
    /// <item><c>IBodyReposition.Position</c>, <c>.RepositionCentre</c> — <c>ibodyreposition_propers.html</c>;</item>
    /// <item><c>ILocalCoordinateSystem.X/Y/Z</c>, <c>.Vector3D</c>, <c>.GetVector</c>,
    /// <c>.ParameterType</c>, <c>.Parameters</c>, <c>.OrientationType</c>,
    /// <c>.LocalCSParameters</c> — <c>ilocalcoordinatesystem_props.html</c> и
    /// <c>ilocalcoordinatesystem_methods.html</c>;</item>
    /// <item><c>IPoint3DParamDisplace.DX/DY/DZ</c> — <c>ipoint3dparamdisplace_props.html</c>;</item>
    /// <item><c>ILocalCSAxesDirectionParam.AngleByOwnAxis</c> (только запись),
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

        // Valid документирован как «Возвращает признак невырожденности объекта», BOOL, только для
        // чтения (ilocalcoordinatesystem_valid.html). Читается ОТДЕЛЬНО, потому что это последний
        // документированный член цепочки, которым читатель мог бы отличить живой объект размещения
        // от объекта, размещение которого не восстановлено при загрузке.
        if (local is not null)
        {
            reading.Values["ILocalCoordinateSystem.Valid"] = Api5.Raw(SafeBool(() => local.Valid));
        }

        // Точка центра смещения — отдельное свойство признака (документировано как IModelObject).
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

        // RepositionObjects — «Перемещаемые объекты (тела, поверхности, кривые, точки)», тип VARIANT,
        // версия v24 (ibodyreposition_repositionobjects.html). Читается потому, что это
        // ДОКУМЕНТИРОВАННЫЙ член признака, а не потому, что в нём ожидается вектор: ожидание
        // проверяется, а не предполагается. Этим перечень документированных членов признака
        // исчерпан — непрочитанных не осталось.
        //
        // Читается ПОЗДНИМ СВЯЗЫВАНИЕМ не по небрежности: библиотека типов продукта объявляет его
        // (RP.1: 805:RepositionObjects), а interop, против которого собирается клиент, — НЕТ.
        // Типизированное обращение к нему не компилируется, и это отдельный измеренный факт о
        // расхождении обёртки и библиотеки типов, а не обход.
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

        // Опорный объект: документирован как «Получить опорный объект», только для чтения. При
        // координатном способе он и есть то, ОТ ЧЕГО отсчитываются X/Y/Z, поэтому его отсутствие
        // меняет смысл нулей: «ноль относительно ни чего» — не то же, что «ноль относительно тела».
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

            // Vector3D(ось) — документирован как «Вектор, задающий направление оси», только для
            // чтения, тип IVector3D. Проверяется отдельно от GetVector: это разные члены.
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

        // Интерфейс ПАРАМЕТРОВ выбирается по ParameterType и берётся у Parameters через QI.
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
                        // Состав членов IVector3D берётся из библиотеки типов (RP.1), а не угадывается:
                        // здесь читаются те, что объявлены, и в отчёт попадает и сам перечень.
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

        // Параметры ОРИЕНТАЦИИ: интерфейс выбирается по OrientationType.
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

                // AngleByOwnAxis документирован как «доступно только для записи» — он НЕ читается
                // по построению, и это записывается, а не выдаётся за отказ маршрута.
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

        // ILocalCSObject — подчинённый объект ЛСК, документирован как получаемый через QueryInterface.
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

    /// <summary>
    /// Найти записанную тройку в значениях чтения. Сравниваются ЧИСЛА, а не подстроки: «17»
    /// содержит «7», и поиск по подстроке объявил бы находкой мусор.
    /// </summary>
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

    /// <summary>
    /// Число, прочитанное у живого объекта поздним связыванием. Только для объектов, у которых
    /// обёртка не объявляет нужного члена: <c>IVector3D</c> приходит из <c>IPoint3DParamDisplace</c>
    /// и его состав читается из библиотеки типов (шаг RP.1).
    /// </summary>
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
            // Не предмет этого шага.
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

    /// <summary>Создание признака изменения положения заданной матрицей 4×4.</summary>
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

    /// <summary>
    /// Правка СУЩЕСТВУЮЩЕГО признака на месте: та же матрица переписывается в тот же элемент, новый
    /// признак не создаётся. Иначе «правка» накопила бы второе смещение.
    /// </summary>
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

    /// <summary>Собирает уже созданный признак: тело + матрица + Update(). Отдельно от
    /// <see cref="CreateReposition"/>, потому что шагу RP.9 нужен САМ объект признака до сборки.</summary>
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

    /// <summary>Указатель IUnknown — единственный способ отличить «тот же объект» от «другой объект
    /// того же класса»: у обоих <c>Api5.RuntimeName</c> одинаков.</summary>
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

    /// <summary>Какие интерфейсы подтверждает живой объект. Приведение в C# и есть QueryInterface,
    /// поэтому «не подтверждает» — это ответ объекта, а не вывод прибора.</summary>
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
            // Возвращается прочитанное.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is { Length: 3 } min && row.Max is { Length: 3 } max
        && Math.Abs(min[0] - x0) < 0.01 && Math.Abs(min[1] - y0) < 0.01 && Math.Abs(min[2] - z0) < 0.01
        && Math.Abs(max[0] - x1) < 0.01 && Math.Abs(max[1] - y1) < 0.01 && Math.Abs(max[2] - z1) < 0.01;

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    /// <summary>Матрица 4×4 переноса в раскладке, измеренной пробой <c>--reposition</c> (шаг RP.2).</summary>
    private static double[] Translate(double[] vector) => new[]
    {
        1d, 0d, 0d, 0d,
        0d, 1d, 0d, 0d,
        0d, 0d, 1d, 0d,
        vector[0], vector[1], vector[2], 1d,
    };

    /// <summary>
    /// Матрица поворота на угол вокруг оси через точку. Формула Родрига; перенос <c>c − R·c</c>,
    /// поэтому точка оси остаётся на месте. 3×3 кладётся ПОСТОЛБЦОВО — та же раскладка, что и в
    /// <c>RepositionMatrix</c> (измерена шагом RP.2/RP.4).
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

    /// <summary>Габарит образа параллелепипеда — контроль, не зависящий от КОМПАСа.</summary>
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
