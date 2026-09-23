using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба B3 — разделение тела плоскостью (SM-16) и отсечение по одну сторону (SM-16.cut_by_plane).
/// </summary>
/// <remarks>
/// <para>
/// <b>Что именно неизвестно.</b> Каталог знает <c>ISplitSolid</c> по библиотеке типов и держит
/// открытый вопрос OQ-A18: у <c>ISplitSolid</c> объявлен только <c>CutObjects</c>, а члена «набор
/// сохраняемых частей» нет, тогда как в интерфейсе КОМПАС части выбираются диалогом «Изменение
/// набора тел». Здесь измеряется, что реально происходит с частями по умолчанию, и что делает
/// <c>ICut.Direction</c>.
/// </para>
/// <para>
/// <b>Плоскость задаётся ТРЕМЯ ТОЧКАМИ модели</b> (<c>o3d_plane3Points</c>): это общий маршрут,
/// который выражает и «точку с нормалью», и наклонную постановку, и не зависит от того, какая
/// базовая плоскость выбрана. Порядок точек задаёт нормаль, поэтому инверсия нормали — это
/// перестановка двух точек, и она измеряется отдельно.
/// </para>
/// <para>
/// <b>Эталон §6.5.</b> Брусок <c>[0,40]×[0,30]×[0,20]</c>, V=24000, плоскость <c>x=10</c>:
/// части 6000 при <c>x∈[0,10]</c> и 18000 при <c>x∈[10,40]</c>. Ожидания печатаются до опыта.
/// </para>
/// </remarks>
internal sealed class SplitProbe
{
    private const double Ax0 = 0d, Ax1 = 40d, Ay0 = 0d, Ay1 = 30d, Az = 20d;
    private const double StrangerX0 = 100d, StrangerX1 = 110d, StrangerY0 = 0d, StrangerY1 = 10d, StrangerZ = 10d;

    private static double VolumeA => (Ax1 - Ax0) * (Ay1 - Ay0) * Az;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public SplitProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "split-plane.json"),
            Path.Combine(options.ReportDir, "split-plane.md"));

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
            AuxiliaryPlane();
            SplitKeepsAllParts();
            PlaneOutsideBody();
            TangentPlane();
            InvertedNormal();
            EditPlaneAndRebuild();
            CutOneSide();
            EditExistingFeatureSupport();
            ReopenReadBack();
            ReadSupportBack();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("SP.0", "Свой невидимый сеанс КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        var clock = Stopwatch.StartNew();
        _app = (KompasObject)Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!)!;
        _app.Visible = false;
        _ownPid = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .FirstOrDefault(pid => !_pidsBefore.Contains(pid));
        clock.Stop();

        step.Observe("экземпляр API5 создан за " + clock.ElapsedMilliseconds + " мс, свой процесс: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    // ══════════════════════════════════════════════════════════════ routes ══

    /// <summary>
    /// Вспомогательная плоскость через <c>Planes3D.Add(o3d_plane3Points)</c> и три точки модели.
    /// Проверяется не «создалась ли», а выражает ли этот маршрут произвольную плоскость: точки
    /// задаются в координатах МОДЕЛИ, а не в локальной системе плоскости.
    /// </summary>
    private void AuxiliaryPlane()
    {
        var step = _report.Begin("SP.1", "Вспомогательная плоскость по трём точкам модели",
            "Выражает ли Planes3D.Add(o3d_plane3Points) плоскость x=10 с нормалью (1,0,0)?");
        var doc = NewPart(out _);
        try
        {
            var container = Container(doc);
            if (container is null)
            {
                step.Fail("контейнер API7 недоступен");
                return;
            }

            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (planes is null)
            {
                step.Fail("IModelContainer не отвечает IAuxiliaryGeomContainer.Planes3D");
                return;
            }

            step.Observe("Planes3D.Count до создания: " + Api5.Raw(SafeInt(() => planes.Count)));
            var plane = MakePlane(container, planes, 10d, 0d, 0d, "x=10, n=(1,0,0)", step);
            if (plane is null)
            {
                step.Fail("плоскость не создана");
                return;
            }

            step.Observe("плоскость создана: " + Api5.RuntimeName(plane));
            step.Observe("  ModelObjectType=" + Api5.Raw(SafeEnum(() => plane.ModelObjectType)));
            step.Observe("  Name=" + Api5.Raw(SafeString(() => plane.Name)));
            step.Observe("  Surface=" + Api5.RuntimeName(SafeObjectOf(() => plane.Surface)));
            step.Data["planes_count_after"] = SafeInt(() => planes.Count);
            step.Pass("плоскость по трём точкам модели создаётся маршрутом API7");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ cases ══

    private void SplitKeepsAllParts()
    {
        var step = _report.Begin("SP.2", "Разделение тела плоскостью: сохраняются ли ВСЕ части",
            "Даёт ли ISplitSolid две части 6000 и 18000, то есть сумму, равную исходному объёму?");
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): брусок V=" + Api5.Num(VolumeA)
            + ", плоскость x=10 → часть x∈[0,10] V=6000 и часть x∈[10,40] V=18000, сумма 24000");
        step.Observe("в документе живёт посторонний куб C — он обязан остаться нетронутым");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: true))
            {
                step.Fail("брусок не построен");
                return;
            }

            var before = BodyRows(part);
            step.Observe("тел до операции: " + before.Count + " → " + Describe(before));

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlane(container, planes, 10d, 0d, 0d, "split-x10", step);
            if (plane is null)
            {
                step.Fail("плоскость не создана");
                return;
            }

            var (ok, note) = CreateSplit(doc, part, plane, step);
            if (!ok)
            {
                step.Fail("разделение: " + note);
                return;
            }

            var after = BodyRows(part);
            step.Observe("тел после разделения: " + after.Count + " → " + Describe(after));
            step.Data["bodies_after"] = after.Count;
            step.Data["total_volume"] = SumVolumes(after);

            var left = after.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 10d, 30d, 20d));
            var right = after.FirstOrDefault(r => Near(r, 10d, 0d, 0d, 40d, 30d, 20d));
            var stranger = after.FirstOrDefault(r => Near(r, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ));
            step.Data["left_part"] = left?.Describe();
            step.Data["right_part"] = right?.Describe();
            if (left is null || right is null)
            {
                step.Fail("не найдены обе части: x∈[0,10]=" + (left is null ? "нет" : "есть")
                    + ", x∈[10,40]=" + (right is null ? "нет" : "есть"));
                return;
            }

            if (stranger is null || Math.Abs((stranger.Volume ?? 0d) - 1000d) > 0.01d)
            {
                step.Fail("постороннее тело изменилось или исчезло: " + (stranger?.Describe() ?? "нет"));
                return;
            }

            var sum = (left.Volume ?? 0d) + (right.Volume ?? 0d);
            step.Data["parts_sum"] = sum;
            if (Math.Abs((left.Volume ?? 0d) - 6000d) > 0.01d || Math.Abs((right.Volume ?? 0d) - 18000d) > 0.01d
                || Math.Abs(sum - VolumeA) > 0.01d)
            {
                step.Fail("объёмы частей " + Api5.Num(left.Volume) + " и " + Api5.Num(right.Volume)
                    + " (сумма " + Api5.Num(sum) + ") против ожидаемых 6000, 18000 и 24000");
                return;
            }

            step.Observe("постороннее тело не тронуто: " + stranger.Describe());
            step.Pass("обе части сохранены, сумма равна исходному объёму, постороннее тело цело");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Плоскость вне тела: резать нечего. Вопрос — отказ это или пустой результат.</summary>
    private void PlaneOutsideBody()
    {
        var step = _report.Begin("SP.3", "Плоскость вне тела",
            "Что делает разделение, если плоскость не пересекает тело?");
        PlaneCase(step, 100d, "x=100 вне бруска", expectBodies: null, expectTotal: 24000d);
    }

    /// <summary>Касательная плоскость: проходит ровно по грани x=0. Вырожденный случай — тело
    /// делится на «всё» и «ничего», и правильный ответ здесь не очевиден заранее.</summary>
    private void TangentPlane()
    {
        var step = _report.Begin("SP.4", "Касательная плоскость x=0",
            "Что делает разделение, если плоскость проходит точно по грани тела?");
        PlaneCase(step, 0d, "x=0 по грани", expectBodies: null, expectTotal: 24000d);
    }

    /// <summary>Инверсия нормали: те же три точки, но две переставлены. Если результат не зависит
    /// от порядка точек, то «сторона» у разделения не выражается плоскостью вовсе.</summary>
    private void InvertedNormal()
    {
        var step = _report.Begin("SP.5", "Инверсия нормали: перестановка двух точек плоскости",
            "Меняет ли порядок точек результат разделения?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlaneInverted(container, planes, 10d, "inverted-x10", step);
            if (plane is null)
            {
                step.Fail("плоскость с переставленными точками не создана");
                return;
            }

            var (ok, note) = CreateSplit(doc, part, plane, step);
            if (!ok)
            {
                step.Observe("разделение с переставленными точками: ОТКАЗ — " + note);
                step.Data["inverted_split"] = "отказ";
                step.Pass("измерено: с переставленными точками разделение отвергнуто ядром");
                return;
            }

            var after = BodyRows(part);
            step.Observe("тел после разделения: " + after.Count + " → " + Describe(after));
            step.Data["inverted_split"] = after.Count + " тел, сумма " + Api5.Num(SumVolumes(after));
            step.Pass("порядок точек не мешает построению; геометрия частей записана выше");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    private void PlaneCase(ProbeStep step, double planeX, string label, int? expectBodies, double expectTotal)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlane(container, planes, planeX, 0d, 0d, label, step);
            if (plane is null)
            {
                step.Fail("плоскость не создана");
                return;
            }

            var (ok, note) = CreateSplit(doc, part, plane, step);
            var after = BodyRows(part);
            step.Observe(label + ": после вызова тел " + after.Count + " → " + Describe(after)
                + "; суммарный объём " + Api5.Num(SumVolumes(after)));
            step.Data["case_" + label] = (ok ? "принято" : "ОТКАЗ (" + note + ")")
                + ", тел " + after.Count + ", сумма " + Api5.Num(SumVolumes(after));

            var total = SumVolumes(after);
            if (total is null || Math.Abs(total.Value - expectTotal) > 0.01d)
            {
                step.Fail(label + ": суммарный объём " + Api5.Num(total) + " против ожидаемых "
                    + Api5.Num(expectTotal) + " — материал потерян или удвоен");
                return;
            }

            if (expectBodies is int expected && after.Count != expected)
            {
                step.Fail(label + ": тел " + after.Count + " против ожидаемых " + expected);
                return;
            }

            step.Observe(label + ": материал сохранён (сумма " + Api5.Num(total) + ")");
            step.Pass("случай «" + label + "» измерен: сумма объёмов равна исходной");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// §6.5: «изменить ту же плоскость на x=15 → части 9000 и 15000». Правка идёт по ТОЙ ЖЕ
    /// плоскости: переставляются координаты точек, затем перестроение. Это проверяет, что признак
    /// разделения читает опору заново, а не помнит первый расчёт.
    /// </summary>
    private void EditPlaneAndRebuild()
    {
        var step = _report.Begin("SP.6", "Правка той же плоскости: x=10 → x=15",
            "Даёт ли изменение опоры новые части 9000 и 15000 при неизменном признаке?");
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var points = new List<IPoint3D>();
            var plane = MakePlane(container, planes, 10d, 0d, 0d, "edit-x10", step, points);
            if (plane is null || points.Count != 3)
            {
                step.Fail("плоскость не создана");
                return;
            }

            var (ok, note) = CreateSplit(doc, part, plane, step);
            if (!ok)
            {
                step.Fail("первое разделение: " + note);
                return;
            }

            var first = BodyRows(part);
            step.Observe("после x=10: " + Describe(first) + " сумма " + Api5.Num(SumVolumes(first)));
            step.Data["at_x10"] = Describe(first);
            var splitsBefore = SplitCount(doc);

            // Двигаем ТУ ЖЕ плоскость: все три точки сдвигаются на 5 мм по X.
            foreach (var point in points)
            {
                point.X += 5d;
                point.Update();
            }

            if (plane is IModelObject planeObject)
            {
                planeObject.Update();
            }

            part.RebuildModel();
            doc.RebuildDocument();

            var after = BodyRows(part);
            step.Observe("после x=15: " + Describe(after) + " сумма " + Api5.Num(SumVolumes(after)));
            step.Observe("признаков разделения: " + Api5.Raw(splitsBefore) + " → " + Api5.Raw(SplitCount(doc)));
            step.Data["at_x15"] = Describe(after);
            step.Data["split_count_before"] = splitsBefore;
            step.Data["split_count_after"] = SplitCount(doc);

            var left = after.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 15d, 30d, 20d));
            var right = after.FirstOrDefault(r => Near(r, 15d, 0d, 0d, 40d, 30d, 20d));
            if (left is null || right is null)
            {
                step.Fail("после сдвига плоскости не найдены части 9000 и 15000: "
                    + (left is null ? "левая нет; " : string.Empty) + (right is null ? "правая нет" : string.Empty));
                return;
            }

            if (Math.Abs((left.Volume ?? 0d) - 9000d) > 0.01d || Math.Abs((right.Volume ?? 0d) - 15000d) > 0.01d)
            {
                step.Fail("объёмы " + Api5.Num(left.Volume) + " и " + Api5.Num(right.Volume)
                    + " против ожидаемых 9000 и 15000");
                return;
            }

            step.Pass("правка опоры существующего признака даёт новые части 9000 и 15000 "
                + "без создания второго признака");
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// Отсечение по одну сторону: <c>ICuts.Add()</c>, <c>BuildingType=ksCutByPlane</c>,
    /// <c>CutObject</c> — та же плоскость, <c>Direction</c> — сторона. Ожидание §6.5: при одном
    /// значении остаётся V=6000 (x∈[0,10]), при другом V=18000 (x∈[10,40]).
    /// </summary>
    private void CutOneSide()
    {
        var step = _report.Begin("SP.7", "Отсечение по одну сторону: что выбирает Direction",
            "Оставляет ли ICut.Direction одну из сторон, и какая сторона соответствует какому значению?");

        foreach (var direction in new[] { true, false })
        {
            var doc = NewPart(out var part);
            try
            {
                if (!BuildBar(part, doc, step, withStranger: true))
                {
                    step.Observe("direction=" + direction + ": брусок не построен");
                    continue;
                }

                var container = Container(doc);
                var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
                if (container is null || planes is null)
                {
                    step.Observe("direction=" + direction + ": контейнер или Planes3D недоступны");
                    continue;
                }

                var plane = MakePlane(container, planes, 10d, 0d, 0d, "cut-x10", step);
                if (plane is null)
                {
                    step.Observe("direction=" + direction + ": плоскость не создана");
                    continue;
                }

                var (ok, note) = CreateCut(doc, part, plane, direction, step);
                var after = BodyRows(part);
                step.Observe("direction=" + direction + ": " + (ok ? "принято" : "ОТКАЗ (" + note + ")")
                    + "; тел " + after.Count + " → " + Describe(after));
                step.Data["direction_" + direction] = (ok ? "принято" : "отказ")
                    + ", тел " + after.Count + ", сумма " + Api5.Num(SumVolumes(after));
            }
            catch (Exception ex)
            {
                step.Observe("direction=" + direction + ": исключение " + HResult.Describe(ex));
            }
            finally
            {
                TryClose(doc);
            }
        }

        step.Pass("оба значения Direction измерены раздельно; какая сторона остаётся — в наблюдениях");
    }

    /// <summary>
    /// Правка опоры СУЩЕСТВУЮЩЕГО признака: какой маршрут действительно меняет результат.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Вопрос поставлен расхождением, а не удобством. Шаг SP.6 менял опору, ПЕРЕДВИГАЯ точки
    /// построения САМОЙ плоскости, и получил части 9000 и 15000; шаг SP.7 строил отсечение заново в
    /// СВЕЖЕМ документе на каждое значение <c>Direction</c>. Ни один из этих шагов не проверял
    /// маршрут «записать в существующий признак ДРУГУЮ опору», а именно он был перенесён в адаптер
    /// как «правка опоры». Измерение через MCP показало, что он НЕ работает: <c>Update()</c>
    /// возвращает <c>true</c>, а части остаются прежними (6000 и 18000). Поэтому маршруты измеряются
    /// здесь раздельно, вместе с отрицательными контролями.
    /// </para>
    /// <para>
    /// ОЖИДАНИЯ ОБЪЯВЛЕНЫ ДО ОПЫТА (брусок A = [0,40]×[0,30]×[0,20], V = 24000; плоскость x = 10):
    /// <list type="bullet">
    /// <item>E-A: чтение опоры обратно с существующего признака разделения даёт НЕПУСТОЙ набор, и
    /// элемент отвечает <c>IPlane3DBy3Points</c>; перенос его трёх точек на +5 по X с <c>Update()</c>
    /// каждой и перестроением даёт части 9000 и 15000 при неизменном числе признаков;</item>
    /// <item>E-B (ОТРИЦАТЕЛЬНЫЙ КОНТРОЛЬ к E-A): запись ВТОРОЙ, только что созданной плоскости
    /// (x = 20) в <c>CutObjects</c> того же существующего признака с <c>Update()</c> и перестроением
    /// результат НЕ меняет — части остаются 9000 и 15000. Если контроль пройдёт «наоборот», то есть
    /// части сдвинутся на x = 20, значит маршрут записи опоры рабочий и адаптер надо вернуть к
    /// нему, а не переписывать на перенос точек;</item>
    /// <item>E-C: чтение <c>ICut.CutObject</c> обратно и перенос его точек на +5 по X (x = 10 → 15)
    /// меняет остаток с 6000 на 9000;</item>
    /// <item>E-D (ОТРИЦАТЕЛЬНЫЙ КОНТРОЛЬ к E-C): смена ТОЛЬКО <c>ICut.Direction</c> на том же
    /// существующем признаке меняет остаток с 9000 на 15000, если маршрут стороны через запись в
    /// существующий признак рабочий; если остаток остаётся 9000 — этот маршрут не работает и
    /// сторону можно менять только вместе с опорой.</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void EditExistingFeatureSupport()
    {
        var step = _report.Begin("SP.9", "Правка опоры существующего признака: маршрут",
            "Меняет ли результат запись другой опоры в существующий признак — и меняет ли её перенос "
            + "точек самой опоры?");

        // ── разделение: E-A (перенос точек опоры) и E-B (запись второй плоскости) ─────────────
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlane(container, planes, 10d, 0d, 0d, "edit-support-x10", step);
            if (plane is null)
            {
                step.Fail("плоскость не создана");
                return;
            }

            var (created, note) = CreateSplit(doc, part, plane, step);
            if (!created)
            {
                step.Fail("разделение не создано: " + note);
                return;
            }

            var atX10 = BodyRows(part);
            step.Observe("E-A: после x=10 " + Describe(atX10) + " сумма " + Api5.Num(SumVolumes(atX10)));
            step.Data["E-A_at_x10"] = Describe(atX10);

            var splitsBefore = SplitCount(doc);
            if (container.SplitSolids is not { } splitCollection)
            {
                step.Fail("SplitSolids недоступен");
                return;
            }

            object? support = null;
            try
            {
                support = splitCollection[0] is ISplitSolid feature ? feature.CutObjects : null;
            }
            catch (Exception ex)
            {
                step.Observe("E-A: чтение CutObjects обратно — исключение " + HResult.Describe(ex));
            }

            var first = FirstOf(support);
            step.Observe("E-A: чтение опоры обратно дало " + Api5.Raw(CountOf(support))
                + " элемент(ов), первый — " + (first is null ? "<нет>" : first.GetType().Name));
            step.Data["E-A_support_read_back"] = first is null ? null : first.GetType().Name;

            if (first is not IPlane3D readBack || readBack is not IPlane3DBy3Points byPoints)
            {
                step.Observe("E-A: опора не отвечает IPlane3DBy3Points — маршрут переноса точек недоступен");
            }
            else
            {
                var moved = MovePointsByX(byPoints, 5d);
                readBack.Update();
                part.RebuildModel();
                doc.RebuildDocument();
                var atX15 = BodyRows(part);
                step.Observe("E-A: перенесено точек опоры " + moved + " из 3 на +5 по X → " + Describe(atX15)
                    + " сумма " + Api5.Num(SumVolumes(atX15)));
                step.Data["E-A_at_x15"] = Describe(atX15);
                var leftA = atX15.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 15d, 30d, 20d));
                var rightA = atX15.FirstOrDefault(r => Near(r, 15d, 0d, 0d, 40d, 30d, 20d));
                var okA = moved == 3 && leftA is not null && rightA is not null
                    && Math.Abs((leftA.Volume ?? 0d) - 9000d) <= 0.01d
                    && Math.Abs((rightA.Volume ?? 0d) - 15000d) <= 0.01d
                    && SplitCount(doc) == splitsBefore;
                if (okA)
                {
                    step.Observe("E-A: подтверждено — перенос точек опоры меняет результат, "
                        + "признаков разделения " + Api5.Raw(splitsBefore) + " → " + Api5.Raw(SplitCount(doc)));
                }
                else
                {
                    step.Observe("E-A: ОПРОВЕРГНУТО — части " + Describe(atX15)
                        + " вместо 9000 и 15000 при признаках " + Api5.Raw(splitsBefore)
                        + " → " + Api5.Raw(SplitCount(doc)));
                }

                // E-B: отрицательный контроль — запись ВТОРОЙ плоскости в тот же признак.
                var second = MakePlane(container, planes, 20d, 0d, 0d, "edit-support-x20", step);
                if (second is not IModelObject secondObject || splitCollection[0] is not ISplitSolid sameSplit)
                {
                    step.Observe("E-B: вторая плоскость или признак недоступны — контроль не выполнен");
                }
                else
                {
                    sameSplit.CutObjects = new object[] { secondObject };
                    var updatedB = sameSplit.Update();
                    part.RebuildModel();
                    doc.RebuildDocument();
                    var atX20 = BodyRows(part);
                    step.Observe("E-B: запись ВТОРОЙ плоскости x=20, Update()=" + updatedB + " → "
                        + Describe(atX20) + " сумма " + Api5.Num(SumVolumes(atX20)));
                    step.Data["E-B_after_second_plane"] = Describe(atX20);
                    var stillX15 = atX20.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 15d, 30d, 20d)) is not null
                        && atX20.FirstOrDefault(r => Near(r, 15d, 0d, 0d, 40d, 30d, 20d)) is not null;
                    var movedToX20 = atX20.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 20d, 30d, 20d)) is not null;
                    step.Observe(stillX15
                        ? "E-B: контроль пройден — запись второй плоскости результат НЕ меняет"
                        : movedToX20
                            ? "E-B: контроль НЕ пройден — запись второй плоскости РАБОТАЕТ (части на x=20)"
                            : "E-B: исход не распознан — части " + Describe(atX20));
                    step.Data["E-B_route_works"] = !stillX15;
                }
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
            return;
        }
        finally
        {
            TryClose(doc);
        }

        // ── отсечение: E-C (перенос точек опоры) и E-D (смена стороны) ────────────────────────
        var cutDoc = NewPart(out var cutPart);
        try
        {
            if (!BuildBar(cutPart, cutDoc, step, withStranger: false))
            {
                step.Observe("E-C: брусок не построен");
            }
            else
            {
                var container = Container(cutDoc);
                var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
                if (container is null || planes is null || container.Cuts is not { } cuts)
                {
                    step.Observe("E-C: контейнер, Planes3D или Cuts недоступны");
                }
                else
                {
                    var plane = MakePlane(container, planes, 10d, 0d, 0d, "edit-cut-x10", step);
                    if (plane is null)
                    {
                        step.Observe("E-C: плоскость не создана");
                    }
                    else if (!CreateCut(cutDoc, cutPart, plane, false, step).Ok)
                    {
                        step.Observe("E-C: отсечение не создано");
                    }
                    else
                    {
                        var atX10 = BodyRows(cutPart);
                        step.Observe("E-C: после x=10, s<0 " + Describe(atX10));
                        step.Data["E-C_at_x10"] = Describe(atX10);

                        object? support = null;
                        try
                        {
                            support = cuts[0] is ICut cut ? cut.CutObject : null;
                        }
                        catch (Exception ex)
                        {
                            step.Observe("E-C: чтение CutObject обратно — исключение " + HResult.Describe(ex));
                        }

                        var first = FirstOf(support);
                        step.Observe("E-C: чтение опоры обратно дало " + (first is null
                            ? "<нет>" : first.GetType().Name));
                        step.Data["E-C_support_read_back"] = first is null ? null : first.GetType().Name;

                        if (first is IPlane3D cutReadBack && cutReadBack is IPlane3DBy3Points cutPoints)
                        {
                            var movedC = MovePointsByX(cutPoints, 5d);
                            cutReadBack.Update();
                            cutPart.RebuildModel();
                            cutDoc.RebuildDocument();
                            var atX15 = BodyRows(cutPart);
                            step.Observe("E-C: перенесено точек опоры " + movedC + " из 3 на +5 по X → "
                                + Describe(atX15));
                            step.Data["E-C_at_x15"] = Describe(atX15);
                            var okC = movedC == 3 && atX15.Count == 1
                                && Math.Abs((atX15[0].Volume ?? 0d) - 9000d) <= 0.01d;
                            step.Observe(okC
                                ? "E-C: подтверждено — остаток 9000"
                                : "E-C: ОПРОВЕРГНУТО — " + Describe(atX15));

                            // E-D: только сторона.
                            if (cuts[0] is ICut sameCut)
                            {
                                sameCut.Direction = true;
                                var updatedD = sameCut.Update();
                                cutPart.RebuildModel();
                                cutDoc.RebuildDocument();
                                var atFlipped = BodyRows(cutPart);
                                step.Observe("E-D: Direction=true на том же признаке, Update()=" + updatedD
                                    + " → " + Describe(atFlipped));
                                step.Data["E-D_after_direction_flip"] = Describe(atFlipped);
                                var flipped = atFlipped.Count == 1
                                    && Math.Abs((atFlipped[0].Volume ?? 0d) - 15000d) <= 0.01d;
                                step.Observe(flipped
                                    ? "E-D: смена стороны на существующем признаке РАБОТАЕТ (остаток 15000)"
                                    : "E-D: смена стороны на существующем признаке не работает (остаток "
                                      + Api5.Num(atFlipped.Count == 1 ? atFlipped[0].Volume : null) + ")");
                                step.Data["E-D_direction_flip_works"] = flipped;
                            }
                        }
                        else
                        {
                            step.Observe("E-C: опора не отвечает IPlane3DBy3Points — перенос точек недоступен");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe("E-C/E-D: исключение " + HResult.Describe(ex));
        }
        finally
        {
            TryClose(cutDoc);
        }

        step.Pass("маршруты правки опоры измерены раздельно; исходы — в наблюдениях и Data");
    }

    /// <summary>
    /// Перенос трёх точек построения плоскости на <paramref name="dx"/> по X. Возвращается число
    /// ПЕРЕДВИНУТЫХ точек: <c>IPlane3DBy3Points.Point1..3</c> объявлены как <c>IModelObject</c>, и
    /// если какая-то из них не отвечает <c>IPoint3D</c>, переносить нечего — это наблюдение, а не
    /// повод упасть.
    /// </summary>
    private static int MovePointsByX(IPlane3DBy3Points plane, double dx)
    {
        var moved = 0;
        foreach (var candidate in new[] { plane.Point1, plane.Point2, plane.Point3 })
        {
            if (candidate is not IPoint3D point)
            {
                continue;
            }

            point.X += dx;
            point.Update();
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Первый элемент прочитанного набора <c>CutObjects</c>: интероп отдаёт его то массивом, то
    /// одним объектом, и предполагать одно из двух значило бы гадать о типе.
    /// </summary>
    private static object? FirstOf(object? readBack) => readBack switch
    {
        Array array when array.Length > 0 => array.GetValue(0),
        Array => null,
        null => null,
        _ => readBack,
    };

    private static int? CountOf(object? readBack) => readBack switch
    {
        Array array => array.Length,
        null => null,
        _ => 1,
    };

    /// <summary>
    /// Читаются ли ОПОРА разделения и сторона отсечения ОБРАТНО — то, чего требует действие
    /// <c>read</c> наряда B3 §7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем отдельный шаг.</b> Шаг SP.9 измерил, что опора читается с живого признака
    /// (<c>CutObjects</c> отдаёт объект, отвечающий <c>IPlane3DBy3Points</c>) — но только как ФАКТ
    /// существования объекта: числа не сверялись ни с чем. Из «объект вернулся» не следует «вернулись
    /// ТЕ числа», а публиковать чтение опоры в продукте можно только по второму утверждению.
    /// Шаг SP.7 измерил, что <c>Direction</c> МЕНЯЕТ результат, но обратного чтения там тоже не было.
    /// </para>
    /// <para>
    /// <b>Ожидание объявлено до опыта и точное.</b> Вспомогательная плоскость строится тремя точками
    /// модели <c>(x,0,0)</c>, <c>(x,1,0)</c>, <c>(x,0,1)</c> (см. <c>MakePlane</c>), и порядок задаёт
    /// нормаль <c>(p2−p1)×(p3−p1) = (1,0,0)</c>. Значит чтение обязано вернуть ровно эти три точки и
    /// эту нормаль — не «примерно», а поимённо по <c>Point1..3</c>.
    /// </para>
    /// <para>
    /// <b>Контроль различающий, и он встроен в пару случаев.</b> Опоры при <c>x = 10</c> и
    /// <c>x = 15</c> отличаются ровно одним числом, поэтому пара A/B различает «читается опора» и
    /// «читается постоянное»: чтение, отдающее одинаковые точки на РАЗНЫХ опорах, читает не опору.
    /// Пара C/D различает сторону при ОДНОЙ И ТОЙ ЖЕ опоре: если <c>Direction</c> не читается,
    /// оба случая дадут одно и то же, и это будет видно.
    /// </para>
    /// </remarks>
    private void ReadSupportBack()
    {
        var step = _report.Begin("SP.10", "Читаются ли опора разделения и сторона отсечения ОБРАТНО",
            "Отдают ли CutObjects/CutObject точку и нормаль опоры, а ICut.Direction — сторону, и различает ли чтение разные опоры?");

        step.Observe("A. разделение при x = 10: ожидание точек построения (10, 0, 0), (10, 1, 0), (10, 0, 1), нормаль (1, 0, 0)");
        step.Observe("B. контроль: разделение при x = 15 — те же чтения обязаны дать (15, 0, 0), (15, 1, 0), (15, 0, 1)");
        step.Observe("C и D. отсечение при x = 10 с Direction = true и false: опора одна и та же, "
            + "различаться обязана СТОРОНА");

        var a = SupportCase(step, "A", 10d, null);
        var b = SupportCase(step, "B", 15d, null);
        var c = SupportCase(step, "C", 10d, true);
        var d = SupportCase(step, "D", 10d, false);

        if (a is null || b is null || c is null || d is null)
        {
            step.Fail("случай не построен — это отмечено выше, а не выдано за чистый опыт");
            return;
        }

        var aOk = MatchesPlane(a, 10d);
        var bOk = MatchesPlane(b, 15d);
        var cOk = MatchesPlane(c, 10d) && c.Direction is true;
        var dOk = MatchesPlane(d, 10d) && d.Direction is false;
        var readable = a.P1 is not null && b.P1 is not null;
        var distinguishable = readable && !SamePoints(a, b);

        step.Data["A_x10_read"] = aOk;
        step.Data["B_x15_read"] = bOk;
        step.Data["C_direction_true"] = c.Direction?.ToString() ?? "<не прочитано>";
        step.Data["D_direction_false"] = d.Direction?.ToString() ?? "<не прочитано>";
        step.Data["A_and_B_differ"] = distinguishable;

        if (!readable)
        {
            step.Fail("опора не читается вовсе: A (x = 10) → " + DescribeSupport(a)
                + ", B (x = 15) → " + DescribeSupport(b));
            return;
        }

        if (!distinguishable)
        {
            step.Fail("контроль не сработал: опоры x = 10 и x = 15 читаются ОДИНАКОВО — такое чтение "
                + "отдаёт постоянное, а не опору признака");
            return;
        }

        if (!aOk || !bOk)
        {
            step.Fail("опора читается, но не поимённо: A (x = 10) → " + DescribeSupport(a)
                + ", B (x = 15) → " + DescribeSupport(b));
            return;
        }

        if (!cOk || !dOk)
        {
            step.Fail("сторона отсечения не читается: при Direction = true прочитано "
                + Api5.Raw(c.Direction) + ", при false — " + Api5.Raw(d.Direction)
                + "; опора при этом " + (MatchesPlane(c, 10d) && MatchesPlane(d, 10d)
                    ? "читается верно в обоих случаях"
                    : "тоже не совпала с объявленной"));
            return;
        }

        step.Pass("опора читается поимённо (три точки построения и нормаль), сторона отсечения "
            + "различается, и разные опоры читаются по-разному");
    }

    /// <summary>
    /// Один случай шага SP.10: создать признак (разделение либо отсечение) и прочитать его опору
    /// ОБРАТНО. <c>cutDirection = null</c> — разделение, иначе отсечение с этой стороной.
    /// <c>null</c> в ответе — случай не построен, и вызывающий обязан сказать это вслух.
    /// </summary>
    private SupportRead? SupportCase(ProbeStep step, string label, double planeX, bool? cutDirection)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildBar(part, doc, step, withStranger: false))
            {
                step.Observe(label + ": брусок не построен");
                return null;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Observe(label + ": контейнер или Planes3D недоступны");
                return null;
            }

            var plane = MakePlane(container, planes, planeX, 0d, 0d, "read-support-x" + planeX, step);
            if (plane is null)
            {
                step.Observe(label + ": плоскость не создана");
                return null;
            }

            object? readBack;
            bool? direction = null;
            if (cutDirection is { } wanted)
            {
                var (ok, note) = CreateCut(doc, part, plane, wanted, step);
                if (!ok)
                {
                    step.Observe(label + ": отсечение не создано — " + note);
                    return null;
                }

                if (container.Cuts is not { } cuts || SafeInt(() => cuts.Count) is not { } cutCount
                    || cutCount == 0 || cuts[cutCount - 1] is not ICut cut)
                {
                    step.Observe(label + ": коллекция Cuts пуста или последний элемент не отдаёт ICut");
                    return null;
                }

                readBack = cut.CutObject;
                direction = SafeBoolOf(() => cut.Direction);
            }
            else
            {
                var (ok, note) = CreateSplit(doc, part, plane, step);
                if (!ok)
                {
                    step.Observe(label + ": разделение не создано — " + note);
                    return null;
                }

                if (container.SplitSolids is not { } splits || SafeInt(() => splits.Count) is not { } splitCount
                    || splitCount == 0 || splits[splitCount - 1] is not ISplitSolid split)
                {
                    step.Observe(label + ": коллекция SplitSolids пуста или последний элемент не отдаёт ISplitSolid");
                    return null;
                }

                readBack = split.CutObjects;
            }

            var read = ReadSupportPoints(FirstOf(readBack)) with { Direction = direction };
            step.Observe(label + ": прочитано " + DescribeSupport(read)
                + (cutDirection is null ? string.Empty : ", Direction=" + Api5.Raw(direction)));
            return read;
        }
        catch (Exception ex)
        {
            step.Observe(label + ": исключение " + HResult.Describe(ex));
            return null;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Прочитанное описание опоры. Каждое поле — «null = не прочитано», не «ноль».</summary>
    private sealed record SupportRead(
        double[]? P1, double[]? P2, double[]? P3, double[]? Normal, bool? Direction, string? Note);

    /// <summary>
    /// Три точки построения опоры и нормаль из них. Нормаль считается по тому же правилу, что и при
    /// создании — <c>(p2−p1)×(p3−p1)</c>, — иначе сверять было бы нечего.
    /// </summary>
    private static SupportRead ReadSupportPoints(object? support)
    {
        if (support is not IPlane3DBy3Points byPoints)
        {
            return new SupportRead(null, null, null, null, null,
                "опора не отвечает IPlane3DBy3Points: " + Api5.RuntimeName(support));
        }

        var p1 = ReadPoint(byPoints.Point1);
        var p2 = ReadPoint(byPoints.Point2);
        var p3 = ReadPoint(byPoints.Point3);
        return new SupportRead(p1, p2, p3, NormalOf(p1, p2, p3), null, null);
    }

    private static double[]? ReadPoint(object? candidate) =>
        candidate is IPoint3D point ? new[] { point.X, point.Y, point.Z } : null;

    private static double[]? NormalOf(double[]? p1, double[]? p2, double[]? p3)
    {
        if (p1 is null || p2 is null || p3 is null)
        {
            return null;
        }

        var u = new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] };
        var v = new[] { p3[0] - p1[0], p3[1] - p1[1], p3[2] - p1[2] };
        var n = new[]
        {
            u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0],
        };
        var norm = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        return norm < 1e-12 ? n : new[] { n[0] / norm, n[1] / norm, n[2] / norm };
    }

    /// <summary>Совпадает ли прочитанная опора с объявленной: точки построения и нормаль.</summary>
    private static bool MatchesPlane(SupportRead read, double planeX) =>
        NearVec(read.P1, new[] { planeX, 0d, 0d })
        && NearVec(read.P2, new[] { planeX, 1d, 0d })
        && NearVec(read.P3, new[] { planeX, 0d, 1d })
        && NearVec(read.Normal, new[] { 1d, 0d, 0d });

    /// <summary>Отличаются ли две прочитанные опоры хотя бы одним числом — различающий контроль.</summary>
    private static bool SamePoints(SupportRead a, SupportRead b) =>
        NearVec(a.P1, b.P1) && NearVec(a.P2, b.P2) && NearVec(a.P3, b.P3);

    private static bool NearVec(double[]? actual, double[]? expected)
    {
        if (actual is not { Length: 3 } || expected is not { Length: 3 })
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (Math.Abs(actual[i] - expected[i]) > 1e-9)
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeSupport(SupportRead read) =>
        "точки " + Vec(read.P1) + ", " + Vec(read.P2) + ", " + Vec(read.P3)
        + ", нормаль " + Vec(read.Normal) + (read.Note is null ? string.Empty : " (" + read.Note + ")");

    private static string Vec(double[]? value) =>
        value is not { Length: 3 } ? "<не прочитано>" : "(" + Api5.Num(value[0]) + ", "
            + Api5.Num(value[1]) + ", " + Api5.Num(value[2]) + ")";

    private void ReopenReadBack()
    {
        var step = _report.Begin("SP.8", "Разделение переживает save → close → reopen",
            "Читаются ли признак разделения и состав частей с переоткрытого файла?");
        var doc = NewPart(out var part);
        var path = Path.Combine(_options.WorkDir, "split-reopen.m3d");
        try
        {
            if (!BuildBar(part, doc, step, withStranger: true))
            {
                step.Fail("брусок не построен");
                return;
            }

            var container = Container(doc);
            var planes = (container as IAuxiliaryGeomContainer)?.Planes3D;
            if (container is null || planes is null)
            {
                step.Fail("контейнер или Planes3D недоступны");
                return;
            }

            var plane = MakePlane(container, planes, 10d, 0d, 0d, "reopen-x10", step);
            if (plane is null)
            {
                step.Fail("плоскость не создана");
                return;
            }

            var (ok, note) = CreateSplit(doc, part, plane, step);
            if (!ok)
            {
                step.Fail("разделение: " + note);
                return;
            }

            var before = BodyRows(part);
            step.Observe("тел до сохранения: " + before.Count + " → " + Describe(before));
            doc.SaveAs(path);
            step.Observe("сохранено: " + path + " (" + new FileInfo(path).Length + " байт)");
            doc.close();

            var reopened = (ksDocument3D)_app.Document3D();
            if (!reopened.Open(path))
            {
                step.Fail("файл не открылся: " + path);
                return;
            }

            try
            {
                var reopenedPart = (ksPart)reopened.GetPart(-1);
                var after = BodyRows(reopenedPart);
                step.Observe("тел после переоткрытия: " + after.Count + " → " + Describe(after));
                step.Observe("признаков разделения после переоткрытия: " + Api5.Raw(SplitCount(reopened)));
                step.Data["bodies_after_reopen"] = after.Count;
                step.Data["volume_after_reopen"] = SumVolumes(after);
                step.Data["split_count_after_reopen"] = SplitCount(reopened);
                if (after.Count != before.Count)
                {
                    step.Fail("состав тел после переоткрытия разошёлся: " + before.Count + " → " + after.Count);
                    return;
                }

                step.Pass("части и признак разделения пережили save → close → reopen");
            }
            finally
            {
                TryClose(reopened);
            }
        }
        catch (Exception ex)
        {
            step.Fail(HResult.Describe(ex));
        }
        finally
        {
            TryClose(doc);
        }
    }

    // ══════════════════════════════════════════════════════════════ core ══

    private (bool Ok, string? Note) CreateSplit(
        ksDocument3D doc, ksPart part, IPlane3D plane, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.SplitSolids?.Add() is not { } created)
            {
                return (false, "SplitSolids.Add() не отдал объект");
            }

            if (created is not ISplitSolid split)
            {
                return (false, "SplitSolids.Add() не отдаёт ISplitSolid");
            }

            if (plane is not IModelObject planeObject)
            {
                return (false, "плоскость не отвечает IModelObject");
            }

            split.CutObjects = new object[] { planeObject };
            var updated = split.Update();
            step.Observe("вызов: ISplitSolid.CutObjects=[плоскость], Update()=" + updated);
            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "ISplitSolid.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    private (bool Ok, string? Note) CreateCut(
        ksDocument3D doc, ksPart part, IPlane3D plane, bool direction, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.Cuts?.Add() is not { } created)
            {
                return (false, "Cuts.Add() не отдал объект");
            }

            if (created is not ICut cut)
            {
                return (false, "Cuts.Add() не отдаёт ICut");
            }

            if (plane is not IModelObject planeObject)
            {
                return (false, "плоскость не отвечает IModelObject");
            }

            cut.BuildingType = ksCutBuildingTypeEnum.ksCutByPlane;
            cut.CutObject = planeObject;
            cut.Direction = direction;
            var updated = cut.Update();
            step.Observe("вызов: ICut.BuildingType=ksCutByPlane, Direction=" + direction
                + ", Update()=" + updated);
            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "ICut.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Плоскость <c>x = x0</c> с нормалью <c>(1,0,0)</c>: три точки модели
    /// <c>(x0,0,0)</c>, <c>(x0,1,0)</c>, <c>(x0,0,1)</c>. Порядок задаёт нормаль
    /// <c>(p2−p1)×(p3−p1) = (0,1,0)×(0,0,1) = (1,0,0)</c>.
    /// </summary>
    private IPlane3D? MakePlane(
        IModelContainer container, IPlanes3D planes, double x0, double y0, double z0,
        string name, ProbeStep step) =>
        MakePlane(container, planes, x0, y0, z0, name, step, null);

    private IPlane3D? MakePlane(
        IModelContainer container, IPlanes3D planes, double x0, double y0, double z0,
        string name, ProbeStep step, List<IPoint3D>? keep)
    {
        var first = MakePoint(container, x0, y0, z0);
        var second = MakePoint(container, x0, y0 + 1d, z0);
        var third = MakePoint(container, x0, y0, z0 + 1d);
        if (first is null || second is null || third is null)
        {
            step.Observe(name + ": точки плоскости не созданы");
            return null;
        }

        keep?.AddRange(new[] { first, second, third });
        return BuildPlane(planes, first, second, third, name, step);
    }

    /// <summary>Те же три точки, но вторая и третья переставлены: нормаль инвертируется.</summary>
    private IPlane3D? MakePlaneInverted(
        IModelContainer container, IPlanes3D planes, double x0, string name, ProbeStep step)
    {
        var first = MakePoint(container, x0, 0d, 0d);
        var second = MakePoint(container, x0, 0d, 1d);
        var third = MakePoint(container, x0, 1d, 0d);
        if (first is null || second is null || third is null)
        {
            step.Observe(name + ": точки плоскости не созданы");
            return null;
        }

        return BuildPlane(planes, first, second, third, name, step);
    }

    private IPlane3D? BuildPlane(
        IPlanes3D planes, IPoint3D first, IPoint3D second, IPoint3D third, string name, ProbeStep step)
    {
        try
        {
            if (planes.Add(ksObj3dTypeEnum.o3d_plane3Points) is not IPlane3D plane)
            {
                step.Observe(name + ": Planes3D.Add не отдал IPlane3D");
                return null;
            }

            if (plane is not IPlane3DBy3Points byPoints)
            {
                step.Observe(name + ": плоскость не отвечает IPlane3DBy3Points");
                return null;
            }

            plane.Name = name;
            byPoints.Point1 = first;
            byPoints.Point2 = second;
            byPoints.Point3 = third;
            var updated = plane.Update();
            step.Observe(name + ": Update()=" + updated);
            return updated ? plane : null;
        }
        catch (Exception ex)
        {
            step.Observe(name + ": исключение " + HResult.Describe(ex));
            return null;
        }
    }

    private static IPoint3D? MakePoint(IModelContainer container, double x, double y, double z)
    {
        try
        {
            if (container.Points3D is not { } points || points.Add() is not IPoint3D point)
            {
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Update();
            return point;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private bool BuildBar(ksPart part, ksDocument3D doc, ProbeStep step, bool withStranger)
    {
        if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A"))
        {
            return false;
        }

        return !withStranger
            || ExtrudeRect(doc, part, StrangerX0, StrangerX1, StrangerY0, StrangerY1, StrangerZ, step, "C");
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

    private int? SplitCount(ksDocument3D doc)
    {
        var container = Container(doc);
        return container?.SplitSolids is { } collection ? SafeInt(() => collection.Count) : null;
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
            // Not this step's subject.
        }
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
                var element = bodies.GetByIndex(i);
                var body = element as ksBody;
                double[]? min = null;
                double[]? max = null;
                int? faceCount = null;
                bool? multiPart = null;
                if (body is not null)
                {
                    if (Api5.SafeBool(() => body.GetGabarit(out _, out _, out _, out _, out _, out _)) == true)
                    {
                        body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                        min = new[] { x1, y1, z1 };
                        max = new[] { x2, y2, z2 };
                    }

                    faceCount = body.FaceCollection() is ksFaceCollection faces
                        ? SafeInt(() => faces.GetCount())
                        : null;
                    multiPart = SafeBoolOf(() => body.MultiBodyParts);
                }

                rows.Add(new BodyRow
                {
                    Index = i,
                    Element = element,
                    Volume = Api5.BodyVolume(element),
                    Min = min,
                    Max = max,
                    FaceCount = faceCount,
                    MultiBodyParts = multiPart,
                });
            }
        }
        catch (Exception)
        {
            // Частичный список честнее пустого.
        }

        return rows;
    }

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        row.Min is not null && row.Max is not null
        && Math.Abs(row.Min[0] - x0) < 1e-6 && Math.Abs(row.Min[1] - y0) < 1e-6 && Math.Abs(row.Min[2] - z0) < 1e-6
        && Math.Abs(row.Max[0] - x1) < 1e-6 && Math.Abs(row.Max[1] - y1) < 1e-6 && Math.Abs(row.Max[2] - z1) < 1e-6;

    private static double? SumVolumes(List<BodyRow> rows) =>
        rows.Any(r => r.Volume is null) ? null : rows.Sum(r => r.Volume!.Value);

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    private static bool ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0, thickness, 0d, false);
        var created = Api5.SafeBool(extrusion.Create) == true;
        part.RebuildModel();
        doc.RebuildDocument();
        if (!created)
        {
            step.Observe(prefix + ": Create() базового выдавливания → false");
        }

        return created;
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

    private static bool? SafeBoolOf(Func<bool> call)
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

    private static string? SafeString(Func<string> call)
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

    private static T? SafeEnum<T>(Func<T> call) where T : struct, Enum
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

    private static object? SafeObjectOf(Func<object> call)
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

    private void Shutdown()
    {
        var step = _report.Begin("SP.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        var waited = 0;
        const int stepMs = 250;
        const int limitMs = 10000;
        while (waited < limitMs && NewProcesses().Count > 0)
        {
            System.Threading.Thread.Sleep(stepMs);
            waited += stepMs;
        }

        var left = NewProcesses();
        step.Observe("своих процессов до запуска: " + _pidsBefore.Count + ", новых после: " + left.Count
            + (left.Count == 0 ? string.Empty : " (" + string.Join(", ", left) + ")"));
        if (_ownPid != 0 && left.Contains(_ownPid))
        {
            step.Fail("свой процесс " + _ownPid + " пережил Quit()");
            return;
        }

        step.Pass("новых процессов не осталось");
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

    private sealed class BodyRow
    {
        public int Index { get; init; }

        public object? Element { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        public int? FaceCount { get; init; }

        public bool? MultiBodyParts { get; init; }

        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>"
                : "(" + string.Join(", ", Min.Select(v => Api5.Num(v))) + ")…("
                    + string.Join(", ", Max.Select(v => Api5.Num(v))) + ")")
            + " граней=" + Api5.Raw(FaceCount) + " многокусочное=" + Api5.Raw(MultiBodyParts);
    }
}
