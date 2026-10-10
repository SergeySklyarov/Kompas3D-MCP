using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace KompasMcp.Api7Probe;

/// <summary>Single entry point of the API7 research probe (ADR-003 §3).</summary>
/// <remarks>Everything that touches KOMPAS runs on one STA thread with a real message pump
/// (<see cref="StaPump"/>), because that is the only configuration the shipping adapter is allowed
/// to use and a route that worked on a pool thread would prove nothing about the product.</remarks>
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
                                                        : options.Mate
                                                            ? "Проба M — сопряжения сборки: грани компонентов как IModelObject и создание сопряжения"
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
        Console.WriteLine($"Режим: passportOnly={options.PassportOnly}, lifecycle={options.Lifecycle}, extrusion={options.Extrusion}, chamfer={options.Chamfer}, sketchReopen={options.SketchReopen}, filletEdgeSet={options.FilletEdgeSet}, filletBaseObjects={options.FilletBaseObjects}, mate={options.Mate}, sketchDefinition={options.SketchDefinition}, controls={options.CollectControls}, keep={options.KeepRunning}, отчёты: {options.ReportDir}");

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
                // Environment passport — no KOMPAS documents.
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
            else if (options.Mate)
            {
                var mate = new MateProbe(report, options);
                pump.Run(() => mate.Run());
                if (options.KeepRunning)
                {
                    MateProbe.Flush(report, options);
                    Console.WriteLine("Отчёт записан; процесс оставлен для диагностики (--keep).");
                    return 0;
                }
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
            else if (options.VmReference is not null)
            {
                var vmReference = new VmReferenceProbe(report, options);
                pump.Run(() =>
                {
                    vmReference.Run(options.VmReference!);
                    if (options.KeepRunning)
                    {
                        VmReferenceProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VmVariableRoute)
            {
                var vmRoute = new VmVariableRouteProbe(report, options);
                pump.Run(() =>
                {
                    vmRoute.Run();
                    if (options.KeepRunning)
                    {
                        VmVariableRouteProbe.Flush(report, options);
                    }
                });
            }
            else if (options.G3ParameterRoute)
            {
                var g3 = new G3ParameterRouteProbe(report, options);
                pump.Run(() =>
                {
                    g3.Run();
                    if (options.KeepRunning)
                    {
                        G3ParameterRouteProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VmControlExpression)
            {
                var vmControl = new VmControlExpressionProbe(report, options);
                pump.Run(() =>
                {
                    vmControl.Run();
                    if (options.KeepRunning)
                    {
                        VmControlExpressionProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VmOpen is not null)
            {
                var vmOpen = new VmOpenRebuildProbe(report, options);
                pump.Run(() =>
                {
                    vmOpen.Run(options.VmOpen!);
                    if (options.KeepRunning)
                    {
                        VmOpenRebuildProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VmDensityUnits)
            {
                var vmDensity = new VmDensityUnitsProbe(report, options);
                pump.Run(() =>
                {
                    vmDensity.Run();
                    if (options.KeepRunning)
                    {
                        VmDensityUnitsProbe.Flush(report, options);
                    }
                });
            }
            else if (options.VmDensityMci)
            {
                var vmDensityMci = new VmDensityMciProbe(report, options);
                pump.Run(() =>
                {
                    vmDensityMci.Run();
                    if (options.KeepRunning)
                    {
                        VmDensityMciProbe.Flush(report, options);
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
                // mentioned a KOMPAS type, the JIT would try to resolve the interop assembly while
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
        // MEASURED: modes missing from this list shared the fallback stem, so their reports overwrote
        // one another inside that single file. Every mode now names its own artefact.
        var stem = options.PassportOnly ? "env-passport"
            : options.Lifecycle ? "sketch-lifecycle"
            : options.Extrusion ? "extrusion-sketch"
            : options.Chamfer ? "chamfer"
            : options.SketchReopen ? "sketch-geometry-reopen"
            : options.FilletEdgeSet ? "fillet-edge-set"
            : options.FilletBaseObjects ? "fillet-base-objects"
            : options.Mate ? "mate"
            : options.Boolean ? "boolean-ops"
            : options.Split ? "split-plane"
            : options.Reposition ? "reposition"
            : options.TreeLifecycle ? "tree-lifecycle"
            : options.Rotation ? "rotation"
            : options.BossFuse ? "boss-fuse"
            : options.VerifyM3d is not null ? "m3d-verification"
            : options.FullTurn ? "full-turn"
            : options.HoleModes ? "hole-modes"
            : options.HoleTree ? "hole-tree"
            : options.SketchDefinition ? "sketch-definition"
            : options.Identity ? "feature-identity"
            : options.Union ? "disconnected-union"
            : options.B5 ? "b5-sweep-loft-shell"
            : options.SketchPlane ? "sketch-plane"
            : options.VmDensityMci ? "vm-density-mci"
            : options.RepositionRead ? "reposition-read"
            : options.RepositionParams ? "reposition-params"
            : options.CutArea ? "cut-area"
            : options.RepositionOrder ? "reposition-order"
            : options.VmOpen is not null ? "vm-open-rebuild"
            : options.VmReference is not null ? "vm-reference"
            : options.VmVariableRoute ? "vm-variable-route"
            : options.G3ParameterRoute ? "g3-parameter-route"
            : options.VmControlExpression ? "vm-control-expression"
            : options.VmDensityUnits ? "vm-density-units"
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

    /// <summary>The KOMPAS installation root, so a step can survey the shipped sample models instead of
    /// hard-coding paths. Supplied at build time as <c>-p:KompasRoot=…</c>.</summary>
    public required string KompasRoot { get; init; }

    public bool PassportOnly { get; private set; }

    public bool KeepRunning { get; private set; }

    /// <summary>--controls: run the whole candidate ladder instead of stopping at the first match.</summary>
    public bool CollectControls { get; private set; }

    /// <summary>--lifecycle: a sketch after reopen and the feature lifecycle (probe L).</summary>
    public bool Lifecycle { get; private set; }

    /// <summary>--extrusion: changing a feature's support sketch through API7 (probe E).</summary>
    public bool Extrusion { get; private set; }

    /// <summary>--chamfer: SM-11 chamfer — API5 and API7 routes, angle units, direction, reopen (probe F).</summary>
    public bool Chamfer { get; private set; }

    /// <summary>--sketch-reopen: editing the geometry of an existing sketch in a reopened document (probe G).</summary>
    public bool SketchReopen { get; private set; }

    /// <summary>--fillet-edge-set: editing the edge set of an existing fillet (probe H).</summary>
    public bool FilletEdgeSet { get; private set; }

    /// <summary>--fillet-base-objects: the inputs of the fillet feature ITSELF through
    /// <c>IFillet.BaseObjects</c> on a live feature after reopen (probe H-2, the decisive experiment of
    /// task SM-09 §2). History: docs/decisions/probes.md#fillet-base-objects</summary>
    public bool FilletBaseObjects { get; private set; }

    /// <summary>--rotation: SM-03 rotation — type number, sketch axis line, full and partial turn (probe R).</summary>
    public bool Rotation { get; private set; }

    /// <summary>--mate: assembly mates — are COMPONENT FACES addressable and is a mate created
    /// (probe M, the entry of the next block after C1). History: docs/decisions/probes.md#mate</summary>
    public bool Mate { get; private set; }

    /// <summary>--full-turn: is it true that a rotation sweep saturates at 180° (probe F2).</summary>
    /// <remarks>DOC: a separate entry because the question is about the PRODUCT, not about probe R.
    /// The angle matrix R26.angles measured the write <c>Angle[true]=360, Angle[false]=0</c> with
    /// <c>dtNormal</c> and, besides it, only <c>CutOffByPoint</c>; from it the conclusion "the sweep
    /// saturates at 180°" was drawn and reached the schema and the adapter as a fact. Here the
    /// <c>Angle[true]/Angle[false]</c> pairs are enumerated, including the one in the shipped file
    /// <c>BEARING 410</c> (<c>180/180</c>, <c>dtBoth</c>), and the profile is arranged so that a full
    /// turn and a half turn DIFFER IN VOLUME. History: docs/decisions/probes.md#full-turn</remarks>
    public bool FullTurn { get; private set; }

    /// <summary><c>--verify-m3d &lt;path&gt;</c>: open a ready .m3d in a NEW session and read its
    /// geometry. The separate mode exists precisely because "the subject was verified" and "the
    /// instrument described itself" are different claims: the saved file is read by another process that
    /// does not know how the file was built (measurement discipline, class 16).</summary>
    public string? VerifyM3d { get; private set; }

    /// <summary><c>--boolean</c>: boolean operations on SM-15 bodies through API7 <c>IBooleans</c> —
    /// explicit target body and tool set, operation kind, retention policy, several tools in one
    /// feature, a disconnected result, negative cases and reopen.</summary>
    public bool Boolean { get; private set; }

    /// <summary><c>--split</c>: splitting an SM-16 body by a plane and cutting to one side — are all
    /// parts kept, what does <c>ICut.Direction</c> select, degenerate setups and reopen.
    /// History: docs/decisions/probes.md#split</summary>
    public bool Split { get; private set; }

    /// <summary><c>--reposition</c>: translation and rotation of an SM-17 body through
    /// <c>IBodyReposition</c> — which member writes the position (<c>Position</c> is propget-only,
    /// OQ-A19), the sign and centre of rotation, editing an existing feature without accumulating the
    /// offset. History: docs/decisions/probes.md#rp-open</summary>
    public bool Reposition { get; private set; }

    /// <summary><c>--tree</c>: how a B3 feature is addressed over its LIFECYCLE, not only at creation
    /// time. The creation of all four families was measured with the API7 factories (<c>Booleans</c>,
    /// <c>SplitSolids</c>, <c>Cuts</c>, <c>BodyRepositions</c>), but suppression, deletion and
    /// <c>list_features</c> go through the API5 feature tree. This measures whether an API7 object lands
    /// there, and whether the boolean operation and the cut have a native API5 route
    /// (<c>o3d_aggregate=69</c>, <c>o3d_cutByPlane=50</c>) that yields a real <c>ksEntity</c>.</summary>
    public bool TreeLifecycle { get; private set; }

    /// <summary><c>--boss-fuse</c>: does a native boss-rotation fuse with an existing body (order
    /// §3).</summary>
    public bool BossFuse { get; private set; }

    /// <summary>--hole-modes: the four modes of native SM-07 holes — counterbore, countersink,
    /// flat bottom, position (probe M).</summary>
    public bool HoleModes { get; private set; }

    /// <summary>--hole-tree: under which number the hole feature appears in the tree (probe N).</summary>
    public bool HoleTree { get; private set; }

    /// <summary><c>--identity</c>: how a just-created feature DIFFERS from an existing one when the
    /// two share the same display name. An acceptance showed that a second reposition feature gets the
    /// same name, and the "new name" rule drops it.
    /// History: docs/decisions/probes.md#identity</summary>
    public bool Identity { get; private set; }

    /// <summary><c>--union</c>: the applicability boundary of ordinary union — the applicability
    /// condition from the product's own help, the kernel's answer for inputs outside the condition, and
    /// a control on touching bodies. History: docs/decisions/probes.md#bo-contact</summary>
    public bool Union { get; private set; }

    /// <summary><c>--cut-area</c>: the "application area" of a plane cut — does a route exist that
    /// directs the operation at SELECTED bodies (probe CA, the first priority of its order). Member
    /// names are read from the installed type library, not taken from a candidate list.</summary>
    public bool CutArea { get; private set; }

    /// <summary><c>--reposition-order</c>: why a feature at 180° is read as a TRANSLATION (probe RO,
    /// priority 3 of its order). The hypothesis under test is about the ORDER of the API7
    /// collection: a tree feature is matched to a collection element by ordinal number, while only the
    /// NUMBER of elements is checked, so suppressing and restoring a feature can shift the order — and
    /// the read describes the neighbour. History: docs/decisions/probes.md#ro-order</summary>
    public bool RepositionOrder { get; private set; }

    /// <summary><c>--sketch-plane</c>: changing an existing sketch's BASE plane via the documented
    /// <c>ksSketchDefinition.SetPlane</c> and reading it back with <c>GetPlane</c> (probe SP, order
    /// <c>AUX_SKETCH_PLANE_EDIT_DEVELOPER_PROMPT.md</c> §3.1). A separate entry because the question is
    /// put to the KERNEL before the product is changed: the route does not exist in the product yet, and
    /// it cannot be measured with the product. History: docs/decisions/probes.md#sp-plane</summary>
    public bool SketchPlane { get; private set; }

    /// <summary><c>--b5</c>: calibration of the last mandatory queue's three families — sweep
    /// (SM-04), loft (SM-05), shell (SM-13). Closes OQ-A1 and OQ-A12 by measurement.
    /// History: docs/decisions/probes.md#b5</summary>
    public bool B5 { get; private set; }

    /// <summary><c>--reposition-read</c>: is the translation vector of a reposition feature readable,
    /// and by which member. The member list is taken from the product's type library, not from a
    /// candidate list. History: docs/decisions/probes.md#reposition-read</summary>
    public bool RepositionRead { get; private set; }

    /// <summary><c>--reposition-params</c>: are the INPUTS of a reposition feature readable by the
    /// DOCUMENTED API7 route — <c>Position</c> → <c>ILocalCoordinateSystem</c> →
    /// <c>ParameterType</c>/<c>Parameters</c> → <c>IPoint3DParamDisplace.DX/DY/DZ</c> and
    /// <c>RepositionCentre</c> → <c>IPoint3D.X/Y/Z</c>. A separate probe from <c>--reposition-read</c>
    /// because that one queried the object by late binding on the class's default interface and typed
    /// only through <c>ILocalCoordinateSystem</c>, i.e. never asked the documented PARAMETERS interface.
    /// History: docs/decisions/probes.md#rp-params</summary>
    public bool RepositionParams { get; private set; }

    /// <summary>--sketch-definition: the sketch certainty "+ / − / !" read from the API (probe S).
    /// History: docs/decisions/probes.md#sketch-definition</summary>
    public bool SketchDefinition { get; private set; }

    /// <summary><c>--vm-reference &lt;path&gt;</c>: build the VM acceptance reference — a parametrized
    /// part (<c>100×80</c>, depth 10) carrying two EXTERNAL variables on the top part collection. The
    /// order forbids creating variables as an MCP function, so the reference is prepared here, once, and
    /// the acceptance group only OPENS the file. History: docs/decisions/variables-material.md</summary>
    public string? VmReference { get; private set; }

    /// <summary><c>--g3-parameter-route</c>: throwaway measurement — which object exposes an extrusion's
    /// parameter variables (block G3 §3.2 route vs the API7 view vs the tree entity).</summary>
    public bool G3ParameterRoute { get; private set; }

    /// <summary><c>--vm-variable-route</c>: throwaway ladder — on which object does the documented
    /// <c>AddNewVariable</c> actually take. Needed because the reference part's variables must be created
    /// by documented means, and the first two candidates refused. History:
    /// docs/decisions/variables-material.md</summary>
    public bool VmVariableRoute { get; private set; }

    /// <summary><c>--vm-control-expression</c>: throwaway ladder for the DOCUMENTED external-variable
    /// route — <c>IProcess3D</c> → QI(<c>IProcessWithVariables</c>) → <c>SetControlExpression</c>. Asked
    /// because the API5 <c>AddNewVariable</c> route was measured NOT to surface external variables on a
    /// top-level part, and the reference (order §5) must be built by documented means or not at all.
    /// History: docs/decisions/variables-material.md</summary>
    public bool VmControlExpression { get; private set; }

    /// <summary><c>--vm-density-units</c>: which unit the two DOCUMENTED density getters really return —
    /// <c>ksPart.GetDensity()</c> and <c>IPart7</c>→QI(<c>IMassInertiaParam7</c>)→<c>Density</c> — measured
    /// on two pre-set densities. History: docs/decisions/variables-material.md#units</summary>
    public bool VmDensityUnits { get; private set; }

    /// <summary><c>--vm-density-mci</c>: does the API5 MCI route publish the density in the units its
    /// <c>bitVector</c> ARGUMENT selects — <c>ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r</c>
    /// in kg/m3 and <c>…ST_MIX_MM|ST_MIX_KG….r</c> in kg/mm3 — measured on two pre-set densities with the
    /// ratio and the mass self-consistency checked. History: docs/decisions/variables-material.md#units</summary>
    public bool VmDensityMci { get; private set; }

    /// <summary><c>--vm-open &lt;path&gt;</c>: which write+rebuild pair moves the geometry of the SAVED
    /// reference file — the question the adapter's behaviour on an opened document turned into.</summary>
    public string? VmOpen { get; private set; }

    /// <summary>PID of the KOMPAS instance the probe launched, written back by the step that measured the
    /// process diff. <c>null</c> means "not attributed yet" — a distinct state from a PID of zero,
    /// and the step that needs it says so rather than comparing against a placeholder.</summary>
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
        var mate = false;
        var sketchDefinition = false;
        var identity = false;
        var union = false;
        var b5 = false;
        var repositionRead = false;
        var repositionParams = false;
        var cutArea = false;
        var repositionOrder = false;
        var sketchPlane = false;
        string? vmReference = null;
        var vmVariableRoute = false;
        var g3ParameterRoute = false;
        var vmControlExpression = false;
        var vmDensityUnits = false;
        var vmDensityMci = false;
        string? vmOpen = null;

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
                case "--mate":
                    mate = true;
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
                case "--vm-reference" when i + 1 < args.Length:
                    vmReference = args[++i];
                    break;
                case "--vm-variable-route":
                    vmVariableRoute = true;
                    break;
                case "--g3-parameter-route":
                    g3ParameterRoute = true;
                    break;
                case "--vm-control-expression":
                    vmControlExpression = true;
                    break;
                case "--vm-density-units":
                    vmDensityUnits = true;
                    break;
                case "--vm-density-mci":
                    vmDensityMci = true;
                    break;
                case "--vm-open" when i + 1 < args.Length:
                    vmOpen = args[++i];
                    break;
                case "--work" when i + 1 < args.Length:
                    workOverride = args[++i];
                    break;
                case "--help":
                    Console.WriteLine("KompasMcp.Api7Probe [--passport] [--controls] [--lifecycle] [--extrusion] [--chamfer] [--sketch-reopen] [--fillet-edge-set] [--fillet-base-objects] [--boolean] [--split] [--reposition] [--tree] [--rotation] [--full-turn] [--boss-fuse] [--verify-m3d PATH] [--hole-modes] [--hole-tree] [--sketch-definition] [--identity] [--union] [--b5] [--reposition-read] [--reposition-params] [--cut-area] [--reposition-order] [--sketch-plane] [--vm-reference PATH] [--vm-variable-route] [--vm-control-expression] [--vm-density-units] [--vm-density-mci] [--vm-open PATH] [--work DIR] [--keep]");
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
            : vmReference is not null ? "vm-reference"
            : vmVariableRoute ? "vm-variable-route"
            : g3ParameterRoute ? "g3-parameter-route"
            : vmControlExpression ? "vm-control-expression"
            : vmDensityUnits ? "vm-density-units"
            : vmDensityMci ? "vm-density-mci"
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
            Mate = mate,
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
            VmReference = vmReference,
            VmVariableRoute = vmVariableRoute,
            G3ParameterRoute = g3ParameterRoute,
            VmControlExpression = vmControlExpression,
            VmDensityUnits = vmDensityUnits,
            VmDensityMci = vmDensityMci,
            VmOpen = vmOpen,
        };
    }

    /// <summary>The installation root baked in at build time as <c>AssemblyMetadata("KompasRoot")</c>.</summary>
    /// <remarks>Returns <c>""</c> rather than guessing when the attribute is absent, so a step that needs the
    /// installation says it could not find it instead of surveying a wrong directory.</remarks>
    private static string ReadKompasRoot()
    {
        var attribute = typeof(Options).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "KompasRoot");
        return attribute?.Value ?? string.Empty;
    }

    /// <summary>Walks upward from the working directory looking for the repository root, which is the folder
    /// that carries <c>KompasMcp.sln</c>. Nothing is assumed about where the probe was launched from.</summary>
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
