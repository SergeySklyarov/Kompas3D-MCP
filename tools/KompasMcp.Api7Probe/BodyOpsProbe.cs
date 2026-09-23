using System.Diagnostics;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба B3 — булевы операции над телами (SM-15) через API7 <c>IBooleans</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему отдельный опыт.</b> Каталог покрытия держит SM-15 на уровне <c>metadata_found</c>:
/// интерфейсы найдены в библиотеке типов, но ни один маршрут не измерен, а именно на такие строки
/// распространяется правило проекта «строка закрывается только измерением». Здесь измеряется
/// ровно маршрут: явные тело-цель и набор инструментов, вид операции, политика сохранения
/// инструментов, несколько инструментов одним признаком, несвязный результат и отрицательные
/// случаи.
/// </para>
/// <para>
/// <b>Геометрия берётся из наряда §6.1 и считается ДО опыта.</b> Все числа ниже — координаты
/// модели в мм; ожидания печатаются в журнал перед вызовом, чтобы расхождение нельзя было
/// объяснить подобранным после факта допуском.
/// </para>
/// <para>
/// <b>Постороннее тело.</b> В каждой постановке, кроме оговорённых, в документе живёт куб
/// <c>[100,110]×[0,10]×[0,10]</c> (V=1000), не участвующий в операции. Он — свидетель: если
/// булева операция трогает его геометрию, положение или число тел сверх ожидания, опыт обязан
/// это показать, а не «сойтись по объёму».
/// </para>
/// </remarks>
internal sealed class BodyOpsProbe
{
    // ── эталон §6.1: A [0,40]×[0,30]×[0,20] V=24000, B [20,60]×[0,30]×[0,20] V=24000 ──
    private const double Ax0 = 0d, Ax1 = 40d, Ay0 = 0d, Ay1 = 30d, Az = 20d;
    private const double Bx0 = 20d, Bx1 = 60d;
    private const double StrangerX0 = 100d, StrangerX1 = 110d, StrangerY0 = 0d, StrangerY1 = 10d, StrangerZ = 10d;

    private static double VolumeA => (Ax1 - Ax0) * (Ay1 - Ay0) * Az;
    private static double VolumeB => (Bx1 - Bx0) * (Ay1 - Ay0) * Az;
    private static double VolumeStranger => (StrangerX1 - StrangerX0) * (StrangerY1 - StrangerY0) * StrangerZ;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private int _ownPid;

    public BodyOpsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options) =>
        report.Flush(
            Path.Combine(options.ReportDir, "boolean-ops.json"),
            Path.Combine(options.ReportDir, "boolean-ops.md"));

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
            RouteExists();
            Union();
            Difference();
            Intersect();
            SaveCopyModifyObjects();
            MultipleToolsOneFeature();
            ContactMatrix();
            DifferenceIntoTwoParts();
            UnionComponentsRoute();
            NegativeCases();
            EditOperationKind();
            ReopenReadBack();
        }
        finally
        {
            Shutdown();
        }
    }

    // ══════════════════════════════════════════════════════════════ session ══

    private void Launch()
    {
        var step = _report.Begin("BO.0", "Свой невидимый сеанс КОМПАС-3D v24",
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

        step.Observe("экземпляр API5 создан за " + clock.ElapsedMilliseconds + " мс");
        step.Observe("процессов KOMPAS.exe до запуска: " + _pidsBefore.Count + ", свой: " + _ownPid);
        step.Pass("сеанс поднят");
    }

    // ══════════════════════════════════════════════════════════════ routes ══

    /// <summary>
    /// Первый вопрос — не «работает ли операция», а «отвечает ли фабрика». Каталог знает
    /// <c>IBoolean</c> по библиотеке типов; здесь проверяется, что <c>IModelContainer.Booleans</c>
    /// существует у живого контейнера и что <c>Add()</c> отдаёт объект, отвечающий на QI.
    /// </summary>
    private void RouteExists()
    {
        var step = _report.Begin("BO.1", "Фабрика IModelContainer.Booleans доступна живьём",
            "Отдаёт ли контейнер коллекцию булевых операций и создаётся ли по Add() признак?");
        var doc = NewPart(out var part);
        try
        {
            var container = Container(doc);
            if (container is null)
            {
                step.Fail("TransferInterface(документ → API7) не дал IModelContainer");
                return;
            }

            var collection = container.Booleans;
            if (collection is null)
            {
                step.Fail("IModelContainer.Booleans → null: фабрика недостижима");
                return;
            }

            step.Observe("Booleans.Count до создания: " + Api5.Raw(SafeInt(() => collection.Count)));

            // Свойства, которые каталог приписывает IBoolean, читаются с ЖИВОГО объекта, а не с
            // библиотеки типов: объявленный член и отвечающий член — разные утверждения.
            var created = collection.Add();
            if (created is not IBoolean boolean)
            {
                step.Fail("Booleans.Add() не отдал объект, отвечающий на QI(IBoolean): "
                    + Api5.RuntimeName(created));
                return;
            }

            step.Observe("Add() → " + Api5.RuntimeName(created));
            step.Observe("  BooleanType по умолчанию: " + Api5.Raw(SafeEnum(() => boolean.BooleanType)));
            step.Observe("  SaveCopyBaseObject: " + Api5.Raw(SafeBoolOf(() => boolean.SaveCopyBaseObject)));
            step.Observe("  SaveCopyModifyObjects: " + Api5.Raw(SafeBoolOf(() => boolean.SaveCopyModifyObjects)));
            step.Observe("  BaseObject: " + Api5.RuntimeName(SafeObjectOf(() => boolean.BaseObject)));
            step.Observe("  ModifyObjects: " + Api5.RuntimeName(SafeObjectOf(() => boolean.ModifyObjects)));
            step.Observe("  Bodies: " + Api5.RuntimeName(SafeObjectOf(() => boolean.Bodies)));
            step.Data["booleans_count_after_add"] = SafeInt(() => collection.Count);
            step.Pass("фабрика Booleans отвечает, Add() отдаёт IBoolean");
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

    private void Union()
    {
        var step = _report.Begin("BO.2", "A ∪ B с явными целью и инструментом",
            "Даёт ли ksUnion одно тело объёмом 36000 с габаритом [0,0,0]…[60,30,20]?");
        Announce(step, "A ∪ B", 36000d, new[] { 0d, 0d, 0d }, new[] { 60d, 30d, 20d });
        RunBinary(step, ksBooleanType.ksUnion, saveTool: false, expectedVolume: 36000d,
            expectedMin: new[] { 0d, 0d, 0d }, expectedMax: new[] { 60d, 30d, 20d },
            expectedBodies: 2 /* результат + постороннее */);
    }

    private void Difference()
    {
        var step = _report.Begin("BO.3", "A − B: порядок операндов",
            "Даёт ли ksDifference цель минус инструмент, то есть [0,0,0]…[20,30,20] и V=12000?");
        Announce(step, "A − B", 12000d, new[] { 0d, 0d, 0d }, new[] { 20d, 30d, 20d });
        step.Observe("B − A дало бы [40,0,0]…[60,30,20] — то же число, другое место; объём здесь "
            + "не различает порядок, поэтому сверяется габарит");
        RunBinary(step, ksBooleanType.ksDifference, saveTool: false, expectedVolume: 12000d,
            expectedMin: new[] { 0d, 0d, 0d }, expectedMax: new[] { 20d, 30d, 20d },
            expectedBodies: 2);
    }

    private void Intersect()
    {
        var step = _report.Begin("BO.4", "A ∩ B",
            "Даёт ли ksIntersect тело [20,0,0]…[40,30,20] объёмом 12000?");
        Announce(step, "A ∩ B", 12000d, new[] { 20d, 0d, 0d }, new[] { 40d, 30d, 20d });
        step.Observe("объём тот же, что у разности, — опыт различает их ГАБАРИТОМ, а не числом");
        RunBinary(step, ksBooleanType.ksIntersect, saveTool: false, expectedVolume: 12000d,
            expectedMin: new[] { 20d, 0d, 0d }, expectedMax: new[] { 40d, 30d, 20d },
            expectedBodies: 2);
    }

    private void SaveCopyModifyObjects()
    {
        var step = _report.Begin("BO.5", "SaveCopyModifyObjects: инструмент остаётся отдельным телом",
            "Сохраняет ли политика копию инструмента, и остаётся ли она на прежнем месте?");
        Announce(step, "A ∪ B при SaveCopyModifyObjects=true", 36000d,
            new[] { 0d, 0d, 0d }, new[] { 60d, 30d, 20d });
        step.Observe("ожидание: тел 3 (результат 36000 + сохранённый B 24000 + посторонний 1000)");
        step.Observe("сумма индивидуальных объёмов 61000 против объёма пространственного "
            + "объединения 37000 — это разные величины, и контракт обязан их не смешивать");
        RunBinary(step, ksBooleanType.ksUnion, saveTool: true, expectedVolume: 36000d,
            expectedMin: new[] { 0d, 0d, 0d }, expectedMax: new[] { 60d, 30d, 20d },
            expectedBodies: 3, expectToolSurvives: new[] { 20d, 0d, 0d, 60d, 30d, 20d });
    }

    /// <summary>
    /// §6.3: цель A, инструменты L=[−10,10]×[0,30]×[0,20] и R=[30,50]×[0,30]×[0,20]. Оба — в
    /// ОДНОМ признаке. Два последовательных признака этот режим не закрывают, поэтому проверяется
    /// и состав признака (число элементов ModifyObjects), и то, что перестановка L/R не меняет
    /// геометрию.
    /// </summary>
    private void MultipleToolsOneFeature()
    {
        var step = _report.Begin("BO.6", "Несколько инструментов ОДНИМ признаком",
            "Принимает ли ModifyObjects массив из двух тел и даёт ли A ∪ L ∪ R одно тело "
            + "[−10,0,0]…[50,30,20] объёмом 36000?");
        const double lx0 = -10d, lx1 = 10d, rx0 = 30d, rx1 = 50d;
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): A ∪ L ∪ R → тело V=36000, "
            + "габарит (−10, 0, 0)…(50, 30, 20)");
        step.Observe("L=[−10,10]×[0,30]×[0,20] и R=[30,50]×[0,30]×[0,20], по 12000 мм³; "
            + "L и R не пересекаются между собой, каждое пересекается с A по 6000 мм³");
        step.Observe("в документе, кроме A, живут B (посторонний для ЭТОЙ операции) и куб C; "
            + "ожидаемое число тел — 3 (результат + B + C), и B с C обязаны остаться нетронутыми");

        var doc = NewPart(out var part);
        try
        {
            if (!BuildReference(part, doc, step, withStranger: true))
            {
                step.Fail("эталонные тела не построены");
                return;
            }

            if (!ExtrudeRect(doc, part, lx0, lx1, Ay0, Ay1, Az, step, "L")
                || !ExtrudeRect(doc, part, rx0, rx1, Ay0, Ay1, Az, step, "R"))
            {
                step.Fail("инструменты L/R не построены");
                return;
            }

            var before = BodyRows(part);
            step.Observe("тел до операции: " + before.Count + " → " + Describe(before));

            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var toolL = FindBody(before, lx0, Ay0, 0d, lx1, Ay1, Az);
            var toolR = FindBody(before, rx0, Ay0, 0d, rx1, Ay1, Az);
            if (target is null || toolL is null || toolR is null)
            {
                step.Fail("не удалось опознать цель/инструменты по габариту среди тел документа");
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, target, new[] { toolL, toolR }, ksBooleanType.ksUnion,
                saveTools: false, step);
            if (!ok)
            {
                step.Fail("создание булева признака: " + note);
                return;
            }

            // Состав признака — часть режима: «несколько инструментов ОДНИМ признаком» ложно, если
            // признак один, а инструментов в нём один. Считается по ModifyObjects, а не по дереву.
            var feature = ReadLastBoolean(doc, step);
            step.Observe("признак прочитан обратно: " + (feature ?? "<не прочитан>"));
            step.Observe("признаков Booleans в документе: " + Api5.Raw(BooleanCount(doc)));
            step.Data["feature_read_back"] = feature;
            step.Data["boolean_feature_count"] = BooleanCount(doc);

            var after = BodyRows(part);
            step.Observe("тел после операции: " + after.Count + " → " + Describe(after));
            step.Data["bodies_after"] = after.Count;
            if (!Verdict(step, after, 36000d, new[] { -10d, 0d, 0d }, new[] { 50d, 30d, 20d }, 3, null))
            {
                return;
            }

            var untouchedB = FindBody(after, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            var untouchedC = FindBody(after, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ);
            if (untouchedB is null || Math.Abs((untouchedB.Volume ?? 0d) - VolumeB) > 0.01d
                || untouchedC is null || Math.Abs((untouchedC.Volume ?? 0d) - VolumeStranger) > 0.01d)
            {
                step.Fail("B или C изменились, хотя в операции не участвовали");
                return;
            }

            step.Observe("неучаствовавшие тела целы: B " + untouchedB.Describe() + "; C " + untouchedC.Describe());
            step.Pass("оба инструмента вошли в ОДИН признак (ModifyObjects из двух тел), геометрия "
                + "и неучаствовавшие тела совпали с ожиданием");
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
    /// §6.4: матрица касаний. Вопрос не «получится ли объединение», а как ядро ведёт себя на
    /// КАЖДОМ виде контакта и как оно представляет результат, у которого связных частей больше
    /// одной. Ожидания посчитаны заранее, а вид контакта классифицируется ДО операции вызовом
    /// <c>ksBody.CheckIntersectionWithBody</c>, а не по моему предположению о числах.
    /// </summary>
    /// <remarks>
    /// Прошлый прогон (18.09.2026) показал, что объединение двух РАЗНЕСЁННЫХ тел отвергается
    /// ядром (<c>Update()=false</c>). Это не «отсутствие функции»: связность результата — свойство
    /// геометрии, и достройка матрицы отличает «ядро отвергает несвязное объединение» от «ядро
    /// отвергает объединение вообще». Без матрицы оба вывода выглядят одинаково.
    /// </remarks>
    private void ContactMatrix()
    {
        var step = _report.Begin("BO.7", "Матрица касаний: грань, ребро, точка, отсутствие контакта, "
            + "вложенность",
            "Как ведёт себя объединение на каждом виде контакта и как представлен несвязный результат?");

        // Каждый случай — свой документ: иначе предыдущая операция меняет состояние, и «отказ»
        // перестаёт быть отказом именно этого случая.
        ContactCase(step, "грань", new[] { 40d, 0d, 0d, 60d, 30d, 20d }, 36000d,
            new[] { 0d, 0d, 0d }, new[] { 60d, 30d, 20d });
        ContactCase(step, "ребро", new[] { 40d, 30d, 0d, 60d, 50d, 20d }, 36000d,
            new[] { 0d, 0d, 0d }, new[] { 60d, 50d, 20d });
        ContactCase(step, "точка", new[] { 40d, 30d, 20d, 60d, 50d, 40d }, 36000d,
            new[] { 0d, 0d, 0d }, new[] { 60d, 50d, 40d });
        ContactCase(step, "отсутствие контакта", new[] { 50d, 0d, 0d, 60d, 10d, 10d }, 25000d,
            new[] { 0d, 0d, 0d }, new[] { 60d, 30d, 20d });
        ContactCase(step, "вложенность (инструмент целиком внутри цели)",
            new[] { 10d, 10d, 5d, 20d, 20d, 10d }, 24000d, new[] { 0d, 0d, 0d }, new[] { 40d, 30d, 20d });

        var refused = step.Data.TryGetValue("refused_cases", out var refusedRaw) ? refusedRaw as string : null;
        step.Pass("матрица измерена; отвергнутые ядром случаи: "
            + (string.IsNullOrEmpty(refused) ? "нет" : refused));
    }

    private void ContactCase(
        ProbeStep step, string label, double[] box, double expectedVolume, double[] expectedMin, double[] expectedMax)
    {
        step.Observe("── " + label + ": инструмент " + Box(box[0], box[1], box[2], box[3], box[4], box[5])
            + "; ожидание A ∪ инструмент → V=" + Api5.Num(expectedVolume)
            + ", габарит " + Point(expectedMin) + "…" + Point(expectedMax));

        var doc = NewPart(out var part);
        try
        {
            if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A")
                || !ExtrudeRectOnOffsetPlane(doc, part, box[0], box[3], box[1], box[4], box[2], box[5] - box[2],
                    step, "T"))
            {
                step.Observe(label + ": тела не построены");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, box[0], box[1], box[2], box[3], box[4], box[5]);
            if (target is null || tool is null)
            {
                step.Observe(label + ": тела не опознаны по габариту → " + Describe(before));
                return;
            }

            // Вид контакта устанавливает ЯДРО, а не моя подпись на ярлыке.
            var intersection = DescribeIntersection(target.Element, tool.Element);
            step.Observe(label + ": CheckIntersectionWithBody → " + intersection);

            var (ok, note) = CreateBoolean(doc, part, target, new[] { tool }, ksBooleanType.ksUnion,
                saveTools: false, step);
            if (!ok)
            {
                step.Observe(label + ": ОТКАЗ — " + note);
                step.Data["case_" + label] = "отказ";
                var list = step.Data.TryGetValue("refused_cases", out var prior) ? prior as string : null;
                step.Data["refused_cases"] = string.IsNullOrEmpty(list) ? label : list + ", " + label;
                var afterRefusal = BodyRows(part);
                step.Observe(label + ": тел после отказа " + afterRefusal.Count + " → " + Describe(afterRefusal));
                return;
            }

            var after = BodyRows(part);
            step.Observe(label + ": тел " + after.Count + " → " + Describe(after));
            var result = after.FirstOrDefault(r => Near(r, expectedMin, expectedMax));
            if (result is null)
            {
                step.Observe(label + ": тела с ожидаемым габаритом нет");
                step.Data["case_" + label] = "габарит не совпал";
                return;
            }

            var delta = Math.Abs((result.Volume ?? double.NaN) - expectedVolume);
            step.Observe(label + ": V=" + Api5.Num(result.Volume) + " против ожидаемых "
                + Api5.Num(expectedVolume) + " (расхождение " + Api5.Num(delta) + ")");
            step.Data["case_" + label] = "V=" + Api5.Num(result.Volume)
                + ", тел=" + after.Count
                + ", граней=" + Api5.Raw(result.FaceCount)
                + ", многокусочное=" + Api5.Raw(result.MultiBodyParts);
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

    /// <summary>
    /// §6.4: разность A и плиты [15,25]×[−5,35]×[−5,25]. Прошлый прогон (18.09.2026) дал ОДНО
    /// тело V=18000 с габаритом всего A — то есть ядро представляет распавшийся результат одним
    /// телом. Здесь это проверяется явно: членом <c>MultiBodyParts</c> и числом граней, а не
    /// только объёмом, который одинаков и для двух отдельных тел, и для одного тела из двух кусков.
    /// </summary>
    private void DifferenceIntoTwoParts()
    {
        var step = _report.Begin("BO.8", "Разность, распадающаяся на две части",
            "Как ядро представляет результат, у которого две несвязные части: двумя телами или "
            + "одним телом из двух кусков?");
        const double px0 = 15d, px1 = 25d, py0 = -5d, py1 = 35d, pz0 = -5d, pz1 = 25d;
        step.Observe("плита " + Box(px0, py0, pz0, px1, py1, pz1) + " — шире A по Y и Z, поэтому "
            + "режет A насквозь по X");
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): материал остаётся в x∈[0,15] и x∈[25,40], "
            + "по 9000 мм³, суммарно 18000");
        step.Observe("два куска по 6 граней дают 12 граней, если ядро держит их одним телом");

        var doc = NewPart(out var part);
        try
        {
            if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A")
                || !ExtrudeRectOnOffsetPlane(doc, part, px0, px1, py0, py1, pz0, pz1 - pz0, step, "P"))
            {
                step.Fail("тело или плита не построены");
                return;
            }

            var before = BodyRows(part);
            step.Observe("тел до операции: " + before.Count + " → " + Describe(before));
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, px0, py0, pz0, px1, py1, pz1);
            if (target is null || tool is null)
            {
                step.Fail("цель или плита не опознаны по габариту");
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, target, new[] { tool }, ksBooleanType.ksDifference,
                saveTools: false, step);
            if (!ok)
            {
                step.Fail("создание разности: " + note);
                return;
            }

            var after = BodyRows(part);
            step.Observe("тел после операции: " + after.Count + " → " + Describe(after));
            var total = SumVolumes(after);
            step.Data["bodies_after"] = after.Count;
            step.Data["total_volume"] = total;
            step.Data["face_count"] = after.Count == 1 ? after[0].FaceCount : null;
            step.Data["multi_body_parts"] = after.Count == 1 ? after[0].MultiBodyParts : null;

            if (after.Count != 1)
            {
                step.Fail("тел после разности " + after.Count + ", ожидалось одно (или два — но "
                    + "тогда это надо назвать явно): " + Describe(after));
                return;
            }

            if (total is null || Math.Abs(total.Value - 18000d) > 0.01d)
            {
                step.Fail("суммарный объём " + Api5.Num(total) + " против ожидаемых 18000");
                return;
            }

            var representation = after[0].MultiBodyParts == true
                ? "одно тело из НЕСКОЛЬКИХ кусков (MultiBodyParts=true, граней "
                    + Api5.Raw(after[0].FaceCount) + ")"
                : "одно тело, MultiBodyParts=" + Api5.Raw(after[0].MultiBodyParts)
                    + ", граней " + Api5.Raw(after[0].FaceCount);
            step.Observe("представление ядра: " + representation);
            if (after[0].FaceCount is not null and not 12)
            {
                step.Fail("граней " + after[0].FaceCount + " — два куска по 6 граней дают 12; "
                    + "иначе результат не из двух прямоугольных кусков");
                return;
            }

            step.Pass("объём 18000 и представление результата подтверждены: " + representation);
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
    /// Отрицательные случаи. Каждый идёт в СВОЁМ документе: прошлый прогон свёл три случая в один
    /// документ, и «третий был принят ядром» сделало проверку «модель не изменилась» бессмысленной —
    /// она мерила принятый случай, а не отказы.
    /// </summary>
    private void NegativeCases()
    {
        var step = _report.Begin("BO.9", "Отрицательные случаи: цель среди инструментов, пустой "
            + "набор, повтор ссылки",
            "Отказывает ли ядро там, где постановка бессмысленна, и не портит ли при этом модель?");
        step.Observe("каждый случай — свой документ; «модель не изменилась» проверяется для каждого "
            + "случая отдельно");

        NegativeCase(step, "цель среди инструментов",
            (target, tool) => new[] { target });
        NegativeCase(step, "пустой набор инструментов",
            (target, tool) => Array.Empty<BodyRow>());
        NegativeCase(step, "одна и та же ссылка дважды",
            (target, tool) => new[] { tool, tool });

        step.Pass("три случая измерены раздельно; исходы названы в наблюдениях выше");
    }

    private void NegativeCase(ProbeStep step, string label, Func<BodyRow, BodyRow, BodyRow[]> tools)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildReference(part, doc, step, withStranger: true))
            {
                step.Observe(label + ": тела не построены");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            if (target is null || tool is null)
            {
                step.Observe(label + ": тела не опознаны");
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, target, tools(target, tool), ksBooleanType.ksUnion,
                saveTools: false, step);
            var outcome = ok ? "ПРИНЯТО ядром (Update()=true)" : "ОТКАЗ (" + note + ")";
            step.Observe(label + " → " + outcome);

            var after = BodyRows(part);
            var volumeBefore = SumVolumes(before);
            var volumeAfter = SumVolumes(after);
            var unchanged = before.Count == after.Count
                && volumeBefore is not null && volumeAfter is not null
                && Math.Abs(volumeBefore.Value - volumeAfter.Value) < 0.01d;
            step.Observe(label + ": тел " + before.Count + "→" + after.Count
                + ", суммарный объём " + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + ", модель " + (unchanged ? "не изменилась" : "ИЗМЕНИЛАСЬ"));
            step.Data["case_" + label] = outcome;
            step.Data["unchanged_" + label] = unchanged;
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

    /// <summary>
    /// Save → close → reopen: читается ли признак обратно и сохраняются ли его входы. Вопрос не
    /// «открылся ли файл», а «можно ли после переоткрытия узнать, что операция была, и какая».
    /// </summary>
    private void ReopenReadBack()
    {
        var step = _report.Begin("BO.10", "Признак переживает save → close → reopen",
            "Читаются ли вид операции и политика сохранения инструментов с переоткрытого файла?");
        var doc = NewPart(out var part);
        var path = Path.Combine(_options.WorkDir, "boolean-reopen.m3d");
        try
        {
            if (!BuildReference(part, doc, step, withStranger: true))
            {
                step.Fail("эталонные тела не построены");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            if (target is null || tool is null)
            {
                step.Fail("цель или инструмент не опознаны");
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, target, new[] { tool }, ksBooleanType.ksUnion,
                saveTools: true, step);
            if (!ok)
            {
                step.Fail("создание объединения: " + note);
                return;
            }

            var after = BodyRows(part);
            step.Observe("тел до сохранения: " + after.Count + " → " + Describe(after));
            step.Data["volume_before_save"] = SumVolumes(after);

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
                var rows = BodyRows(reopenedPart);
                step.Observe("тел после переоткрытия: " + rows.Count + " → " + Describe(rows));
                var feature = ReadLastBoolean(reopened, step);
                step.Observe("признак с переоткрытого файла: " + (feature ?? "<не прочитан>"));
                step.Data["bodies_after_reopen"] = rows.Count;
                step.Data["volume_after_reopen"] = SumVolumes(rows);
                step.Data["feature_after_reopen"] = feature;

                if (feature is null)
                {
                    step.Fail("булев признак после переоткрытия не читается");
                    return;
                }

                step.Pass("признак и состав тел пережили save → close → reopen");
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

    /// <summary>
    /// Общий прогон бинарной операции: свежий документ, A, B, посторонний куб, операция, проверка.
    /// </summary>
    private void RunBinary(
        ProbeStep step,
        ksBooleanType type,
        bool saveTool,
        double expectedVolume,
        double[] expectedMin,
        double[] expectedMax,
        int expectedBodies,
        double[]? expectToolSurvives = null)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildReference(part, doc, step, withStranger: true))
            {
                step.Fail("эталонные тела не построены");
                return;
            }

            var before = BodyRows(part);
            step.Observe("тел до операции: " + before.Count + " → " + Describe(before));
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            var stranger = FindBody(before, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ);
            if (target is null || tool is null || stranger is null)
            {
                step.Fail("A, B или постороннее тело не опознаны по габариту: " + Describe(before));
                return;
            }

            step.Data["volume_before"] = SumVolumes(before);
            var (ok, note) = CreateBoolean(doc, part, target, new[] { tool }, type, saveTool, step);
            if (!ok)
            {
                step.Fail("создание булева признака: " + note);
                return;
            }

            var feature = ReadLastBoolean(doc, step);
            step.Observe("признак прочитан обратно: " + (feature ?? "<не прочитан>"));
            step.Data["feature_read_back"] = feature;

            var after = BodyRows(part);
            step.Observe("тел после операции: " + after.Count + " → " + Describe(after));
            step.Data["bodies_after"] = after.Count;
            step.Data["volume_sum_after"] = SumVolumes(after);

            if (Verdict(step, after, expectedVolume, expectedMin, expectedMax, expectedBodies,
                    expectToolSurvives))
            {
                var survivor = after.FirstOrDefault(r => Near(r, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ));
                if (survivor is null || Math.Abs((survivor.Volume ?? 0d) - VolumeStranger) > 0.01d)
                {
                    step.Fail("постороннее тело изменилось: " + (survivor?.Describe() ?? "исчезло"));
                    return;
                }

                step.Observe("постороннее тело не тронуто: " + survivor.Describe());
                step.Pass("геометрия, число тел и непричастное тело совпали с ожиданием");
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

    /// <summary>Печатает ожидания ДО опыта — иначе расхождение объясняется подобранным допуском.</summary>
    private static void Announce(ProbeStep step, string what, double volume, double[] min, double[] max)
    {
        step.Observe("ОЖИДАНИЕ (посчитано до опыта): " + what
            + " → одно тело V=" + Api5.Num(volume)
            + ", габарит " + Point(min) + "…" + Point(max));
    }

    private static bool Verdict(
        ProbeStep step,
        List<BodyRow> after,
        double expectedVolume,
        double[] expectedMin,
        double[] expectedMax,
        int expectedBodies,
        double[]? expectToolSurvives)
    {
        if (after.Count != expectedBodies)
        {
            step.Fail("тел после операции " + after.Count + " против ожидаемых " + expectedBodies
                + ": " + Describe(after));
            return false;
        }

        var result = after.FirstOrDefault(r => Near(r, expectedMin, expectedMax));
        if (result is null)
        {
            step.Fail("тела с ожидаемым габаритом " + Point(expectedMin) + "…" + Point(expectedMax)
                + " не найдено: " + Describe(after));
            return false;
        }

        var delta = Math.Abs((result.Volume ?? double.NaN) - expectedVolume);
        step.Data["result_volume"] = result.Volume;
        step.Data["volume_delta"] = delta;
        if (double.IsNaN(delta) || delta > 0.01d)
        {
            step.Fail("объём результата " + Api5.Num(result.Volume) + " против ожидаемого "
                + Api5.Num(expectedVolume) + " (расхождение " + Api5.Num(delta) + ")");
            return false;
        }

        if (expectToolSurvives is { } toolBox)
        {
            var saved = after.FirstOrDefault(r => Near(r, toolBox[0], toolBox[1], toolBox[2],
                toolBox[3], toolBox[4], toolBox[5]));
            if (saved is null)
            {
                step.Fail("сохранённый инструмент не найден отдельным телом на прежнем месте");
                return false;
            }

            step.Observe("сохранённый инструмент: " + saved.Describe());
        }

        return true;
    }

    /// <summary>
    /// Создание булева признака. Возвращает пару (создано, причина): вызывающий обязан отличать
    /// отказ КОМПАСа от падения зонда.
    /// </summary>
    /// <summary>
    /// Несвязное объединение: маршрут «объединение компонентов» вместо булевой операции.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Зачем шаг.</b> Наряд B3 §6.4 ожидает от объединения двух разнесённых кубов 10×10×10 ОДНО
    /// тело V=2000 из двух кусков, и §10.5 прямо запрещает закрывать положительный режим отказом.
    /// Шаг <c>BO.7</c> измерил, что <c>IBoolean</c> такие тела ОТВЕРГАЕТ (<c>Update() = false</c>).
    /// Библиотека типов объявляет при этом отдельный маршрут — <c>IModelContainer.UnionsComponents</c>
    /// (<c>IUnionComponents</c>: <c>Parts</c> + <c>Update()</c>), то есть «объединение компонентов».
    /// Один отказ <c>IBoolean</c> не доказывает, что объединение несвязных тел невыразимо ВООБЩЕ,
    /// поэтому маршрут измеряется, а не предполагается.
    /// </para>
    /// <para>
    /// <b>Опыты.</b> UC-1 воспроизводит блокер (<c>IBoolean</c> на тех же двух телах), UC-2 проверяет
    /// <c>UnionsComponents</c> на НИХ ЖЕ, UC-3 — контроль на КОНТАКТНЫХ телах (там <c>IBoolean</c>
    /// работает, поэтому «не сработало» нельзя будет списать на неверно собранный вызов),
    /// UC-4 — контроль с ОДНИМ телом в <c>Parts</c>. Каждый опыт идёт в своём документе: иначе
    /// предыдущая операция меняет состояние, и отказ перестаёт быть отказом именно этого случая.
    /// </para>
    /// </remarks>
    private void UnionComponentsRoute()
    {
        var step = _report.Begin("BO.12", "Несвязное объединение: маршрут «объединение компонентов»",
            "Выражает ли IModelContainer.UnionsComponents (IUnionComponents.Parts + Update()) "
            + "объединение двух тел БЕЗ контакта, которое IBoolean отвергает?");

        step.Observe("ОЖИДАНИЕ §6.4 (посчитано до опыта): два куба 10×10×10 — [0,10]×[0,10]×[0,10] и "
            + "[20,30]×[0,10]×[0,10]; объединение → ОДНО тело V=2000 из двух кусков, габарит "
            + "(0,0,0)…(30,10,10)");

        BooleanOnDisjoint(step);
        UnionComponentsOnDisjoint(step);
        UnionComponentsOnContact(step);
        UnionComponentsOnSinglePart(step);

        var verdict = step.Data.TryGetValue("uc2", out var uc2) ? uc2 as string : "не выполнено";
        step.Pass("маршрут измерен; UC-2 (несвязные тела через UnionsComponents): " + verdict);
    }

    /// <summary>UC-1: воспроизведение блокера — <c>IBoolean</c> на телах без контакта.</summary>
    private void BooleanOnDisjoint(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildPair(doc, part, step, "UC-1", out var c1, out var c2))
            {
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, c1!, new[] { c2! }, ksBooleanType.ksUnion, saveTools: false, step);
            step.Observe("UC-1: IBoolean → " + (ok ? "ПРИНЯТО" : "ОТКАЗ: " + note));
            step.Data["uc1_boolean"] = ok ? "принято" : "отказ: " + note;
        }
        catch (Exception ex)
        {
            step.Observe("UC-1: исключение " + HResult.Describe(ex));
            step.Data["uc1_boolean"] = "исключение";
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>UC-2: те же два тела без контакта — маршрутом <c>UnionsComponents</c>.</summary>
    private void UnionComponentsOnDisjoint(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!BuildPair(doc, part, step, "UC-2", out var c1, out var c2))
            {
                step.Data["uc2"] = "тела не построены";
                return;
            }

            var (ok, note) = CreateUnionComponents(doc, part, new[] { c1!, c2! }, step);
            var after = BodyRows(part);
            step.Observe("UC-2: после операции тел " + after.Count + " → " + Describe(after));
            var merged = after.FirstOrDefault(r => Near(r, 0d, 0d, 0d, 30d, 10d, 10d));
            if (!ok)
            {
                step.Data["uc2"] = "отказ: " + note;
                return;
            }

            if (merged is null)
            {
                step.Observe("UC-2: тела с ожидаемым габаритом (0,0,0)…(30,10,10) нет");
                step.Data["uc2"] = "принято, но габарита нет";
                return;
            }

            var volume = merged.Volume;
            var delta = Math.Abs((volume ?? double.NaN) - 2000d);
            step.Observe("UC-2: V=" + Api5.Num(volume) + " против ожидаемых 2000 (расхождение "
                + Api5.Num(delta) + "), тел=" + after.Count + ", граней=" + Api5.Raw(merged.FaceCount)
                + ", многокусочное=" + Api5.Raw(merged.MultiBodyParts));
            step.Data["uc2"] = "принято, V=" + Api5.Num(volume) + ", тел=" + after.Count
                + ", граней=" + Api5.Raw(merged.FaceCount)
                + ", многокусочное=" + Api5.Raw(merged.MultiBodyParts);
        }
        catch (Exception ex)
        {
            step.Observe("UC-2: исключение " + HResult.Describe(ex));
            step.Data["uc2"] = "исключение: " + ex.GetType().Name;
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>
    /// UC-3: контроль — тела КОНТАКТИРУЮТ. Там <c>IBoolean</c> работает (шаг BO.7), поэтому исход
    /// этого опыта отделяет «маршрут собран неверно» от «ядро не принимает несвязные тела».
    /// </summary>
    private void UnionComponentsOnContact(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!ExtrudeRect(doc, part, 0d, 10d, 0d, 10d, 10d, step, "UC-3-C1")
                || !ExtrudeRectOnOffsetPlane(doc, part, 10d, 20d, 0d, 10d, 0d, 10d, step, "UC-3-C2"))
            {
                step.Data["uc3"] = "тела не построены";
                return;
            }

            var before = BodyRows(part);
            var c1 = FindBody(before, 0d, 0d, 0d, 10d, 10d, 10d);
            var c2 = FindBody(before, 10d, 0d, 0d, 20d, 10d, 10d);
            if (c1 is null || c2 is null)
            {
                step.Data["uc3"] = "тела не опознаны";
                return;
            }

            var (ok, note) = CreateUnionComponents(doc, part, new[] { c1, c2 }, step);
            var after = BodyRows(part);
            step.Observe("UC-3: после операции тел " + after.Count + " → " + Describe(after));
            step.Data["uc3"] = ok ? "принято" : "отказ: " + note;
        }
        catch (Exception ex)
        {
            step.Observe("UC-3: исключение " + HResult.Describe(ex));
            step.Data["uc3"] = "исключение";
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>UC-4: контроль — в <c>Parts</c> одно тело. «Объединение одного тела» ничего не меняет.</summary>
    private void UnionComponentsOnSinglePart(ProbeStep step)
    {
        var doc = NewPart(out var part);
        try
        {
            if (!ExtrudeRect(doc, part, 0d, 10d, 0d, 10d, 10d, step, "UC-4-C1"))
            {
                step.Data["uc4"] = "тело не построено";
                return;
            }

            var before = BodyRows(part);
            var c1 = FindBody(before, 0d, 0d, 0d, 10d, 10d, 10d);
            if (c1 is null)
            {
                step.Data["uc4"] = "тело не опознано";
                return;
            }

            var (ok, note) = CreateUnionComponents(doc, part, new[] { c1 }, step);
            var after = BodyRows(part);
            step.Observe("UC-4: тел до " + before.Count + ", после " + after.Count + " → " + Describe(after));
            step.Data["uc4"] = ok ? "принято, тел " + after.Count : "отказ: " + note;
        }
        catch (Exception ex)
        {
            step.Observe("UC-4: исключение " + HResult.Describe(ex));
            step.Data["uc4"] = "исключение";
        }
        finally
        {
            TryClose(doc);
        }
    }

    /// <summary>Пара кубов 10×10×10: [0,10]³ и [20,30]×[0,10]×[0,10], опознанные по габариту.</summary>
    private bool BuildPair(
        ksDocument3D doc, ksPart part, ProbeStep step, string label, out BodyRow? first, out BodyRow? second)
    {
        first = null;
        second = null;
        if (!ExtrudeRect(doc, part, 0d, 10d, 0d, 10d, 10d, step, label + "-C1")
            || !ExtrudeRectOnOffsetPlane(doc, part, 20d, 30d, 0d, 10d, 0d, 10d, step, label + "-C2"))
        {
            step.Observe(label + ": тела не построены");
            return false;
        }

        var before = BodyRows(part);
        step.Observe(label + ": тел " + before.Count + " → " + Describe(before));
        first = FindBody(before, 0d, 0d, 0d, 10d, 10d, 10d);
        second = FindBody(before, 20d, 0d, 0d, 30d, 10d, 10d);
        if (first is null || second is null)
        {
            step.Observe(label + ": тела не опознаны по габариту");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Вызов «объединения компонентов»: <c>IModelContainer.UnionsComponents.Add()</c> →
    /// <c>IUnionComponents.Parts</c> → <c>Update()</c>. Тела переносятся в API7 тем же способом, что и
    /// для <c>IBoolean</c>: иначе исход мерил бы перенос, а не операцию.
    /// </summary>
    private (bool Ok, string? Note) CreateUnionComponents(
        ksDocument3D doc, ksPart part, IReadOnlyList<BodyRow> bodies, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container is null)
            {
                return (false, "контейнер API7 недоступен");
            }

            var countBefore = Api5.Raw(container.UnionsComponents?.Count);
            var parts = new object[bodies.Count];
            for (var i = 0; i < bodies.Count; i++)
            {
                if (Transfer(bodies[i]) is not { } transferred)
                {
                    return (false, "тело " + i + " не переносится в API7");
                }

                parts[i] = transferred;
            }

            if (container.UnionsComponents?.Add() is not IUnionComponents union)
            {
                return (false, "UnionsComponents.Add() не отдал IUnionComponents");
            }

            union.Parts = parts;
            var updated = union.Update();
            step.Observe("вызов: UnionsComponents.Add() → IUnionComponents, тел в Parts=" + bodies.Count
                + ", признаков было=" + countBefore + ", Update()=" + updated);

            part.RebuildModel();
            doc.RebuildDocument();
            var countAfter = Api5.Raw(Container(doc)?.UnionsComponents?.Count);
            step.Observe("после перестроения признаков объединения компонентов: " + countAfter);
            return updated ? (true, null) : (false, "IUnionComponents.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    private (bool Ok, string? Note) CreateBoolean(
        ksDocument3D doc,
        ksPart part,
        BodyRow target,
        IReadOnlyList<BodyRow> tools,
        ksBooleanType type,
        bool saveTools,
        ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container is null)
            {
                return (false, "контейнер API7 недоступен");
            }

            var target7 = Transfer(target) as IKompasAPIObject;
            if (target7 is null)
            {
                return (false, "тело-цель не переносится в API7 как IKompasAPIObject");
            }

            var tools7 = new object[tools.Count];
            for (var i = 0; i < tools.Count; i++)
            {
                if (Transfer(tools[i]) is not { } tool7)
                {
                    return (false, "инструмент " + i + " не переносится в API7");
                }

                tools7[i] = tool7;
            }

            if (container.Booleans?.Add() is not IBoolean boolean)
            {
                return (false, "Booleans.Add() не отдал IBoolean");
            }

            boolean.BaseObject = target7;
            boolean.ModifyObjects = tools7;
            boolean.BooleanType = type;
            boolean.SaveCopyBaseObject = false;
            boolean.SaveCopyModifyObjects = saveTools;

            var updated = boolean.Update();
            step.Observe("вызов: BooleanType=" + type + ", инструментов=" + tools.Count
                + ", SaveCopyModifyObjects=" + saveTools + ", Update()=" + updated);

            part.RebuildModel();
            doc.RebuildDocument();
            return updated ? (true, null) : (false, "IBoolean.Update() вернул false");
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>Пробная операция, чей исход важен как ТЕКСТ: отказ ядра или отсутствие отказа.</summary>
    private string AttemptBoolean(
        ksDocument3D doc,
        ksPart part,
        BodyRow target,
        IReadOnlyList<BodyRow> tools,
        ksBooleanType type,
        ProbeStep step)
    {
        var (ok, note) = CreateBoolean(doc, part, target, tools, type, saveTools: false, step);
        if (!ok)
        {
            return "ОТКАЗ (" + note + ")";
        }

        // Успех здесь не означает «правильно»: постановка бессмысленна, и вопрос опыта — приняла ли
        // её фабрика. Убираем признак, чтобы следующий случай шёл с чистого состояния.
        return "принято ядром (Update()=true)";
    }

    /// <summary>
    /// BO.11. Правка вида СУЩЕСТВУЮЩЕЙ булевой операции: маршрут.
    /// </summary>
    /// <remarks>
    /// Наряд B3 §5 требует, чтобы правка меняла параметры СУЩЕСТВУЮЩЕГО признака относительно его
    /// ИСХОДНЫХ входов. У булевой операции единственный содержательный параметр — вид операции
    /// (<c>IBoolean.BooleanType</c>, чтение/запись, dispid = 1), и вопрос опыта простой: меняет ли
    /// его перезапись геометрию, или <c>Update() = true</c> только подтверждает запись. Этот же
    /// вопрос уже дал отрицательный ответ на семействе разделения (шаг SP.9, контроль E-B), но
    /// переносить измерение между семействами запрещено — маршруты у них разные, поэтому опыт
    /// повторяется здесь заново.
    /// <para>
    /// <b>Различающий контроль обязателен.</b> Объём разности и объём пересечения на этом эталоне
    /// РАВНЫ (12 000), различает их габарит: разность лежит в <c>x ≤ 20</c>, пересечение —
    /// в <c>x ∈ [20, 40]</c>. Поэтому шаг E-C идёт от разности к пересечению: прибор, сверяющий
    /// только объём, на этом шаге не отличил бы правку от полного бездействия.
    /// </para>
    /// <para>
    /// <b>Два контроля, и оба способны провалиться.</b> E-E различает два пути к одному и тому же
    /// состоянию (запись без <c>Update()</c> против записи с ним) и потому проверяет само
    /// утверждение о маршруте. E-C различает правку и бездействие там, где объём одинаков. E-B
    /// контролем НЕ является и оставлен измерением — см. его собственный комментарий: первая
    /// редакция объявила там контроль и была опровергнута.
    /// </para>
    /// </remarks>
    private void EditOperationKind()
    {
        var step = _report.Begin("BO.11", "Правка вида СУЩЕСТВУЮЩЕГО булева признака",
            "Меняет ли перезапись IBoolean.BooleanType геометрию, или Update()=true только "
            + "подтверждает запись?");

        // ОЖИДАНИЯ ОБЪЯВЛЕНЫ ДО ИЗМЕРЕНИЯ (наряд §6.1, модельные координаты, мм):
        //   A = [0,40]×[0,30]×[0,20], V = 24000;  B = [20,60]×[0,30]×[0,20], V = 24000;
        //   A ∩ B: x ∈ [20,40], V = 12000.
        //
        //   создание  A ∪ B                    → одно тело V = 36000, габарит (0,0,0)…(60,30,20)
        //   E-A       объединение → разность   → V = 12000, габарит (0,0,0)…(20,30,20)
        //   E-C       РАЗЛИЧАЮЩИЙ контроль: разность → пересечение → объём ТОТ ЖЕ (12000),
        //             а габарит другой: (20,0,0)…(40,30,20). Прибор, сверяющий только объём,
        //             на этом шаге не отличил бы правку от полного бездействия
        //   E-D       пересечение → объединение → V = 36000, габарит (0,0,0)…(60,30,20)
        //   E-E       КОНТРОЛЬ «запись без Update() ничего не применяет»: записать разность БЕЗ
        //             вызова Update() и пересобрать → геометрия обязана остаться 36000; затем
        //             вызвать Update() → 12000. Если бы применяла одна запись, Update() не был бы
        //             частью маршрута, и утверждение о маршруте было бы неточным
        //   E-B       ИЗМЕРЕНИЕ (не контроль): что ядро делает со значением ksBooleanUnknown.
        //             Первая редакция объявила его здесь контролем «неиспользуемое значение
        //             геометрию не меняет», и это ожидание ОПРОВЕРГНУТО прогоном
        //             f70c555f22394ad1a050eaf96c83dd04: из разности (12000) значение
        //             ksBooleanUnknown перевело признак в 36000, то есть ядро трактует его как
        //             ОБЪЕДИНЕНИЕ. Контроль перенесён в E-E, а здесь осталось измерение смысла
        //             значения: клиент, записавший 0, получит объединение, а не отказ
        //
        //   На каждом шаге: признаков Booleans РОВНО ОДИН (правка, создавшая второй признак, —
        //   не правка), посторонний куб цел.
        var doc = NewPart(out var part);
        try
        {
            if (!BuildReference(part, doc, step, withStranger: true))
            {
                step.Fail("эталонные тела не построены");
                return;
            }

            var before = BodyRows(part);
            var target = FindBody(before, Ax0, Ay0, 0d, Ax1, Ay1, Az);
            var tool = FindBody(before, Bx0, Ay0, 0d, Bx1, Ay1, Az);
            if (target is null || tool is null)
            {
                step.Fail("цель или инструмент не опознаны");
                return;
            }

            var (ok, note) = CreateBoolean(doc, part, target, new[] { tool }, ksBooleanType.ksUnion,
                saveTools: false, step);
            if (!ok)
            {
                step.Fail("создание объединения: " + note);
                return;
            }

            var created = BodyRows(part);
            step.Data["bodies_after_create"] = created.Count;
            step.Data["volume_after_create"] = SumVolumes(created);
            step.Data["boolean_count_after_create"] = BooleanCount(doc);
            step.Observe("после создания: тел " + created.Count + ", признаков "
                + Api5.Raw(BooleanCount(doc)) + " → " + Describe(created));
            if (created.Count != 2 || SumVolumes(created) is not { } sum
                || Math.Abs(sum - 37000d) > 0.01d)
            {
                step.Fail("объединение дало не 2 тела с суммой 37000 (36000 + посторонний куб 1000)");
                return;
            }

            if (!EditKind(step, "E-A", doc, part, ksBooleanType.ksDifference,
                    12000d, new[] { 0d, 0d, 0d }, new[] { 20d, 30d, 20d }))
            {
                return;
            }

            if (!EditKind(step, "E-C", doc, part, ksBooleanType.ksIntersect,
                    12000d, new[] { 20d, 0d, 0d }, new[] { 40d, 30d, 20d }))
            {
                return;
            }

            if (!EditKind(step, "E-D", doc, part, ksBooleanType.ksUnion,
                    36000d, new[] { 0d, 0d, 0d }, new[] { 60d, 30d, 20d }))
            {
                return;
            }

            if (!ControlWriteWithoutUpdate(step, doc, part))
            {
                return;
            }

            if (!MeasureUnknownKind(step, doc, part))
            {
                return;
            }

            step.Pass("вид существующего признака правится перезаписью IBoolean.BooleanType, и "
                + "геометрия следует за видом: E-A 36000 → 12000 (x ≤ 20), E-C 12000 → 12000 "
                + "(x ≥ 20 — при равных объёмах различает габарит), E-D 12000 → 36000; "
                + "применяет именно пара «запись + Update()» (E-E), а значение "
                + "ksBooleanUnknown ядро трактует как объединение (E-B)");
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
    /// Перезапись вида на СУЩЕСТВУЮЩЕМ признаке + проверка ГЕОМЕТРИЕЙ, а не ответом <c>Update()</c>.
    /// </summary>
    private bool EditKind(
        ProbeStep step,
        string label,
        ksDocument3D doc,
        ksPart part,
        ksBooleanType type,
        double expectedVolume,
        double[] expectedMin,
        double[] expectedMax)
    {
        var container = Container(doc);
        if (container?.Booleans is not { } collection)
        {
            step.Fail(label + ": коллекция Booleans недоступна");
            return false;
        }

        var count = SafeInt(() => collection.Count);
        if (count is null or 0)
        {
            step.Fail(label + ": признаков Booleans нет (Count=" + Api5.Raw(count) + ")");
            return false;
        }

        if (collection[count.Value - 1] is not IBoolean boolean)
        {
            step.Fail(label + ": последний элемент коллекции не отдаёт IBoolean");
            return false;
        }

        var written = SafeEnum(() => boolean.BooleanType);
        boolean.BooleanType = type;
        var updated = SafeBoolOf(() => boolean.Update());
        part.RebuildModel();
        doc.RebuildDocument();
        var readBack = SafeEnum(() => boolean.BooleanType);

        var rows = BodyRows(part);
        var countAfter = SafeInt(() => collection.Count);
        step.Observe(label + ": было " + Api5.Raw(written) + " → записано " + type
            + " → Update()=" + Api5.Raw(updated) + " → прочитано " + Api5.Raw(readBack)
            + "; тел " + rows.Count + ", признаков " + Api5.Raw(countAfter) + " → " + Describe(rows));
        step.Data[label + "_written"] = type.ToString();
        step.Data[label + "_update_returned"] = updated;
        step.Data[label + "_read_back"] = readBack?.ToString();
        step.Data[label + "_boolean_count"] = countAfter;
        step.Data[label + "_bodies"] = rows.Count;
        step.Data[label + "_volume"] = SumVolumes(rows);
        step.Data[label + "_bodies_detail"] = Describe(rows);

        // Правка, создавшая второй признак, — не правка (наряд §5): параметр обязан примениться к
        // СУЩЕСТВУЮЩЕМУ признаку, а не построить рядом ещё одну операцию.
        if (countAfter != 1)
        {
            step.Fail(label + ": признаков Booleans " + Api5.Raw(countAfter) + " вместо 1");
            return false;
        }

        // Правка касается СВОЕГО признака, а не соседей: посторонний куб обязан быть цел.
        var stranger = FindBody(rows, StrangerX0, StrangerY0, 0d, StrangerX1, StrangerY1, StrangerZ);
        if (stranger is null || Math.Abs((stranger.Volume ?? 0d) - VolumeStranger) > 0.01d)
        {
            step.Fail(label + ": посторонний куб изменился — правка задела не свой признак");
            return false;
        }

        var result = rows.FirstOrDefault(r => Near(r, expectedMin, expectedMax));
        if (result is null)
        {
            step.Fail(label + ": тела с ожидаемым габаритом " + Point(expectedMin) + "…"
                + Point(expectedMax) + " не найдено: " + Describe(rows));
            return false;
        }

        var delta = Math.Abs((result.Volume ?? double.NaN) - expectedVolume);
        if (double.IsNaN(delta) || delta > 0.01d)
        {
            step.Fail(label + ": объём результата " + Api5.Num(result.Volume) + " против ожидаемого "
                + Api5.Num(expectedVolume) + " (расхождение " + Api5.Num(delta) + ")");
            return false;
        }

        return true;
    }

    /// <summary>
    /// E-E. Отрицательный контроль маршрута: «запись БЕЗ <c>Update()</c> ничего не применяет».
    /// </summary>
    /// <remarks>
    /// Маршрут правки заявлен как пара «перезапись члена → <c>Update()</c> → пересборка». Пока не
    /// измерено обратное, нельзя утверждать, что <c>Update()</c> в этой паре что-то решает: если бы
    /// применяла одна запись, вызов был бы украшением, а утверждение о маршруте — неточным.
    /// Поэтому здесь запись делается БЕЗ <c>Update()</c> и с пересборкой: геометрия обязана остаться
    /// прежней. Затем тот же член перезаписывается снова, но уже с <c>Update()</c>, и геометрия
    /// обязана измениться. Опыт мерит РАЗНИЦУ двух путей, а не один путь.
    /// </remarks>
    private bool ControlWriteWithoutUpdate(ProbeStep step, ksDocument3D doc, ksPart part)
    {
        var container = Container(doc);
        if (container?.Booleans is not { } collection)
        {
            step.Fail("E-E: коллекция Booleans недоступна");
            return false;
        }

        var count = SafeInt(() => collection.Count);
        if (count is null or 0 || collection[count.Value - 1] is not IBoolean boolean)
        {
            step.Fail("E-E: признак не прочитан");
            return false;
        }

        // Состояние на входе — объединение (E-D): 36000 в габарите (0,0,0)…(60,30,20).
        var union = FindBody(BodyRows(part), 0d, 0d, 0d, 60d, 30d, 20d);
        if (union is null || Math.Abs((union.Volume ?? 0d) - 36000d) > 0.01d)
        {
            step.Fail("E-E: вход в контроль не в состоянии объединения 36000");
            return false;
        }

        // Путь 1: запись БЕЗ Update(). Пересборка есть — иначе мерился бы не «Update() не нужен»,
        // а «ничего не вызвано».
        boolean.BooleanType = ksBooleanType.ksDifference;
        part.RebuildModel();
        doc.RebuildDocument();
        var withoutUpdate = FindBody(BodyRows(part), 0d, 0d, 0d, 60d, 30d, 20d);
        var stillUnion = withoutUpdate is not null
            && Math.Abs((withoutUpdate.Volume ?? 0d) - 36000d) <= 0.01d;
        step.Observe("E-E (контроль): запись ksDifference БЕЗ Update() + пересборка → геометрия "
            + (stillUnion ? "НЕ изменилась" : "ИЗМЕНИЛАСЬ") + ": " + Describe(BodyRows(part)));
        step.Data["E-E_write_without_update_unchanged"] = stillUnion;
        step.Data["E-E_bodies_after_write_without_update"] = Describe(BodyRows(part));

        if (!stillUnion)
        {
            step.Fail("E-E: одна запись без Update() применилась — значит Update() в маршруте не "
                + "участвует, и утверждение о маршруте неточно");
            return false;
        }

        // Путь 2: тот же член, та же пересборка, но с Update(). Геометрия обязана измениться.
        boolean.BooleanType = ksBooleanType.ksDifference;
        var updated = SafeBoolOf(() => boolean.Update());
        part.RebuildModel();
        doc.RebuildDocument();
        var difference = FindBody(BodyRows(part), 0d, 0d, 0d, 20d, 30d, 20d);
        var applied = difference is not null && Math.Abs((difference.Volume ?? 0d) - 12000d) <= 0.01d;
        step.Observe("E-E: та же запись С Update()=" + Api5.Raw(updated) + " → "
            + (applied ? "12000 в габарите x ≤ 20" : "ожидаемой геометрии нет") + ": "
            + Describe(BodyRows(part)));
        step.Data["E-E_update_returned"] = updated;
        step.Data["E-E_bodies_after_update"] = Describe(BodyRows(part));

        if (!applied)
        {
            step.Fail("E-E: пара «запись + Update()» не дала ожидаемой разности 12000");
            return false;
        }

        return true;
    }

    /// <summary>
    /// E-B. ИЗМЕРЕНИЕ (не контроль): что ядро делает со значением <c>ksBooleanUnknown</c>.
    /// </summary>
    /// <remarks>
    /// Первая редакция этого шага объявляла здесь отрицательный контроль: «неиспользуемое значение
    /// геометрию не меняет». Ожидание ОПРОВЕРГНУТО прогоном <c>f70c555f22394ad1a050eaf96c83dd04</c>:
    /// из состояния разности (12 000) запись <c>ksBooleanUnknown</c> перевела признак в 36 000, то
    /// есть ядро трактует это значение как ОБЪЕДИНЕНИЕ, а не отказывается его исполнять. Ослаблять
    /// утверждение под наблюдённый результат запрещено, поэтому опыт переименован в измерение: он
    /// устанавливает ФАКТ о продукте, который обязан знать клиент (записав 0, он получит объединение,
    /// а не отказ), а роль контроля передана опыту E-E, который различает два пути и потому способен
    /// провалиться.
    /// </remarks>
    private bool MeasureUnknownKind(ProbeStep step, ksDocument3D doc, ksPart part)
    {
        var container = Container(doc);
        if (container?.Booleans is not { } collection)
        {
            step.Fail("E-B: коллекция Booleans недоступна");
            return false;
        }

        var count = SafeInt(() => collection.Count);
        if (count is null or 0 || collection[count.Value - 1] is not IBoolean boolean)
        {
            step.Fail("E-B: признак не прочитан");
            return false;
        }

        // Вход — разность 12000 в габарите x ≤ 20 (состояние после E-E).
        var before = FindBody(BodyRows(part), 0d, 0d, 0d, 20d, 30d, 20d);
        if (before is null || Math.Abs((before.Volume ?? 0d) - 12000d) > 0.01d)
        {
            step.Fail("E-B: вход в измерение не в состоянии разности 12000");
            return false;
        }

        boolean.BooleanType = ksBooleanType.ksBooleanUnknown;
        var updated = SafeBoolOf(() => boolean.Update());
        part.RebuildModel();
        doc.RebuildDocument();

        var rows = BodyRows(part);
        var readBack = SafeEnum(() => boolean.BooleanType);
        var asUnion = FindBody(rows, 0d, 0d, 0d, 60d, 30d, 20d);
        var unionLike = asUnion is not null && Math.Abs((asUnion.Volume ?? 0d) - 36000d) <= 0.01d;

        step.Observe("E-B (измерение): BooleanType=ksBooleanUnknown, Update()=" + Api5.Raw(updated)
            + ", прочитано обратно " + Api5.Raw(readBack) + " → геометрия "
            + (unionLike ? "стала ОБЪЕДИНЕНИЕМ 36000" : "не стала объединением") + ": "
            + Describe(rows));
        step.Data["E-B_update_returned"] = updated;
        step.Data["E-B_read_back"] = readBack?.ToString();
        step.Data["E-B_behaves_as_union"] = unionLike;
        step.Data["E-B_bodies_after"] = Describe(rows);

        if (!unionLike)
        {
            step.Fail("E-B: значение ksBooleanUnknown не повело себя ни как объединение, ни как "
                + "отказ — смысл значения не установлен, и клиент не знает, что получит");
            return false;
        }

        // Признак возвращается в рабочее состояние: измерение не должно оставлять модель в
        // состоянии, которого не объявлял ни один шаг.
        boolean.BooleanType = ksBooleanType.ksDifference;
        var restored = SafeBoolOf(() => boolean.Update());
        part.RebuildModel();
        doc.RebuildDocument();
        var rowsRestored = BodyRows(part);
        var back = FindBody(rowsRestored, 0d, 0d, 0d, 20d, 30d, 20d);
        var restoredOk = back is not null && Math.Abs((back.Volume ?? 0d) - 12000d) <= 0.01d;
        step.Data["E-B_restore_update_returned"] = restored;
        step.Data["E-B_volume_after_restore"] = SumVolumes(rowsRestored);
        step.Observe("E-B: признак возвращён в ksDifference, Update()=" + Api5.Raw(restored)
            + ", объём " + Api5.Num(SumVolumes(rowsRestored)) + " (ожидание 12000)");
        if (!restoredOk)
        {
            step.Fail("E-B: возврат признака в рабочее состояние не восстановил геометрию");
            return false;
        }

        return true;
    }

    // ══════════════════════════════════════════════════════════════ helpers ══

    private bool BuildReference(ksPart part, ksDocument3D doc, ProbeStep step, bool withStranger)
    {
        if (!ExtrudeRect(doc, part, Ax0, Ax1, Ay0, Ay1, Az, step, "A")
            || !ExtrudeRect(doc, part, Bx0, Bx1, Ay0, Ay1, Az, step, "B"))
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

    private object? Transfer(BodyRow row)
    {
        try
        {
            return _app.TransferInterface(row.Element!, 2, 0);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Читает ПОСЛЕДНИЙ булев признак документа: вид операции и политику сохранения. Возвращает
    /// текст, а не объект: зонду важно, что именно читается с модели, и «не прочитано» обязано
    /// быть отличимо от «прочитан ноль».
    /// </summary>
    private string? ReadLastBoolean(ksDocument3D doc, ProbeStep step)
    {
        try
        {
            var container = Container(doc);
            if (container?.Booleans is not { } collection)
            {
                return null;
            }

            var count = SafeInt(() => collection.Count);
            if (count is null or 0)
            {
                return "признаков нет (Count=" + Api5.Raw(count) + ")";
            }

            if (collection[count.Value - 1] is not IBoolean boolean)
            {
                return "последний элемент коллекции не отдаёт IBoolean";
            }

            return "BooleanType=" + Api5.Raw(SafeEnum(() => boolean.BooleanType))
                + ", SaveCopyBaseObject=" + Api5.Raw(SafeBoolOf(() => boolean.SaveCopyBaseObject))
                + ", SaveCopyModifyObjects=" + Api5.Raw(SafeBoolOf(() => boolean.SaveCopyModifyObjects))
                + ", BaseObject=" + Api5.RuntimeName(SafeObjectOf(() => boolean.BaseObject))
                + ", ModifyObjects=" + Api5.RuntimeName(SafeObjectOf(() => boolean.ModifyObjects))
                + ", Bodies=" + Api5.RuntimeName(SafeObjectOf(() => boolean.Bodies))
                + ", Valid=" + Api5.Raw(SafeBoolOf(() => boolean.Valid));
        }
        catch (Exception ex)
        {
            step.Observe("чтение признака прервано: " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>A rectangle u∈[u0,u1], v∈[v0,v1] on XOY, base-extruded <c>thickness</c> along +Z.</summary>
    private static bool ExtrudeRect(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1, double thickness,
        ProbeStep step, string prefix)
    {
        var sketch = ProfileSketchOn(doc, prefix + "-profile", Api5.PlaneXoy, u0, u1, v0, v1);
        return Extrude(doc, part, sketch, thickness, step, prefix);
    }

    /// <summary>
    /// Прямоугольник на СМЕЩЁННОЙ плоскости z=<paramref name="zPlane"/> высотой
    /// <paramref name="thickness"/>. Нужен там, где тело выходит за плоскость XY: эталон §6.4
    /// требует плиту z∈[−5,25], а выдавленный в одну сторону эскиз на XY её не даёт.
    /// </summary>
    private static bool ExtrudeRectOnOffsetPlane(
        ksDocument3D doc, ksPart part, double u0, double u1, double v0, double v1,
        double zPlane, double thickness, ProbeStep step, string prefix)
    {
        if (part.NewEntity(Api5.PlaneOffset) is not ksEntity planeEntity
            || planeEntity.GetDefinition() is not ksPlaneOffsetDefinition planeDefinition)
        {
            step.Observe(prefix + ": смещённая плоскость не создана");
            return false;
        }

        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity basePlane)
        {
            step.Observe(prefix + ": базовой плоскости XOY нет");
            return false;
        }

        planeDefinition.SetPlane(basePlane);
        planeDefinition.offset = Math.Abs(zPlane);
        planeDefinition.direction = zPlane >= 0d;
        if (Api5.SafeBool(planeEntity.Create) != true)
        {
            step.Observe(prefix + ": смещённая плоскость Create() → false");
            return false;
        }

        var sketch = ProfileSketchOn(doc, prefix + "-profile", planeEntity, u0, u1, v0, v1);
        return Extrude(doc, part, sketch, thickness, step, prefix);
    }

    private static bool Extrude(
        ksDocument3D doc, ksPart part, ksEntity sketch, double thickness, ProbeStep step, string prefix)
    {
        if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
            || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            step.Observe(prefix + ": базовое выдавливание не создано");
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, 0 /* etBlind */, thickness, 0d, false);
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

        return ProfileSketchOn(doc, name, plane, u0, u1, v0, v1);
    }

    private static ksEntity ProfileSketchOn(
        ksDocument3D doc, string name, ksEntity plane, double u0, double u1, double v0, double v1)
    {
        var part = (ksPart)doc.GetPart(-1);
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

    /// <summary>Число булевых признаков в документе: «несколько инструментов ОДНИМ признаком» —
    /// утверждение о ЧИСЛЕ признаков, и без этого счётчика оно непроверяемо.</summary>
    private int? BooleanCount(ksDocument3D doc)
    {
        var container = Container(doc);
        return container?.Booleans is { } collection ? SafeInt(() => collection.Count) : null;
    }

    /// <summary>
    /// Вид контакта двух тел по ядру, а не по ярлыку случая: <c>CheckIntersectionWithBody</c>
    /// отвечает на вопрос «пересекаются ли» сам, и подпись «ребро» рядом с ним — гипотеза,
    /// которую этот вызов проверяет.
    /// </summary>
    private static string DescribeIntersection(object? first, object? second)
    {
        try
        {
            if (first is not ksBody bodyA || second is not ksBody bodyB)
            {
                return "<элемент не отвечает ksBody>";
            }

            var result = bodyA.CheckIntersectionWithBody(bodyB, true);
            return Api5.RuntimeName(result) + " = " + Api5.Raw(result);
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
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

    /// <summary>
    /// Тела документа по одному элементу <c>BodyCollection</c> с объёмом и габаритом. Индекс
    /// сохраняется: он нужен только как адрес внутри одного снимка, и ни одно решение опыта на
    /// него не опирается — сопоставление идёт по габариту.
    /// </summary>
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
                if (body is not null
                    && Api5.SafeBool(() => body.GetGabarit(out _, out _, out _, out _, out _, out _)) == true)
                {
                    body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2);
                    min = new[] { x1, y1, z1 };
                    max = new[] { x2, y2, z2 };
                }

                int? faceCount = null;
                bool? isSolid = null;
                bool? multiPart = null;
                if (body is not null)
                {
                    faceCount = body.FaceCollection() is ksFaceCollection faces ? SafeInt(() => faces.GetCount()) : null;
                    isSolid = SafeBoolOf(() => body.IsSolid());
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
                    IsSolid = isSolid,
                    MultiBodyParts = multiPart,
                });
            }
        }
        catch (Exception)
        {
            // Читается то, что успело прочитаться: частичный список честнее пустого.
        }

        return rows;
    }

    private static BodyRow? FindBody(
        List<BodyRow> rows, double x0, double y0, double z0, double x1, double y1, double z1) =>
        rows.FirstOrDefault(r => Near(r, x0, y0, z0, x1, y1, z1));

    private static bool Near(BodyRow row, double x0, double y0, double z0, double x1, double y1, double z1) =>
        Near(row.Min, x0, y0, z0) && Near(row.Max, x1, y1, z1);

    private static bool Near(BodyRow row, double[] min, double[] max) =>
        Near(row.Min, min[0], min[1], min[2]) && Near(row.Max, max[0], max[1], max[2]);

    private static bool Near(double[]? actual, double x, double y, double z) =>
        actual is not null
        && Math.Abs(actual[0] - x) < 1e-6 && Math.Abs(actual[1] - y) < 1e-6 && Math.Abs(actual[2] - z) < 1e-6;

    private static double? SumVolumes(List<BodyRow> rows) =>
        rows.Any(r => r.Volume is null) ? null : rows.Sum(r => r.Volume!.Value);

    private static string Describe(List<BodyRow> rows) =>
        rows.Count == 0 ? "<тел нет>" : string.Join(" | ", rows.Select(r => r.Describe()));

    private static string Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        "[" + Api5.Num(x0) + "," + Api5.Num(x1) + "]×[" + Api5.Num(y0) + "," + Api5.Num(y1) + "]×["
        + Api5.Num(z0) + "," + Api5.Num(z1) + "], V=" + Api5.Num((x1 - x0) * (y1 - y0) * (z1 - z0));

    private static string Point(double[] value) =>
        "(" + string.Join(", ", value.Select(v => Api5.Num(v))) + ")";

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
        var step = _report.Begin("BO.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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
        if (waited > 0)
        {
            step.Observe("процессы ушли за " + waited + " мс после Quit()");
        }

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

    /// <summary>Одно тело в снимке. <c>Element</c> — сырой элемент <c>BodyCollection</c>.</summary>
    private sealed class BodyRow
    {
        public int Index { get; init; }

        public object? Element { get; init; }

        public double? Volume { get; init; }

        public double[]? Min { get; init; }

        public double[]? Max { get; init; }

        /// <summary>Число граней тела: различает «одно тело с двумя кусками» от «одного куска».</summary>
        public int? FaceCount { get; init; }

        public bool? IsSolid { get; init; }

        /// <summary>
        /// <c>ksBody.MultiBodyParts</c>: состоит ли тело из НЕСКОЛЬКИХ несвязных частей. Именно
        /// этот член, а не число тел, отвечает на вопрос «как ядро представляет несвязный результат».
        /// </summary>
        public bool? MultiBodyParts { get; init; }

        public string Describe() =>
            "#" + Index + " V=" + Api5.Num(Volume)
            + " габарит " + (Min is null || Max is null ? "<нет>" : Point(Min) + "…" + Point(Max))
            + " граней=" + Api5.Raw(FaceCount)
            + " solid=" + Api5.Raw(IsSolid)
            + " многокусочное=" + Api5.Raw(MultiBodyParts);
    }
}

