using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Single entry point of the API7 research probe (ADR-003 §3).
/// </summary>
/// <remarks>
/// Everything that touches КОМПАС runs on one STA thread with a real message pump
/// (<see cref="StaPump"/>), because that is the only configuration the shipping adapter is allowed
/// to use and a route that worked on a pool thread would prove nothing about the product.
/// </remarks>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = Options.Parse(args);

        var report = new ProbeReport
        {
            Title = options.PassportOnly
                ? "Паспорт среды пробы API7 (ADR-003 §4)"
                : options.TreeLifecycle
                    ? "Проба T — как признак B3 адресуется в жизненном цикле: дерево API5 против объектов API7"
                : options.Lifecycle
                    ? "Проба L — эскиз после reopen и жизненный цикл признака (КОМПАС-3D v24)"
                    : options.Extrusion
                        ? "Проба E — смена опорного эскиза признака через API7 (КОМПАС-3D v24)"
                        : options.Chamfer
                            ? "Проба F — фаска SM-11: маршруты API5 и API7, единицы угла, направление, reopen"
                            : options.SketchReopen
                                ? "Проба G — геометрия существующего эскиза после reopen: поиск по телу, правка, измерение зависимого тела"
                                : options.FilletEdgeSet
                                    ? "Проба H — правка набора рёбер существующего скругления: array() против IFillet.BaseObjects"
                                    : options.Rotation
                                        ? "Проба R — вращение SM-03: номер типа, осевая линия эскиза, полный и частичный оборот"
                                        : options.FullTurn
                                            ? "Проба F2 — полный оборот вращения: насыщается ли развёртка на 180° или маршрут 360° существует (КОМПАС-3D v24)"
                                        : options.HoleModes
                                            ? "Проба M — четыре режима родных отверстий SM-07: цековка, зенковка, плоское дно, положение"
                                                : options.HoleTree
                                                    ? "Проба N — под каким номером признак отверстия виден в дереве после создания маршрутом API7, и как читается производная глубина зенковки"
                                                    : options.FilletBaseObjects
                                                        ? "Проба H-2 — входы существующего скругления через IFillet.BaseObjects: чтение, сокращение, замена и различающие контроли"
                                                        : options.SketchDefinition
                                                            ? "Проба S — определённость эскиза: читается ли статус «+ / − / !» из API и различает ли он состояния"
                                                            : options.Identity
                                                                ? "Проба I — идентичность признака в дереве API5 при одинаковых отображаемых именах"
                                                            : options.Union
                                                                ? "Проба U — граница применимости обычного объединения: условие из справки продукта, ответ ядра на разнесённые тела и контроль на контактных"
                                                            : options.B5
                                                                ? "Проба B5 — кинематическая операция, по сечениям и оболочка: документированный маршрут, значения направления и эталоны §9"
                                                            : options.CutArea
                                                                ? "Проба CA — область применения отсечения: назначение, чтение, сохранение при правке и переоткрытии"
                                                            : options.RepositionOrder
                                                                ? "Проба RO — порядок и адрес признаков изменения положения с одинаковыми именами, и что читается с переоткрытого файла"
                                                            : options.SketchPlane
                                                                ? "Проба SP — смена опорной плоскости существующего эскиза: документированный SetPlane/GetPlane на поставленной сборке"
                                                            : "Проба API7 — родное «Отверстие» в КОМПАС-3D v24 (ADR-003 §3)",
        };
        var clock = Stopwatch.StartNew();
        Console.WriteLine($"A7 probe run {report.RunId} — рабочая папка: {options.WorkDir}");
        Console.WriteLine($"Режим: passportOnly={options.PassportOnly}, lifecycle={options.Lifecycle}, extrusion={options.Extrusion}, chamfer={options.Chamfer}, sketchReopen={options.SketchReopen}, filletEdgeSet={options.FilletEdgeSet}, filletBaseObjects={options.FilletBaseObjects}, sketchDefinition={options.SketchDefinition}, controls={options.CollectControls}, keep={options.KeepRunning}, отчёты: {options.ReportDir}");

        // Before any Kompas6API5/KompasAPI7 type is first touched: the vendor interop is referenced,
        // not copied, so it only resolves from the installation directory at run time.
        if (!InteropResolver.TryInstall(out var reason))
        {
            Console.Error.WriteLine("[A7] " + reason);
            var blocked = report.Begin("A7.0", "Поиск вендорского interop (API5 + API7)");
            blocked.Fail(reason ?? "Interop не найден.");
            blocked.Data["probed"] = InteropResolver.ProbedDirectories;
            report.Environment["interop_resolved"] = false;
            Write(report, options);
            return 3;
        }

        Console.WriteLine($"Interop resolution: {InteropResolver.ResolvedDirectory}");

        // The exact command line that produced this report, in the report. A probe whose result can
        // only be reproduced by someone who remembers how it was launched is not evidence.
        report.Environment["reproduce"] =
            "dotnet build tools\\KompasMcp.Api7Probe\\KompasMcp.Api7Probe.csproj -c Debug -p:Platform=x64"
            + " && tools\\KompasMcp.Api7Probe\\bin\\x64\\Debug\\net10.0-windows\\KompasMcp.Api7Probe.exe"
            + (options.PassportOnly ? " --passport" : string.Empty);
        report.Environment["reproduce_note"] =
            "Сборка обязана быть поудолевым проектом с -p:Platform=x64: сборка решения оставляет рядом с бинарями "
            + "устаревые копии (docs/STATUS.md), а dotnet run --no-build берёт AnyCPU-выход.";

        using var pump = new StaPump("api7-probe-sta");
        pump.Start();

        try
        {
            pump.Run(() => Passport.Collect(report, options));
            if (options.PassportOnly)
            {
                // Паспорт среды — без КОМПАС-документов.
            }
            else if (options.Lifecycle)
            {
                var lifecycle = new SketchLifecycleProbe(report, options);
                pump.Run(() => lifecycle.Run());
            }
            else if (options.Extrusion)
            {
                var extrusion = new ExtrusionSketchProbe(report, options);
                pump.Run(() => extrusion.Run());
            }
            else if (options.Chamfer)
            {
                var chamfer = new ChamferProbe(report, options);

                // The attribution run measures a whole ladder of candidate routes and must report all
                // of them; the plain run stops at the first match.
                pump.Run(() => chamfer.Run());
            }
            else if (options.SketchReopen)
            {
                var sketchReopen = new SketchGeometryReopenProbe(report, options);
                pump.Run(() => sketchReopen.Run());
            }
            else if (options.FilletEdgeSet)
            {
                var filletEdgeSet = new FilletEdgeSetProbe(report, options);
                pump.Run(() => filletEdgeSet.Run());
            }
            else if (options.FilletBaseObjects)
            {
                var baseObjects = new FilletBaseObjectsProbe(report, options);
                pump.Run(() => baseObjects.Run());
                if (options.KeepRunning)
                {
                    FilletBaseObjectsProbe.Flush(report, options);
                }
            }
            else if (options.Boolean)
            {
                var bodyOps = new BodyOpsProbe(report, options);
                pump.Run(() =>
                {
                    bodyOps.Run();
                    if (options.KeepRunning)
                    {
                        BodyOpsProbe.Flush(report, options);
                    }
                });
            }
            else if (options.Split)
            {
                var split = new SplitProbe(report, options);
                pump.Run(() =>
                {
                    split.Run();
                    if (options.KeepRunning)
                    {
                        SplitProbe.Flush(report, options);
                    }
                });
            }
            else if (options.Reposition)
            {
                var reposition = new RepositionProbe(report, options);
                pump.Run(() =>
                {
                    reposition.Run();
                    if (options.KeepRunning)
                    {
                        RepositionProbe.Flush(report, options);
                    }
                });
            }
            else if (options.TreeLifecycle)
            {
                var tree = new TreeLifecycleProbe(report, options);
                pump.Run(() =>
                {
                    tree.Run();
                    if (options.KeepRunning)
                    {
                        TreeLifecycleProbe.Flush(report, options);
                    }
                });
            }
            else if (options.BossFuse)
            {
                var bossFuse = new BossFuseProbe(report, options);
                pump.Run(() =>
                {
                    bossFuse.Run();
                    if (options.KeepRunning)
                    {
                        BossFuseProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VerifyM3d is not null)
            {
                var verify = new M3dVerificationProbe(report, options);
                pump.Run(() => verify.Run(options.VerifyM3d!));
            }
            else if (options.FullTurn)
            {
                var fullTurn = new FullTurnProbe(report, options);
                // A diagnostic run must leave its observations on disk even if a COM call hangs the
                // apartment afterwards: --keep writes the report, then parks the process so the run
                // is not torn down mid-measurement.
                pump.Run(() =>
                {
                    fullTurn.Run();
                    if (options.KeepRunning)
                    {
                        FullTurnProbe.Flush(report, options);
                    }
                });
            }
            else if (options.HoleModes)
            {
                var holeModes = new HoleModesProbe(report, options);
                pump.Run(() => holeModes.Run());
            }
            else if (options.HoleTree)
            {
                var holeTree = new HoleTreeProbe(report, options);
                pump.Run(() => holeTree.Run());
            }
            else if (options.SketchDefinition)
            {
                var sketchDefinition = new SketchDefinitionProbe(report, options);
                pump.Run(() => sketchDefinition.Run());
            }
            else if (options.Identity)
            {
                var identity = new IdentityProbe(report, options);
                pump.Run(() =>
                {
                    identity.Run();
                    if (options.KeepRunning)
                    {
                        IdentityProbe.Flush(report, options);
                    }
                });
            }
            else if (options.Union)
            {
                var union = new UnionProbe(report, options);
                pump.Run(() =>
                {
                    union.Run();
                    if (options.KeepRunning)
                    {
                        UnionProbe.Flush(report, options);
                    }
                });
            }
            else if (options.B5)
            {
                var b5 = new B5Probe(report, options);
                pump.Run(() =>
                {
                    b5.Run();
                    if (options.KeepRunning)
                    {
                        B5Probe.Flush(report, options);
                    }
                });
            }
            else if (options.CutArea)
            {
                var cutArea = new CutAreaProbe(report, options);
                pump.Run(() =>
                {
                    cutArea.Run();
                    if (options.KeepRunning)
                    {
                        CutAreaProbe.Flush(report, options);
                    }
                });
            }
            else if (options.RepositionOrder)
            {
                var repositionOrder = new RepositionOrderProbe(report, options);
                pump.Run(() =>
                {
                    repositionOrder.Run();
                    if (options.KeepRunning)
                    {
                        RepositionOrderProbe.Flush(report, options);
                    }
                });
            }
            else if (options.SketchPlane)
            {
                var sketchPlane = new SketchPlaneProbe(report, options);
                pump.Run(() =>
                {
                    sketchPlane.Run();
                    if (options.KeepRunning)
                    {
                        SketchPlaneProbe.Flush(report, options);
                    }
                });
            }
            else if (options.RepositionParams)
            {
                var repositionParams = new RepositionParamsProbe(report, options);
                pump.Run(() =>
                {
                    repositionParams.Run();
                    if (options.KeepRunning)
                    {
                        RepositionParamsProbe.Flush(report, options);
                    }
                });
            }
            else if (options.RepositionRead)
            {
                var repositionRead = new RepositionReadProbe(report, options);
                pump.Run(() =>
                {
                    repositionRead.Run();
                    if (options.KeepRunning)
                    {
                        RepositionReadProbe.Flush(report, options);
                    }
                });
            }
            else if (options.Rotation)
            {
                var rotation = new RotationProbe(report, options);
                // A diagnostic run must leave its observations on disk even if a COM call hangs the
                // apartment afterwards: --keep writes the report, then parks the process so the run
                // is not torn down mid-measurement.
                pump.Run(() =>
                {
                    rotation.Run();
                    if (options.KeepRunning)
                    {
                        RotationProbe.Flush(report, options);
                    }
                });
                if (options.KeepRunning)
                {
                    Console.WriteLine("[api7-probe] --keep: отчёт записан, процесс ждёт Enter.");
                    Console.ReadLine();
                }
            }
            else
            {
                // The connect call happens inside the queued lambda on purpose: if Main's body
                // mentioned a КОМПАС type, the JIT would try to resolve the interop assembly while
                // compiling Main — before the resolver above had a chance to install.
                var probe = new HoleProbe(report, options);
                pump.Run(() => probe.Run());
                report.Environment["transferinterface_transitions"] = probe.Transitions;
                if (probe.SavedPath is not null)
                {
                    report.Environment["test_model_path"] = probe.SavedPath;
                }
            }
        }
        catch (Exception ex)
        {
            var crash = report.Begin("A7.E", "Необработанное исключение зонда");
            crash.Fail("Зонд упал: " + ex.GetType().Name + ": " + ex.Message);
            crash.Errors.Add(ex.ToString());
            Console.Error.WriteLine("[A7] UNHANDLED: " + ex);
            Write(report, options);
        }

        clock.Stop();
        report.Environment["probe_total_ms"] = clock.ElapsedMilliseconds;
        report.Environment["sta_executed"] = pump.Executed;
        report.Environment["sta_messages_pumped"] = pump.MessagesPumped;
        return Write(report, options);
    }

    private static int Write(ProbeReport report, Options options)
    {
        // Three different questions, three different artefacts: the passport, the definitive
        // measurement run, and the attribution run that keeps going after the first match. They must
        // not overwrite one another.
        var stem = options.PassportOnly ? "env-passport"
            : options.Lifecycle ? "sketch-lifecycle"
            : options.Extrusion ? "extrusion-sketch"
            : options.Chamfer ? "chamfer"
            : options.SketchReopen ? "sketch-geometry-reopen"
            : options.FilletEdgeSet ? "fillet-edge-set"
            : options.FilletBaseObjects ? "fillet-base-objects"
            : options.Boolean ? "boolean-ops"
            : options.Split ? "split-plane"
            : options.Reposition ? "reposition"
            : options.TreeLifecycle ? "tree-lifecycle"
            : options.Rotation ? "rotation"
            : options.BossFuse ? "boss-fuse"
            : options.VerifyM3d is not null ? "m3d-verification"
            : options.FullTurn ? "full-turn"
            : options.HoleModes ? "hole-modes"
            : options.SketchDefinition ? "sketch-definition"
            : options.Identity ? "feature-identity"
            : options.Union ? "disconnected-union"
            : options.B5 ? "b5-sweep-loft-shell"
            : options.RepositionRead ? "reposition-read"
            : options.RepositionParams ? "reposition-params"
            : options.CutArea ? "cut-area"
            : options.RepositionOrder ? "reposition-order"
            : options.CollectControls ? "api7-attribution"
            : "api7-probe-report";
        var jsonPath = Path.Combine(options.ReportDir, stem + ".json");
        var markdownPath = Path.Combine(options.ReportDir, stem + ".md");
        report.Write(jsonPath, markdownPath);

        Console.WriteLine();
        Console.WriteLine($"Отчёт: {markdownPath}");
        Console.WriteLine($"Отчёт (JSON): {jsonPath}");
        foreach (var step in report.Steps)
        {
            Console.WriteLine($"  [{step.Verdict.ToString().ToUpperInvariant(),-7}] {step.Id} {step.Title}");
            if (step.Conclusion is not null)
            {
                Console.WriteLine($"            → {step.Conclusion}");
            }
        }

        var failed = report.Steps.Any(s => s.Verdict == Verdict.Fail);
        return failed ? 2 : 0;
    }
}

/// <summary>Command line of the probe. Deliberately tiny: it is a measurement instrument.</summary>
public sealed class Options
{
    public required string ProjectRoot { get; init; }

    public required string WorkDir { get; set; }

    public required string ReportDir { get; init; }

    /// <summary>
    /// The КОМПАС installation root, so a step can survey the shipped sample models instead of
    /// hard-coding paths. Supplied at build time as <c>-p:KompasRoot=…</c>.
    /// </summary>
    public required string KompasRoot { get; init; }

    public bool PassportOnly { get; private set; }

    public bool KeepRunning { get; private set; }

    /// <summary>--controls: run the whole candidate ladder instead of stopping at the first match.</summary>
    public bool CollectControls { get; private set; }

    /// <summary>--lifecycle: эскиз после reopen и жизненный цикл признака (проба L).</summary>
    public bool Lifecycle { get; private set; }

    /// <summary>--extrusion: смена опорного эскиза признака через API7 (проба E).</summary>
    public bool Extrusion { get; private set; }

    /// <summary>--chamfer: фаска SM-11 — маршруты API5 и API7, единицы угла, направление, reopen (проба F).</summary>
    public bool Chamfer { get; private set; }

    /// <summary>--sketch-reopen: правка геометрии существующего эскиза в документе после reopen (проба G).</summary>
    public bool SketchReopen { get; private set; }

    /// <summary>--fillet-edge-set: правка набора рёбер существующего скругления (проба H).</summary>
    public bool FilletEdgeSet { get; private set; }

    /// <summary>
    /// --fillet-base-objects: входы САМОГО признака скругления через <c>IFillet.BaseObjects</c> на
    /// живом признаке после reopen (проба H-2, решающий опыт задания SM-09 §2).
    /// </summary>
    public bool FilletBaseObjects { get; private set; }

    /// <summary>--rotation: вращение SM-03 — номер типа, осевая линия эскиза, полный и частичный оборот (проба R).</summary>
    public bool Rotation { get; private set; }

    /// <summary>
    /// --full-turn: правда ли, что развёртка вращения насыщается на 180° (проба F2).
    /// </summary>
    /// <remarks>
    /// Отдельный вход, потому что вопрос этот — о ПРОДУКТЕ, а не о пробе R. Матрица углов R26.angles
    /// измеряла запись <c>Angle[true]=360, Angle[false]=0</c> с <c>dtNormal</c> и, кроме неё, только
    /// <c>CutOffByPoint</c>; из неё был сделан вывод «развёртка насыщается на 180°», попавший в схему
    /// и в адаптер как факт. Здесь перебираются пары <c>Angle[true]/Angle[false]</c>, включая ту, что
    /// стоит в файле поставки <c>BEARING 410</c> (<c>180/180</c>, <c>dtBoth</c>), и профиль устроен
    /// так, что полный оборот и полуоборот РАЗЛИЧАЮТСЯ ОБЪЁМОМ.
    /// </remarks>
    public bool FullTurn { get; private set; }

    /// <summary>
    /// <c>--verify-m3d &lt;path&gt;</c>: открыть готовый .m3d в НОВОМ сеансе и прочитать его
    /// геометрию. Отдельный режим существует ровно потому, что «предмет проверен» и «прибор описал
    /// себя» — разные утверждения: сохранённый файл читает другой процесс, который не знает, как
    /// файл построен (дисциплина измерений, класс 16).
    /// </summary>
    public string? VerifyM3d { get; private set; }

    /// <summary>
    /// <c>--boolean</c>: булевы операции над телами SM-15 через API7 <c>IBooleans</c> — явные
    /// тело-цель и набор инструментов, вид операции, политика сохранения, несколько инструментов
    /// одним признаком, несвязный результат, отрицательные случаи и reopen.
    /// </summary>
    public bool Boolean { get; private set; }

    /// <summary>
    /// <c>--split</c>: разделение тела плоскостью SM-16 и отсечение по одну сторону — сохраняются
    /// ли все части, что выбирает <c>ICut.Direction</c>, вырожденные постановки и reopen.
    /// </summary>
    public bool Split { get; private set; }

    /// <summary>
    /// <c>--reposition</c>: перенос и поворот тела SM-17 через <c>IBodyReposition</c> — каким членом
    /// пишется положение (<c>Position</c> отдаёт только propget, OQ-A19), знак и центр поворота,
    /// правка существующего признака без накопления смещения.
    /// </summary>
    public bool Reposition { get; private set; }

    /// <summary>
    /// <c>--tree</c>: как признак B3 адресуется в ЖИЗНЕННОМ ЦИКЛЕ, а не только в момент создания.
    /// Создание всех четырёх семейств измерено фабриками API7 (<c>Booleans</c>, <c>SplitSolids</c>,
    /// <c>Cuts</c>, <c>BodyRepositions</c>), но подавление, удаление и <c>list_features</c> работают
    /// через дерево признаков API5. Здесь измеряется, попадает ли туда объект API7, и есть ли у
    /// булевой операции и отсечения родной маршрут API5 (<c>o3d_aggregate=69</c>,
    /// <c>o3d_cutByPlane=50</c>), дающий настоящий <c>ksEntity</c>.
    /// </summary>
    public bool TreeLifecycle { get; private set; }

    /// <summary>
    /// <c>--boss-fuse</c>: сращивается ли родное вращение-бобышка с существующим телом (§3 наряда).
    /// </summary>
    public bool BossFuse { get; private set; }

    /// <summary>--hole-modes: четыре режима родных отверстий SM-07 — цековка, зенковка, плоское дно, положение (проба M).</summary>
    public bool HoleModes { get; private set; }

    /// <summary>--hole-tree: под каким номером признак отверстия виден в дереве (проба N).</summary>
    public bool HoleTree { get; private set; }

    /// <summary>
    /// <c>--identity</c>: чем ОТЛИЧАЕТСЯ только что созданный признак от уже существующего, когда
    /// отображаемое имя у них одинаковое. Клиентская приёмка 19.09.2026 показала, что второй
    /// признак изменения положения получает то же имя, и правило «новое имя» его отбрасывает.
    /// </summary>
    public bool Identity { get; private set; }

    /// <summary>
    /// <c>--union</c>: граница применимости обычного объединения — условие применимости из справки
    /// самого продукта, ответ ядра на входы вне условия и контроль на контактных телах.
    /// </summary>
    public bool Union { get; private set; }

    /// <summary>
    /// <c>--cut-area</c>: «Область применения» отсечения по плоскости — существует ли маршрут,
    /// направляющий операцию на ВЫБРАННЫЕ тела (проба CA, приоритет 1 наряда 19.09.2026). Имена
    /// членов читаются из установленной библиотеки типов, а не берутся из списка кандидатов.
    /// </summary>
    public bool CutArea { get; private set; }

    /// <summary>
    /// <c>--reposition-order</c>: почему признак, стоящий на 180°, читается как ПЕРЕНОС (проба RO,
    /// приоритет 3 наряда 19.09.2026). Измеряется гипотеза о ПОРЯДКЕ коллекции API7: сопоставление
    /// признака дерева с элементом коллекции идёт по порядковому номеру, а проверяется только
    /// совпадение ЧИСЛА элементов, поэтому подавление и восстановление признака может сдвинуть
    /// порядок — и чтение опишет соседа.
    /// </summary>
    public bool RepositionOrder { get; private set; }

    /// <summary>
    /// <c>--sketch-plane</c>: смена ОПОРНОЙ плоскости существующего эскиза документированным
    /// <c>ksSketchDefinition.SetPlane</c> и чтение её обратно <c>GetPlane</c> (проба SP, наряд
    /// <c>AUX_SKETCH_PLANE_EDIT_DEVELOPER_PROMPT.md</c> §3.1). Отдельный вход, потому что вопрос
    /// задаётся ЯДРУ до правки продукта: маршрута в продукте ещё нет, и мерить его продуктом нельзя.
    /// </summary>
    public bool SketchPlane { get; private set; }

    /// <summary>
    /// <c>--b5</c>: калибровка трёх семейств последней обязательной очереди — кинематическая операция
    /// (SM-04), по сечениям (SM-05), оболочка (SM-13). Закрывает измерением OQ-A1 и OQ-A12.
    /// </summary>
    public bool B5 { get; private set; }

    /// <summary>
    /// <c>--reposition-read</c>: читается ли вектор переноса признака изменения положения, и каким
    /// членом. Перечень членов берётся из библиотеки типов продукта, а не из списка кандидатов.
    /// </summary>
    public bool RepositionRead { get; private set; }

    /// <summary>
    /// <c>--reposition-params</c>: читаются ли ВХОДЫ признака изменения положения по
    /// ДОКУМЕНТИРОВАННОМУ маршруту API7 — <c>Position</c> → <c>ILocalCoordinateSystem</c> →
    /// <c>ParameterType</c>/<c>Parameters</c> → <c>IPoint3DParamDisplace.DX/DY/DZ</c> и
    /// <c>RepositionCentre</c> → <c>IPoint3D.X/Y/Z</c>. Отдельная проба от <c>--reposition-read</c>
    /// потому, что та опрашивала объект поздним связыванием по умолчательному интерфейсу класса и
    /// типизированно только по <c>ILocalCoordinateSystem</c>, то есть документированный интерфейс
    /// ПАРАМЕТРОВ не спрашивала вовсе.
    /// </summary>
    public bool RepositionParams { get; private set; }

    /// <summary>--sketch-definition: определённость эскиза «+ / − / !» из API (проба S).</summary>
    public bool SketchDefinition { get; private set; }

    /// <summary>
    /// PID of the КОМПАС instance the probe launched, written back by the step that measured the
    /// process diff. <c>null</c> means "not attributed yet" — a distinct state from a PID of zero,
    /// and the step that needs it says so rather than comparing against a placeholder.
    /// </summary>
    public int? ProcessId { get; set; }

    public static Options Parse(string[] args)
    {
        var root = FindProjectRoot();
        string? workOverride = null;

        var passportOnly = false;
        var keep = false;
        var controls = false;
        var lifecycle = false;
        var extrusion = false;
        var chamfer = false;
        var sketchReopen = false;
        var filletEdgeSet = false;
        var rotation = false;
        var fullTurn = false;
            string? verifyM3d = null;
            var bossFuse = false;
        var boolean = false;
        var split = false;
        var reposition = false;
        var treeLifecycle = false;
        var holeModes = false;
        var holeTree = false;
        var filletBaseObjects = false;
        var sketchDefinition = false;
        var identity = false;
        var union = false;
        var b5 = false;
        var repositionRead = false;
        var repositionParams = false;
        var cutArea = false;
        var repositionOrder = false;
        var sketchPlane = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--passport":
                    passportOnly = true;
                    break;
                case "--keep":
                    keep = true;
                    break;
                case "--controls":
                    controls = true;
                    break;
                case "--lifecycle":
                    lifecycle = true;
                    break;
                case "--extrusion":
                    extrusion = true;
                    break;
                case "--chamfer":
                    chamfer = true;
                    break;
                case "--sketch-reopen":
                    sketchReopen = true;
                    break;
                case "--fillet-edge-set":
                    filletEdgeSet = true;
                    break;
                case "--fillet-base-objects":
                    filletBaseObjects = true;
                    break;
                case "--rotation":
                    rotation = true;
                    break;
                case "--full-turn":
                    fullTurn = true;
                    break;
                case "--boss-fuse":
                    bossFuse = true;
                    break;
                case "--boolean":
                    boolean = true;
                    break;
                case "--split":
                    split = true;
                    break;
                case "--reposition":
                    reposition = true;
                    break;
                case "--tree":
                    treeLifecycle = true;
                    break;
                case "--verify-m3d" when i + 1 < args.Length:
                    verifyM3d = args[++i];
                    break;
                case "--hole-modes":
                    holeModes = true;
                    break;
                case "--hole-tree":
                    holeTree = true;
                    break;
                case "--sketch-definition":
                    sketchDefinition = true;
                    break;
                case "--identity":
                    identity = true;
                    break;
                case "--union":
                    union = true;
                    break;
                case "--b5":
                    b5 = true;
                    break;
                case "--reposition-read":
                    repositionRead = true;
                    break;
                case "--reposition-params":
                    repositionParams = true;
                    break;
                case "--cut-area":
                    cutArea = true;
                    break;
                case "--reposition-order":
                    repositionOrder = true;
                    break;
                case "--sketch-plane":
                    sketchPlane = true;
                    break;
                case "--work" when i + 1 < args.Length:
                    workOverride = args[++i];
                    break;
                case "--help":
                    Console.WriteLine("KompasMcp.Api7Probe [--passport] [--controls] [--lifecycle] [--extrusion] [--chamfer] [--sketch-reopen] [--fillet-edge-set] [--fillet-base-objects] [--boolean] [--split] [--reposition] [--tree] [--rotation] [--full-turn] [--boss-fuse] [--verify-m3d PATH] [--hole-modes] [--hole-tree] [--sketch-definition] [--identity] [--union] [--b5] [--reposition-read] [--reposition-params] [--cut-area] [--reposition-order] [--sketch-plane] [--work DIR] [--keep]");
                    Environment.Exit(0);
                    break;
            }
        }

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stem = passportOnly ? "env-passport" : lifecycle ? "sketch-lifecycle"
            : extrusion ? "extrusion-sketch" : chamfer ? "chamfer" : sketchReopen ? "sketch-geometry-reopen"
            : filletEdgeSet ? "fillet-edge-set"
            : filletBaseObjects ? "fillet-base-objects"
            : boolean ? "boolean-ops"
            : split ? "split-plane"
            : reposition ? "reposition"
            : rotation ? "rotation"
            : fullTurn ? "full-turn"
            : holeModes ? "hole-modes"
            : holeTree ? "hole-tree"
            : sketchDefinition ? "sketch-definition"
            : identity ? "feature-identity"
            : union ? "disconnected-union"
            : b5 ? "b5-sweep-loft-shell"
            : repositionParams ? "reposition-params"
            : repositionRead ? "reposition-read"
            : cutArea ? "cut-area"
            : repositionOrder ? "reposition-order"
            : sketchPlane ? "sketch-plane"
            : controls ? "api7-attribution" : "api7-probe-report";
        var work = workOverride ?? Path.Combine(root, "scratch", $"api7-{stem}-{runId}");
        var reportDir = Path.Combine(root, "docs", "acceptance", "api7");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(reportDir);

        return new Options
        {
            ProjectRoot = root,
            WorkDir = Path.GetFullPath(work),
            ReportDir = Path.GetFullPath(reportDir),
            KompasRoot = ReadKompasRoot(),
            PassportOnly = passportOnly,
            KeepRunning = keep,
            CollectControls = controls,
            Lifecycle = lifecycle,
            Extrusion = extrusion,
            Chamfer = chamfer,
            SketchReopen = sketchReopen,
            FilletEdgeSet = filletEdgeSet,
            FilletBaseObjects = filletBaseObjects,
            Boolean = boolean,
            Split = split,
            Reposition = reposition,
            TreeLifecycle = treeLifecycle,
            Rotation = rotation,
            FullTurn = fullTurn,
            VerifyM3d = verifyM3d,
            BossFuse = bossFuse,
            HoleModes = holeModes,
            HoleTree = holeTree,
            SketchDefinition = sketchDefinition,
            Identity = identity,
            Union = union,
            B5 = b5,
            RepositionRead = repositionRead,
            RepositionParams = repositionParams,
            CutArea = cutArea,
            RepositionOrder = repositionOrder,
            SketchPlane = sketchPlane,
        };
    }

    /// <summary>
    /// The installation root baked in at build time as <c>AssemblyMetadata("KompasRoot")</c>.
    /// </summary>
    /// <remarks>
    /// Returns <c>""</c> rather than guessing when the attribute is absent, so a step that needs the
    /// installation says it could not find it instead of surveying a wrong directory.
    /// </remarks>
    private static string ReadKompasRoot()
    {
        var attribute = typeof(Options).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "KompasRoot");
        return attribute?.Value ?? string.Empty;
    }

    /// <summary>
    /// Walks upward from the working directory looking for the repository root, which is the folder
    /// that carries <c>KompasMcp.sln</c>. Nothing is assumed about where the probe was launched from.
    /// </summary>
    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "KompasMcp.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
