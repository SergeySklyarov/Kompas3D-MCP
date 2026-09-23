using System.Diagnostics;
using System.Globalization;
using System.IO;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;

namespace KompasMcp.Api7Probe;

/// <summary>
/// SM-03 (rows of queue B2): does API5 rotation work, and where does its axis come from?
/// </summary>
/// <remarks>
/// <para>
/// The catalog is explicit that no rotation route leaving the axis implicit will be accepted: the
/// axis source is a sketch construction line, a built axis or cylindrical geometry. The metadata
/// dump settles the shape of the question — <c>ksBaseRotatedDefinition</c> has <b>no</b>
/// <c>SetAxis</c>, only <c>SetSketch</c>, so the axis must already be inside the sketch the
/// definition is handed. This probe therefore measures, in order: which entity type yields that
/// definition at all, whether a sketch can carry a construction line
/// (<c>ksDocument2D.ksAxisLine</c>), and only then whether a cylinder comes out with π·r²·h.
/// </para>
/// <para>
/// <b>Nothing is assumed about the type number.</b> The probe walks the entity ids the way the
/// catalog's own note demands ("уточнить точные типы и номера метаданными установленного
/// приложения") and reports the first one whose definition casts to <c>ksBaseRotatedDefinition</c>.
/// </para>
/// <para>
/// <b>Proof standard.</b> As in the other probes: a non-null object or a true Create is never a
/// result. Only the measured volume and the read-back parameter decide, against
/// <c>max(0.01 mm³, 1e-6 · expectation)</c>, which is not widened after a failure.
/// </para>
/// </remarks>
internal sealed class RotationProbe
{
    /// <summary>Radius of the cylinder: the rectangle's extent from the axis.</summary>
    private const double RadiusMm = 20d;

    /// <summary>Height of the cylinder: the rectangle's extent along the axis.</summary>
    private const double HeightMm = 40d;

    /// <summary>
    /// The style an axis line inside a rotation profile must be born with:
    /// <c>ksCurveStyleEnum.ksCSConstruction = 6</c>.
    /// </summary>
    /// <remarks>
    /// A construction segment is explicitly not part of the sketch's region, which is what an axis
    /// line has to be. R.15 and R.16 measured that a fifth segment drawn with style 1
    /// (<c>ksCSNormal</c>) makes the sketch refuse to extrude at every gap, while the same rectangle
    /// without it extrudes to its exact analytic volume — so the style at creation, not the segment's
    /// position, is the variable.
    /// </remarks>
    private const int AxisStyle = 6;

    /// <summary>A full turn of that rectangle is a cylinder, π·r²·h = 50265.48245743669 мм³.</summary>
    private static double FullTurnVolume => Math.PI * RadiusMm * RadiusMm * HeightMm;

    private static double Tolerance(double expectation) => Math.Max(0.01d, 1e-6d * Math.Abs(expectation));

    /// <summary>
    /// A few ULPs of a reading, i.e. the band in which two doubles are the same number measured twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not assumed: a variant of R.21 reported <c>ΔV=-0.000000000116</c> against a baseline
    /// of <c>710194.690350851</c>, and the real spacing of doubles at that magnitude is
    /// <c>1.1641532182693481e-10</c> — the delta is exactly one ULP. Reported as a bare number it reads
    /// like a tiny real change; named as noise it is what it is, an unchanged volume.
    /// </para>
    /// <para>
    /// <b>Do not reach for <c>double.Epsilon</c> here.</b> The first version of this helper multiplied
    /// by <c>double.Epsilon</c>, which in .NET is <c>4.9e-324</c> — the smallest <i>denormal</i>, not
    /// the machine epsilon. The band came out as <c>2.8e-317</c>, about 4·10⁶ times too small, and the
    /// step printed «БОЛЬШЕ шума» for a delta that is precisely one ULP. The spacing of doubles at
    /// magnitude <c>v</c> is <c>|v| · 2⁻⁵²</c>; that is the factor written below, spelled as an explicit
    /// hex constant so its meaning cannot drift.
    /// </para>
    /// </remarks>
    private static double NoiseFloor(double value) =>
        Math.Max(Math.Abs(value), 1d) * 8d * MachineEpsilon;

    /// <summary>The machine epsilon of IEEE-754 binary64: 2⁻⁵², the relative spacing of doubles.</summary>
    private const double MachineEpsilon = 2.2204460492503131e-16;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();

    /// <summary>
    /// PIDs of the КОМПАС processes that already existed when the probe started.
    /// </summary>
    /// <remarks>
    /// <b>Measured the hard way.</b> The first version of R.Z counted every <c>KOMPAS.exe</c> by image
    /// name and reported "осталось процессов: 5" — which reads as a leak and is not one: four sessions
    /// belonging to the developer were already running, and the probe's own instance is the fifth
    /// until the local server reaps it. A verdict computed from a count that the environment can
    /// move is not a measurement. So the rule is stated as what it can actually prove: no process may
    /// survive that was not there before.
    /// </remarks>
    private readonly HashSet<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private ksDocument3D _doc = null!;
    private short? _rotatedType;
    private int _ownPid;

    /// <summary>
    /// The КОМПАС installation root, from <c>KompasRoot</c> at build time. Used by R.20 to survey the
    /// shipped sample models rather than to assume where they are.
    /// </summary>
    private string _kompasRoot = string.Empty;

    /// <summary>Which step the profile builder is currently serving, so its notes land in the right one.</summary>
    private string _currentStepId = "R.2";

    /// <summary>Whether the profile last handed to the rotation carried a construction line.</summary>
    private bool _lastSketchHadAxis;

    /// <summary>
    /// The volume of the document's main body before the current step ran. The probe reuses one
    /// document for every step (see <c>Run</c>), so a step that reads <c>Api5.Volume(part)</c> after
    /// its own <c>Create()=false</c> is reading whatever an <em>earlier</em> step left behind. Every
    /// step that builds in the shared document records this before and after, so a number that did
    /// not move is reported as not having moved rather than as a geometric result.
    /// </summary>
    private double? _bodyVolumeBeforeStep;

    /// <summary>
    /// The volume of the R.8 plate measured <em>before</em> it was drilled. Without this the step has
    /// no honest baseline: subtracting a theoretical hole from a theoretical plate assumes the hole was
    /// actually cut, which is the very thing under test. Measured, not assumed.
    /// </summary>
    private double? _plateOnlyVolume;

    public RotationProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
        _kompasRoot = options.KompasRoot;
    }

    /// <summary>Writes the report from inside a run, for the <c>--keep</c> diagnostic path.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        var stem = "rotation";
        report.Flush(
            Path.Combine(options.ReportDir, stem + ".json"),
            Path.Combine(options.ReportDir, stem + ".md"));
    }

    /// <summary>Snapshots the КОМПАС processes present before the probe's own instance is created.</summary>
    private void SampleProcessesBefore()
    {
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }
    }

    public void Run()
    {
        SampleProcessesBefore();
        Launch();
        try
        {
            RunnerDiagnostic();
            RotationFamilyProbe();

            // The type relationship of a live entity. Registered HERE, beside the other diagnostics,
            // and not inside FindRotatedType(): that method is a FALLBACK, reached only when
            // QuickSweep(1,120) comes up empty. Because QuickSweep does find the rotation id (27), a
            // question placed inside FindRotatedType is a question that can never be asked — the same
            // unreachable-diagnostic trap this file has already been bitten by twice.
            EntityFeatureTypeRoute();

            // The type must be known before anything is built: every construction step goes through
            // CreateRotation, which refuses to run without it. Leaving the sweep until after the
            // diagnostics is how four variants "measured" nothing but the probe's own ordering.
            if (QuickSweep(1, 120) is not { } type)
            {
                type = FindRotatedType() ?? default;
            }

            if (_rotatedType is not null)
            {
                FindAxisStyle();
                AxisVariants();
                VariantSweep();
                SideDirectionMatrix();
                AxisAuthoringRoute();
                RotatedSetAxisRoute();
                RotatedAxisCylinderFace();
                Axis3DContainerRoute();
                Axis3DByEdgeRoute();
                AxisLineParamRoute();
                ObjectTypeNumberRoute();
                RotatedTypeAndWriteOrderRoute();
                EdgeKindAndTwoPointAxisRoute();
                ContourProfileRoute();
                AxisOutsideProfileRoute();
                ConstructionAxisStyleRoute();
                FreshDocumentRoute();
                BooleanOperationRoute();
                ShippedShaftRoute();

                // The two member families reflection says no step has EVER written on a rotation
                // definition. Registered here, beside the other measured routes, and not inside a
                // helper that may not be reached — this file has already been bitten twice by a
                // question placed where it can never be asked.
                UnwrittenRotationMembersRoute();

                // R.20 left one question open and named it: a real rotation out of a shipped file IS a
                // feature of the model, but no cast through the entity route reached its definition,
                // and R.20 printed `System.__ComObject` for the definition it did read — which is what
                // Api5.RuntimeName says for every COM-callable wrapper and therefore distinguishes
                // nothing. R.22 asks the question R.7 answered for the probe's own feature: not "does
                // the managed cast succeed" but "does the OBJECT answer QueryInterface".
                ShippedDefinitionInterfaceRoute();

                // R.22 measured that a shipped rotation answers QI(IRotated) and hands back live Axis
                // and Profile objects, where the probe's own feature hands back Axis=null. That is the
                // difference that matters: R.7 proved Axis can be SET and still rejected, but no step
                // has read an axis the API itself considers acceptable. R.23 asks whether that axis
                // can be carried into a document the probe owns and reused to build a rotation — the
                // one route that would turn the blocked row into a working one rather than a
                // better-described refusal.
                ShippedAxisReuseRoute();

                // R.24 — the route the task file names as the unmeasured one, and the reason every
                // rung before it can refuse while the product still rotates: all of them create the
                // operation SHELL through API5 `NewEntity(...)` and finish with API5 `Create()`.
                // That is a MIXED lifecycle. R.24 creates the operation itself through the API7
                // factory, `IModelContainer.Rotateds.Add(o3d_bossRotated)`, and never calls the API5
                // `Create()` at all. Placed BEFORE the shared-document rungs on purpose: it builds in
                // its own document (so it neither pollutes nor is polluted by R.2/R.4), and its result
                // is the one the remaining rungs can be compared against.
                DirectFactoryRoute();

                // R.25 — the same factory, on the reference profile the task file specifies: rectangle
                // u∈[0,20], v∈[-20,20], axis fixed in the SAME part as a real two-point axis object on
                // the line u=0. R.24 measures whether the factory builds; R.25 measures whether it
                // builds the ANALYTIC body (π·r²·h, an independent shape/size check) and whether the
                // parameters survive save→close→reopen and 360°→180°.
                DirectFactoryReferenceRoute();

                // R.26 — the two questions R.24/R.25 raised but did not answer.
                //
                // (a) R.24 wrote `OperationResult = ksOperationNewBody` on ALL THREE factory types and
                //     all three ADDED material (+50265 each). A "cut" that glues is not a cut, so either
                //     OperationResult is ignored and RotatedType alone decides the action, or the write
                //     never reached the object. Measured here on a prepared body, where a cut has an
                //     analytically known volume change.
                // (b) R.25 changed Direction at 180° and the volume did not move — as expected for a
                //     symmetric body — but the BOUNDING BOX did not move either, so "the sector moved"
                //     was never actually established. Measured here by which side of the axis the
                //     material ends up on.
                OperationSemanticsRoute();

                FullTurn(type);
                HalfTurn(type);
                Direction(type);
                NoAxis(type);
            }
            else
            {
                var blocked = _report.Begin("R.E", "Вращение не измерено",
                    "Почему ни один шаг вращения не выполнен?");
                blocked.Fail("тип определения вращения не найден ни быстрым проходом 1…120, ни подробным перебором — "
                    + "измерять вращение нечем");
            }
        }
        finally
        {
            Shutdown();
        }
    }

    private void Launch()
    {
        var step = _report.Begin("R.0", "Свой невидимый экземпляр КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        _clock.Restart();
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
        step.Observe("создан экземпляр API5 за " + _clock.ElapsedMilliseconds + " мс");
        step.Observe("процессов KOMPAS.exe было до запуска: " + _pidsBefore.Count + " (" + string.Join(", ", _pidsBefore) + ")"
            + (_ownPid == 0 ? "; новый процесс сразу не виден (in-process или сервер ещё не поднялся)" : "; свой процесс " + _ownPid));
        if (_ownPid == 0)
        {
            step.Observe("ЗАМЕЧАНИЕ: без собственного PID правило R.Z «не осталось новых процессов» вырождается - "
                + "сверить будет нечего, и шаг честно скажет об этом, а не выдаст PASS.");
        }

        var document = (ksDocument3D)_app.Document3D();
        _doc = document;
        document.Create(true, true);
        step.Observe("создан невидимый документ-деталь");
        step.Pass("сеанс поднят");
    }

    private void Shutdown()
    {
        var step = _report.Begin("R.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _doc?.close();
        }
        catch (Exception ex)
        {
            step.Observe("документ не закрылся: " + ex.GetType().Name);
        }

        try
        {
            _app.Quit();
            step.Observe("Quit() вызван на собственном экземпляре");
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил " + ex.GetType().Name);
        }

        // The local server does not die the instant Quit() returns; give it a moment before judging.
        //
        // It sometimes takes longer than the loop below allows: run 33ebe1b7 judged PIDs 23968/6816 as
        // leaked, and by the time the next command ran, both had exited on their own. A verdict that
        // depends on how quickly the operating system reaps a process measures the race, not the
        // probe. So the wait follows the probe's OWN pid specifically — the other new pid on this
        // machine belongs to КОМПАС's own helper arrangement and is not a leak this probe can close.
        var appeared = new List<int>();
        var ownStillAlive = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            appeared = CurrentProcessIds().Where(pid => !_pidsBefore.Contains(pid)).ToList();
            ownStillAlive = _ownPid != 0 && appeared.Contains(_ownPid);
            if (!ownStillAlive)
            {
                break;
            }

            Thread.Sleep(500);
        }

        step.Observe("посторонних процессов КОМПАС.exe до запуска: " + _pidsBefore.Count
            + " (" + string.Join(", ", _pidsBefore) + ")");
        step.Observe("новых процессов после Quit(): " + appeared.Count
            + (appeared.Count == 0 ? string.Empty : " (" + string.Join(", ", appeared) + ")")
            + (appeared.Count > 0 ? "; процесс зонда " + _ownPid + " среди них: " + ownStillAlive : string.Empty));

        if (appeared.Count == 0)
        {
            step.Pass("новых процессов не осталось: зонд не оставил осиротевшего экземпляра");
        }
        else if (_ownPid != 0 && !ownStillAlive)
        {
            // Processes belonging to someone else's session are not this probe's leak, and calling
            // them one would be exactly the kind of unmeasured verdict this project forbids.
            step.Observe("процесс зонда (" + _ownPid + ") завершился; оставшийся новый процесс — чужой "
                + "сеанс либо вспомогательный процесс самого КОМПАСа, а не утечка пробы.");
            step.Pass("своего процесса среди оставшихся нет");
        }
        else
        {
            step.Fail("после Quit() остался процесс зонда: " + string.Join(", ", appeared));
        }
    }

    private static List<int> CurrentProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            ids.Add(process.Id);
            process.Dispose();
        }

        return ids;
    }

    // ═══════════════════════════════════════════════════════ entity type search ══

    /// <summary>
    /// R.0d — whether a live element of a feature tree is a <c>ksEntity</c>, a <c>ksFeature</c>, or a
    /// bare <c>System.__ComObject</c> that answers one cast and refuses the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this step exists.</b> R.20 walks the feature tree of files КОМПАС itself wrote. On those
    /// files the walk demonstrably works — it returns real feature names (<c>«Операция вращения:1»</c>,
    /// <c>«Вырезать элемент вращения:2»</c>) rather than the empty document's two service features. But
    /// <c>element as ksFeature</c> succeeds while <c>element as ksEntity</c> returns <b>null for every
    /// element in every file</b>, and <c>Api5.RuntimeName(element)</c> reports <c>System.__ComObject</c>
    /// in both cases. Those two facts cannot both be about the model: one object, two casts, two
    /// answers is a statement about the interop wrapper.
    /// </para>
    /// <para>
    /// <b>Why it is registered here and not inside <c>FindRotatedType</c>.</b> That method is a
    /// fallback — reached only when <c>QuickSweep(1, 120)</c> finds nothing. Because the sweep does
    /// find the rotation id (27), a question placed inside the fallback is a question that is never
    /// asked, and its absence from the journal looks exactly like a negative answer. This file has
    /// been bitten by the unreachable-diagnostic trap twice already, so the check is a step of its own.
    /// </para>
    /// <para>
    /// Both directions are measured: a freshly created entity asked whether it is a feature, and a real
    /// tree walked with both casts counted, so "the collection is empty" and "the cast is refused" are
    /// distinguishable in the journal.
    /// </para>
    /// </remarks>
    private void EntityFeatureTypeRoute()
    {
        _currentStepId = "R.0d";
        var step = _report.Begin("R.0d", "Класс живого элемента дерева: ksEntity, ksFeature или __ComObject",
            "Почему элемент дерева приводится к ksFeature, но не к ksEntity — и на каком объекте это видно?");

        // ── direction 1: a live entity, asked about the other interface ──────────────────────
        try
        {
            var part = (ksPart)_doc.GetPart(-1);
            var live = part.NewEntity(Api5.Sketch) as ksEntity;
            if (live is null)
            {
                step.Observe("NewEntity(эскиз) не дал ksEntity — вопрос о парах типов остался без ответа");
            }
            else
            {
                var transferred = Api5.SafeObject(() => _app.TransferInterface(live, 1 /* ksAPI5Auto */, 0));
                step.Observe("свежая сущность: RuntimeName=" + Api5.RuntimeName(live)
                    + ", это ksFeature: " + (live is ksFeature ? "да" : "НЕТ")
                    + ", type=" + Api5.Raw(live.type)
                    + "; через TransferInterface → " + Api5.RuntimeName(transferred)
                    + ", это ksFeature: " + (transferred is ksFeature ? "да" : "НЕТ")
                    + ", это ksEntity: " + (transferred is ksEntity ? "да" : "НЕТ"));
                live.excluded = true;
            }
        }
        catch (Exception ex)
        {
            step.Observe("свежая сущность: вопрос бросил " + HResult.Describe(ex));
        }

        // ── direction 2: a real tree, both casts counted ────────────────────────────────────
        try
        {
            var part = (ksPart)_doc.GetPart(-1);
            if (part.GetFeature() is not ksFeature root)
            {
                step.Observe("дерево: part.GetFeature() вернул не ksFeature — считать нечего");
                step.Pass("вопрос о парах типов задан; дерева для перебора нет");
                return;
            }

            if (root.SubFeatureCollection(true, false) is not ksFeatureCollection tree)
            {
                step.Observe("дерево: SubFeatureCollection(true,false) вернул не коллекцию");
                step.Pass("вопрос о парах типов задан; коллекция не получена");
                return;
            }

            var count = tree.GetCount();
            var asFeature = 0;
            var asEntity = 0;
            var samples = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var element = tree.GetByIndex(i);
                var isFeature = element as ksFeature is not null;
                var isEntity = element as ksEntity is not null;
                if (isFeature) { asFeature++; }
                if (isEntity) { asEntity++; }
                if (i < 4)
                {
                    samples.Add("[" + i + "] " + Api5.RuntimeName(element)
                        + " ksFeature=" + (isFeature ? "да" : "НЕТ")
                        + " ksEntity=" + (isEntity ? "да" : "НЕТ"));
                }
            }

            step.Observe("дерево детали: элементов " + count + ", как ksFeature — " + asFeature
                + ", как ksEntity — " + asEntity);
            step.Observe("первые элементы: " + (samples.Count == 0 ? "дерево пусто" : string.Join("; ", samples)));

            // What DOES the element answer, if not ksEntity? A `ksFeature` element from this collection
            // was already measured to refuse both `as ksEntity` and a TransferInterface hop, so the
            // remaining question is which interface it does support — and that is read off the live
            // object rather than assumed. `Api5.RuntimeName` reporting __ComObject is consistent with
            // many different failures and picks between none of them.
            if (count > 0)
            {
                var first = tree.GetByIndex(0);
                if (first is null)
                {
                    step.Observe("первый элемент: GetByIndex(0) вернул null");
                }
                else
                {
                    var firstAsFeature = first as ksFeature;
                    step.Observe("первый элемент: RuntimeName=" + Api5.RuntimeName(first)
                        + "; объявленные члены — ниже");
                    if (firstAsFeature is not null)
                    {
                        var members = firstAsFeature.GetType().GetMembers()
                            .Select(m => m.Name)
                            .Distinct()
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
                        step.Observe("ksFeature объявляет " + members.Count + " членов: "
                            + string.Join(", ", members.Take(40)));
                    }

                    var moved = Api5.SafeObject(() => _app.TransferInterface(first, 1, 0));
                    step.Observe("первый элемент через TransferInterface(ksAPI5Auto=1): "
                        + Api5.RuntimeName(moved)
                        + "; ksFeature: " + (moved is ksFeature ? "да" : "НЕТ")
                        + "; ksEntity: " + (moved is ksEntity ? "да" : "НЕТ"));

                    var moved7 = Api5.SafeObject(() => _app.TransferInterface(first, 2 /* ksAPI7Dual */, 0));
                    step.Observe("первый элемент через TransferInterface(ksAPI7Dual=2): "
                        + Api5.RuntimeName(moved7));
                }
            }

            // The route the WORKING probes use, and the one R.20 had not tried. Every lifecycle step in
            // `SketchLifecycleProbe.LastFeature` reaches a real feature through
            // `part.EntityCollection(o3d_operationElement = 110)` — not through `SubFeatureCollection` —
            // and the elements that collection returns cast to `ksEntity`, definitions and all.
            // `SubFeatureCollection` is the tree walk (good for names and order); `EntityCollection(110)`
            // is the feature set (good for handles). This document is an empty part built by this step,
            // so the collection is expected to be empty here — the informative measurement is the same
            // one taken on an opened file, inside R.20. Asking it here is the control that says whether
            // an empty answer means "wrong route" or "nothing to enumerate".
            if (part.EntityCollection(Api5.OperationElement) is ksEntityCollection operations)
            {
                step.Observe("EntityCollection(" + Api5.OperationElement + ") на пустой детали: элементов "
                    + operations.GetCount() + " (ожидание 0 — признаков не строили; контроль для R.20)");
            }
            else
            {
                step.Observe("EntityCollection(" + Api5.OperationElement + ") недоступна");
            }

            // ── the experiment that decides between "the file is different" and "these are not my features" ──
            //
            // Everything so far measures ONE document: this empty part. R.20 measures ANOTHER: a file
            // КОМПАС wrote years ago. They disagree — a feature built here yields a definition, a
            // feature read from a shipped file does not — and a disagreement between two documents is
            // not yet a mechanism, because the two differ in a dozen ways at once (author, version,
            // age, file origin).
            //
            // So: put a feature of THIS probe's own making into the SAME kind of collection that the
            // shipped file was asked through, and ask it the same question. If a fresh boss extrusion
            // reached through EntityCollection(110) still gives a definition while a shipped rotation
            // through the same collection does not, then the difference is a property of the features
            // (how they were authored), not of the collections (which one the caller picked) — and the
            // whole "find the right collection" line of work is over. If the fresh feature ALSO
            // refuses, then the collection is the culprit after all and R.20's route is the problem.
            //
            // This is the one control that separates the two hypotheses, and it costs one boss
            // extrusion on a part that is thrown away in the next line.
            BuildFreshFeatureForCastingControl(part, step);

            // The finding, stated as the measurement rather than as a conclusion about КОМПАС: if both
            // counts are zero the collection is empty; if ksFeature != ksEntity the cast, not the
            // model, decides which interface a caller can reach.
            if (count == 0)
            {
                step.Observe("исход: дерево пусто — на этом объекте пару типов не измерить");
            }
            else if (asFeature == asEntity)
            {
                step.Observe("исход: обе приведения совпали (" + asFeature + " из " + count
                    + ") — разницы между ksEntity и ksFeature на этих элементах нет");
            }
            else
            {
                step.Observe("исход: приведения РАСХОДЯТСЯ (ksFeature=" + asFeature
                    + ", ksEntity=" + asEntity + " из " + count
                    + ") — какой интерфейс доступен, решает приведение, а не модель");
            }
        }
        catch (Exception ex)
        {
            step.Observe("дерево: перебор бросил " + HResult.Describe(ex));
        }

        step.Pass("обе стороны вопроса измерены: свежая сущность и живое дерево");
    }

    /// <summary>
    /// R.0b — a runner that proves the mechanism R.1 depends on. The first two attempts at R.1 both
    /// came back with <b>zero</b> observations, which means they failed before their own first
    /// diagnostic line — and a step that cannot say how far it walked cannot be told apart from a
    /// step that walked everything and found nothing. This walks one known id (sketch = 5, already
    /// measured elsewhere in this repository) through the exact same two reference routes the real
    /// sweep uses, so that a silent no-answer becomes a located fault.
    /// </summary>
    /// <remarks>
    /// <b>What this step exists to expose.</b> A bare <c>entity.GetDefinition()</c> on a freshly
    /// created entity hands back a raw <c>System.__ComObject</c>, and a C# <c>is</c> against an
    /// interop interface on that object is a real <c>QueryInterface</c> the vendor object can refuse
    /// — whereupon the naive sweep concludes "this family does not exist" for every id in the band.
    /// The two routes measured here are (1) the extra <c>TransferInterface</c> hop both the chamfer
    /// and the hole probes use before touching a definition, and (2) re-reading the definition back
    /// off the model after a rebuild, which is how <c>SketchLifecycleProbe.FindSketchAfterReopen</c>
    /// obtains its <c>ksSketchDefinition</c>.
    /// </remarks>
    private void RunnerDiagnostic()
    {
        var step = _report.Begin("R.0b", "Диагностика: чем на самом деле достаётся определение",
            "Почему свежая сущность отдаёт __ComObject и какой маршрут даёт настоящее определение?");

        var part = (ksPart)_doc.GetPart(-1);
        step.Observe("GetPart(-1) → " + part.GetType().Name);

        // ── route 0: the naive one, for the record ────────────────────────────────────────────
        ksEntity? probe = null;
        object? definition = null;
        try
        {
            probe = part.NewEntity(Api5.Sketch) as ksEntity;
            if (probe is null)
            {
                step.Fail("NewEntity(5) не дал ksEntity — без эскиза дальше нечего мерить.");
                return;
            }

            definition = probe.GetDefinition();
            step.Observe("маршрут 0 (как было): GetDefinition() → " + Api5.RuntimeName(definition)
                + ", приведение к ksSketchDefinition: " + (definition is ksSketchDefinition));
        }
        catch (Exception ex)
        {
            step.Observe("маршрут 0 бросил " + HResult.Describe(ex));
        }

        // ── route 1: the TransferInterface hop the chamfer and hole probes use ────────────────
        try
        {
            if (definition is not null)
            {
                var moved = _app.TransferInterface(definition, 1 /* ksAPI5Auto */, 0);
                step.Observe("маршрут 1 (TransferInterface(definition, ksAPI5Auto=1, 0)) → "
                    + Api5.RuntimeName(moved) + ", приведение к ksSketchDefinition: " + (moved is ksSketchDefinition));
            }
        }
        catch (Exception ex)
        {
            step.Observe("маршрут 1 бросил " + HResult.Describe(ex));
        }

        // ── route 2: read the definition back off the model after the entity is real ──────────
        try
        {
            if (probe is not null)
            {
                if (part.GetDefaultEntity(Api5.PlaneXoy) is ksEntity plane
                    && definition is ksSketchDefinition sketchDefinition)
                {
                    probe.name = "R0b-probe";
                    sketchDefinition.SetPlane(plane);
                    probe.Create();
                    _doc.RebuildDocument();
                    step.Observe("маршрут 2: эскиз построен и документ перестроен");
                }

                object? back = null;
                foreach (var objType in new short[] { Api5.Sketch, 15, 16 })
                {
                    var collection = part.EntityCollection(objType) as ksEntityCollection;
                    var count = collection?.GetCount() ?? 0;
                    step.Observe("маршрут 2: EntityCollection(" + objType + ") → " + (collection is null ? "не коллекция" : "count=" + count));
                    for (var i = 0; i < count && back is null; i++)
                    {
                        if (collection!.GetByIndex(i) is not ksEntity entity)
                        {
                            continue;
                        }

                        var read = entity.GetDefinition();
                        step.Observe("маршрут 2: [" + i + "] «" + entity.name + "» type=" + entity.type
                            + " → " + Api5.RuntimeName(read) + ", ksSketchDefinition: " + (read is ksSketchDefinition));
                        if (read is ksSketchDefinition)
                        {
                            back = read;
                        }
                    }
                }

                step.Observe(back is null
                    ? "маршрут 2: определения эскиза по модели не нашлось"
                    : "маршрут 2 даёт настоящее определение: " + Api5.RuntimeName(back));
            }
        }
        catch (Exception ex)
        {
            step.Observe("маршрут 2 бросил " + HResult.Describe(ex));
        }

        step.Pass("механизм перебора работает: у каждого маршрута есть наблюдаемый ответ");
    }

    /// <summary>
    /// R.1 — which entity id gives a <c>ksBaseRotatedDefinition</c>. The catalog names
    /// <c>o3d_baseRotated</c>/<c>bossRotated</c>/<c>cutRotated</c> but states the numbers are not yet
    /// confirmed; the metadata dump has the definition interfaces but not the id constants, so this
    /// walks the plausible range and reports what answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Staged, and it says which stage it reached.</b> Two blind sweeps over 1…60 producing no
    /// observations at all is not something to repeat a third time: this version reports the walk as
    /// five labelled chunks, so the report distinguishes "the band was wrong" from "the sweep never
    /// ran".
    /// </para>
    /// <para>
    /// <b>Two probes per id, because a NaN name is not a verdict.</b> The first instrumented sweep
    /// asked only <c>entity.GetDefinition()</c> and got <c>__ComObject</c> for all sixty ids — which
    /// measures the wrapper, not the product: the vendor returns a raw <c>__ComObject</c> whose
    /// <c>QueryInterface</c> to the definition interface may need the <c>TransferInterface</c> hop
    /// that the chamfer and hole probes already use. So each id is now asked twice, and an id is
    /// reported as a hit when <em>either</em> route yields a definition whose name marks the rotation
    /// family. The definition is never inferred from the id number.
    /// </para>
    /// </remarks>
    private short? FindRotatedType()
    {
        var step = _report.Begin("R.1", "Номер типа сущности вращения",
            "Какой id даёт определение ksBaseRotatedDefinition, а не молча другое семейство?");

        // Ids the catalog and docs already measured for neighbouring families, as a sanity frame:
        // sketch=5, baseExtrusion=24, chamfer=33, fillet=34. Rotation sits in the same neighbourhood
        // in the vendor enumeration, so the walk covers that band and reports every hit.
        var found = new List<(short Id, string Definition)>();
        var tried = new List<string>();
        var failed = new List<string>();
        short lastAttempted = 0;
        short lastAnswering = 0;
        var rejectionCount = 0;

        foreach (var (label, first, last) in new (string, short, short)[]
                 {
                     ("1…10", 1, 10),
                     ("11…20", 11, 20),
                     ("21…30", 21, 30),
                     ("31…40", 31, 40),
                     ("41…60", 41, 60),
                 })
        {
            var asked = 0;
            var answered = 0;
            for (short id = first; id <= last; id++)
            {
                lastAttempted = id;
                var part = (ksPart)_doc.GetPart(-1);
                try
                {
                    asked++;
                    if (part.NewEntity(id) is not ksEntity entity)
                    {
                        continue;
                    }

                    lastAnswering = id;
                    answered++;

                    var naive = Name(GetDefinition(entity, failed, id, "прямой"));
                    var name = naive;
                    if (naive == "__ComObject")
                    {
                        var moved = Name(TransferDefinition(entity, failed, id));
                        if (moved != "__ComObject" && moved != "null")
                        {
                            name = moved + "(передача)";
                        }
                    }

                    tried.Add(id + "=" + name);

                    if (IsRotationFamily(name))
                    {
                        found.Add((id, name));
                    }

                    // ksEntity declares no Delete; the vendor route for discarding a freshly created
                    // entity is `excluded`, the same flag the feature lifecycle uses in production.
                    entity.excluded = true;
                }
                catch (Exception ex)
                {
                    // An id the part does not accept is not an error for the question being asked —
                    // the id simply is not this family. It is recorded, and the sweep goes on: unlike
                    // the first draft, one refusal no longer aborts the remaining ids.
                    rejectionCount++;
                    step.Observe("NewEntity(" + id + ") → отказ: " + HResult.Describe(ex));
                }
            }

            step.Observe("ход " + label + ": спрошено " + asked + ", ответило ksEntity " + answered);
        }

        step.Observe("последний спрошенный id: " + lastAttempted
            + "; последний ответивший: " + lastAnswering
            + "; отказов NewEntity всего: " + rejectionCount);
        step.Observe("отказов при получении определения: " + failed.Count
            + (failed.Count == 0 ? string.Empty : " (" + string.Join(", ", failed.Take(10)) + ")"));
        step.Observe("полученные определения (" + tried.Count + ", семейство вращения помечено): "
            + string.Join(" ", tried.Select(t => IsRotationFamily(t[(t.IndexOf('=') + 1)..]) ? "*" + t : t)));

        foreach (var (id, definition) in found)
        {
            step.Observe("id=" + id + " → " + definition);
        }

        var rotated = found.FirstOrDefault(f => f.Definition.Contains("BaseRotated", StringComparison.Ordinal));
        if (rotated.Definition is null && found.Count > 0)
        {
            rotated = found[0];
            step.Observe("ksBaseRotatedDefinition не встретился, но найдено родственное семейство — берётся оно, "
                + "и это записывается как есть, а не выдаётся за базовое вращение.");
        }

        if (rotated.Definition is null)
        {
            step.Fail("ни один id из 1…" + lastAttempted + " не дал определения семейства вращения"
                + (failed.Count > 0 ? "; отказов получения определения: " + failed.Count : string.Empty));
            return null;
        }

        _rotatedType = rotated.Id;
        step.Pass("вращение найдено: NewEntity(" + rotated.Id + ") → " + rotated.Definition);
        return rotated.Id;
    }

    /// <summary>Is this definition name one of the rotation family the catalog discusses?</summary>
    private static bool IsRotationFamily(string name) =>
        name.Contains("Rotated", StringComparison.Ordinal)
        || name.Contains("Rotation", StringComparison.Ordinal)
        || name.Contains("Extrusion", StringComparison.Ordinal);

    /// <summary>
    /// The definition of a freshly created entity, by the direct route.
    /// </summary>
    /// <remarks>
    /// Kept separate from the transferred route so the report can say which of the two answered.
    /// Failures are collected, never swallowed: an id where the vendor refused a definition is a
    /// measurement about the vendor, and a sweep that hides it repeats the mistake the first two
    /// drafts made.
    /// </remarks>
    private static object? GetDefinition(ksEntity entity, List<string> failed, short id, string route)
    {
        try
        {
            return entity.GetDefinition();
        }
        catch (Exception ex)
        {
            failed.Add(id + "/" + route + ":" + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The second route: hand the definition through <c>TransferInterface</c> before casting it.
    /// </summary>
    /// <remarks>
    /// Both the chamfer probe and the hole probe do this before they ask a definition any questions,
    /// and the difference is not cosmetic: a raw <c>__ComObject</c> answers a C# <c>is</c> against an
    /// interop interface with whatever its <c>QueryInterface</c> chooses, and a "no" from that path
    /// is a statement about the wrapper chain rather than about the product (ADR-003 §4).
    /// </remarks>
    private object? TransferDefinition(ksEntity entity, List<string> failed, short id)
    {
        try
        {
            var raw = entity.GetDefinition();
            return raw is null ? null : _app.TransferInterface(raw, 1 /* ksAPI5Auto */, 0);
        }
        catch (Exception ex)
        {
            failed.Add(id + "/передача:" + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>The definition's name, or a marker that says it was not obtained.</summary>
    private static string Name(object? definition) => definition switch
    {
        null => "null",
        ksSketchDefinition => "ksSketchDefinition",
        _ => Api5.RuntimeName(definition),
    };

    /// <summary>
    /// R.1e — the three rotation definitions and the parameter block's own <c>direction</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The metadata settles a question the first drafts got wrong: the definition carries
    /// <c>directionType</c> <em>and</em> the parameter block carries a separate <c>direction</c>
    /// (Int32, read/write). Those are two different fields with two different meanings, and until now
    /// only the first was set. The second is a candidate for the reason <c>Create()</c> refuses.
    /// </para>
    /// <para>
    /// The vendor also ships three separate interfaces for this family —
    /// <c>ksBaseRotatedDefinition</c>, <c>ksBossRotatedDefinition</c>, <c>ksCutRotatedDefinition</c> —
    /// so the ids next to 27 are walked and each cast is reported. A base rotation over a base part
    /// and a rotation of a boss are not the same operation, and which id is which is measured here
    /// rather than assumed from the neighbour.
    /// </para>
    /// </remarks>
    private void VariantSweep()
    {
        _currentStepId = "R.1e";
        var step = _report.Begin("R.1e", "Три определения вращения и поле direction в параметрах",
            "Какие id отвечают каждому из трёх определений и меняет ли direction исход Create()?");

        // Which ids answer which of the three rotation interfaces.
        var part0 = (ksPart)_doc.GetPart(-1);
        foreach (short id in new short[] { 25, 26, 27, 28, 29 })
        {
            try
            {
                var entity = part0.NewEntity(id) as ksEntity;
                if (entity is null)
                {
                    step.Observe("id=" + id + ": ksEntity не получен");
                    continue;
                }

                var definition = entity.GetDefinition();
                var kind = definition switch
                {
                    ksBaseRotatedDefinition => "ksBaseRotatedDefinition",
                    ksBossRotatedDefinition => "ksBossRotatedDefinition",
                    ksCutRotatedDefinition => "ksCutRotatedDefinition",
                    null => "null",
                    _ => "не вращение (" + Api5.RuntimeName(definition) + ")",
                };
                step.Observe("id=" + id + " → " + kind);
                entity.excluded = true;
            }
            catch (Exception ex)
            {
                step.Observe("id=" + id + " → отказ " + HResult.Describe(ex));
            }
        }

        // direction, and the two side angles, varied one at a time over the same profile.
        foreach (var (direction, angleNormal, angleReverse) in new (int, double, double)[]
                 {
                     (0, 360d, 0d),
                     (1, 360d, 0d),
                     (2, 360d, 0d),
                     (1, 180d, 180d),
                 })
        {
            ksPart part;
            ksEntity sketch;
            try
            {
                (part, sketch) = RectangleSketch("R1e-" + step.Observations.Count, withAxis: true, gapMm: 5d);
            }
            catch (Exception ex)
            {
                step.Observe("direction=" + direction + ": профиль не построен — " + ex.Message);
                continue;
            }

            var detail = CreateRotationDetailed(part, sketch, direction, angleNormal, angleReverse);
            part.RebuildModel();
            _doc.RebuildDocument();
            var volume = Api5.Volume(part);
            step.Observe("direction=" + direction + ", angleNormal=" + Api5.Num(angleNormal)
                + ", angleReverse=" + Api5.Num(angleReverse) + ": " + detail
                + ", V=" + Api5.Num(volume));
        }

        step.Pass("варианты определены и измерены — что из них собралось, сказано в наблюдениях");
    }

    /// <summary>
    /// R.1f — the side-vs-direction guard, borrowed from the extrusion defect this project already
    /// paid for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConfigureBase</c> in the shipping adapter carries a measured lesson: <c>directionType</c>
    /// <em>is</em> the direction, so the side passed to <c>SetSideParam</c> must agree with it. When it
    /// did not, a base extrusion answered <c>Create()=false</c> and the feature never appeared, while
    /// with the guard the identical call produced the exact mirror body. Rotation has the same two
    /// fields — <c>directionType</c> on the definition, and <c>SetSideParam(side1, angle)</c> — and
    /// the run above shows <c>direction=1</c> silently zeroing <c>angleNormal</c>, which is the same
    /// signature: a setter that was accepted and then dropped.
    /// </para>
    /// <para>
    /// So the four combinations are walked: both directions against both sides. This is the cheapest
    /// remaining hypothesis, and if it is right it is the same defect twice — which would be worth
    /// knowing in the adapter rather than in a probe.
    /// </para>
    /// </remarks>
    private void SideDirectionMatrix()
    {
        _currentStepId = "R.1f";
        var step = _report.Begin("R.1f", "Матрица «сторона × направление» для вращения",
            "Повторяет ли вращение дефект выдавливания: сторона должна согласоваться с направлением?");

        foreach (var direction in new short[] { 0, 1 })
        {
            foreach (var side1 in new[] { true, false })
            {
                ksPart part;
                ksEntity sketch;
                try
                {
                    (part, sketch) = RectangleSketch(
                        "R1f-" + direction + "-" + side1, withAxis: true, gapMm: 5d);
                }
                catch (Exception ex)
                {
                    step.Observe("direction=" + direction + ", side1=" + side1 + ": профиль не построен — " + ex.Message);
                    continue;
                }

                var result = RotateWithSide(part, sketch, 360d, direction, side1);
                part.RebuildModel();
                _doc.RebuildDocument();
                var volume = Api5.Volume(part);
                var cylinders = Api5.CylinderFaces(part)?.Count ?? 0;
                step.Observe("direction=" + direction + ", side1=" + side1 + ": " + result
                    + ", V=" + Api5.Num(volume) + ", цилиндрических граней " + cylinders);
            }
        }

        step.Pass("матрица измерена — согласование стороны и направления либо работает, либо нет, и это видно выше");
    }

    /// <summary>Rotation with both the direction and the side under the caller's control.</summary>
    private string RotateWithSide(
        ksPart part, ksEntity sketch, double angleDeg, short directionType, bool side1)
    {
        if (_rotatedType is not { } type)
        {
            return "тип вращения не найден";
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return "NewEntity(" + type + ") не дал ksBaseRotatedDefinition";
        }

        var setSketch = Api5.SafeBool(() => definition.SetSketch(sketch));
        definition.directionType = directionType;
        var side = Api5.SafeBool(() => definition.SetSideParam(side1, angleDeg));

        double? readAngle = null;
        var getSide = "—";
        try
        {
            definition.GetSideParam(side1, out var angle);
            readAngle = angle;
            getSide = Api5.Num(angle);
        }
        catch (Exception ex)
        {
            getSide = HResult.Describe(ex);
        }

        var created = Api5.SafeBool(feature.Create) == true;
        return "SetSketch=" + setSketch + ", SetSideParam(" + side1 + "," + Api5.Num(angleDeg) + ")=" + side
            + ", GetSideParam(" + side1 + ")=" + getSide + ", Create()=" + created;
    }
    private string CreateRotationDetailed(
        ksPart part, ksEntity sketch, int direction, double angleNormal, double angleReverse)
    {
        if (_rotatedType is not { } type)
        {
            return "тип вращения не найден";
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return "NewEntity(" + type + ") не дал ksBaseRotatedDefinition";
        }

        definition.SetSketch(sketch);
        definition.directionType = (short)direction;
        var side = Api5.SafeBool(() => definition.SetSideParam(true, angleNormal));

        try
        {
            var raw = definition.RotatedParam();
            if (raw is not ksRotatedParam rotated)
            {
                return "RotatedParam() → " + Api5.RuntimeName(raw);
            }

            rotated.angleNormal = angleNormal;
            rotated.angleReverse = angleReverse;
            rotated.direction = direction;

            var read = "SetSideParam=" + side
                + ", прочитано direction=" + rotated.direction
                + ", angleNormal=" + Api5.Num(rotated.angleNormal)
                + ", angleReverse=" + Api5.Num(rotated.angleReverse);
            var created = Api5.SafeBool(feature.Create) == true;
            return "Create()=" + created + " (" + read + ")";
        }
        catch (Exception ex)
        {
            return "бросило " + HResult.Describe(ex);
        }
    }

    /// <summary>
    /// R.1b — what the definition of a freshly created entity really is.
    /// </summary>
    /// <remarks>
    /// A bare <c>entity.GetDefinition()</c> hands back a raw <c>__ComObject</c> for almost every id,
    /// and a C# cast against it can be refused by the wrapper chain rather than by the object. So the
    /// same question is asked of the ids whose family is already measured — extrusion, chamfer,
    /// fillet — where a working cast proves the mechanism and a refusal is a fact about that family.
    /// </remarks>
    private void RotationFamilyProbe()
    {
        var step = _report.Begin("R.1b", "Чем на самом деле создаётся вращение: определение из эскиза",
            "Даёт ли ksEntity.GetDefinition() из уже созданной сущности-эскиза определение вращения?");

        var seen = new List<string>();
        foreach (var (id, label) in new (short, string)[]
                 {
                     (24, "o3d_baseExtrusion"),
                     (26, "o3d_cutExtrusion"),
                     (33, "o3d_chamfer"),
                     (34, "o3d_fillet"),
                 })
        {
            try
            {
                var part = (ksPart)_doc.GetPart(-1);
                var entity = part.NewEntity(id) as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                object? definition;
                try
                {
                    definition = entity.GetDefinition();
                }
                catch (Exception ex)
                {
                    seen.Add(id + "(" + label + ") → отказ: " + HResult.Describe(ex));
                    continue;
                }

                var name = definition?.GetType().Name ?? "null";
                var cast = definition is ksBaseExtrusionDefinition;
                seen.Add(id + "(" + label + ") → " + name
                    + (cast ? " [приведение к ksBaseExtrusionDefinition: да]" : string.Empty));

                if (definition is null)
                {
                    continue;
                }

                var members = TlbScan.LiveMembers(
                    definition,
                    new[] { "SetSketch", "GetSketch", "SetAxis", "GetAxis", "Axis", "Sketch", "RotatedParam", "directionType" });

                step.Observe("id=" + id + " (" + label + "): " + name + ", приведение к ksBaseExtrusionDefinition: " + cast);
                step.Observe("  живые члены определения: " + string.Join("; ", members.Select(m => m.Key + "=" + m.Value)));

                entity.excluded = true;
            }
            catch (Exception ex)
            {
                step.Observe("id=" + id + " (" + label + ") бросил " + HResult.Describe(ex));
            }
        }

        step.Observe("свод: " + string.Join(" | ", seen));
        step.Pass("семейство выдавленных определений измерено ленивым маршрутом (определение запрашивается у типа, "
            + "который уже создан, а не у голого номера)");
    }

    /// <summary>
    /// The swift sweep R.1 was supposed to be.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists next to R.1.</b> R.1 asks each id twice and re-acquires the part every
    /// iteration; over 1…60 that is 120 cross-process calls and it took minutes without ever leaving
    /// the apartment. The same question — "which id has a rotated definition" — is answerable with one
    /// call per id: the chatty part of the sweep is the diagnostics, not the probe. So this walks the
    /// band again, cheaply, and the two steps together separate "the band is wrong" from "the
    /// diagnostics are expensive".
    /// </remarks>
    private short? QuickSweep(short first, short last)
    {
        var step = _report.Begin("R.1a", "Быстрый проход id " + first + "…" + last + ": только определение вращения",
            "Есть ли в диапазоне id определение типа *Rotated* хоть у одного номера?");

        var part = (ksPart)_doc.GetPart(-1);
        var hits = new List<(short Id, string Definition)>();
        var refusals = 0;
        var answered = 0;
        short lastAsked = 0;
        for (short id = first; id <= last; id++)
        {
            lastAsked = id;
            try
            {
                if (part.NewEntity(id) is not ksEntity entity)
                {
                    continue;
                }

                answered++;
                object? definition;
                try
                {
                    definition = entity.GetDefinition();
                }
                catch (Exception)
                {
                    refusals++;
                    continue;
                }

                entity.excluded = true;

                var cast = definition as ksBaseRotatedDefinition;
                if (cast is not null)
                {
                    hits.Add((id, definition!.GetType().Name + " [приведение к ksBaseRotatedDefinition: да]"));
                    continue;
                }

                if (definition is ksBaseExtrusionDefinition or ksCutExtrusionDefinition)
                {
                    hits.Add((id, definition.GetType().Name + " [выдавливание, не вращение]"));
                    continue;
                }

                // Ask the live object directly whether it is the rotated family. This is a question
                // to the object, not a guess about a number, and its answer is a measurement.
                try
                {
                    _ = definition!.GetType();
                    var raw = _app.TransferInterface(definition, 1 /* ksAPI5Auto */, 0);
                    if (raw is ksBaseRotatedDefinition)
                    {
                        hits.Add((id, "ksBaseRotatedDefinition через TransferInterface"));
                    }
                }
                catch (Exception)
                {
                    refusals++;
                }
            }
            catch (Exception)
            {
                refusals++;
            }
        }

        step.Observe("проход " + first + "…" + last + ": ответило ksEntity " + answered
            + ", отказов определения " + refusals + ", последний спрошенный id " + lastAsked);
        foreach (var (id, definition) in hits)
        {
            step.Observe("id=" + id + " → " + definition);
        }

        var rotated = hits.FirstOrDefault(h => h.Definition.Contains("ksBaseRotatedDefinition", StringComparison.Ordinal));
        if (rotated.Definition is null)
        {
            step.Fail("среди id " + first + "…" + last + " определения вращения нет"
                + (hits.Count == 0 ? "; выдавленных определений тоже не встречено" : "; встречены: "
                    + string.Join(", ", hits.Select(h => h.Id + "→" + h.Definition))));
            return null;
        }

        step.Pass("вращение найдено быстрым проходом: NewEntity(" + rotated.Id + ")");
        _rotatedType = rotated.Id;
        return rotated.Id;
    }

    // ══════════════════════════════════════════════════════════════ building ══

    /// <summary>
    /// R.1d — what a rotation actually needs from its sketch, asked as four controlled variants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reading so far: the rotated definition is found (id 27), the angle reads back, the profile
    /// is accepted, and <c>Create()</c> still returns false. Three candidate explanations remain and
    /// they are cheap to separate: the axis is not in the sketch at all; the axis is there but not
    /// styled as a construction line; or the profile's position relative to the intended axis is
    /// degenerate (the rectangle touches the axis, so the swept body would be self-touching).
    /// </para>
    /// <para>
    /// Each variant differs from the next in exactly one thing, and the report keeps the four outcomes
    /// side by side. Whether an axis line is needed at all is <b>not</b> assumed — it is the first
    /// variant.
    /// </para>
    /// </remarks>
    private void AxisVariants()
    {
        _currentStepId = "R.1d";
        var step = _report.Begin("R.1d", "Что вращению нужно от эскиза: четыре варианта",
            "Нужна ли вообще осевая линия, и играет ли роль её стиль?");

        var variants = new (string Label, bool Axis, double Gap, int AxisStyle)[]
        {
            ("без осевой, прямоугольник от оси", false, 0d, 1),
            ("осевой отрезок стилем 1, прямоугольник от оси", true, 0d, 1),
            ("осевой отрезок стилем 26 (особый диапазон), от оси", true, 0d, 26),
            ("осевой отрезок стилем 22, прямоугольник от оси", true, 0d, 22),
            ("осевой отрезок стилем 1, зазор 5 мм от оси", true, 5d, 1),
        };

        foreach (var variant in variants)
        {
            ksPart part;
            ksEntity sketch;
            try
            {
                (part, sketch) = RectangleSketch(
                    "R1d-" + step.Observations.Count, withAxis: variant.Axis, gapMm: variant.Gap, axisStyle: variant.AxisStyle);
            }
            catch (Exception ex)
            {
                step.Observe(variant.Label + ": построение не удалось — " + ex.Message);
                continue;
            }

            var (created, detail) = CreateRotation(part, sketch, 360d, 0);
            part.RebuildModel();
            _doc.RebuildDocument();
            var volume = Api5.Volume(part);
            var cylinders = Api5.CylinderFaces(part)?.Count ?? 0;
            step.Observe(variant.Label + ": Create() → " + created
                + ", V=" + Api5.Num(volume)
                + ", цилиндрических граней " + cylinders
                + " (" + detail + ")");
        }

        step.Pass("четыре варианта измерены — какой из них работает, сказано в наблюдениях выше");
    }

    /// <summary>
    /// A fresh document holding one rectangle sketch, optionally carrying a construction axis.
    /// The rectangle spans u ∈ [gap, gap + r], v ∈ [−h/2, h/2], so a full turn about the v axis
    /// sweeps exactly π·r²·h and a half turn exactly half of it.
    /// </summary>
    private (ksPart Part, ksEntity Sketch) RectangleSketch(
        string name, bool withAxis, double gapMm = 0d, int axisStyle = AxisStyle)
    {
        var part = (ksPart)_doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane)
        {
            throw new InvalidOperationException(name + ": плоскости XOY нет");
        }

        if (part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        var u0 = gapMm;
        var u1 = gapMm + RadiusMm;
        var v0 = -HeightMm / 2d;
        var v1 = HeightMm / 2d;
        int? axisRef = null;
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
            if (withAxis)
            {
                axisRef = DrawAxis(editor, v0, v1, axisStyle);
            }
        }

        definition.EndEdit();
        part.RebuildModel();
        _doc.RebuildDocument();
        _lastSketchHadAxis = axisRef is not null;
        if (!_lastSketchHadAxis && withAxis)
        {
            // The profile was asked for an axis and did not get one drawn. Say so here rather than
            // letting the rotation's own failure speak for it: a rotation that does not rebuild
            // because it had no axis is a different measurement from one that refuses a good profile.
            _report.Note(_currentStepId, "ВНИМАНИЕ: осевая линия в эскизе не нарисована (ksLineSeg вернул 0)");
        }

        return (part, sketch);
    }

    /// <summary>
    /// The construction line the rotation is meant to turn about, drawn as an ordinary segment and
    /// then given a style by number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The metadata closes off the obvious route: <c>ksAxisLineParam</c> declares only <c>Init()</c>
    /// with no arguments plus <c>GetBegPoint</c>/<c>GetEndPoint</c> — it can <b>read</b> an axis, not
    /// author one — and <c>ksDocument2D</c> does not declare <c>AxisLineParam</c> at all. What the
    /// document does declare is <c>ksSetObjectStyle(obj, style)</c> and <c>ksGetObjectStyle(obj)</c>,
    /// so the line is drawn normally and then given the axis style.
    /// </para>
    /// <para>
    /// <b>Which number that style is is not in the metadata</b> — the styles live in the
    /// application's own style library, one <c>spt*.lyt</c> file on disk — so this walks the numbers
    /// and reports what each one produced rather than asserting one. The walk is bounded and its
    /// outcome is the first style that makes the line read back as an axis.
    /// </para>
    /// </remarks>
    private int? DrawAxis(ksDocument2D editor, double v0, double v1, int style = AxisStyle)
    {
        // The style is passed at CREATION, not applied afterwards. The first form of this method drew
        // the segment as ksCSNormal (1) and then called ksSetObjectStyle — which means the segment
        // had already joined the sketch's region before it was re-styled, and R.16 measured that a
        // fifth segment drawn this way makes the sketch refuse to extrude at every gap (0, 1 and
        // 5 mm). A construction segment (ksCSConstruction = 6) is explicitly not part of the region,
        // so it must be born that way.
        var reference = editor.ksLineSeg(0d, v0, 0d, v1, style);
        if (reference == 0)
        {
            return null;
        }

        var original = editor.ksGetObjectStyle(reference);
        editor.ksSetObjectStyle(reference, style);
        var after = editor.ksGetObjectStyle(reference);
        _report.Note(_currentStepId, "отрезок оси: ref=" + reference + ", стиль при создании=" + style
            + ", прочитан до перезаписи=" + original + ", после " + after);
        return reference;
    }

    /// <summary>
    /// R.1c — which style number turns an ordinary segment into a construction axis.
    /// </summary>
    /// <remarks>
    /// Carried as its own step because it is the one question in SM-03 whose answer is a <em>number
    /// outside the type library</em>: the axis is not a separate object in this API, it is a segment
    /// wearing the axis style, and the style table belongs to the application's per-install style
    /// library. Reading the styles back is measured here rather than guessed from a document.
    /// </remarks>
    private int? FindAxisStyle()
    {
        var step = _report.Begin("R.1c", "Номер стиля осевой линии",
            "Каким номером ksSetObjectStyle делает отрезок осевой линией?");

        var part = (ksPart)_doc.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("эскиз для опыта со стилями не создан");
            return null;
        }

        sketch.name = "R1c-styles";
        definition.SetPlane(plane);
        sketch.Create();

        int? axisStyle = null;
        var readings = new List<string>();
        try
        {
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() вернул не ksDocument2D");
                return null;
            }

            for (var style = 1; style <= 40; style++)
            {
                var reference = editor.ksLineSeg(0d, style * 2d, 10d, style * 2d, 1);
                if (reference == 0)
                {
                    readings.Add(style + ":отрезок не создан");
                    continue;
                }

                // Style 0 is the "leave it alone" value; the others are asked about and read back,
                // because the interesting number is the one the document itself reports afterwards.
                try
                {
                    editor.ksSetObjectStyle(reference, style);
                }
                catch (Exception ex)
                {
                    readings.Add(style + ":отказ " + HResult.Describe(ex));
                    continue;
                }

                var readBack = editor.ksGetObjectStyle(reference);
                readings.Add(style + "→" + readBack);

                // What makes a segment an axis is not its style number in the abstract but whether the
                // axis reader recognises it, so the number that survives is the one whose segment
                // answers GetBegPoint after the style is applied.
                if (readBack == style && axisStyle is null)
                {
                    axisStyle = style;
                }
            }

            definition.EndEdit();
        }
        catch (Exception ex)
        {
            step.Fail("опыт со стилями прерван: " + HResult.Describe(ex));
            return null;
        }

        step.Observe("прочитано стилей: " + readings.Count);
        step.Observe("стиль→прочитанный номер: " + string.Join(", ", readings.Take(40)));

        if (axisStyle is null)
        {
            step.Fail("ни один номер от 1 до 40 не принялся как стиль отрезка");
            return null;
        }

        step.Pass("стили принимаются документом; первый принятый номер: " + axisStyle
            + " (осевой линией он от этого не становится — это отдельный вопрос)");
        return axisStyle;
    }

    /// <summary>Creates the rotation over the sketch and returns the raw outcome, unjudged.</summary>
    private (bool Created, string Detail) CreateRotation(
        ksPart part, ksEntity sketch, double angleDeg, short directionType, int? axisStyle = null)
    {
        if (_rotatedType is not { } type)
        {
            return (false, "тип вращения не найден");
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return (false, "NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
        }

        definition.SetSketch(sketch);
        definition.directionType = directionType;

        var notes = new List<string>();
        try
        {
            // The documented route to the angle is SetSideParam(side1, angle) — the metadata lists it
            // first-class on this definition, alongside GetSideParam which reads it back. RotatedParam
            // is the parameter block, and the earlier runs showed its angleNormal alone does not make
            // Create() succeed, so both are set and both are read back.
            var sideOk = Api5.SafeBool(() => definition.SetSideParam(true, angleDeg));
            notes.Add("SetSideParam(true," + Api5.Num(angleDeg) + ") → " + sideOk);

            double? readSide = null;
            try
            {
                definition.GetSideParam(true, out var angle);
                readSide = angle;
            }
            catch (Exception ex)
            {
                notes.Add("GetSideParam бросил " + HResult.Describe(ex));
            }

            notes.Add("GetSideParam(true) → " + Api5.Num(readSide));

            var raw = definition.RotatedParam();
            if (raw is not ksRotatedParam rotated)
            {
                notes.Add("RotatedParam() → " + Api5.RuntimeName(raw));
                return (false, string.Join("; ", notes));
            }

            rotated.angleNormal = angleDeg;
            notes.Add("angleNormal=" + Api5.Num(rotated.angleNormal)
                + " angleReverse=" + Api5.Num(rotated.angleReverse)
                + " direction=" + rotated.direction);

            var created = Api5.SafeBool(feature.Create) == true;
            if (!created)
            {
                // Nothing to guess at: the object said no. The notes say what was asked of it.
                return (false, string.Join("; ", notes));
            }

            return (true, string.Join("; ", notes));
        }
        catch (Exception ex)
        {
            notes.Add("построение бросило " + HResult.Describe(ex));
            return (false, string.Join("; ", notes));
        }
    }

    // ══════════════════════════════════════════════════════════════ steps ══

    /// <summary>
    /// R.6 — how a real construction axis is authored, measured rather than guessed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rotation rung before this one ends with <c>Create()=false</c>, and the reason is the same
    /// in all of them: the sketch holds a rectangle plus an ordinary segment that was <em>relabelled</em>
    /// with <c>ksSetObjectStyle</c>, and a relabelled segment is not a construction axis. R.1c already
    /// said as much in its own conclusion — "стили принимаются документом; осевой линией он от этого
    /// не становится". So the missing piece is not a parameter of the rotation definition but the axis
    /// object itself.
    /// </para>
    /// <para>
    /// The type library answers where to look: <c>ksLineSegParam</c> exists as a parameter block, and
    /// <c>ksSetObjParam(referObj, parType, param)</c> is the documented way to put one onto a drawn
    /// object. <c>ksAxisLineParam</c> also exists, but the metadata declares only <c>Init()</c>,
    /// <c>GetBegPoint</c> and <c>GetEndPoint</c> on it — it can read an axis and cannot author one — so
    /// the segment-parameter route is the one worth measuring.
    /// </para>
    /// <para>
    /// What is measured here, in order: the live member list of <c>ksLineSegParam</c>; what
    /// <c>ksGetObjParam</c> returns for each <c>parType</c> on a plain segment; and finally whether a
    /// rotation over a sketch whose axis came from that route produces a body. The last one is the only
    /// one that decides anything, and a route that authors no axis will simply show Create()=false again
    /// — recorded, not hidden.
    /// </para>
    /// </remarks>
    private void AxisAuthoringRoute()
    {
        _currentStepId = "R.6";
        var step = _report.Begin("R.6", "Чем на самом деле заводится осевая линия",
            "ksLineSegParam через ksSetObjParam — это тот маршрут, которого не хватало вращению?");

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R6-param", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("определение эскиза недоступно");
            return;
        }

        try
        {
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() вернул не ksDocument2D");
                return;
            }

            var segment = editor.ksLineSeg(0d, -HeightMm / 2d, 0d, HeightMm / 2d, 1);
            step.Observe("отрезок для опыта: ref=" + segment);
            if (segment == 0)
            {
                step.Fail("отрезок не создан — измерять параметры не на чем");
                return;
            }

            // The live member list of the parameter block, so the next step is not a guess from
            // names in a binary. A parameter block that cannot be asked for is not a route.
            //
            // The block is taken from the interop ASSEMBLY (`new Kompas6API5.LineSegParamClass()`),
            // not by ProgID. Measured here: every `Kompas6API5.*` ProgID — `ksLineSegParam`,
            // `LineSegParamClass`, even `Kompas6API5.Application` — is absent from HKEY_CLASSES_ROOT,
            // because this installation registers its COM classes with a manifest rather than in the
            // registry. `Activator.CreateInstance(Type.GetTypeFromProgID(...))` therefore answered
            // CO_E_CLASSSTRING on every parType and this step measured nothing on its first run. The
            // co-class is declared in the interop, so it is constructed directly.
            foreach (var parType in new[] { 1, 2, 3, 4, 5, 6 })
            {
                try
                {
                    var param = new LineSegParamClass();
                    var code = editor.ksGetObjParam(segment, param, parType);
                    var members = string.Join(", ", param.GetType().GetMembers()
                        .Where(m => m.MemberType is System.Reflection.MemberTypes.Property
                            or System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Field)
                        .Select(m => m.Name)
                        .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_"))
                        .Distinct()
                        .OrderBy(n => n)
                        .Take(24));
                    step.Observe($"parType={parType}: code={code} члены=[{members}]");
                }
                catch (Exception ex)
                {
                    step.Observe($"parType={parType}: {HResult.Describe(ex)}");
                }
            }

            definition.EndEdit();
            part.RebuildModel();
            _doc.RebuildDocument();
        }
        catch (Exception ex)
        {
            step.Observe("опыт прерван: " + HResult.Describe(ex));
        }

        var (created, detail) = CreateRotation(part, sketch, angleDeg: 360d, directionType: 0);
        step.Observe("вращение поверх этого эскиза: Create() → " + created + "; " + detail);

        double? volume = Api5.Volume(part);
        step.Observe("объём: " + Api5.Num(volume) + ", ожидание π·r²·h = "
            + Api5.Num(Math.PI * RadiusMm * RadiusMm * HeightMm));

        if (created && Math.Abs((volume ?? 0d) - Math.PI * RadiusMm * RadiusMm * HeightMm) < 1d)
        {
            step.Pass("осевая линия заведена через ksLineSegParam — вращение построено и объём сошёлся");
        }
        else
        {
            // Honest failure: this route authors no axis either, and saying so keeps the blocker
            // visible instead of letting four earlier red rungs look like an accident of ordering.
            step.Fail("маршрут через ksLineSegParam осевой линии не завёл: Create()=" + created
                + " — блокировка SM-03 остаётся открытой");
        }
    }

    /// <summary>
    /// R.7 — does <c>IRotated.SetAxis</c> build the rotation that <c>ksBaseRotatedDefinition</c> refuses?
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.6 closed the segment-parameter route and left the blocker stated as "there is no way to author
    /// a construction axis". That statement was too strong, and the API7 interface dump already on
    /// disk said so: <c>IRotated</c> (IID <c>{7BB28AD1-CCAE-449C-9086-A97470543089}</c>) declares
    /// <c>_set_Axis(POINTER(IModelObject))</c> — a real <c>SetAxis</c> that takes a model object, which
    /// is exactly the member <c>ksBaseRotatedDefinition</c> does not have.
    /// </para>
    /// <para>
    /// This is also the shape <c>IHoleDisposal</c> already proved: the useful members of a feature are
    /// not on its API5 definition, they are on a <b>separate API7 interface reached by QI on the
    /// transferred object</b>. The hole's placement lives on <c>IHoleDisposal</c>, not on
    /// <c>IHole3D</c>; the rotation's axis may likewise live on <c>IRotated</c>, not on the definition.
    /// </para>
    /// <para>
    /// What is measured, in order: whether the transferred feature answers QI for <c>IRotated</c> at
    /// all; what its <c>Axis</c> currently reads; whether an object can be assigned to it (tried with
    /// both a transferred edge of the profile's plane and an API7 construction object); and finally
    /// whether a body of volume π·r²·h appears. Only the volume decides — a non-null Axis is not proof.
    /// </para>
    /// </remarks>
    private void RotatedSetAxisRoute()
    {
        _currentStepId = "R.7";
        var step = _report.Begin("R.7", "Ось вращения через API7 IRotated.SetAxis",
            "Есть ли у вращения отдельный интерфейс с SetAxis, как IHoleDisposal у отверстия?");

        ksPart part;
        ksEntity sketch;
        try
        {
            // The control body is built FIRST, while the part's single base-operation slot is still
            // free: once the rotation feature exists, a second base extrusion refuses to build (this
            // was measured — the first version of this step built the control afterwards and got
            // Create()=False, which would have read as "no candidate" rather than "wrong order").
            part = (ksPart)_doc.GetPart(-1);
            var control = Api5.BasePlate(part, 20d, 20d, 10d, step, "R7");
            step.Observe("контрольное тело (источник рёбер для оси): "
                + (control is null ? "не построено" : "построено, " + control.name));

            // No axis is drawn in the rotation's own sketch on purpose: if the rotation builds, the
            // axis came from the API7 member and not from the profile, which is the whole question.
            (part, sketch) = RectangleSketch("R7-setaxis", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            step.Fail("NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
            return;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        Api5.SafeBool(() => definition.SetSideParam(true, 360d));

        // The API5 shell is created first so there is something to transfer, exactly as the adapter
        // does: the bridge works on live objects, not on definitions.
        var shellCreated = Api5.SafeBool(feature.Create) == true;
        step.Observe("API5: Create() базового вращения без оси → " + shellCreated
            + " (ожидается False: это и есть измеренный блокер)");

        part.RebuildModel();
        _doc.RebuildDocument();

        // What the shared body already contains, so the verdict below can tell "the rotation built
        // something" from "the rotation built nothing and I read a leftover number".
        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до подачи оси: " + Api5.Num(_bodyVolumeBeforeStep));

        // ── the API7 hop ────────────────────────────────────────────────────────────────────────
        KompasAPI7.IModelObject? object7;
        try
        {
            object7 = _app.TransferInterface(feature, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        }
        catch (Exception ex)
        {
            step.Fail("TransferInterface(признак вращения) бросил " + HResult.Describe(ex));
            return;
        }

        if (object7 is null)
        {
            step.Fail("признак вращения не переносится в API7 как IModelObject — маршрут недостижим");
            return;
        }

        step.Observe("признак → API7: OK (" + Api5.RuntimeName(object7) + ")");

        // QI for IRotated. This is the single measurement the step turns on: if the transferred
        // feature does not even answer for the interface, no amount of parameter setting matters.
        if (object7 is not KompasAPI7.IRotated rotated)
        {
            step.Fail("признак вращения не отвечает на QI(IRotated) — отдельного интерфейса с SetAxis "
                + "у этого признака нет, и маршрут закрыт не догадкой, а типом");
            return;
        }

        step.Observe("QI(IRotated) → OK; члены, доступные живьём: "
            + string.Join(", ", typeof(KompasAPI7.IRotated).GetMembers()
                .Where(m => m.MemberType is System.Reflection.MemberTypes.Property
                    or System.Reflection.MemberTypes.Method)
                .Select(m => m.Name)
                .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_"))
                .Distinct()
                .OrderBy(n => n)));

        // What the axis reads before anything is written. A member that cannot even be read is a
        // different finding from one that reads null, and both are worth printing.
        try
        {
            var before = rotated.Axis;
            step.Observe("Axis до записи: " + (before is null
                ? "null"
                : Api5.RuntimeName(before) + " (IID " + before.GetType().GUID.ToString("B") + ")"));
        }
        catch (Exception ex)
        {
            step.Observe("чтение Axis бросило " + HResult.Describe(ex));
        }

        // The axis object has to come from somewhere. The catalog says the axis source may be a sketch
        // construction line, a built axis, or cylindrical geometry. The candidate here is an edge of
        // the control plate built at the top of this step — real geometry that already exists.
        var candidates = new List<(string Label, object Source)>();
        if (TryAnyEdge(part, out var edge))
        {
            candidates.Add(("ребро контрольного тела", edge!));
        }

        if (candidates.Count == 0)
        {
            step.Observe("ни одного кандидата в ось не нашлось: рёбер у контрольного тела нет");
        }

        // `Profile` is offered alongside `Axis`, because the first run of this step measured that
        // `Axis` alone is accepted and then silently discarded (read back null). The same object
        // interface carries both members, and a rotation with an axis but no profile is not a
        // rotation — so the pair is what is actually being tested here, and testing only half of it
        // would have produced a confident wrong answer.
        var profiles = new List<(string Label, object Source)>();
        try
        {
            if (sketch.GetDefinition() is not null)
            {
                profiles.Add(("эскиз профиля", sketch));
            }
        }
        catch (Exception ex)
        {
            step.Observe("эскиз профиля как кандидат: " + HResult.Describe(ex));
        }

        step.Observe("кандидатов в профиль: " + profiles.Count);

        var anyAccepted = false;
        foreach (var (label, source) in candidates)
        {
            object? transferred = null;
            try
            {
                transferred = _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0);
                step.Observe(label + " → API7: " + (transferred is null ? "null" : "OK"));
                if (transferred is not KompasAPI7.IModelObject axis7)
                {
                    step.Observe(label + ": перенос дал " + Api5.RuntimeName(transferred)
                        + ", а не IModelObject — в SetAxis такое не подать");
                    continue;
                }

                foreach (var (profileLabel, profileSource) in profiles)
                {
                    try
                    {
                        var profile7 = _app.TransferInterface(profileSource, 2 /* ksAPI7Dual */, 0);
                        if (profile7 is KompasAPI7.IModelObject profileObject)
                        {
                            rotated.Profile = profileObject;
                            var readBack = rotated.Profile;
                            step.Observe(profileLabel + ": Profile принят → "
                                + (readBack is null ? "null (не сохранён)" : "сохранён"));
                        }
                        else
                        {
                            step.Observe(profileLabel + ": перенос дал " + Api5.RuntimeName(profile7));
                        }
                    }
                    catch (Exception ex)
                    {
                        step.Observe(profileLabel + ": Profile бросил " + HResult.Describe(ex));
                    }
                }

                rotated.Axis = axis7;
                anyAccepted = true;
                step.Observe(label + ": SetAxis принят без исключения");

                // What the member reads back after the write. An accepted-then-ignored assignment is
                // the failure mode this project has measured before (fillet radius, hole depth), so
                // the read-back is printed rather than assumed.
                var after = rotated.Axis;
                step.Observe(label + ": Axis после записи → " + (after is null
                    ? "null (принято и НЕ сохранено)"
                    : Api5.RuntimeName(after) + " (сохранено)"));
            }
            catch (Exception ex)
            {
                step.Observe(label + ": SetAxis бросил " + HResult.Describe(ex));
            }
        }

        if (!anyAccepted)
        {
            step.Fail("ни один кандидат не принят в IRotated.Axis: SetAxis есть, но подать в него нечего "
                + "из того, что удалось построить");
            return;
        }

        // ── the only thing that decides ─────────────────────────────────────────────────────────
        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        step.Observe("V после сборки: " + Api5.Num(volume)
            + ", ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        // The same gate R.2 needed, and needed here first: this step measured V=4000 for a long time
        // and the number was read as a geometric result, when the document's body is shared with every
        // other step and 4000 was simply what happened to be in it. A volume that did not move is not
        // a small rotation — it is no rotation.
        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - _bodyVolumeBeforeStep.Value) <= 1d)
        {
            step.Fail("SetAxis принят, но сборка не изменила объём тела (V=" + Api5.Num(volume)
                + " — столько же было в документе до шага): признак вращения не построен, "
                + "и число 4000 прежде читалось как его объём ошибочно");
            return;
        }

        if (volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("IRotated.SetAxis построил вращение: объём сошёлся с π·r²·h — блокер SM-03 снят "
                + "маршрутом API7, а не параметрами определения");
        }
        else
        {
            step.Fail("SetAxis принят, но тела аналитического объёма нет (V=" + Api5.Num(volume)
                + ") — маршрут IRotated.Axis заводит ось не так, как ожидалось");
        }
    }

    /// <summary>
    /// R.8 — is a real cylindrical face the object <c>IRotated.Axis</c> will actually hold?
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.7 localized the rotation blocker to a single member: <c>IRotated.Axis</c> accepts an object
    /// and reads back <c>null</c>, while <c>Profile</c> on the same object, over the same transfer,
    /// persists. That leaves exactly one open question — which object the member will hold — and the
    /// catalog names the admissible axis sources itself: a sketch construction line, a built axis, or
    /// <b>cylindrical geometry</b>.
    /// </para>
    /// <para>
    /// The first two are measured and closed (R.1c/R.6: a relabelled segment is not an axis, and the
    /// parameter block is unreachable). R.7 tried an edge — a straight line, which is arguably not
    /// "cylindrical geometry" at all, and may simply be the wrong kind of object. This step tries the
    /// third source properly: a real through hole is drilled so the body contains a genuine cylindrical
    /// face, and that face is offered as the axis.
    /// </para>
    /// <para>
    /// A hole is used rather than a turned cylinder because the hole route is already proven in this
    /// probe suite (R.7's sibling, the hole group, drills through holes at will), and because the
    /// resulting face's axis is exactly the line a rotation about it would turn on.
    /// </para>
    /// </remarks>
    private void RotatedAxisCylinderFace()
    {
        _currentStepId = "R.8";
        var step = _report.Begin("R.8", "Ось вращения — цилиндрическая грань",
            "Возьмёт ли IRotated.Axis настоящую цилиндрическую грань, если ребро он отбросил?");

        ksPart part;
        ksEntity sketch;
        try
        {
            // Control first, while the base-operation slot is free — the same ordering R.7 had to
            // learn. The plate gives the hole something to be drilled into.
            part = (ksPart)_doc.GetPart(-1);
            var control = Api5.BasePlate(part, 100d, 80d, 10d, step, "R8");
            step.Observe("плита под отверстие: " + (control is null ? "не построена" : "построена"));
            _plateOnlyVolume = Api5.Volume(part);
            step.Observe("объём плиты до сверления: " + Api5.Num(_plateOnlyVolume)
                + " (ожидание 100×80×10 = 80000)");

            (part, sketch) = RectangleSketch("R8-profile", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        // ── make a genuine cylindrical face ─────────────────────────────────────────────────────
        var document7 = _app.TransferInterface(_doc, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IKompasDocument3D;
        var container = document7?.TopPart as KompasAPI7.IModelContainer;
        var holes = container?.Holes3D;
        if (holes is null)
        {
            step.Fail("Holes3D недоступно — цилиндрическую грань сделать нечем");
            return;
        }

        // The top face of the plate, where the hole is drilled. Reached through the same reader the
        // hole group uses, so the drill lands on a face rather than in the air.
        var topFace = LargestPlanarFace(part, step);
        if (topFace is null)
        {
            step.Fail("верхняя грань плиты не найдена — отверстие просверлить негде");
            return;
        }

        var transferredFace = _app.TransferInterface(topFace, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        if (transferredFace is null)
        {
            step.Fail("грань плиты не переносится в API7");
            return;
        }

        try
        {
            var hole = holes.Add();
            if (hole is not KompasAPI7.IHoleDisposal disposal)
            {
                step.Fail("IHoles3D.Add() не дал IHoleDisposal");
                return;
            }

            hole.Diameter = 8d;
            // ksDTReachThrough — a through hole, so the wall is a full cylinder rather than a pocket.
            // Spelled as the vendor enum rather than `1`: the member is typed, and a bare integer is
            // exactly the kind of guess this project forbids.
            hole.DepthType = Kompas6Constants3D.ksDepthTypeEnum.ksDTReachThrough;
            disposal.BaseSurface = transferredFace;
            disposal.Perpendicular = true;

            var created = Api5.SafeBool(() => hole.Update());
            part.RebuildModel();
            _doc.RebuildDocument();
            step.Observe("сквозное отверстие Ø8: Update() → " + created);
        }
        catch (Exception ex)
        {
            step.Fail("отверстие не построено: " + HResult.Describe(ex));
            return;
        }

        // ── find the cylindrical face it made ───────────────────────────────────────────────────
        var cylinders = Api5.CylinderFaces(part);
        step.Observe("цилиндрических граней в теле: " + cylinders.Count);
        foreach (var face in cylinders.Take(4))
        {
            step.Observe("  грань #" + face.Index + ": r=" + Api5.Num(face.CylinderRadius)
                + " h=" + Api5.Num(face.CylinderHeight)
                + " axis=" + (face.CylinderAxis is null
                    ? "null"
                    : "[" + string.Join(", ", face.CylinderAxis.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + "]"));
        }

        if (cylinders.Count == 0)
        {
            step.Fail("отверстие не дало ни одной цилиндрической грани — подавать в Axis нечего");
            return;
        }

        // ── the rotation, with the cylindrical face as its axis ─────────────────────────────────
        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден");
            return;
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            step.Fail("NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
            return;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        Api5.SafeBool(() => definition.SetSideParam(true, 360d));

        var object7 = _app.TransferInterface(feature, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        if (object7 is not KompasAPI7.IRotated rotated)
        {
            step.Fail("признак вращения не отвечает на QI(IRotated)");
            return;
        }

        step.Observe("Axis до записи: " + (rotated.Axis is null ? "null" : "не null"));

        // Profile first, since R.7 measured it persists: if the pair still fails, the fault is in Axis
        // and not in a missing profile.
        try
        {
            var profile7 = _app.TransferInterface(sketch, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
            if (profile7 is not null)
            {
                rotated.Profile = profile7;
                step.Observe("Profile принят → " + (rotated.Profile is null ? "null (не сохранён)" : "сохранён"));
            }
        }
        catch (Exception ex)
        {
            step.Observe("Profile бросил " + HResult.Describe(ex));
        }

        // The candidate: the cylindrical face itself, transferred.
        var cylinderFace = FindCylinderFaceSource(part, cylinders[0]);
        if (cylinderFace is null)
        {
            step.Fail("цилиндрическая грань найдена при чтении, но не найдена как объект для переноса");
            return;
        }

        try
        {
            var transferred = _app.TransferInterface(cylinderFace, 2 /* ksAPI7Dual */, 0);
            step.Observe("цилиндрическая грань → API7: " + (transferred is null ? "null" : "OK"));
            if (transferred is not KompasAPI7.IModelObject axis7)
            {
                step.Fail("перенос цилиндрической грани дал " + Api5.RuntimeName(transferred)
                    + ", а не IModelObject");
                return;
            }

            rotated.Axis = axis7;
            step.Observe("цилиндрическая грань: SetAxis принят без исключения");

            var axisAfter = rotated.Axis;
            step.Observe("цилиндрическая грань: Axis после записи → " + (axisAfter is null
                ? "null (принято и НЕ сохранено)"
                : Api5.RuntimeName(axisAfter) + " (сохранено)"));

            // What the feature says about itself, asked of the object that was just written to. If a
            // real axis landed, Valid/Updated should reflect a buildable feature; if the write only
            // took on the surface, these read back the state of a feature that still has no axis.
            var live = TlbScan.LiveMembers(rotated, new[]
            {
                "Axis", "Profile", "Angle", "Direction", "Valid", "Updated", "RotatedType",
            });
            foreach (var pair in live)
            {
                step.Observe("  признак вращения: " + pair.Key + " = " + pair.Value);
            }
        }
        catch (Exception ex)
        {
            step.Fail("SetAxis с цилиндрической гранью бросил " + HResult.Describe(ex));
            return;
        }

        // ── the only thing that decides ─────────────────────────────────────────────────────────
        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        var holeRemoved = Math.PI * 4d * 4d * 10d;
        // The baseline is the plate as it was actually measured before drilling. The analytical
        // 100×80×10 is printed beside it, because they disagree — the modeller's own volume is the
        // only one that may be subtracted from.
        var baseline = _plateOnlyVolume;
        step.Observe("V после сборки: " + Api5.Num(volume)
            + "; плита измерена до сверления: " + Api5.Num(_plateOnlyVolume)
            + " (аналитическая 100×80×10 = " + Api5.Num(100d * 80d * 10d) + ")");
        if (volume is not null && baseline is not null)
        {
            var drop = baseline.Value - volume.Value;
            var ratio = drop / holeRemoved;
            // Arithmetic only. The ratio being 2.000 to four places is noteworthy and is printed as
            // the number it is; it is NOT written up as a law of how the hole is sized. `2` also
            // equals (√2·(2r))²/(2r)², so a diameter of √2·(2r) fits the digits equally well, and a
            // law that fits two candidate stories is not a measurement of either.
            step.Observe("просадка против измеренной плиты: " + Api5.Num(drop)
                + " — это ровно K·(πr²h) при K=" + Api5.Num(ratio)
                + " (отверстие Ø8 сквозь плиту дало бы " + Api5.Num(holeRemoved) + ")");
            if (Math.Abs(ratio - 2d) < 1e-3)
            {
                step.Observe("ЗАМЕЧАНИЕ: просадка ровно вдвое больше ожидаемой. Диаметр √2·D=11.314 "
                    + "или глубина 2·h=20 подходят по цифрам одинаково — какой из них верен, это "
                    + "наблюдение НЕ устанавливает, и записывать закон по нему нельзя");
            }
        }

        step.Observe("для сравнения, ожидание вращения профиля 20×40: π·r²·h = " + Api5.Num(FullTurnVolume));

        // A volume alone does not say what was built, and this project has already been burned once by
        // fitting a law to a single number (`4/tan` matched one row by coincidence). So the resulting
        // body is read back: how many cylindrical faces, of what radius and height. That is what makes
        // the difference between "a body appeared" and "the body I asked for appeared".
        var cylindersAfter = Api5.CylinderFaces(part);
        step.Observe("цилиндрических граней после сборки: " + cylindersAfter.Count);
        foreach (var face in cylindersAfter.Take(8))
        {
            step.Observe("  грань #" + face.Index + ": r=" + Api5.Num(face.CylinderRadius)
                + " h=" + Api5.Num(face.CylinderHeight)
                + " axis=" + (face.CylinderAxis is null
                    ? "null"
                    : "[" + string.Join(", ", face.CylinderAxis.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) + "]"));
        }

        var hasRequestedCylinder = cylindersAfter.Any(f =>
            f.CylinderRadius is double r && Math.Abs(r - RadiusMm) <= 0.01d
            && f.CylinderHeight is double h && Math.Abs(h - HeightMm) <= 0.5d);

        // Guarded against the comparison that flatters the result. An earlier version of this step
        // compared against `100×80×10 − πr²h` and reported a "+3497 gain" for a rotation that in fact
        // added nothing: the modeller's own plate reads 84000, not 80000, so the subtracted hole was
        // pure fiction. The baseline is now the plate measured before drilling, and nothing else.
        var grew = volume is not null && baseline is not null && volume.Value > baseline.Value + 1d;

        if (volume is not null && hasRequestedCylinder)
        {
            step.Pass("цилиндрическая грань удержана в Axis и получен именно запрошенный цилиндр "
                + "Ø" + (2 * RadiusMm) + "×" + HeightMm + ": V=" + Api5.Num(volume));
        }
        else if (grew)
        {
            // A body appeared, but not the one that was asked for. Reported as its own outcome rather
            // than as a pass: "Axis held the object" and "the rotation is usable" are different claims.
            step.Fail("Axis удержал цилиндрическую грань и тело появилось (V=" + Api5.Num(volume)
                + "), но это НЕ запрошенный цилиндр Ø" + (2 * RadiusMm) + "×" + HeightMm
                + " — маршрут найден, закон построения ещё не измерен");
        }
        else
        {
            // No gain over the un-drilled plate: whatever the Axis write did, the rebuild added no
            // material at all. Named for what it is. The interesting by-product — the hole cut at
            // √2·(2r) rather than 2r — is recorded as a measurement, NOT as an explanation: the
            // ratio 1.4142 is sqrt(2) exactly, and this project has already mistaken a clean-looking
            // numeric coincidence for a law once.
            step.Fail("Axis не удержал и цилиндрическую грань: сборка не добавила материала "
                + "(V=" + Api5.Num(volume) + " ≤ измеренной плиты " + Api5.Num(baseline)
                + ") — тела вращения нет");
        }
    }

    /// <summary>
    /// R.9 — does API7 expose a genuine <b>axis object</b>, created as an object rather than derived
    /// from a profile?
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the first of the two sources the rotation blocker was narrowed down to. It comes from
    /// the type library rather than from a guess: <c>IModelContainer</c> declares <c>Axes3D</c>
    /// alongside the <c>Holes3D</c> the hole group already proved, and <c>IAxes3D.Add</c> takes a
    /// <c>ksObj3dTypeEnum</c> and hands back an <c>IAxis3D</c>. The enum's axis members are
    /// <c>o3d_axis2Planes(9)</c>, <c>o3d_axis2Points(10)</c>, <c>o3d_axisConeFace(11)</c>,
    /// <c>o3d_axisEdge(12)</c>, <c>o3d_axisOperation(13)</c>, plus the three global axes
    /// <c>o3d_axisOX/OY/OZ(71…73)</c>.
    /// </para>
    /// <para>
    /// <b>What this step decides.</b> A rotation needs an axis object. Every earlier rung offered the
    /// feature something it already had — an edge, a face, a sketch — and the member either discarded
    /// it or built nothing. Here the axis is <em>created as an axis</em> first, which is the one
    /// composition not yet tried. The step walks all five axis types, reports which of them
    /// <c>Add</c> actually builds, and for those that read back as a real object it goes on to offer
    /// the created axis to <c>IRotated.Axis</c> and rebuilds.
    /// </para>
    /// <para>
    /// The verdict is deliberately three-way and the middle case is not a pass: an axis object that
    /// exists but does not make the rotation build leaves the blocker exactly where it was.
    /// </para>
    /// </remarks>
    private void Axis3DContainerRoute()
    {
        _currentStepId = "R.9";
        var step = _report.Begin("R.9", "API7: ось как отдельный объект через контейнер Axes3D",
            "Есть ли в API7 контейнер осей, и даёт ли он ось, которую примет вращение?");

        var type = _rotatedType;
        if (type is null)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        // The control body first, while the base-operation slot is free — the ordering R.7 had to learn.
        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R9");
            step.Observe("контрольное тело (источник геометрии для осей): "
                + (plate is null ? "не построено" : "построено, " + plate.name));
            _bodyVolumeBeforeStep = Api5.Volume(part);
            step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        var document7 = _app.TransferInterface(_doc, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IKompasDocument3D;
        var topPart = document7?.TopPart;
        if (topPart is null)
        {
            step.Fail("TopPart недоступен — до осей не добраться");
            return;
        }

        // `Axes3D` is declared on `IAuxiliaryGeomContainer`, which is NOT a base of the container the
        // hole group uses and is returned by no member of any interface in the assembly. It is reached
        // the same way the hole's own working members were: by a QI on the live part object. The cast
        // is the measurement, and a failure here is reported rather than assumed away.
        step.Observe("TopPart → " + Api5.RuntimeName(topPart));
        var axesContainer = topPart as KompasAPI7.IAuxiliaryGeomContainer;
        if (axesContainer is null)
        {
            step.Fail("TopPart не отвечает на QI(IAuxiliaryGeomContainer) — контейнер вспомогательной "
                + "геометрии недостижим с этого объекта, и Axes3D взять неоткуда");
            return;
        }

        step.Observe("QI(IAuxiliaryGeomContainer) → OK");

        KompasAPI7.IAxes3D? axes;
        try
        {
            axes = axesContainer.Axes3D;
        }
        catch (Exception ex)
        {
            step.Fail("AuxiliaryGeomContainer.Axes3D бросил " + HResult.Describe(ex));
            return;
        }

        if (axes is null)
        {
            step.Fail("container.Axes3D вернул null — контейнера осей в API7 нет, и этот маршрут закрыт");
            return;
        }

        step.Observe("Axes3D → " + Api5.RuntimeName(axes) + "; осей до создания: " + axes.Count);

        // Every axis type the enum offers. Spelled as the vendor enum rather than as bare integers:
        // these numbers go into the catalog, and a literal 9 would be exactly the kind of guess this
        // project forbids.
        var types = new (Kompas6Constants3D.ksObj3dTypeEnum Type, string Label)[]
        {
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisEdge, "o3d_axisEdge(12)"),
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisOperation, "o3d_axisOperation(13)"),
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Planes, "o3d_axis2Planes(9)"),
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points, "o3d_axis2Points(10)"),
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisConeFace, "o3d_axisConeFace(11)"),
            (Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisOX, "o3d_axisOX(71)"),
        };

        // What geometry can be fed to the axes that need a source. Reached through the same readers
        // the rest of the suite uses, then transferred, because the axis members expect API7 objects.
        var edge = Transfer(part, TryAnyEdge(part, out var edgeObj) ? edgeObj : null);
        var plane1 = Transfer(part, LargestPlanarFace(part, step));
        var cylinders = Api5.CylinderFaces(part);
        var cylinder = Transfer(part,
            cylinders.Count > 0 ? FindCylinderFaceSource(part, cylinders[0]) : null);
        step.Observe("доступные источники: ребро=" + (edge is null ? "нет" : "есть")
            + ", плоскость=" + (plane1 is null ? "нет" : "есть")
            + ", цилиндрическая грань=" + (cylinder is null ? "нет" : "есть"));

        var built = new List<(string Label, KompasAPI7.IAxis3D Axis)>();
        foreach (var (axisType, label) in types)
        {
            try
            {
                var created = axes.Add(axisType);
                if (created is null)
                {
                    step.Observe(label + ": Add → null");
                    continue;
                }

                // An axis built without its source may be an empty shell. The sources are attached
                // where the concrete interface offers one, and the failure to cast is itself worth
                // reporting: it says the type came back as a different axis class than asked for.
                var attached = AttachAxisSource(created, edge, plane1, cylinder, step);

                part.RebuildModel();
                _doc.RebuildDocument();

                var readBack = axes.Count;
                step.Observe(label + ": Add → OK (" + Api5.RuntimeName(created) + ")"
                    + ", источник: " + attached + ", осей стало " + readBack
                    + ", Name=" + SafeName(created));
                built.Add((label, created));
            }
            catch (Exception ex)
            {
                step.Observe(label + ": Add бросил " + HResult.Describe(ex));
            }
        }

        if (built.Count == 0)
        {
            step.Fail("ни один тип оси не создался через Axes3D.Add — контейнер есть, а оси не строятся");
            return;
        }

        // ── does an axis object make the rotation build? ────────────────────────────────────────
        var outcomes = new List<string>();
        foreach (var (label, axisObject) in built)
        {
            var (created, detail) = TryRotationWithAxisObject(part, axisObject, step, "R9-" + label);
            outcomes.Add(label + " → Create()=" + created + (created ? "" : ": " + detail));
        }

        step.Observe("вращение с каждой созданной осью: " + string.Join(" | ", outcomes));

        var volume = Api5.Volume(part);
        step.Observe("V после сборки: " + Api5.Num(volume) + " (было "
            + Api5.Num(_bodyVolumeBeforeStep) + "); ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        // The gate this suite has had to learn twice already: the document's body carries every
        // previous step, so a large volume is not evidence that this step built anything. Measured
        // before the verdict, because both numbers here are large and neither belongs to the rotation.
        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "создание осей через Axes3D"))
        {
            return;
        }

        if (volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("ось, созданная объектом в Axes3D, принята вращением и цилиндр построен: "
                + "V=" + Api5.Num(volume) + " = π·r²·h — блокер SM-03 снят");
        }
        else if (outcomes.Any(o => o.Contains("Create()=True")))
        {
            step.Fail("ось создана и Create() вернул True, но тела ожидаемого объёма нет (V="
                + Api5.Num(volume) + ") — маршрут найден, признак не собрался");
        }
        else
        {
            step.Fail("ось как отдельный объект создаётся (" + built.Count + " из " + types.Length
                + ": Add возвращает объект), но вращение её не принимает: Axis читается обратно "
                + "null, Create()=False на каждой. Блокер SM-03 остаётся");
        }
    }

    /// <summary>
    /// R.10 — the axis object reached from an <b>edge or an operation</b> of the real body, then fed
    /// to the rotation.
    /// </summary>
    /// <remarks>
    /// R.9 creates axes from the container and reports which types build. This step closes the loop on
    /// the two concrete axis classes whose source the probe can actually supply — <c>IAxis3DByEdge</c>
    /// (<c>Edge</c>) and <c>IAxis3DByOperation</c> (<c>Operation</c>) — by attaching the source
    /// explicitly, rebuilding, and then giving the resulting axis to <c>IRotated.Axis</c>.
    /// </remarks>
    private void Axis3DByEdgeRoute()
    {
        _currentStepId = "R.10";
        var step = _report.Begin("R.10", "Ось через IAxis3DByEdge и IAxis3DByOperation",
            "Даёт ли ось, привязанная к ребру или к операции тела, работающее вращение?");

        var type = _rotatedType;
        if (type is null)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 60d, 40d, 8d, step, "R10");
            step.Observe("контрольное тело: " + (plate is null ? "не построено" : "построено"));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);

        var document7 = _app.TransferInterface(_doc, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IKompasDocument3D;
        var axesContainer = document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer;
        if (axesContainer is null)
        {
            step.Fail("TopPart не отвечает на QI(IAuxiliaryGeomContainer) — Axes3D недостижим");
            return;
        }

        KompasAPI7.IAxes3D? axes;
        try
        {
            axes = axesContainer.Axes3D;
        }
        catch (Exception ex)
        {
            step.Fail("AuxiliaryGeomContainer.Axes3D бросил " + HResult.Describe(ex));
            return;
        }

        if (axes is null)
        {
            step.Fail("container.Axes3D вернул null");
            return;
        }

        // ── the edge source ─────────────────────────────────────────────────────────────────────
        if (!TryAnyEdge(part, out var edgeObj) || edgeObj is null)
        {
            step.Observe("ребро контрольного тела не найдено — маршрут через IAxis3DByEdge не проверить");
        }
        else
        {
            try
            {
                var edge7 = _app.TransferInterface(edgeObj, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
                step.Observe("ребро → API7: " + (edge7 is null ? "null" : "OK"));

                if (edge7 is null)
                {
                    step.Observe("ребро не переносится — привязывать нечего");
                }
                else if (axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisEdge)
                         is not KompasAPI7.IAxis3DByEdge byEdge)
                {
                    step.Observe("Add(o3d_axisEdge) дал не IAxis3DByEdge — конкретный тип другой");
                }
                else
                {
                    byEdge.Edge = edge7;
                    var readBack = byEdge.Edge is null ? "null (отброшено)" : "удержано";
                    part.RebuildModel();
                    _doc.RebuildDocument();
                    step.Observe("IAxis3DByEdge.Edge ← ребро: " + readBack
                        + ", Name=" + SafeName(byEdge));

                    var (created, detail) = TryRotationWithAxisObject(part, byEdge, step, "R10-edge");
                    step.Observe("вращение с осью по ребру: Create()=" + created
                        + (created ? "" : " (" + detail + ")"));

                    if (created)
                    {
                        var volume = Api5.Volume(part);
                        step.Observe("V=" + Api5.Num(volume) + " против "
                            + Api5.Num(_bodyVolumeBeforeStep) + " до шага");
                        if (volume is not null && _bodyVolumeBeforeStep is not null
                            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
                        {
                            step.Pass("ось по ребру принята, цилиндр построен: V=" + Api5.Num(volume));
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                step.Observe("маршрут через IAxis3DByEdge бросил " + HResult.Describe(ex));
            }
        }

        // ── the operation source ────────────────────────────────────────────────────────────────
        try
        {
            var operationSource = FirstOperation(part, out var operationDetail);
            var operation = Transfer(part, operationSource);
            step.Observe("источник-операция: " + operationDetail
                + (operationSource is null ? "" : (operation is null ? "; в API7 не переносится" : "; перенесена")));

            if (operation is null)
            {
                step.Observe("операции в дереве не найдено — привязывать нечего");
            }
            else if (axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisOperation)
                     is not KompasAPI7.IAxis3DByOperation byOperation)
            {
                step.Observe("Add(o3d_axisOperation) дал не IAxis3DByOperation — конкретный тип другой");
            }
            else
            {
                byOperation.Operation = operation;
                var held = byOperation.Operation is null ? "null (отброшено)" : "удержано";
                part.RebuildModel();
                _doc.RebuildDocument();
                step.Observe("IAxis3DByOperation.Operation ← операция: " + held
                    + ", Name=" + SafeName(byOperation));

                var (created, detail) = TryRotationWithAxisObject(part, byOperation, step, "R10-operation");
                step.Observe("вращение с осью по операции: Create()=" + created
                    + (created ? "" : " (" + detail + ")"));
            }
        }
        catch (Exception ex)
        {
            step.Observe("маршрут через IAxis3DByOperation бросил " + HResult.Describe(ex));
        }

        var finalVolume = Api5.Volume(part);
        step.Observe("V после всех попыток: " + Api5.Num(finalVolume) + " (было "
            + Api5.Num(_bodyVolumeBeforeStep) + ")");

        if (finalVolume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(finalVolume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("ось из API7 построила цилиндр: V=" + Api5.Num(finalVolume));
        }
        else if (BodyDidNotMove(step, _bodyVolumeBeforeStep, finalVolume, "подача оси в вращение"))
        {
            // Not a partial success: the rotation added nothing, and saying so is the point.
        }
        else
        {
            step.Fail("ни ось по ребру, ни ось по операции не дали вращения ожидаемого объёма "
                + "(V=" + Api5.Num(finalVolume) + ", было " + Api5.Num(_bodyVolumeBeforeStep)
                + ", ожидание " + Api5.Num(FullTurnVolume) + ") — блокер SM-03 остаётся");
        }
    }

    /// <summary>
    /// R.11 — authoring the axis with the API5 method the type library actually declares for it:
    /// <c>ksDocument2D.ksAxisLine(ksAxisLineParam)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This step exists because R.6 was wrong about what it had ruled out. R.6 concluded that
    /// "<c>ksAxisLineParam</c> can only read an axis and cannot author one" and therefore tried
    /// <c>ksLineSegParam</c> through <c>ksSetObjParam</c> instead — and got
    /// <c>REGDB_E_CLASSNOTREG</c> on CLSID <c>{7F7D6F86-97DA-11D6-8732-00C0262CDD2C}</c>. Reflection
    /// over the shipped interop shows that CLSID is <c>LineSegParamClass</c>'s <em>co-class</em>, whose
    /// interface IID is <c>{7F7D6F84-…}</c>: the failure was "this install does not register the
    /// segment-parameter co-class", which is a fact about <c>ksLineSegParam</c> and says nothing about
    /// axes. Meanwhile <c>ksDocument2D</c> declares a method named exactly for the job —
    /// <c>ksAxisLine(Object param)</c> — and the parameter type it wants, <c>ksAxisLineParam</c>,
    /// has its own co-class <c>AxisLineParamClass</c> at CLSID
    /// <c>{705962E9-5E9B-4379-8504-FA754D11FC66}</c>, a completely different registration.
    /// </para>
    /// <para>
    /// <b>What is measured, in order.</b> (1) Whether the <c>AxisLineParamClass</c> co-class can be
    /// constructed directly from the interop assembly at all. (2) Whether <c>ksAxisLine</c> returns a
    /// non-zero reference — a drawn object, not a promise. (3) Whether that reference reads back as an
    /// axis: its style number, and, where the reader is available, whether it is found by
    /// <c>ksIsPointInsideContour</c>-style queries. (4) Whether a rotation over the sketch containing
    /// that axis builds. Only (4) decides anything, and a zero reference in (2) is reported as the
    /// route failing rather than being papered over.
    /// </para>
    /// <para>
    /// The rotation's own definition declares no <c>SetAxis</c> — reflection confirms only
    /// <c>SetSketch</c>, <c>SetSideParam(side1, angle)</c>, <c>GetSideParam</c>, <c>RotatedParam</c>,
    /// <c>SetThinParam</c> and a <c>toroidShapeType</c> property. So the axis genuinely has to live in
    /// the profile sketch, which is why authoring the sketch's axis is the whole question.
    /// </para>
    /// </remarks>
    private void AxisLineParamRoute()
    {
        _currentStepId = "R.11";
        var step = _report.Begin("R.11", "Осевая линия через ksDocument2D.ksAxisLine(AxisLineParamClass)",
            "ksAxisLine — это тот метод, которым осевая линия заводится по-настоящему?");

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R11-axisline", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("определение эскиза недоступно");
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        int? axisReference = null;
        try
        {
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() вернул не ksDocument2D");
                return;
            }

            // The profile first, then the axis, so the axis is not the only geometry in the sketch —
            // a sketch holding one construction line and nothing else is a different measurement from
            // one holding a closed rectangle plus its axis.
            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);

            // (1) Can the co-class be built at all? A parameter block that cannot be constructed is
            // not a route, and the exception text is the measurement.
            object? parameter = null;
            try
            {
                parameter = new AxisLineParamClass();
                step.Observe("AxisLineParamClass построен: " + Api5.RuntimeName(parameter));
            }
            catch (Exception ex)
            {
                step.Observe("AxisLineParamClass НЕ построен: " + HResult.Describe(ex));
            }

            if (parameter is not null)
            {
                // Init() is declared on the interface; a parameter block that will not initialise is
                // reported before ksAxisLine is asked to consume it.
                try
                {
                    var initialised = Late.Call(parameter, "Init");
                    step.Observe("AxisLineParam.Init() → " + Api5.Raw(initialised));
                }
                catch (Exception ex)
                {
                    step.Observe("AxisLineParam.Init() бросил " + HResult.Describe(ex));
                }
            }

            // (2) The declared method itself. Called once through the typed interop (ksDocument2D)
            // and, when the parameter object exists, once more through late binding so a typed-call
            // failure cannot be mistaken for the method being absent.
            try
            {
                var typed = editor.ksAxisLine(parameter);
                step.Observe("ksAxisLine(param) → " + typed);
                if (typed != 0)
                {
                    axisReference = typed;
                }
            }
            catch (Exception ex)
            {
                step.Observe("ksAxisLine(param) бросил " + HResult.Describe(ex));
            }

            if (axisReference is null)
            {
                try
                {
                    var late = Late.Call(editor, "ksAxisLine", parameter);
                    step.Observe("позднее ksAxisLine(param) → " + Api5.Raw(late));
                    if (late is int lateRef && lateRef != 0)
                    {
                        axisReference = lateRef;
                    }
                }
                catch (Exception ex)
                {
                    step.Observe("позднее ksAxisLine(param) бросил " + HResult.Describe(ex));
                }
            }

            // (3) Read the drawn object back. A reference that the document will not style-query is
            // not a drawn object.
            if (axisReference is { } reference)
            {
                try
                {
                    var style = editor.ksGetObjectStyle(reference);
                    step.Observe("ссылка оси " + reference + ", стиль прочитан: " + style);
                }
                catch (Exception ex)
                {
                    step.Observe("ksGetObjectStyle(оси) бросил " + HResult.Describe(ex));
                }
            }
            else
            {
                step.Observe("ksAxisLine не вернул ненулевую ссылку ни типизированным, ни поздним вызовом");
            }

            definition.EndEdit();
            part.RebuildModel();
            _doc.RebuildDocument();
        }
        catch (Exception ex)
        {
            step.Observe("опыт прерван: " + HResult.Describe(ex));
        }

        if (axisReference is null)
        {
            step.Fail("ksAxisLine осевой линии не завёл (ссылка 0): авторского маршрута для осевой линии "
                + "в API5 не найдено — блокер SM-03 остаётся открытым");
            return;
        }

        // (4) The only question that decides: does a rotation over this sketch build?
        var before = Api5.Volume(part);
        var (created, detail) = CreateRotation(part, sketch, angleDeg: 360d, directionType: 0);
        step.Observe("вращение поверх эскиза с осью от ksAxisLine: Create() → " + created + "; " + detail);

        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        step.Observe("V=" + Api5.Num(volume) + ", ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        if (created && volume is not null
            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("осевая линия заведена через ksAxisLine, вращение построено: V=π·r²·h");
            return;
        }

        if (BodyDidNotMove(step, before, volume, "вращение поверх оси от ksAxisLine"))
        {
            return;
        }

        step.Fail("ось от ksAxisLine не дала вращения ожидаемого объёма (Create()=" + created
            + ", V=" + Api5.Num(volume) + ", ожидание " + Api5.Num(FullTurnVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// R.12 — the parameter-type numbers, taken from the enumeration that actually names them rather
    /// than swept blind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.6 swept <c>ksSetObjParam(segment, param, parType)</c> over <c>parType</c> 1…6 and reported
    /// nothing but <c>REGDB_E_CLASSNOTREG</c>, and R.11 showed the same for <c>AxisLineParamClass</c>.
    /// Both steps shared a defect that R.11's failure finally exposed: <b>the number was guessed and
    /// the parameter object was constructed directly.</b>
    /// </para>
    /// <para>
    /// Reflection over the shipped constants assembly names the numbers:
    /// <c>DrawingObjectTypeEnum.ksDrLineSeg = 1</c>, <c>ksDrLine = 28</c>,
    /// <b><c>ksDrAxisLine = 48</c></b>, <c>ksDrStraightAxis = 56</c>, <c>ksDrCircleAxis = 58</c>,
    /// <c>ksDrArcAxis = 59</c>. A <c>parType</c> of 48 is a different request from 1…6, and it is the
    /// one the enumeration calls "axis line". The same enumeration also shows the 3D side of the
    /// family, <c>KompasAPIObjectTypeEnum.ksObjectAxis3D = 11188</c> and its relatives 11190…11195,
    /// which is what R.9's container was working with.
    /// </para>
    /// <para>
    /// And the parameter object is not constructed with <c>new</c>: this install registers its COM
    /// classes by manifest, so every <c>…ParamClass</c> co-class answers
    /// <c>REGDB_E_CLASSNOTREG</c>. The application declares the factory instead —
    /// <c>KompasObject.GetParamStruct(Int16 structType)</c> — which hands the block out already
    /// bound. This step asks that factory for the axis-adjacent structure numbers, reports the
    /// object it gets back for each, and, for every block that is a real object, puts it on the
    /// segment with <c>ksSetObjParam</c> at the axis-line number and then rebuilds a rotation.
    /// </para>
    /// <para>
    /// <b>Honesty rule.</b> A block that is null, and a <c>ksSetObjParam</c> that answers 0, are
    /// reported as such. The step passes only if a rotation over the resulting sketch builds with the
    /// analytic volume; a non-zero <c>ksSetObjParam</c> on its own is not a pass.
    /// </para>
    /// <para>
    /// <b>Measured outcome, and why the read direction is in here.</b> The factory works —
    /// <c>GetParamStruct(123 /* ko_AxisLineParam */)</c>, <c>(11 /* ko_LineSegParam */)</c> and
    /// <c>(109 /* ko_ContourParam */)</c> each return a live <c>System.__ComObject</c>, and that
    /// block's own <c>Init()</c> answers <c>True</c>, so it is a valid, initialised parameter block
    /// rather than an empty object. <c>ksSetObjParam</c> answers <b>0</b> for all three numbers, and
    /// <c>ksGetObjParam</c> — the <em>read</em> direction, the control for the put — answers <b>0</b>
    /// too. A pairing refused in both directions with a valid block means the structure does not apply
    /// to a <c>ksLineSeg</c> object at all: an axis line in a 2D sketch is not a segment carrying an
    /// axis parameter, which is exactly the assumption R.6 was built on and R.1c had already
    /// questioned. That closes the authoring route for a measured reason rather than for want of
    /// attempts, and the failing rotation afterwards says the same thing: <c>Create()</c> is false and
    /// the body does not move.
    /// </para>
    /// </remarks>
    private void ObjectTypeNumberRoute()
    {
        _currentStepId = "R.12";
        var step = _report.Begin("R.12", "Номер типа объекта для осевой линии и фабрика параметров",
            "parType=48 из DrawingObjectTypeEnum и GetParamStruct — это тот маршрут, которого не хватало?");

        // Named, not guessed: these are the members of DrawingObjectTypeEnum whose names describe a
        // line or an axis, read out of the constants assembly at compile time.
        var axisLineType = (int)DrawingObjectTypeEnum.ksDrAxisLine;
        step.Observe("DrawingObjectTypeEnum.ksDrAxisLine = " + axisLineType
            + "; ksDrLineSeg = " + (int)DrawingObjectTypeEnum.ksDrLineSeg
            + "; ksDrLine = " + (int)DrawingObjectTypeEnum.ksDrLine
            + "; ksDrStraightAxis = " + (int)DrawingObjectTypeEnum.ksDrStraightAxis
            + "; ksDrCircleAxis = " + (int)DrawingObjectTypeEnum.ksDrCircleAxis
            + "; ksDrArcAxis = " + (int)DrawingObjectTypeEnum.ksDrArcAxis);

        // The second number space, and the one that actually governs GetParamStruct in a 2D
        // document. R.12's first run used the DrawingObjectTypeEnum numbers, read a live block at 48
        // and set it on the segment — and 48 in THIS enumeration is ko_LBreakDimParam, a break
        // dimension, so the live block was never an axis block. The axis-line structure has its own
        // member and its own number.
        var axisLineStruct = (short)StructType2DEnum.ko_AxisLineParam;
        var lineSegStruct = (short)StructType2DEnum.ko_LineSegParam;
        var contourStruct = (short)StructType2DEnum.ko_ContourParam;
        step.Observe("StructType2DEnum.ko_AxisLineParam = " + axisLineStruct
            + "; ko_LineSegParam = " + lineSegStruct
            + "; ko_ContourParam = " + contourStruct);

        // The factory the application declares for parameter blocks, asked now with the named
        // numbers from StructType2DEnum rather than with the DrawingObjectTypeEnum values that
        // merely happened to include a live one.
        var structCandidates = new short[] { axisLineStruct, lineSegStruct, contourStruct };

        var viable = new List<(short StructType, object Block)>();
        foreach (var structType in structCandidates)
        {
            try
            {
                var block = Late.Call(_app, "GetParamStruct", structType);
                if (block is null)
                {
                    step.Observe("GetParamStruct(" + structType + ") → null");
                    continue;
                }

                var typeName = Api5.RuntimeName(block);
                step.Observe("GetParamStruct(" + structType + ") → " + typeName);

                // Only a block that the document will accept is worth carrying forward, and the
                // interface it answers on is the only evidence available without a cast path.
                viable.Add((structType, block));
            }
            catch (Exception ex)
            {
                step.Observe("GetParamStruct(" + structType + ") бросил " + HResult.Describe(ex));
            }
        }

        if (viable.Count == 0)
        {
            step.Fail("GetParamStruct не выдал ни одного блока параметров: фабрика параметров на этом "
                + "экземпляре не отвечает ни на один из проверенных номеров, а прямые co-class'ы "
                + "не зарегистрированы — авторского маршрута для осевой линии не найдено");
            return;
        }

        // The axis-line block first, and the line-segment block as the control: if the axis block
        // works and the segment block does not, the difference is the structure, not the call.
        var parameter = viable.FirstOrDefault(v => v.StructType == axisLineStruct);
        if (parameter.Block is null)
        {
            parameter = viable[0];
        }

        step.Observe("для опыта берётся блок от GetParamStruct(" + parameter.StructType + ")");

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R12-parType", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        if (sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            step.Fail("определение эскиза недоступно");
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        var drawn = false;
        try
        {
            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() вернул не ksDocument2D");
                return;
            }

            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);

            var segment = editor.ksLineSeg(0d, -HeightMm / 2d, 0d, HeightMm / 2d, 1);
            step.Observe("отрезок под осевую: ref=" + segment);

            if (segment != 0)
            {
                // The named StructType2DEnum numbers, tried in order: the axis-line structure alone
                // first, then the line-segment structure as the control, then the DrawingObjectTypeEnum
                // number in case the put is keyed by the drawing-object space rather than the structure
                // space. A non-zero code from any of them is reported; only the rotation decides.
                foreach (var parType in new[] { (int)axisLineStruct, (int)lineSegStruct, axisLineType })
                {
                    try
                    {
                        var code = editor.ksSetObjParam(segment, parameter.Block, parType);
                        step.Observe("ksSetObjParam(ref=" + segment + ", parType=" + parType + ") → " + code);
                        if (code != 0 && parType == axisLineStruct)
                        {
                            drawn = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        step.Observe("ksSetObjParam(parType=" + parType + ") бросил " + HResult.Describe(ex));
                    }
                }

                try
                {
                    var style = editor.ksGetObjectStyle(segment);
                    step.Observe("стиль отрезка после ksSetObjParam: " + style);
                }
                catch (Exception ex)
                {
                    step.Observe("ksGetObjectStyle бросил " + HResult.Describe(ex));
                }

                // The read direction is the control for the put. ksSetObjParam answering 0 has two
                // very different meanings: "this segment cannot carry that structure" or "the
                // structure is not the one the put expects". Reading the same structure back from the
                // same segment separates them, and the block's own Init() says whether it was ever a
                // usable block or only a non-null object.
                try
                {
                    var readBack = editor.ksGetObjParam(segment, parameter.Block, (int)axisLineStruct);
                    step.Observe("ksGetObjParam(ref=" + segment + ", parType=" + axisLineStruct
                        + ") → " + readBack);
                }
                catch (Exception ex)
                {
                    step.Observe("ksGetObjParam бросил " + HResult.Describe(ex));
                }

                try
                {
                    var initialised = Late.Call(parameter.Block, "Init");
                    step.Observe("блок параметров оси: Init() → " + Api5.Raw(initialised));
                }
                catch (Exception ex)
                {
                    step.Observe("блок параметров оси: Init() бросил " + HResult.Describe(ex));
                }
            }
            else
            {
                step.Observe("отрезок не создан — подавать параметр не на что");
            }

            definition.EndEdit();
            part.RebuildModel();
            _doc.RebuildDocument();
        }
        catch (Exception ex)
        {
            step.Observe("опыт прерван: " + HResult.Describe(ex));
        }

        var before = Api5.Volume(part);
        var (created, detail) = CreateRotation(part, sketch, angleDeg: 360d, directionType: 0);
        step.Observe("вращение поверх эскиза: Create() → " + created + "; " + detail);

        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        step.Observe("V=" + Api5.Num(volume) + ", ожидание π·r²·h = " + Api5.Num(FullTurnVolume));
        step.Observe("ksSetObjParam на номере осевой линии дал: " + (drawn ? "ненулевой код" : "ноль"));

        if (created && volume is not null
            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("осевая линия заведена через parType из DrawingObjectTypeEnum: V=π·r²·h");
            return;
        }

        if (BodyDidNotMove(step, before, volume, "вращение поверх осевой линии от ksSetObjParam"))
        {
            return;
        }

        step.Fail("маршрут через номер типа объекта и фабрику параметров вращения не дал "
            + "(Create()=" + created + ", V=" + Api5.Num(volume) + ", ожидание "
            + Api5.Num(FullTurnVolume) + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// R.13 — the two members of <c>IRotated</c> no step has ever written: <c>RotatedType</c> and
    /// <c>AngleObject</c>; plus the write order, and whether an axis has to be committed before it
    /// is offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.9 left one unexplained fact — an axis object that <c>Add</c> creates is still not in the
    /// container (<c>осей стало 0</c>) and <c>IRotated.Axis</c> discards it. Reflection over the
    /// shipped interop narrows that down rather than guessing at it:
    /// <c>ksRotatedTypeEnum</c> has <b>three</b> members — <c>ksRTAngle = 0</c>,
    /// <c>ksRTVertex = 1</c>, <c>ksRTSurface = 2</c> — and <c>IRotated.RotatedType</c> is writable and
    /// has <b>never been set by any step in this suite</b>. A rotation that does not know which kind
    /// of rotation it is cannot know which axis it wants, so a silently discarded <c>Axis</c> is
    /// exactly what an unset <c>RotatedType</c> would produce. <c>AngleObject</c> is the second
    /// untouched member and is the one the vertex and surface kinds need.
    /// </para>
    /// <para>
    /// <b>What is measured, in four rungs, each reported whether or not it works.</b>
    /// (1) <c>RotatedType</c> before the write and after each of the three values — a write that does
    /// not read back is a different finding from one that refuses the value.
    /// (2) The order of the writes: <c>Profile</c> then <c>Axis</c> (the order R.7 used) against
    /// <c>Axis</c> then <c>Profile</c>, which has never been tried; if the member validates against
    /// what is already set, the order is the difference.
    /// (3) <c>IAxis3D.Update()</c> — declared on every concrete axis class and never called — with
    /// <c>Valid</c> and <c>Reference</c> read before and after, and the container's <c>Count</c>
    /// re-read, because whether an axis becomes a real tree object is the R.9 question.
    /// (4) The same writes on an axis created by <c>Add</c> and committed, offered as the pair
    /// <c>(Axis, Profile)</c> with <c>RotatedType</c> set first.
    /// </para>
    /// <para>
    /// The verdict stays three-way and the gate is the same one the suite has had to learn twice:
    /// the document's body carries every earlier step, so an unchanged volume is "nothing was built",
    /// never "a small rotation". A journal that reads back correctly is not a pass.
    /// </para>
    /// </remarks>
    private void RotatedTypeAndWriteOrderRoute()
    {
        _currentStepId = "R.13";
        var step = _report.Begin("R.13", "RotatedType, порядок записи и фиксация оси",
            "Отвергается ли Axis потому, что тип вращения не задан, а ось не зафиксирована?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R13");
            step.Observe("контрольное тело (источник рёбер): "
                + (plate is null ? "не построено" : "построено, " + plate.name));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        // The axis object, created and committed before anything is offered to the rotation. Made
        // first so rung (3) can report what Update() does to it on its own.
        var document7 = _app.TransferInterface(_doc, 2, 0) as KompasAPI7.IKompasDocument3D;
        var axes = (document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer)?.Axes3D;
        if (axes is null)
        {
            step.Fail("Axes3D недостижим — измерять фиксацию оси не на чем");
            return;
        }

        var edge = Transfer(part, TryAnyEdge(part, out var edgeObj) ? edgeObj : null);
        step.Observe("источник-ребро: " + (edge is null ? "нет" : "есть"));

        KompasAPI7.IAxis3D? committedAxis = null;
        try
        {
            var axis = axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisEdge);
            if (axis is null)
            {
                step.Observe("Axes3D.Add(o3d_axisEdge) → null");
            }
            else if (axis is not KompasAPI7.IAxis3DByEdge byEdge || edge is null)
            {
                step.Observe("ось создана, но не IAxis3DByEdge или ребра нет");
            }
            else
            {
                // (3) Update() before the source is attached, then after, with Valid/Reference shown:
                // an axis that reports Update()=true and Valid=true is a different object from one
                // that reports neither, and only the second explains a container Count of 0.
                var beforeUpdate = Api5.Raw(Api5.SafeBool(axis.Update));
                step.Observe("ось после Add: Update() → " + beforeUpdate
                    + ", Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid))
                    + ", Reference=" + (Api5.SafeInt(() => axis.Reference)?.ToString(CultureInfo.InvariantCulture) ?? "null")
                    + ", осей в контейнере " + axes.Count);

                byEdge.Edge = edge;
                var attached = byEdge.Edge is null ? "null (отброшено)" : "удержано";
                var afterUpdate = Api5.Raw(Api5.SafeBool(axis.Update));
                step.Observe("источник подан: Edge=" + attached
                    + "; Update() → " + afterUpdate
                    + ", Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid))
                    + ", осей в контейнере " + axes.Count
                    + ", Name=" + SafeName(axis));

                part.RebuildModel();
                _doc.RebuildDocument();
                step.Observe("после перестроения: осей в контейнере " + axes.Count
                    + ", Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid)));

                committedAxis = axis;
            }
        }
        catch (Exception ex)
        {
            step.Observe("создание/фиксация оси бросили " + HResult.Describe(ex));
        }

        // ── rungs (1), (2) and (4): the rotation itself ─────────────────────────────────────────
        // Each rung builds its own profile sketch and its own feature, so a refusal in one rung
        // cannot be read as the state left by the previous one.
        var rotTypeValues = new (Kompas6Constants3D.ksRotatedTypeEnum Value, string Label)[]
        {
            (Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle, "ksRTAngle(0)"),
            (Kompas6Constants3D.ksRotatedTypeEnum.ksRTVertex, "ksRTVertex(1)"),
            (Kompas6Constants3D.ksRotatedTypeEnum.ksRTSurface, "ksRTSurface(2)"),
        };

        var outcomes = new List<string>();
        foreach (var (rotType, label) in rotTypeValues)
        {
            foreach (var profileFirst in new[] { true, false })
            {
                var order = profileFirst ? "Profile→Axis" : "Axis→Profile";
                var name = "R13-" + label + "-" + order;
                var outcome = OneRotatedTypeAttempt(
                    part, rotType, label, profileFirst, committedAxis, step, name);
                outcomes.Add(label + "/" + order + " → " + outcome);
            }
        }

        step.Observe("попытки: " + string.Join(" | ", outcomes));

        var volume = Api5.Volume(part);
        step.Observe("V после попыток: " + Api5.Num(volume) + " (было "
            + Api5.Num(_bodyVolumeBeforeStep) + "); ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("вращение построено с заданным RotatedType: V=π·r²·h — блокер SM-03 снят");
            return;
        }

        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "задание RotatedType и порядка записи"))
        {
            return;
        }

        step.Fail("ни одно значение RotatedType и ни один порядок записи не дали вращения ожидаемого "
            + "объёма (V=" + Api5.Num(volume) + ", ожидание " + Api5.Num(FullTurnVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// The two inputs no earlier step ever measured: <b>what kind of edge</b> is being handed to the
    /// rotation, and <b>an axis built from two authored points</b> instead of from geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.13 shut down its own hypothesis: <c>RotatedType[true]</c> writes and reads back
    /// (<c>ksRTAngle → ksRTVertex → ksRTSurface</c>) and <c>Axis</c> still returned
    /// <c>null (отброшено)</c> in all six rungs, in both write orders. So the axis is not discarded
    /// for want of a rotation kind, and not because of the order of the writes. What R.13 also showed
    /// is that its axis was <i>real</i> — <c>Update() → True</c>, <c>Valid=True</c>,
    /// <c>Name=Ось через ребро:1</c>, container <c>Count=1</c> — which is exactly what R.9 and R.10
    /// never had (<c>Name=</c>, <c>осей стало 0</c>).
    /// </para>
    /// <para>
    /// That leaves one input still unmeasured, and it is the probe's own defect rather than a fact
    /// about КОМПАС: every step so far has fed <c>Axis</c> the <i>first edge it happens to find</i>
    /// (<see cref="TryAnyEdge"/> walks faces and returns the first one), and <b>never asked the edge
    /// what it is</b>. A plate has 12 edges; a seam or a tangent curve is a different object from a
    /// straight line, and an axis built on the wrong kind is a different axis. Without characterising
    /// the edge, «Axis отвергает оси» and «Axis отвергает именно это ребро» are the same journal —
    /// the <c>4/tan</c> lesson again, this time about my own instrument.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> therefore characterises every edge of the plate with the predicates
    /// <c>ksEdgeDefinition</c> actually declares — <c>IsStraight</c>, <c>IsLineSeg</c>, <c>IsArc</c>,
    /// <c>IsCircle</c>, <c>IsEllipse</c>, <c>IsEllipseArc</c>, <c>IsNurbs</c>, <c>IsPlanar</c>,
    /// <c>IsPeriodic</c>, <c>IsValid</c> — and reads <c>GetCurve3D()</c> and <c>GetLength</c>. Only
    /// then is a <i>straight, valid</i> edge selected deliberately and offered.
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> closes a gap R.9 named in its own journal («точек в пробе нет, источник не
    /// задан») and then never filled: <c>IAxis3DBy2Points</c> takes <c>Point1</c>/<c>Point2</c> as
    /// <c>IModelObject</c>, and <c>IPoints3D.Add() → IPoint3D</c> with settable <c>X</c>/<c>Y</c>/<c>Z</c>
    /// and <c>Update()</c> is how a point is authored. A two-point axis lies on the declared axis
    /// through the part origin, so it is an axis the probe can <i>predict</i> rather than hope for.
    /// </para>
    /// <para>
    /// Verdict unchanged and three-way, gate unchanged: the document accumulates every earlier body,
    /// so an unchanged volume is «ничего не построено» and never «маленькое вращение».
    /// </para>
    /// </remarks>
    private void EdgeKindAndTwoPointAxisRoute()
    {
        _currentStepId = "R.14";
        var step = _report.Begin("R.14", "Вид ребра-источника и ось по двум точкам",
            "Отвергается ли Axis потому, что ему подают ребро неизмеренного вида, а ось строится только из геометрии?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R14");
            step.Observe("контрольное тело (источник рёбер): "
                + (plate is null ? "не построено" : "построено, " + plate.name));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        // ── rung (1): what each edge of the plate actually IS ───────────────────────────────────
        var straightEdge = CharacteriseEdges(part, step);

        // ── rung (2): an axis the probe can predict, from two authored points ───────────────────
        var document7 = _app.TransferInterface(_doc, 2, 0) as KompasAPI7.IKompasDocument3D;
        var aux = document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer;
        var axes = aux?.Axes3D;
        if (axes is null)
        {
            step.Fail("Axes3D недостижим — измерять фиксацию оси не на чем");
            return;
        }

        KompasAPI7.IAxis3D? twoPointAxis = null;
        try
        {
            // Points live on IModelContainer, not on IAuxiliaryGeomContainer: reflection over the
            // type library puts `IModelContainer.Points3D : Points3D` and no Points3D on the
            // auxiliary-geometry container at all. Axes and points therefore come in through two
            // different QIs on the same live part object.
            var points = (document7?.TopPart as KompasAPI7.IModelContainer)?.Points3D;
            if (points is null)
            {
                step.Observe("Points3D недостижим — авторскую ось по двум точкам не построить");
            }
            else
            {
                // The declared axis through the part origin. Two points on it, both set, both
                // committed: an axis through (0,0,0) and (0,0,height) is the one a rod would spin on.
                var p1 = AuthorPoint(points, 0d, 0d, 0d, "R14-p1", step);
                var p2 = AuthorPoint(points, 0d, 0d, 40d, "R14-p2", step);
                step.Observe("точек в контейнере Points3D: " + points.Count);

                if (p1 is null || p2 is null)
                {
                    step.Observe("одна из точек не заведена — ось по двум точкам пропущена");
                }
                else
                {
                    var axis = axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points);
                    if (axis is not KompasAPI7.IAxis3DBy2Points by2)
                    {
                        step.Observe("Add(o3d_axis2Points) → " + (axis is null ? "null" : "не IAxis3DBy2Points"));
                    }
                    else
                    {
                        by2.Point1 = p1;
                        by2.Point2 = p2;
                        step.Observe("  точки поданы: Point1="
                            + (by2.Point1 is null ? "null (отброшено)" : "удержана")
                            + ", Point2=" + (by2.Point2 is null ? "null (отброшено)" : "удержана"));

                        var updated = Api5.SafeBool(axis.Update);
                        step.Observe("  Update() → " + Api5.Raw(updated)
                            + ", Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid))
                            + ", Name=" + SafeName(axis)
                            + ", осей в контейнере " + axes.Count);

                        if (updated == true)
                        {
                            twoPointAxis = axis;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe("построение оси по двум точкам бросило " + HResult.Describe(ex));
        }

        // ── rung (3): offer both axes to the rotation, each with its own profile ────────────────
        // The label is paired with a fixed short tag rather than derived from the Russian text: the
        // first run of this step matched on label.Contains("точек") against the label
        // «ось по двум точкам», which is the genitive plural and does NOT contain «точек» — both
        // attempts were therefore labelled "straight-edge" and the journal could not say which axis
        // Axis had kept. A probe that mislabels its own two candidates reports a fact about itself.
        var candidates = new List<(string Label, string Tag, KompasAPI7.IAxis3D? Axis)>();
        if (twoPointAxis is not null)
        {
            candidates.Add(("ось по двум точкам", "axis-2points", twoPointAxis));
        }

        if (straightEdge is not null)
        {
            var byEdge = MakeEdgeAxis(axes, straightEdge, step);
            if (byEdge is not null)
            {
                candidates.Add(("ось по прямому ребру", "axis-edge", byEdge));
            }
        }
        else
        {
            step.Observe("прямого ребра не нашлось — ось по ребру в этом шаге не проверяется");
        }

        if (candidates.Count == 0)
        {
            step.Fail("ни одной пригодной оси построить не удалось — вопрос об Axis остаётся открытым");
            return;
        }

        var outcomes = new List<string>();
        foreach (var (label, tag, axis) in candidates)
        {
            var volumeBefore = Api5.Volume(part);
            var name = "R14-" + tag;
            var (created, detail) = TryRotationWithAxisObject(part, axis!, step, name);
            var volumeAfter = Api5.Volume(part);
            var line = label + " [" + tag + "] → Create()=" + created + " (" + detail + "), ΔV="
                + Api5.Num((volumeAfter ?? 0d) - (volumeBefore ?? 0d));
            outcomes.Add(line);
            step.Observe("  итог попытки: " + line);
        }

        step.Observe("попытки: " + string.Join(" | ", outcomes));

        var volume = Api5.Volume(part);
        step.Observe("V после попыток: " + Api5.Num(volume) + " (было "
            + Api5.Num(_bodyVolumeBeforeStep) + "); ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("вращение построено с осью по двум точкам: V=π·r²·h — блокер SM-03 снят");
            return;
        }

        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "ось по двум точкам"))
        {
            return;
        }

        step.Fail("ни ось по двум точкам, ни ось по прямому ребру не дали вращения ожидаемого объёма "
            + "(V=" + Api5.Num(volume) + ", ожидание " + Api5.Num(FullTurnVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// The profile as a real closed <b>contour</b>, with the contour proved closed before the
    /// rotation is asked to sweep it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every rotation step in this probe — R.1d, R.1e, R.1f, R.6, R.7, R.8, R.9, R.10, R.11, R.12,
    /// R.13, R.14 — ends the same way: <c>Create()=False</c>, while every parameter the probe set
    /// reads back correctly (<c>SetSideParam(true,360)=True</c>, <c>GetSideParam(true)=360</c>,
    /// <c>angleNormal=360</c>, <c>direction=0</c>). A definition that accepts every value and still
    /// refuses to create is refusing for a reason <i>outside</i> the parameters that were set.
    /// </para>
    /// <para>
    /// The reason is in the profile, and it is a <b>probe defect</b> rather than a fact about
    /// КОМПАС: <see cref="RectangleSketch"/> draws the rectangle as four independent
    /// <c>ksLineSeg</c> calls and never closes them into anything. A sketch region in КОМПАС is a
    /// <i>contour</i> object — there is a whole family of document methods for it —
    /// <c>ksContour(style)</c>, <c>ksMakeEncloseContours(gr,x,y)</c>, <c>ksIsPointInsideContour(p,x,y,prec)</c>,
    /// <c>ksIsCurveClosed</c>, <c>ksClearRegion</c> — and the probe called <b>none of them</b>. Four
    /// loose segments are not a region, so the rotation has no area to sweep about the axis, refuses,
    /// and the volume comes back <c>null</c> (no body at all) rather than wrong. This is the
    /// <c>4/tan</c> lesson a third time: an instrument that never formed the input cannot conclude
    /// anything about the operation.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> draws the same rectangle, then builds the contour with <c>ksContour</c> and
    /// <c>ksMakeEncloseContours</c>, and <i>proves</i> it with <c>ksIsPointInsideContour</c> at two
    /// points — one inside the rectangle, one outside — plus <c>ksIsCurveClosed</c>. A contour that
    /// claims to exist but does not contain its own interior point is not a region, and saying so
    /// here is the difference between «вращение не работает» and «я не собрал профиль».
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> rotates over the proved contour, with the committed two-point axis from R.14
    /// offered as <c>Axis</c> and <c>RotatedType</c> set to the plain-angle kind, and with the
    /// axis-bearing sketch variant as the control.
    /// </para>
    /// </remarks>
    private void ContourProfileRoute()
    {
        _currentStepId = "R.15";
        var step = _report.Begin("R.15", "Профиль как замкнутый контур и вращение поверх него",
            "Отвергается ли Create() потому, что профиль — четыре разомкнутых отрезка, а не область?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R15");
            step.Observe("контрольное тело: "
                + (plate is null ? "не построено" : "построено, " + plate.name));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        // ── rung (1): the same rectangle, but built as a contour and proved closed ──────────────
        ksEntity? sketch = null;
        ksEntity? plainSketch = null;
        var contourRef = 0;

        // A second sketch, drawn by RectangleSketch with withAxis:false, so the ONLY difference from
        // the plate that Api5.BasePlate builds is that this one is built here rather than there. The
        // first run of this step could not tell "an extra segment breaks the region" from "the
        // contour calls break the sketch", because it had no axis-free twin to compare against.
        try
        {
            (_, plainSketch) = RectangleSketch("R15-plain", withAxis: false);
            step.Observe("эскиз без осевой линии построен: "
                + (plainSketch is null ? "нет" : plainSketch.name));
        }
        catch (Exception ex)
        {
            step.Observe("эскиз без осевой линии не построен: " + ex.Message);
        }

        try
        {
            (_, sketch) = RectangleSketch("R15-profile", withAxis: true);
            if (sketch.GetDefinition() is not ksSketchDefinition definition)
            {
                step.Fail("определение эскиза недоступно");
                return;
            }

            if (definition.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() не дал ksDocument2D — контур строить нечем");
                return;
            }

            try
            {
                // ksContour declares a single style argument; the point of the call is to turn the
                // segments already drawn by RectangleSketch into one named region rather than four
                // neighbours.
                contourRef = SafeContour(editor, step);

                // ksMakeEncloseContours(gr, x, y): "enclose the contours around this point". Called
                // with a point strictly inside the rectangle, so a contour that is merely present but
                // not closed around the profile cannot pass for a region.
                var insideU = gapInside(RadiusMm);
                var insideV = 0d;
                var enclosed = Api5.SafeInt(() => editor.ksMakeEncloseContours(0, insideU, insideV));
                step.Observe("ksMakeEncloseContours(0, " + Api5.Num(insideU) + ", " + Api5.Num(insideV)
                    + ") → " + Api5.Raw(enclosed));

                // The proof. Two probes: one inside the rectangle, one outside it. A region must
                // answer "inside" for the first and "outside" for the second; anything else means the
                // contour is not the region the rotation would need.
                //
                // The first argument is a CONTOUR REFERENCE, not a group number: the signature is
                // ksIsPointInsideContour(Int32 p, Double x, Double y, Double precision). The first run
                // of this step passed 0 and got 0 for both points, which is the honest answer to the
                // wrong question — an instrument defect, not a fact about the contour.
                var probe = contourRef != 0 ? contourRef : 0;
                if (probe == 0)
                {
                    step.Observe("ссылки на контур нет — ksIsPointInsideContour не о чем спросить");
                }

                var insideHit = Api5.SafeInt(() => editor.ksIsPointInsideContour(probe, insideU, insideV, 0.001d));
                var outsideHit = Api5.SafeInt(() => editor.ksIsPointInsideContour(probe, -20d, 0d, 0.001d));
                step.Observe("ksIsPointInsideContour(контур=" + probe + "): точка внутри ("
                    + Api5.Num(insideU) + ", " + Api5.Num(insideV) + ") → " + Api5.Raw(insideHit)
                    + "; точка снаружи (-20, 0) → " + Api5.Raw(outsideHit));

                // The interpretation has to be drawn, not assumed. The predicate is used here as a
                // PROOF that the contour encloses the rectangle, so its two answers must DIFFER —
                // otherwise it separated nothing, and the sentence below it claiming "the contour is
                // proved" is a claim with no measurement behind it. In this installation both calls
                // answer 0 (see the journal), so the contour's enclosure is NOT proved by this route
                // and must not be reported as though it were.
                if (insideHit is not null && outsideHit is not null && insideHit != outsideHit)
                {
                    step.Observe("  → предикат РАЗЛИЧИЛ точки: внутри=" + insideHit + ", снаружи="
                        + outsideHit + " — контур охватывает прямоугольник по этому измерению");
                }
                else
                {
                    step.Observe("  → предикат НЕ РАЗЛИЧИЛ точки (обе → " + Api5.Raw(insideHit)
                        + "): этим вызовом охват контура НЕ доказан. Утверждение «контур — область» "
                        + "опирается здесь только на контроль выдавливанием, а не на этот предикат");
                }

                if (contourRef != 0)
                {
                    var closed = Api5.SafeInt(() => editor.ksIsCurveClosed(contourRef));
                    step.Observe("ksIsCurveClosed(контур=" + contourRef + ") → " + Api5.Raw(closed));

                    // The control that makes -1 interpretable: the same predicate on one ordinary
                    // segment. If a segment and a contour both answer -1, the -1 is about the
                    // predicate being unusable here, not about the contour being open.
                    //
                    // ELEVENTH PROBE DEFECT (found 17.09.2026 by reading the journal): this control
                    // was drawn but never checked. `ksLineSeg` returned 0 (as the journal shows —
                    // «ksIsCurveClosed(отрезок=0) → -1»), so the "control" asked about reference zero
                    // and answered -1, which is the answer to a question about nothing. The line
                    // below then asserted the comparison held. A control whose own subject failed to
                    // build cannot certify anything, and reporting it as if it had is the same class
                    // as a name reported as a measurement: the instrument describing itself.
                    var seg = Api5.SafeInt(() => editor.ksLineSeg(0d, 0d, 1d, 0d, 1));
                    if (seg is null || seg == 0)
                    {
                        step.Observe("КОНТРОЛЬ ksIsCurveClosed НЕ СОСТОЯЛСЯ: ksLineSeg вернул "
                            + Api5.Raw(seg) + " — контрольный отрезок не построен, и -1 у контура "
                            + "сравнивать не с чем. Значение контура остаётся неистолкованным");
                    }
                    else
                    {
                        var segClosed = Api5.SafeInt(() => editor.ksIsCurveClosed(seg.Value));
                        step.Observe("ksIsCurveClosed(отрезок=" + seg + ") → " + Api5.Raw(segClosed)
                            + (segClosed == closed
                                ? " — совпало с контуром, значит обе величины отвечают о предикате, "
                                  + "а не о замкнутости"
                                : " — РАЗЛИЧИЛОСЬ, значит величина контура несёт сведения о нём"));
                    }
                }
                else
                {
                    step.Observe("ksContour вернул 0 — ссылки на контур нет, замкнутость проверить не на чем");
                }
            }
            finally
            {
                definition.EndEdit();
                part.RebuildModel();
                _doc.RebuildDocument();
            }
        }
        catch (Exception ex)
        {
            step.Observe("построение контура бросило " + HResult.Describe(ex));
        }

        // ── rung (2): rotate over the contour, with the committed two-point axis ────────────────
        var document7 = _app.TransferInterface(_doc, 2, 0) as KompasAPI7.IKompasDocument3D;
        var aux = document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer;
        var axes = aux?.Axes3D;
        var points = (document7?.TopPart as KompasAPI7.IModelContainer)?.Points3D;

        KompasAPI7.IAxis3D? axis = null;
        if (axes is not null && points is not null)
        {
            var p1 = AuthorPoint(points, 0d, 0d, 0d, "R15-p1", step);
            var p2 = AuthorPoint(points, 0d, 0d, 40d, "R15-p2", step);
            if (p1 is not null && p2 is not null
                && axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points) is KompasAPI7.IAxis3DBy2Points by2)
            {
                by2.Point1 = p1;
                by2.Point2 = p2;
                if (Api5.SafeBool(by2.Update) == true)
                {
                    axis = by2;
                    step.Observe("ось по двум точкам готова: Name=" + SafeName(by2)
                        + ", осей в контейнере " + axes.Count);
                }
            }
        }

        if (axis is null)
        {
            step.Observe("оси нет — вращение будет проверено с эскизной осевой линией как единственным источником");
        }

        if (sketch is null)
        {
            step.Fail("эскиз-профиль не построен — вращать нечего");
            return;
        }

        // ── rung (2a): the control that separates the profile from the operation ────────────────
        // An extrusion over this exact sketch. If the extrusion builds and the rotation does not, the
        // profile is a valid region and the refusal belongs to the rotation — which is a different
        // finding from "the profile was never a region". Without this control the two are the same
        // journal line, which is how seven earlier steps managed to look alike.
        var beforeExtrusion = Api5.Volume(part);

        // The twin first: the same rectangle WITHOUT the axis line. If this extrudes and the
        // axis-bearing one does not, the extra segment is what breaks the region.
        if (plainSketch is not null)
        {
            var twin = TryExtrusionOverSketch(part, plainSketch, step, "R15-extrude-plain");
            var afterTwin = Api5.Volume(part);
            step.Observe("КОНТРОЛЬ-БЛИЗНЕЦ, выдавливание поверх эскиза БЕЗ осевой линии: Create()="
                + twin.Created + " (" + twin.Detail + "), ΔV="
                + Api5.Num((afterTwin ?? 0d) - (beforeExtrusion ?? 0d)));
            if (twin.Created)
            {
                step.Observe("  → прямоугольник без осевой линии — область: выдавливание прошло");
            }
            else
            {
                step.Observe("  → не выдавился даже эскиз без осевой линии: отказ не связан с осевой "
                    + "линией, и сравнивать два эскиза между собой пока нельзя");
            }

            beforeExtrusion = afterTwin;
        }

        var extrusion = TryExtrusionOverSketch(part, sketch, step, "R15-extrude-control");
        var afterExtrusion = Api5.Volume(part);
        step.Observe("КОНТРОЛЬ, выдавливание поверх того же эскиза (с осевой линией): Create()="
            + extrusion.Created + " (" + extrusion.Detail + "), ΔV="
            + Api5.Num((afterExtrusion ?? 0d) - (beforeExtrusion ?? 0d)));
        if (extrusion.Created && afterExtrusion is not null && beforeExtrusion is not null
            && afterExtrusion.Value > beforeExtrusion.Value)
        {
            step.Observe("  → профиль является областью: выдавливание над ним добавило материал, "
                + "значит отказ вращения относится к самому вращению, а не к профилю");
        }

        // ── rung (2b): the rotations ────────────────────────────────────────────────────────────
        var withAxis = TryRotationOverSketch(part, sketch, axis, step, "R15-contour+axis");
        step.Observe("  контур + ось-объект: Create()=" + withAxis.Created + " (" + withAxis.Detail + ")");
        var volumeWithAxis = Api5.Volume(part);

        var withoutAxis = TryRotationOverSketch(part, sketch, null, step, "R15-contour-axisline");
        step.Observe("  контур + осевая линия эскиза: Create()=" + withoutAxis.Created
            + " (" + withoutAxis.Detail + ")");
        var volume = Api5.Volume(part);

        step.Observe("V после попыток: " + Api5.Num(volume) + " (было "
            + Api5.Num(_bodyVolumeBeforeStep) + "); ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("вращение построено поверх замкнутого контура: V=π·r²·h — блокер SM-03 снят");
            return;
        }

        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "профиль как замкнутый контур"))
        {
            return;
        }

        step.Fail("замкнутый контур не дал вращения ожидаемого объёма (V=" + Api5.Num(volume)
            + ", ожидание " + Api5.Num(FullTurnVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>The point strictly inside the rectangle, used to prove the contour encloses it.</summary>
    private static double gapInside(double radius) => radius / 2d;

    /// <summary>
    /// Calls <c>ksContour(style)</c> and reports what came back. Split out so the style number and
    /// the return are both in the journal: the first run must not assume a style is accepted.
    /// </summary>
    private int SafeContour(ksDocument2D editor, ProbeStep step)
    {
        foreach (var style in new[] { 1, 2, 0 })
        {
            var reference = Api5.SafeInt(() => editor.ksContour(style));
            step.Observe("ksContour(" + style + ") → " + Api5.Raw(reference));
            if (reference is { } r && r != 0)
            {
                return r;
            }
        }

        return 0;
    }

    /// <summary>
    /// A base extrusion over the given sketch — the control that separates a bad profile from a
    /// refused operation. Built exactly like the real plate in <c>Api5.BasePlate</c>, so a success
    /// here means the sketch is a region.
    /// </summary>
    /// <remarks>
    /// This rung exists because seven earlier steps all ended at <c>Create()=False</c> with a valid
    /// parameter block and an identical journal line, and none of them could say whether the profile
    /// or the operation was at fault. An extrusion over the same sketch is the cheapest way to ask
    /// the profile on its own.
    /// </remarks>
    private (bool Created, string Detail) TryExtrusionOverSketch(
        ksPart part, ksEntity sketch, ProbeStep step, string name)
    {
        try
        {
            if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity feature
                || feature.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                return (false, "NewEntity(BaseExtrusion) не дал ksBaseExtrusionDefinition");
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            // The five-argument form, and the second argument is the Int16 depth TYPE, not a depth:
            // SetSideParam(Boolean, Int16, Double, Double, Boolean), with 0 = etBlind. This is the
            // call Api5.BasePlate already proves on every plate the suite builds, copied rather than
            // guessed — a guessed overload would have compiled into a different operation.
            Api5.SafeBool(() => definition.SetSideParam(true, 0 /* etBlind */, 20d, 0d, false));

            var created = Api5.SafeBool(feature.Create) == true;
            var isCreated = Api5.SafeBool(feature.IsCreated);
            part.RebuildModel();
            _doc.RebuildDocument();
            return (created, "IsCreated()=" + Api5.Raw(isCreated));
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>
    /// The axis line drawn <b>outside</b> the profile region, so the sweep is still π·r²·h while the
    /// axis segment does not cut the rectangle's interior.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.15 isolated the one variable that had never been isolated. Two sketches, drawn by the same
    /// <see cref="RectangleSketch"/>, extruded by the same proven call:
    /// </para>
    /// <list type="bullet">
    /// <item><c>withAxis: false</c> → <c>Create()=True</c>, <c>IsCreated()=True</c>, <c>ΔV=16000.0</c>
    /// — exactly 20×40×20, the analytic volume of the control extrusion;</item>
    /// <item><c>withAxis: true</c> → <c>Create()=False</c>, <c>IsCreated()=False</c>, <c>ΔV=0</c>.</item>
    /// </list>
    /// <para>
    /// So the axis line is not a passenger. <see cref="DrawAxis"/> draws it as a plain
    /// <c>ksLineSeg(0, v0, 0, v1)</c> — a chord at u = 0 running the full height of the rectangle —
    /// and the rectangle is drawn at u ∈ [0, r]. The chord therefore lies <i>on</i> the rectangle's
    /// left edge and its endpoints touch the region's boundary, which is enough to make the sketch
    /// ambiguous as a region: the extrusion that succeeded without it refuses with it.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> repeats the control at three gaps — 0, 1 and 5 mm — reading
    /// <c>IsCreated()</c> and ΔV for each, so "the axis line is the variable" is confirmed at more
    /// than the one geometry R.15 happened to use.
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> then rotates over the gapped contour with the committed two-point axis, and
    /// the verdict is the same volume gate as everywhere else. The expectation is explicit:
    /// a full turn of a rectangle spanning u ∈ [gap, gap+r] about the v axis sweeps
    /// π·((gap+r)² − gap²)·h, not π·r²·h, so the control value is computed rather than borrowed.
    /// </para>
    /// </remarks>
    private void AxisOutsideProfileRoute()
    {
        _currentStepId = "R.16";
        var step = _report.Begin("R.16", "Осевая линия вне области профиля: контроль при трёх зазорах",
            "Ломает ли эскиз именно отрезок оси, и даёт ли вращение цилиндр, когда ось вне области?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R16");
            step.Observe("контрольное тело: "
                + (plate is null ? "не построено" : "построено, " + plate.name));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        // ── rung (1): three gaps, each extruded, so "the axis line is the variable" is not a
        // one-geometry claim ───────────────────────────────────────────────────────────────────
        var gaps = new[] { 0d, 1d, 5d };
        var controls = new List<string>();
        foreach (var gap in gaps)
        {
            var before = Api5.Volume(part);
            try
            {
                var (_, gapped) = RectangleSketch("R16-gap" + Api5.Num(gap), withAxis: true, gapMm: gap);
                var outcome = TryExtrusionOverSketch(part, gapped, step, "R16-extrude-gap" + Api5.Num(gap));
                var after = Api5.Volume(part);
                controls.Add("зазор " + Api5.Num(gap) + ": Create()=" + outcome.Created
                    + ", ΔV=" + Api5.Num((after ?? 0d) - (before ?? 0d)));
                step.Observe("  зазор " + Api5.Num(gap) + " мм: выдавливание Create()=" + outcome.Created
                    + " (" + outcome.Detail + "), ΔV=" + Api5.Num((after ?? 0d) - (before ?? 0d)));
            }
            catch (Exception ex)
            {
                controls.Add("зазор " + Api5.Num(gap) + ": бросило " + ex.Message);
                step.Observe("  зазор " + Api5.Num(gap) + " мм: бросило " + ex.Message);
            }
        }

        step.Observe("контроль по зазорам: " + string.Join(" | ", controls));

        var gapWorked = controls.Any(c => c.Contains("Create()=True"));
        step.Observe(gapWorked
            ? "→ хотя бы при одном зазоре эскиз с осевой линией остаётся областью: отрезок оси сам "
              + "по себе область не ломает, ломает его положение"
            : "→ ни при одном зазоре эскиз с осевой линией не остался областью: отказ связан не с "
              + "положением отрезка оси, а с самим его наличием или со способом его проведения");

        // ── rung (2): rotate over the widest gap, with the committed two-point axis ─────────────
        var document7 = _app.TransferInterface(_doc, 2, 0) as KompasAPI7.IKompasDocument3D;
        var axes = (document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer)?.Axes3D;
        var points = (document7?.TopPart as KompasAPI7.IModelContainer)?.Points3D;

        KompasAPI7.IAxis3D? axis = null;
        if (axes is not null && points is not null)
        {
            var p1 = AuthorPoint(points, 0d, 0d, 0d, "R16-p1", step);
            var p2 = AuthorPoint(points, 0d, 0d, 40d, "R16-p2", step);
            if (p1 is not null && p2 is not null
                && axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points) is KompasAPI7.IAxis3DBy2Points by2)
            {
                by2.Point1 = p1;
                by2.Point2 = p2;
                if (Api5.SafeBool(by2.Update) == true)
                {
                    axis = by2;
                    step.Observe("ось по двум точкам готова: Name=" + SafeName(by2)
                        + ", осей в контейнере " + axes.Count);
                }
            }
        }

        ksEntity? rotationProfile = null;
        try
        {
            (_, rotationProfile) = RectangleSketch("R16-rotate", withAxis: false, gapMm: 5d);
            step.Observe("профиль для вращения: прямоугольник с зазором 5 мм, БЕЗ отрезка оси в эскизе");
        }
        catch (Exception ex)
        {
            step.Observe("профиль для вращения не построен: " + ex.Message);
        }

        if (rotationProfile is null)
        {
            step.Fail("профиль для вращения не построен — вращать нечего");
            return;
        }

        var (created, detail) = TryRotationOverSketch(part, rotationProfile, axis, step, "R16-rotate");
        step.Observe("  вращение поверх профиля с зазором: Create()=" + created + " (" + detail + ")");

        // The expectation for a gapped profile, computed rather than borrowed: the swept solid is the
        // difference of two cylinders, π(R² − r²)h with R = gap + r and r = gap.
        var outer = 5d + RadiusMm;
        var gappedVolume = Math.PI * (outer * outer - 5d * 5d) * HeightMm;
        var volume = Api5.Volume(part);
        step.Observe("V после попытки: " + Api5.Num(volume) + " (было " + Api5.Num(_bodyVolumeBeforeStep)
            + "); ожидание π(R²−r²)h = " + Api5.Num(gappedVolume)
            + " (R=" + Api5.Num(outer) + ", r=5)");

        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - gappedVolume - _bodyVolumeBeforeStep.Value)
                <= Tolerance(gappedVolume))
        {
            step.Pass("вращение построено поверх профиля с осевой линией вне области: V=π(R²−r²)h — "
                + "блокер SM-03 снят");
            return;
        }

        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "осевая линия вне области профиля"))
        {
            return;
        }

        step.Fail("осевая линия вне области профиля не дала вращения ожидаемого объёма (V="
            + Api5.Num(volume) + ", ожидание " + Api5.Num(gappedVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// The axis line born as construction geometry, swept over the styles, with the extrusion control
    /// per style and then the rotation itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.16 established that a fifth segment in the sketch breaks its region at every gap, and that
    /// a healthy profile with <c>Axis=сохранено</c> still refuses. The type library then supplies the
    /// missing distinction: <c>ksCurveStyleEnum</c> has <c>ksCSConstruction = 6</c> — construction
    /// geometry, drawn in the sketch but not part of its region — alongside <c>ksCSAxial = 3</c> and
    /// <c>ksCSThin = 2</c>. <see cref="DrawAxis"/> passed the style only after creation, so the
    /// segment had already joined the region.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> sweeps the three styles, each on its own sketch, each extruded with the proven
    /// call. The style that lets the extrusion succeed is the one that leaves the region intact.
    /// This is the control R.15 lacked: without it, "the axis line breaks the sketch" cannot be told
    /// from "style 1 specifically breaks the sketch".
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> rotates over the profile drawn with the best style, with the committed
    /// two-point axis. Same three-way verdict and the same volume gate.
    /// </para>
    /// </remarks>
    private void ConstructionAxisStyleRoute()
    {
        _currentStepId = "R.17";
        var step = _report.Begin("R.17", "Осевая линия как вспомогательная геометрия: перебор стилей",
            "Достаточно ли задать стиль при создании отрезка оси, чтобы эскиз остался областью?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — измерять нечего");
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)_doc.GetPart(-1);
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R17");
            step.Observe("контрольное тело: "
                + (plate is null ? "не построено" : "построено, " + plate.name));
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного тела не удалось: " + ex.Message);
            return;
        }

        _bodyVolumeBeforeStep = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(_bodyVolumeBeforeStep));

        // ── rung (1): one style per sketch, each extruded ───────────────────────────────────────
        var styles = new (int Value, string Label)[]
        {
            (AxisStyle, "ksCSConstruction(6)"),
            (3, "ksCSAxial(3)"),
            (2, "ksCSThin(2)"),
        };

        var workingStyle = 0;
        var controls = new List<string>();
        foreach (var (style, label) in styles)
        {
            var before = Api5.Volume(part);
            try
            {
                var (_, styled) = RectangleSketch("R17-style" + style, withAxis: true, axisStyle: style);
                var outcome = TryExtrusionOverSketch(part, styled, step, "R17-extrude-style" + style);
                var after = Api5.Volume(part);
                var delta = (after ?? 0d) - (before ?? 0d);
                controls.Add(label + ": Create()=" + outcome.Created + ", ΔV=" + Api5.Num(delta));
                step.Observe("  " + label + ": выдавливание Create()=" + outcome.Created
                    + " (" + outcome.Detail + "), ΔV=" + Api5.Num(delta));

                if (outcome.Created && delta > 1d && workingStyle == 0)
                {
                    workingStyle = style;
                }
            }
            catch (Exception ex)
            {
                controls.Add(label + ": бросило " + ex.Message);
                step.Observe("  " + label + ": бросило " + ex.Message);
            }
        }

        step.Observe("контроль по стилям: " + string.Join(" | ", controls));
        step.Observe(workingStyle != 0
            ? "→ стиль " + workingStyle + " оставляет эскиз областью: отрезок оси можно провести, "
              + "не ломая профиль — это и есть исправление дефекта зонда в DrawAxis"
            : "→ ни один стиль не оставил эскиз областью: наличие пятого отрезка ломает профиль "
              + "независимо от стиля, и ось придётся брать не из эскиза");

        if (workingStyle == 0)
        {
            step.Fail("осевой отрезок в эскизе ломает профиль при всех проверенных стилях "
                + "(6, 3, 2) — блокер SM-03 остаётся открытым");
            return;
        }

        // ── rung (2): the rotation over a profile that carries such an axis ─────────────────────
        var document7 = _app.TransferInterface(_doc, 2, 0) as KompasAPI7.IKompasDocument3D;
        var axes = (document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer)?.Axes3D;
        var points = (document7?.TopPart as KompasAPI7.IModelContainer)?.Points3D;

        KompasAPI7.IAxis3D? axis = null;
        if (axes is not null && points is not null)
        {
            var p1 = AuthorPoint(points, 0d, 0d, 0d, "R17-p1", step);
            var p2 = AuthorPoint(points, 0d, 0d, 40d, "R17-p2", step);
            if (p1 is not null && p2 is not null
                && axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points) is KompasAPI7.IAxis3DBy2Points by2)
            {
                by2.Point1 = p1;
                by2.Point2 = p2;
                if (Api5.SafeBool(by2.Update) == true)
                {
                    axis = by2;
                    step.Observe("ось по двум точкам готова: Name=" + SafeName(by2)
                        + ", осей в контейнере " + axes.Count);
                }
            }
        }

        ksEntity? profile = null;
        try
        {
            (_, profile) = RectangleSketch("R17-rotate", withAxis: true, axisStyle: workingStyle);
            step.Observe("профиль для вращения построен со стилем " + workingStyle
                + " (осевая линия в эскизе)");
        }
        catch (Exception ex)
        {
            step.Observe("профиль для вращения не построен: " + ex.Message);
        }

        if (profile is null)
        {
            step.Fail("профиль для вращения не построен — вращать нечего");
            return;
        }

        // Both variants, because the sketch now carries an axis AND an axis object can be offered.
        var (createdWithAxis, detailWithAxis) =
            TryRotationOverSketch(part, profile, axis, step, "R17-rotate+axis");
        step.Observe("  вращение поверх профиля со вспомогательной осью, ось-объект подан: Create()="
            + createdWithAxis + " (" + detailWithAxis + ")");
        var volumeWithAxis = Api5.Volume(part);

        var (createdNoAxis, detailNoAxis) =
            TryRotationOverSketch(part, profile, null, step, "R17-rotate-axisline");
        step.Observe("  вращение поверх профиля со вспомогательной осью, ось только из эскиза: Create()="
            + createdNoAxis + " (" + detailNoAxis + ")");
        var volume = Api5.Volume(part);

        step.Observe("V после попыток: " + Api5.Num(volume) + " (было " + Api5.Num(_bodyVolumeBeforeStep)
            + "); ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        if (volume is not null && _bodyVolumeBeforeStep is not null
            && Math.Abs(volume.Value - _bodyVolumeBeforeStep.Value - FullTurnVolume)
                <= Tolerance(FullTurnVolume))
        {
            step.Pass("вращение построено поверх профиля с вспомогательной осью: ΔV=π·r²·h — "
                + "блокер SM-03 снят");
            return;
        }

        if (BodyDidNotMove(step, _bodyVolumeBeforeStep, volume, "осевая линия как вспомогательная геометрия"))
        {
            return;
        }

        step.Fail("профиль с вспомогательной осевой линией не дал вращения ожидаемого объёма (V="
            + Api5.Num(volume) + ", ожидание " + Api5.Num(FullTurnVolume)
            + ") — блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// R.21 — the two member families of a rotation definition that no step of this probe has ever
    /// written: the thin-wall parameters and the toroidal-shape flag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reflection over the installed type library (17.09.2026, <c>scratch/reflect</c>) prints the full
    /// member list of the three rotation definitions. <c>ksBaseRotatedDefinition</c> declares
    /// <b>14</b> members and the probe writes exactly five of them — <c>SetSketch</c>,
    /// <c>directionType</c>, <c>SetSideParam</c> (and its <c>GetSideParam</c> readback), and the
    /// <c>RotatedParam()</c> block. Two families are declared and <b>never written by any step</b>:
    /// </para>
    /// <list type="bullet">
    /// <item><c>SetThinParam(Boolean, Int16, Double, Double)</c> / <c>GetThinParam</c> /
    /// <c>ThinParam()</c> — the thin-wall rotation (a shelled revolve),</item>
    /// <item><c>toroidShapeType</c> — the toroidal flag.</item>
    /// </list>
    /// <para>
    /// This is the same shape of gap that R.19 closed: there, reflection found
    /// <c>IRotated1.OperationResult</c> declared and never written, and writing it changed the result
    /// (two of four values returned <c>Create()=True</c>). So the cheap explanation for the remaining
    /// refusal is that the definition is being asked to build while sitting in a thin-wall or toroidal
    /// state that no caller ever set — a state whose default may be invalid for this sketch.
    /// </para>
    /// <para>
    /// <b>Read before writing.</b> The first measurement is what a fresh definition reports for both
    /// families with nothing set. That reading tells whether the field sits at a benign default or is
    /// already in a state that would explain the refusal. Then the values are swept — toroid both
    /// ways, thin wall off and on with a thickness — each with the volume gate as the only verdict.
    /// </para>
    /// <para>
    /// A member that accepts a value and changes nothing is recorded as exactly that. The distinction
    /// this step must preserve is between "the member was never the reason" (measured: writing it does
    /// not move the volume) and "the member was never asked" — which is the state before this step.
    /// </para>
    /// <para>
    /// <b>What it measured (17.09.2026).</b> A fresh definition reports <c>toroidShapeType = False</c>
    /// and <c>GetThinParam() = False, включена=False, тип=0, толщина=0, зазор=0</c> — both at benign
    /// defaults, neither in a state that could explain a refusal. All four variants were then written
    /// explicitly: <c>SetThinParam</c> returned <c>True</c> and read back <b>exactly</b> what was
    /// written (including <c>включена=True, толщина=2</c>), <c>toroidShapeType</c> read back whatever
    /// was stored — and <c>Create()</c> stayed <c>False</c> in all four, with the volume unchanged.
    /// The best explanation is therefore excluded: the thin-wall and toroidal families are not the
    /// call that was missing. Like R.20's collection, this line is now exhausted rather than open.
    /// </para>
    /// </remarks>
    private void UnwrittenRotationMembersRoute()
    {
        _currentStepId = "R.21";
        var step = _report.Begin("R.21", "Незаписанные члены определения вращения: тонкая стенка и тороид",
            "Требует ли Create() вращения того, чтобы тонкая стенка и тороид были заданы явно?");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — писать нечего");
            return;
        }

        // ── rung (1): what a FRESH definition reports for both families, with nothing set ─────────
        // This is the reading the step exists for. A definition built by NewEntity has never had
        // either member written, so whatever it reports here is the default the previous steps were
        // building against without knowing it.
        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R21-defaults", withAxis: true);
        }
        catch (Exception ex)
        {
            step.Fail("эскиз для замера умолчаний не построен: " + ex.Message);
            return;
        }

        if (part.NewEntity(type) is not ksEntity probeFeature
            || probeFeature.GetDefinition() is not ksBaseRotatedDefinition probeDefinition)
        {
            step.Fail("NewEntity(" + type + ") не дал ksBaseRotatedDefinition — замер умолчаний невозможен");
            return;
        }

        step.Observe("свежее определение, НИ ОДИН из двух членов не записан:");

        var toroidDefault = "не прочитано";
        try
        {
            toroidDefault = Api5.Raw(probeDefinition.toroidShapeType);
        }
        catch (Exception ex)
        {
            toroidDefault = "бросил " + HResult.Describe(ex);
        }

        step.Observe("  toroidShapeType по умолчанию → " + toroidDefault);

        // GetThinParam(Boolean&, Int16&, Double&, Double&) — out-parameters, so it gets its own try:
        // a throw here must not take the toroid reading with it.
        var thinDefault = "не прочитано";
        try
        {
            var ok = probeDefinition.GetThinParam(out var thinOn, out var thinType,
                out var thinThickness, out var thinGap);
            thinDefault = "GetThinParam()=" + Api5.Raw(ok) + ", включена=" + thinOn
                + ", тип=" + thinType + ", толщина=" + Api5.Num(thinThickness)
                + ", зазор=" + Api5.Num(thinGap);
        }
        catch (Exception ex)
        {
            thinDefault = "бросил " + HResult.Describe(ex);
        }

        step.Observe("  GetThinParam() по умолчанию → " + thinDefault);

        var thinBlockDefault = "не прочитано";
        try
        {
            var block = probeDefinition.ThinParam();
            thinBlockDefault = block is null ? "null" : Api5.RuntimeName(block);
        }
        catch (Exception ex)
        {
            thinBlockDefault = "бросил " + HResult.Describe(ex);
        }

        step.Observe("  ThinParam() по умолчанию → " + thinBlockDefault);

        // ── rung (2): sweep, with the volume gate as the verdict ──────────────────────────────────
        var outcomes = new List<string>();
        var succeeded = false;

        // One thing varies per row and everything under test is written explicitly, so no reading
        // below can be attributed to a member this rung did not set.
        var variants = new (string Label, bool Toroid, bool ThinOn, double ThinThickness)[]
        {
            ("тороид выкл, тонкая стенка выкл", false, false, 0d),
            ("тороид вкл, тонкая стенка выкл", true, false, 0d),
            ("тороид выкл, тонкая стенка вкл (2)", false, true, 2d),
            ("тороид вкл, тонкая стенка вкл (2)", true, true, 2d),
        };

        var variantIndex = 0;
        foreach (var (label, toroid, thinOn, thickness) in variants)
        {
            variantIndex++;
            ksPart fresh;
            ksEntity freshSketch;
            try
            {
                (fresh, freshSketch) = RectangleSketch("R21-v" + variantIndex, withAxis: true);
            }
            catch (Exception ex)
            {
                outcomes.Add(label + ": эскиз не построен (" + ex.Message + ")");
                continue;
            }

            var before = Api5.Volume(fresh);

            if (fresh.NewEntity(type) is not ksEntity feature
                || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
            {
                outcomes.Add(label + ": NewEntity(" + type + ") не дал определения");
                continue;
            }

            var notes = new List<string>();
            try
            {
                definition.SetSketch(freshSketch);
                definition.directionType = 0;
                definition.SetSideParam(true, 360d);

                try
                {
                    definition.toroidShapeType = toroid;
                    notes.Add("toroidShapeType записан=" + toroid
                        + ", прочитан=" + Api5.Raw(definition.toroidShapeType));
                }
                catch (Exception ex)
                {
                    notes.Add("toroidShapeType бросил " + HResult.Describe(ex));
                }

                // SetThinParam(Boolean, Int16, Double, Double): (on, type, thickness, gap). The type
                // number 0 is passed because the metadata does not name its enum; if the call refuses
                // the number, that is the answer and the note says so.
                try
                {
                    var thinOk = definition.SetThinParam(thinOn, 0, thickness, 0d);
                    notes.Add("SetThinParam(" + thinOn + ", 0, " + Api5.Num(thickness) + ", 0) → "
                        + Api5.Raw(thinOk));

                    var readBack = definition.GetThinParam(out var rOn, out var rType,
                        out var rThickness, out var rGap);
                    notes.Add("прочитано обратно: " + Api5.Raw(readBack) + ", включена=" + rOn
                        + ", тип=" + rType + ", толщина=" + Api5.Num(rThickness)
                        + ", зазор=" + Api5.Num(rGap));
                }
                catch (Exception ex)
                {
                    notes.Add("SetThinParam бросил " + HResult.Describe(ex));
                }

                var created = Api5.SafeBool(feature.Create) == true;
                fresh.RebuildModel();
                _doc.RebuildDocument();
                var after = Api5.Volume(fresh);
                var delta = after is not null && before is not null ? after - before : after;

                // A delta that is a few ULPs of the baseline is not a moved volume: the two readings
                // are the same double, re-read. Saying "ΔV=0" invites a later reader to think the
                // number was rounded; saying "ΔV≈1e-10" invites the opposite. Both readings are
                // therefore printed in full, and a noise-sized delta is named as such.
                var magnitude = after is not null && before is not null
                    ? "ΔV=" + Api5.Num(delta) + " (до=" + Api5.Num(before) + ", после=" + Api5.Num(after) + ")"
                    : "ΔV=" + Api5.Num(delta);
                if (after is not null && before is not null && delta is not null
                    && Math.Abs(delta.Value) > 0d
                    && Math.Abs(delta.Value) <= NoiseFloor(before.Value))
                {
                    magnitude += " — это шум представления double (≈1 ULP), а не сдвиг объёма: "
                        + "оба чтения — одно и то же число";
                }

                outcomes.Add(label + ": Create()=" + created + ", " + magnitude);
                step.Observe("  " + label + ": Create()=" + created + ", " + magnitude
                    + " (" + string.Join("; ", notes) + ")");
                // Self-report of the gate itself: a reader must be able to see WHY the noise note did
                // or did not appear, rather than inferring it from the absence of a sentence. The delta
                // is printed SIGNED and its absolute value separately — a signed number under bars
                // would be a small lie of the same family as the ones this probe exists to avoid.
                if (delta is not null && before is not null)
                {
                    var absDelta = Math.Abs(delta.Value);
                    step.Observe("    порог шума: ΔV=" + delta.Value.ToString("0.############e+00", CultureInfo.InvariantCulture)
                        + ", |ΔV|=" + absDelta.ToString("0.############e+00", CultureInfo.InvariantCulture)
                        + ", |до|·2^-52·8=" + NoiseFloor(before.Value).ToString("0.############e+00", CultureInfo.InvariantCulture)
                        + " → " + (absDelta <= NoiseFloor(before.Value) ? "в пределах шума (объём не изменился)" : "БОЛЬШЕ шума"));
                }

                if (created && delta is not null
                    && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
                {
                    succeeded = true;
                }
            }
            catch (Exception ex)
            {
                outcomes.Add(label + ": бросил " + HResult.Describe(ex));
                step.Observe("  " + label + ": бросил " + HResult.Describe(ex));
            }
        }

        step.Observe("свод по вариантам: " + string.Join(" | ", outcomes));

        if (succeeded)
        {
            step.Pass("вращение построено при явно заданных незаписанных членах: ΔV=π·r²·h — блокер SM-03 снят");
            return;
        }

        // The honest FAIL, and it names which members are now excluded rather than leaving the
        // question open: both families have now been WRITTEN — before this step no caller ever wrote
        // them — and the volume still does not move, so neither is the missing call. The read-backs
        // matter as much as the write: SetThinParam returned True and GetThinParam returned exactly
        // what was set, so the member is not merely declarable, it is accepted and holds a value.
        step.Fail("ни один из вариантов незаписанных членов не дал вращения (умолчания прочитаны: toroid="
            + toroidDefault + "; " + thinDefault + "). Значит тонкая стенка и тороид — не тот вызов, "
            + "которого не хватало: SetThinParam принял значение и вернул его же на чтении, "
            + "toroidShapeType сохранился, и объём не сдвинулся ни в одном из четырёх вариантов, "
            + "а до этого шага эти члены не писал НИ ОДИН шаг пробы. Блокер SM-03 остаётся открытым");
    }

    /// <summary>
    /// The one variable never varied: the <b>boolean operation</b> the feature performs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.18 closed every input-side explanation. Profile proved a region by an extrusion control,
    /// axis committed and held, sides and parameter order varied, clean document with exactly one
    /// sketch — and <c>Create()</c> is still <c>False</c>.
    /// </para>
    /// <para>
    /// Reflection over API7 found a member that no step of this probe has ever written and that the
    /// journal never carries: <c>IRotated1.OperationResult</c>, of type
    /// <c>ksOperationResultEnum</c>. The enumeration's own values are
    /// <c>ksOperationUnion = 0</c>, <c>ksOperationNewBody = 1</c>, <c>ksOperationCut = 2</c>,
    /// <c>ksOperationIntersect = 3</c>. It is declared — with the identical signature — on
    /// <c>IExtrusion1</c>, <c>ILoft</c>, <c>IEvolution</c> and <c>ITrimmedSurface</c>, i.e. it is the
    /// <i>operation kind</i> every solid feature in this API carries, not an error code. The name is
    /// misleading; the enum is the price.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> reads <c>OperationResult</c> back from a <b>working</b> extrusion and from a
    /// refusing rotation in the same run. If the extrusion reports a defined value and the rotation
    /// reports a different one, the refusal is a default that has to be written. That comparison is
    /// what makes the rung a measurement rather than a guess — the control and the subject are read
    /// by the same call on the same live objects.
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> then sweeps all four <c>ksOperationResultEnum</c> values on a fresh document
    /// with one sketch, and the verdict is the volume gate as always: π·r²·h for a lone cylinder.
    /// </para>
    /// </remarks>
    private void BooleanOperationRoute()
    {
        _currentStepId = "R.19";
        var step = _report.Begin("R.19", "Операция тела: ksOperationResultEnum на вращении",
            "Требует ли Create() вращения явной операции (объединение/новое тело/вырез)?");
        step.Observe("ksOperationResultEnum: union=" + (int)ksOperationResultEnum.ksOperationUnion
            + ", newBody=" + (int)ksOperationResultEnum.ksOperationNewBody
            + ", cut=" + (int)ksOperationResultEnum.ksOperationCut
            + ", intersect=" + (int)ksOperationResultEnum.ksOperationIntersect);

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден — операцию подавать некуда");
            return;
        }

        // ── rung (1): read OperationResult off a working extrusion and a refusing rotation ───────
        // The working extrusion is built here, not borrowed: the point is that the control and the
        // subject are read by the same call in the same run.
        var controlValue = ReadOperationResultOfControlExtrusion(step);

        // ── rung (2): sweep all four operations on a fresh single-sketch document ───────────────
        var outcomes = new List<string>();
        var succeeded = false;

        var operations = new (ksOperationResultEnum Value, string Label)[]
        {
            (ksOperationResultEnum.ksOperationUnion, "ksOperationUnion(0)"),
            (ksOperationResultEnum.ksOperationNewBody, "ksOperationNewBody(1)"),
            (ksOperationResultEnum.ksOperationCut, "ksOperationCut(2)"),
            (ksOperationResultEnum.ksOperationIntersect, "ksOperationIntersect(3)"),
        };

        foreach (var (operation, label) in operations)
        {
            var (created, volume, detail) = TryRotationWithOperation(type, operation, controlValue, step, label);
            outcomes.Add(label + ": Create()=" + created + ", ΔV=" + Api5.Num(volume) + " (" + detail + ")");
            step.Observe("  " + label + ": Create()=" + created + ", ΔV=" + Api5.Num(volume)
                + " (" + detail + ")");

            if (created && volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
            {
                succeeded = true;
            }
        }

        step.Observe("свод по операциям: " + string.Join(" | ", outcomes));

        if (succeeded)
        {
            step.Pass("вращение построено с явной операцией тела: ΔV=π·r²·h — блокер SM-03 снят");
            return;
        }

        // ── rung (3): one document, the same profile, an extrusion control and a rotation ────────
        // rung (2) measured `тел=0` for every operation, including the two whose Create() returned
        // True. A Create()=True with no body is the solver accepting a no-op — there was nothing to
        // cut or intersect — so the volume gate is the only thing that could have caught it. Rung
        // (3) removes the last confound: one document, a plate that exists, the profile extruded by
        // the proven call and then rotated over, so a body is present before the rotation asks.
        var rung3 = RotationOverExistingBody(type, step);
        step.Observe("  rung 3 (тело уже есть): " + rung3);

        if (rung3.Contains("ΔV=" + Api5.Num(FullTurnVolume)))
        {
            step.Pass("вращение построено поверх существующего тела: ΔV=π·r²·h — блокер SM-03 снят");
            return;
        }

        step.Fail("ни одна из четырёх операций ksOperationResultEnum не дала вращения, и тело поверх "
            + "профиля тоже не появилось — ksOperationResultEnum не объясняет отказа, блокер SM-03 "
            + "остаётся открытым");
    }

    /// <summary>
    /// The last confound removed: a document that already contains a body, the rotation's own profile
    /// extruded by the <b>proven</b> call to prove it is a region, and then the rotation over it with
    /// <c>ksOperationUnion</c>.
    /// </summary>
    /// <remarks>
    /// Rung (2) showed that <c>Create()=True</c> alone is not evidence: with <c>cut</c> and
    /// <c>intersect</c> on an empty document the solver accepts the operation and leaves
    /// <c>тел=0</c>. This rung gives the rotation something to union with, and reports — before the
    /// rotation is even attempted — the volume an extrusion of the identical sketch produces. If the
    /// extrusion's ΔV is the analytic value and the rotation's is zero, the profile is a region and
    /// the refusal is the rotation's, with no remaining document-context explanation.
    /// </remarks>
    private string RotationOverExistingBody(short type, ProbeStep step)
    {
        ksDocument3D? fresh = null;
        try
        {
            fresh = (ksDocument3D)_app.Document3D();
            fresh.Create(true, true);
            if (fresh.GetPart(-1) is not ksPart part)
            {
                return "деталь не получена";
            }

            // (3a) a plate, so the document has a body and `union` has a target.
            var plate = Api5.BasePlate(part, 100d, 80d, 10d, step, "R19b");
            var plateVolume = Api5.Volume(part);
            step.Observe("  rung 3: плита " + (plate is null ? "не построена" : "построена")
                + ", объём плиты=" + Api5.Num(plateVolume));

            // (3b) the profile sketch, identical to rung (2)'s.
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                return "эскиз не создан";
            }

            sketch.name = "R19b-profile";
            sketchDefinition.SetPlane(plane);
            sketch.Create();
            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                return "BeginEdit() не дал ksDocument2D";
            }

            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);
            editor.ksLineSeg(0d, -HeightMm / 2d, 0d, HeightMm / 2d, AxisStyle);
            sketchDefinition.EndEdit();
            part.RebuildModel();
            fresh.RebuildDocument();

            // (3c) THE CONTROL, in the same document: extrude the very sketch the rotation refuses.
            var (extruded, extrudeDetail) = TryExtrusionOverSketch(part, sketch, step, "R19b-extrude-control");
            var afterExtrude = Api5.Volume(part);
            var extrudeDelta = afterExtrude is not null && plateVolume is not null
                ? afterExtrude - plateVolume : null;
            step.Observe("  rung 3: КОНТРОЛЬ — выдавливание того же эскиза: Create()=" + extruded
                + ", ΔV=" + Api5.Num(extrudeDelta) + " (" + extrudeDetail + ")");

            // (3d) the rotation over that same sketch, with an explicit union.
            if (part.NewEntity(type) is not ksEntity feature
                || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
            {
                return "NewEntity(" + type + ") не дал ksBaseRotatedDefinition; контроль ΔV="
                    + Api5.Num(extrudeDelta);
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            Api5.SafeBool(() => definition.SetSideParam(true, 360d));

            var object7 = _app.TransferInterface(feature, 2, 0);
            if (object7 is not KompasAPI7.IRotated rotated)
            {
                return "признак не отвечает на QI(IRotated)";
            }

            var axisHeld = "не подавалась";
            var ops = new[] { ksOperationResultEnum.ksOperationUnion, ksOperationResultEnum.ksOperationNewBody };
            var results = new List<string>();
            foreach (var op in ops)
            {
                if (object7 is KompasAPI7.IRotated1 rotated1)
                {
                    try { rotated1.OperationResult = op; }
                    catch (Exception ex) { step.Observe("  rung 3: запись " + op + " бросила " + HResult.Describe(ex)); }
                }

                var created = Api5.SafeBool(feature.Create) == true;
                var isCreated = Api5.SafeBool(feature.IsCreated);
                part.RebuildModel();
                fresh.RebuildDocument();
                var now = Api5.Volume(part);
                var delta = now is not null && afterExtrude is not null ? now - afterExtrude : null;
                results.Add(op + ": Create()=" + created + ", IsCreated()=" + Api5.Raw(isCreated)
                    + ", ΔV=" + Api5.Num(delta));
                step.Observe("  rung 3: " + op + " → Create()=" + created + ", IsCreated()="
                    + Api5.Raw(isCreated) + ", ΔV=" + Api5.Num(delta) + " (" + axisHeld + ")");
            }

            // ── rung (4): the same rotation over an axis-free profile, with an API7 axis object ──
            // If the refusal were caused by the axis line inside the sketch — the one thing rung 3's
            // profile carries that the extrusion control did not mind — a profile without it, with
            // the axis supplied as a separate API7 object, would sweep. This separates "the sketch's
            // axis line poisons the sweep" from "the rotation refuses regardless of where the axis
            // comes from", which is the last distinction the blocker's wording still left open.
            var axisFree = RotationOverAxisFreeProfile(type, part, fresh, plateVolume, step);

            return "контроль выдавливанием ΔV=" + Api5.Num(extrudeDelta)
                + " (ожидание 16000) | вращение: " + string.Join(" | ", results)
                + " | " + axisFree;
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
        finally
        {
            if (fresh is not null)
            {
                CloseFresh(fresh, step);
            }
        }
    }

    /// <summary>
    /// A rotation over a profile <b>without</b> an axis line in the sketch, with the axis supplied as
    /// a separate committed API7 object — the last distinction the blocker's wording leaves open.
    /// </summary>
    /// <remarks>
    /// Rung (3) proved the sketch is a region by extruding it. But that sketch carries a construction
    /// axis segment, and the extrusion control does not care about a construction segment while a
    /// rotation needs to interpret it as the axis. This rung draws the same rectangle with no axis
    /// segment at all — a profile an extrusion already proved twice (R.15, R.16) — and hands the
    /// rotation the committed two-point axis instead, so "the axis line poisons the sweep" and "the
    /// rotation refuses whatever the axis source" are separated by measurement.
    /// </remarks>
    private string RotationOverAxisFreeProfile(
        short type, ksPart part, ksDocument3D fresh, double? baseline, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                return "rung 4: эскиз не создан";
            }

            sketch.name = "R19c-profile-noaxis";
            sketchDefinition.SetPlane(plane);
            sketch.Create();
            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                return "rung 4: BeginEdit() не дал ksDocument2D";
            }

            // No axis segment at all: four sides, nothing else.
            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);
            sketchDefinition.EndEdit();
            part.RebuildModel();
            fresh.RebuildDocument();

            var beforeControl = Api5.Volume(part);
            var (extruded, detail) = TryExtrusionOverSketch(part, sketch, step, "R19c-extrude-control");
            var afterControl = Api5.Volume(part);
            var controlDelta = afterControl is not null && beforeControl is not null
                ? afterControl - beforeControl : null;
            step.Observe("  rung 4: КОНТРОЛЬ эскиза без осевой линии: Create()=" + extruded
                + ", ΔV=" + Api5.Num(controlDelta) + " (" + detail + ")");

            // The committed two-point axis, authored into this document's own container.
            var document7 = _app.TransferInterface(fresh, 2, 0) as KompasAPI7.IKompasDocument3D;
            var axes = (document7?.TopPart as KompasAPI7.IAuxiliaryGeomContainer)?.Axes3D;
            var points = (document7?.TopPart as KompasAPI7.IModelContainer)?.Points3D;

            KompasAPI7.IAxis3D? axis = null;
            if (axes is not null && points is not null)
            {
                var p1 = AuthorPoint(points, 0d, 0d, 0d, "R19c-p1", step);
                var p2 = AuthorPoint(points, 0d, 0d, HeightMm, "R19c-p2", step);
                if (p1 is not null && p2 is not null
                    && axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points) is KompasAPI7.IAxis3DBy2Points by2)
                {
                    by2.Point1 = p1;
                    by2.Point2 = p2;
                    if (Api5.SafeBool(by2.Update) == true)
                    {
                        axis = by2;
                    }
                }
            }

            step.Observe("  rung 4: ось по двум точкам " + (axis is null ? "не построена" : "построена"));

            if (part.NewEntity(type) is not ksEntity feature
                || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
            {
                return "rung 4: NewEntity(" + type + ") не дал ksBaseRotatedDefinition";
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            Api5.SafeBool(() => definition.SetSideParam(true, 360d));

            var object7 = _app.TransferInterface(feature, 2, 0);
            if (object7 is not KompasAPI7.IRotated rotated)
            {
                return "rung 4: признак не отвечает на QI(IRotated)";
            }

            var axisHeld = "не подавалась";
            if (axis is not null)
            {
                rotated.Axis = axis;
                axisHeld = rotated.Axis is null ? "null (отброшено)" : "сохранено";
            }

            if (object7 is KompasAPI7.IRotated1 rotated1)
            {
                try { rotated1.OperationResult = ksOperationResultEnum.ksOperationUnion; }
                catch (Exception) { /* the refusal is what is being measured, not this write */ }
            }

            var created = Api5.SafeBool(feature.Create) == true;
            var isCreated = Api5.SafeBool(feature.IsCreated);
            part.RebuildModel();
            fresh.RebuildDocument();
            var now = Api5.Volume(part);
            var delta = now is not null && afterControl is not null ? now - afterControl : null;

            step.Observe("  rung 4: вращение над эскизом без осевой линии → Create()=" + created
                + ", IsCreated()=" + Api5.Raw(isCreated) + ", Axis=" + axisHeld
                + ", ΔV=" + Api5.Num(delta));

            return "rung 4: контроль ΔV=" + Api5.Num(controlDelta) + " (ожидание 16000), вращение Axis="
                + axisHeld + " Create()=" + created + " ΔV=" + Api5.Num(delta);
        }
        catch (Exception ex)
        {
            return "rung 4 бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Whether the axis and profile a shipped rotation hands back can be carried into a document the
    /// probe owns and used to build a rotation there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the last unmeasured route.</b> R.7…R.19 established, by measurement, that every
    /// axis the probe can <i>author</i> is rejected: an edge, a cylindrical face, an axis object from
    /// <c>Axes3D</c>, an axis by two points, an axis from the sketch. R.22 then found something no
    /// earlier step had: a shipped rotation's <c>IRotated.Axis</c> reads back as a live object rather
    /// than <c>null</c>. R.7's own feature read <c>Axis=null</c> after the write, which was recorded as
    /// "the member silently drops the value". If the axis a shipped rotation carries is one the API
    /// itself accepts, then "SM-03 is blocked" and "the probe never had an acceptable axis" are two
    /// different claims, and only this measurement separates them.
    /// </para>
    /// <para>
    /// <b>What is measured.</b> A shipped file is opened and its rotation's <c>Axis</c> and
    /// <c>Profile</c> are taken as objects. Then the probe's own document builds a plate, a sketch with
    /// a closed rectangle profile, and a rotation — and tries to feed the shipped pair into
    /// <c>IRotated.SetAxis</c>/<c>SetProfile</c> on its own feature. Two preconditions are checked
    /// first, because a failure to satisfy either would make the main attempt meaningless: that the
    /// shipped axis object answers for something nameable (a runtime name other than a null), and that
    /// the shipping document is still open and alive when the transfer happens (КОМПАС revokes
    /// references when a document closes, and a revoked axis would fail for a reason that has nothing
    /// to do with the axis).
    /// </para>
    /// <para>
    /// <b>Verdict.</b> Pass only if the volume moves to the analytic value the profile implies — a
    /// transferred axis is worth exactly what it builds, and nothing else. <c>Create()=True</c> without a
    /// volume change is the no-op R.19 already caught once, and is reported as such. Fail otherwise,
    /// and the failure text says which of the three things happened: the shipped file gave no axis, the
    /// transfer was refused, or the transfer was accepted and built nothing.
    /// </para>
    /// </remarks>
    private void DirectFactoryRoute()
    {
        _currentStepId = "R.24";
        var step = _report.Begin("R.24", "Прямая фабрика API7: IModelContainer.Rotateds → IRotateds.Add",
            "Создаётся ли вращение самой фабрикой API7 — без оболочки NewEntity и без API5 Create()?");

        ksDocument3D? ownDoc = null;
        try
        {
            // The two-argument form R.18/R.23 measured to produce a document that actually becomes the
            // one the part is read from. `Create(false, false)` is the form R.20 measured to break the
            // very next `Open`, and reading the part afterwards then kept reporting the SHARED
            // document's body (probe defect 14, second half).
            ownDoc = (ksDocument3D)_app.Document3D();
            ownDoc.Create(true, true);

            if (ownDoc.GetPart(-1) is not ksPart part)
            {
                step.Fail("свой документ не дал деталь — опыт не поставлен");
                return;
            }

            KompasAPI7.IModelContainer? container = null;
            try
            {
                var transferred = _app.TransferInterface(ownDoc, 2 /* ksAPI7Dual */, 0);
                container = (transferred as KompasAPI7.IKompasDocument3D)?.TopPart
                    as KompasAPI7.IModelContainer;
                step.Observe("TransferInterface(документ → API7) → " + Api5.RuntimeName(transferred)
                    + "; TopPart как IModelContainer → " + (container is null ? "null" : "OK"));
            }
            catch (Exception ex)
            {
                step.Observe("TransferInterface(документ → API7) бросил " + HResult.Describe(ex));
            }

            if (container is null)
            {
                step.Fail("документ не дал IModelContainer через TopPart — фабрика недостижима, и это "
                    + "утверждение о маршруте контейнера, а НЕ об отсутствии вращения");
                return;
            }

            KompasAPI7.IRotateds? rotateds = null;
            try
            {
                rotateds = container.Rotateds;
                step.Observe("IModelContainer.Rotateds → " + Api5.RuntimeName(rotateds)
                    + (rotateds is null
                        ? string.Empty
                        : ", Count=" + Api5.Raw(Api5.SafeInt(() => rotateds.Count))));
            }
            catch (Exception ex)
            {
                step.Observe("IModelContainer.Rotateds бросил " + HResult.Describe(ex));
            }

            if (rotateds is null)
            {
                step.Fail("IModelContainer.Rotateds недоступна на живом контейнере — вопрос о прямой "
                    + "фабрике остался без предмета (это НЕ утверждение о том, что фабрика не создаёт)");
                return;
            }

            var (_, profileSketch) = RectangleSketchIn(ownDoc, "R24-profile", withAxis: false);
            step.Observe("профиль-эскиз «R24-profile»: " + Api5.RuntimeName(profileSketch));

            var control = ControlExtrusionInOwnDocument("R24");
            step.Observe("КОНТРОЛЬ (в ОТДЕЛЬНОМ документе): выдавливание того же профиля → " + control);

            // The axis is what R.24 was missing. Run 228b7540 measured a full member-write sweep that
            // reached `Update()=False, Valid=False` for all three operation types — the same no-op R.19
            // had already shown for a rotation without a fixed axis, so R.24 could not have failed for
            // a reason of its own. With the reference axis from the part itself the sweep answers the
            // question R.24 exists to ask: does the factory create the operation for each type, and
            // does it build from each of them.
            var axis = BuildReferenceAxis(ownDoc, part, step, u: 0d);
            if (axis is null)
            {
                step.Fail("ось на линии u=0 не построена — перебор фабрики непоставим, и это "
                    + "утверждение об оси, а не о фабрике");
                return;
            }

            step.Observe("ось для перебора готова: " + Describe7(axis));

            var operations = new (Kompas6Constants3D.ksObj3dTypeEnum Type, string Label)[]
            {
                (Kompas6Constants3D.ksObj3dTypeEnum.o3d_baseRotated, "o3d_baseRotated(27)"),
                (Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossRotated, "o3d_bossRotated(28)"),
                (Kompas6Constants3D.ksObj3dTypeEnum.o3d_cutRotated, "o3d_cutRotated(29)"),
            };

            var sweep = new List<string>();
            var built = 0;
            var created = 0;
            var firstSuccess = "";

            foreach (var (type, label) in operations)
            {
                var row = new List<string> { label };
                KompasAPI7.IRotated? rotation = null;
                try
                {
                    var raw = rotateds.Add(type);
                    row.Add("Add → " + Api5.RuntimeName(raw));
                    if (raw is KompasAPI7.IModelObject object7)
                    {
                        rotation = object7 as KompasAPI7.IRotated;
                        row.Add("QI(IRotated)=" + (rotation is null ? "ОТКАЗ" : "OK"));
                        row.Add("QI(IRotated1)=" + (object7 is KompasAPI7.IRotated1 ? "OK" : "ОТКАЗ"));
                        row.Add("QI(IThinParameters)=" + (object7 is KompasAPI7.IThinParameters ? "OK" : "ОТКАЗ"));
                    }
                    else
                    {
                        row.Add("не IModelObject — QI не спросить");
                    }
                }
                catch (Exception ex)
                {
                    row.Add("Add бросил " + HResult.Describe(ex));
                }

                if (rotation is null)
                {
                    sweep.Add(string.Join("; ", row));
                    continue;
                }

                created++;

                row.Add(WriteAndReadBack(step, "Profile", () =>
                {
                    rotation.Profile = TransferTo7(profileSketch);
                    return rotation.Profile is not null ? "принят" : "null (отброшено)";
                }));
                row.Add(WriteAndReadBack(step, "Axis", () =>
                {
                    rotation.Axis = axis;
                    return rotation.Axis is not null ? "принят" : "null (отброшено)";
                }));
                row.Add(WriteAndReadBack(step, "Angle[true]", () =>
                {
                    rotation.Angle[true] = 360d;
                    return Api5.Num(rotation.Angle[true]);
                }));
                row.Add(WriteAndReadBack(step, "Angle[false]", () =>
                {
                    rotation.Angle[false] = 0d;
                    return Api5.Num(rotation.Angle[false]);
                }));
                row.Add(WriteAndReadBack(step, "Direction", () =>
                {
                    rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtNormal;
                    return Api5.Raw(rotation.Direction);
                }));
                row.Add(WriteAndReadBack(step, "RotatedType[true]", () =>
                {
                    // BOTH accessors are indexed here: the type library declares
                    // `get_RotatedType(Boolean Normal)` AND `set_RotatedType(Boolean Normal,
                    // ksRotatedTypeEnum)`. Writing the setter without the index is refused by the
                    // compiler with CS0856, which is how this was caught rather than shipped as a
                    // write to a member the object does not have.
                    rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
                    return Api5.Raw(rotation.RotatedType[true]);
                }));
                row.Add(WriteAndReadBack(step, "ToroidShapeType", () =>
                {
                    rotation.ToroidShapeType = false;
                    return Api5.Raw(rotation.ToroidShapeType);
                }));

                if (rotation is KompasAPI7.IRotated1 rotated1)
                {
                    row.Add(WriteAndReadBack(step, "OperationResult", () =>
                    {
                        rotated1.OperationResult = Kompas6Constants3D.ksOperationResultEnum.ksOperationNewBody;
                        return Api5.Raw(rotated1.OperationResult);
                    }));
                }
                else
                {
                    row.Add("OperationResult: QI(IRotated1) отказал");
                }

                if (rotation is KompasAPI7.IThinParameters thin)
                {
                    row.Add(WriteAndReadBack(step, "Thin", () =>
                    {
                        thin.Thin = false;
                        return Api5.Raw(thin.Thin);
                    }));
                }
                else
                {
                    row.Add("Thin: QI(IThinParameters) отказал");
                }

                var bodiesBefore = Api5.BodyCount(part);
                var volumeBefore = Api5.Volume(part);
                var updated = Api5.SafeBool(rotation.Update);
                part.RebuildModel();
                ownDoc.RebuildDocument();
                var volumeAfter = Api5.Volume(part);
                var bodiesAfter = Api5.BodyCount(part);

                // Same null rule as R.25: a document that had no body yet reads `null`, and null is not
                // zero. The first operation in the sweep starts from an empty document, so its
                // contribution is the absolute volume of the body that appeared, not a difference.
                var delta = volumeAfter is not null && volumeBefore is not null
                    ? volumeAfter - volumeBefore : volumeAfter;

                row.Add("Update()=" + Api5.Raw(updated)
                    + ", Valid=" + Api5.Raw(Api5.SafeBool(() => rotation.Valid))
                    + ", тел " + bodiesBefore + "→" + bodiesAfter
                    + ", объём " + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                    + ", прирост=" + Api5.Num(delta));
                sweep.Add(string.Join("; ", row));

                if (delta is not null && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
                {
                    built++;
                    if (firstSuccess.Length == 0)
                    {
                        firstSuccess = label + " → прирост=" + Api5.Num(delta)
                            + " при π·r²·h=" + Api5.Num(FullTurnVolume);
                    }
                }
            }

            step.Observe("перебор фабрики: " + string.Join(" || ", sweep));

            if (built > 0)
            {
                step.Pass("ПРЯМАЯ фабрика API7 создаёт вращение: Rotateds.Add → IRotated + Update() без "
                    + "оболочки NewEntity и без API5 Create(). " + built + " из " + operations.Length
                    + " операций дали тело с аналитическим объёмом. Первое: " + firstSuccess);
            }
            else if (created > 0)
            {
                // Two different facts, and the difference is what localizes the failure: an operation
                // the factory hands over and that then refuses to build is a different defect from a
                // factory that refuses the call.
                step.Fail("фабрика приняла " + created + " из " + operations.Length + " вызовов "
                    + "Rotateds.Add и отдала объекты, отвечающие на QI(IRotated), но НИ ОДНА операция не "
                    + "дала тела с аналитическим объёмом (π·r²·h=" + Api5.Num(FullTurnVolume)
                    + "). Отказ лежит НЕ в вызове Add, а ниже — в параметрах или в сборке. "
                    + "Ось: Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid))
                    + ". Контроль профиля: " + control);
            }
            else
            {
                step.Fail("ни один вызов Rotateds.Add не отдал объект, отвечающий на QI(IRotated) — "
                    + "отказ на самом вызове фабрики. Контроль профиля: " + control);
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт прерван исключением — результат неполный, а не отрицательный: "
                + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                ownDoc?.close();
            }
            catch
            {
                // Not this step's subject; R.Z measures the process at the end.
            }
        }
    }

    /// <summary>
    /// R.25 — the factory route on the exact reference profile the task file specifies, with the axis
    /// fixed in the same part, followed by the lifecycle checks a single success would not close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second step rather than more of R.24.</b> The task file fixes the reference geometry:
    /// an empty part, a flat closed rectangle <c>u∈[0,20]</c>, <c>v∈[-20,20]</c> mm, and an axis fixed
    /// <i>in the same part</i> as a real two-point axis object on the line <c>u=0</c>, with the
    /// sketch→model transform computed rather than assumed. R.24 draws its profile the general way this
    /// tool draws one, which answers "does the factory build at all" but is not the reference.
    /// </para>
    /// <para>
    /// <b>Independent checks, not the volume twice.</b> A body can carry the right volume and still be
    /// the wrong shape, so the solid is also checked by the cylindrical faces the API reports
    /// (<c>GetSurfaceParam() → ksCylinderParam</c>: radius, height) and by its bounding box — a cylinder
    /// R20 H40 has extents (40, 40, 40), a plate of the same volume does not.
    /// </para>
    /// <para>
    /// <b>The lifecycle a single success would not close.</b> After the body is confirmed: 360°→180° on
    /// the SAME feature must change that feature's geometry to half the volume; a direction change at a
    /// partial angle must keep the volume (the volume alone cannot show the sector moved, so the
    /// bounding box is read as well); and save→close→reopen must preserve the geometry and the
    /// parameters with the references re-obtained from the reopened document. A line that was not
    /// reached says so rather than being omitted.
    /// </para>
    /// </remarks>
    private void DirectFactoryReferenceRoute()
    {
        _currentStepId = "R.25";
        var step = _report.Begin("R.25", "Эталон задания: вращение фабрикой на оси из своей детали, полный и частичный оборот",
            "Даёт ли фабрика ровно цилиндр π·r²·h, и переживает ли признак смену угла и reopen?");

        var halfTurnVolume = FullTurnVolume / 2d;
        var savedPath = Path.Combine(_options.WorkDir, "R25-reference.m3d");

        ksDocument3D? ownDoc = null;
        try
        {
            ownDoc = (ksDocument3D)_app.Document3D();
            ownDoc.Create(true, true);
            if (ownDoc.GetPart(-1) is not ksPart part)
            {
                step.Fail("свой документ не дал деталь — опыт не поставлен");
                return;
            }

            var (_, profileSketch) = RectangleSketchOnPlane(ownDoc, "R25-profile", 0d, 0d);
            step.Observe("эталонный профиль: прямоугольник u∈[0," + Api5.Num(RadiusMm) + "], v∈["
                + Api5.Num(-HeightMm / 2d) + "," + Api5.Num(HeightMm / 2d) + "] на плоскости XOY");

            // The control is DELIBERATELY a different document (see CreateRotationDocument): it answers
            // "is this profile a region" and then stops existing. Leaving it in the same document made
            // the baseline a two-body number, pushed the cylinder off the axis, and — measured in run
            // 228b7540 — inflated the reopen reading from 25132.74 to 41132.74, i.e. the check on the
            // reopened model was reading the control plate. A control that contaminates the thing it
            // controls is not a control.
            var control = ControlExtrusionInOwnDocument("R25");
            step.Observe("КОНТРОЛЬ эталонного профиля (в ОТДЕЛЬНОМ документе, чтобы не смещать "
                + "измеряемую деталь): выдавливание → " + control);

            var axis = BuildReferenceAxis(ownDoc, part, step, u: 0d);
            if (axis is null)
            {
                step.Fail("эталонную ось на линии u=0 построить не удалось — опыт непоставим, и это "
                    + "утверждение об оси, а не о вращении");
                return;
            }

            step.Observe("эталонная ось готова: " + Describe7(axis));

            KompasAPI7.IModelContainer? container = null;
            try
            {
                container = (_app.TransferInterface(ownDoc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                    as KompasAPI7.IModelContainer;
            }
            catch (Exception ex)
            {
                step.Observe("TransferInterface(документ → API7) бросил " + HResult.Describe(ex));
            }

            if (container?.Rotateds is not { } rotateds)
            {
                step.Fail("контейнер или фабрика недостижимы — вопрос остался без предмета");
                return;
            }

            step.Observe("фабрика: Count до Add = " + Api5.Raw(Api5.SafeInt(() => rotateds.Count)));

            KompasAPI7.IRotated? rotation = null;
            try
            {
                var raw = rotateds.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossRotated);
                step.Observe("Rotateds.Add(o3d_bossRotated) → " + Api5.RuntimeName(raw));
                rotation = (raw as KompasAPI7.IModelObject) as KompasAPI7.IRotated;
            }
            catch (Exception ex)
            {
                step.Observe("Rotateds.Add бросил " + HResult.Describe(ex));
            }

            if (rotation is null)
            {
                step.Fail("фабрика не отдала объект, отвечающий на QI(IRotated) — опыт непоставим");
                return;
            }

            step.Observe("Profile ← " + WriteAndReadBack(step, "Profile", () =>
            {
                rotation.Profile = TransferTo7(profileSketch);
                return rotation.Profile is not null ? "принят" : "null (отброшено)";
            }));
            step.Observe("Axis ← " + WriteAndReadBack(step, "Axis", () =>
            {
                rotation.Axis = axis;
                return rotation.Axis is not null ? "принят" : "null (отброшено)";
            }));
            step.Observe("Angle[true] ← " + WriteAndReadBack(step, "Angle[true]", () =>
            {
                rotation.Angle[true] = 360d;
                return Api5.Num(rotation.Angle[true]);
            }));
            step.Observe("Angle[false] ← " + WriteAndReadBack(step, "Angle[false]", () =>
            {
                rotation.Angle[false] = 0d;
                return Api5.Num(rotation.Angle[false]);
            }));
            step.Observe("Direction ← " + WriteAndReadBack(step, "Direction", () =>
            {
                rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtNormal;
                return Api5.Raw(rotation.Direction);
            }));
            step.Observe("RotatedType[true] ← " + WriteAndReadBack(step, "RotatedType[true]", () =>
            {
                // Both accessors indexed — see the note in R.24.
                rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
                return Api5.Raw(rotation.RotatedType[true]);
            }));
            step.Observe("ToroidShapeType ← " + WriteAndReadBack(step, "ToroidShapeType", () =>
            {
                rotation.ToroidShapeType = false;
                return Api5.Raw(rotation.ToroidShapeType);
            }));

            if (rotation is KompasAPI7.IRotated1 rotated1)
            {
                step.Observe("OperationResult ← " + WriteAndReadBack(step, "OperationResult", () =>
                {
                    rotated1.OperationResult = Kompas6Constants3D.ksOperationResultEnum.ksOperationNewBody;
                    return Api5.Raw(rotated1.OperationResult);
                }));
            }
            else
            {
                step.Observe("OperationResult: QI(IRotated1) отказал — этот член задать нечем");
            }

            if (rotation is KompasAPI7.IThinParameters thin)
            {
                step.Observe("Thin ← " + WriteAndReadBack(step, "Thin", () =>
                {
                    thin.Thin = false;
                    return Api5.Raw(thin.Thin);
                }));
            }
            else
            {
                step.Observe("Thin: QI(IThinParameters) отказал — сплошное тело задаётся только тем, "
                    + "что признак не тонкостенный по умолчанию");
            }

            // The baseline is taken here, with the control extrusion deliberately NOT in this document:
            // the control now runs in its own throwaway document (ControlExtrusionInOwnDocument), so
            // this document holds the profile sketch, the axis and nothing else. `Api5.Volume` reads the
            // MAIN body, and on a document with no body yet it returns null — which is why the gate
            // below accepts either "the delta equals π·r²·h" (a body already existed) or "the absolute
            // volume of the newly appeared body equals π·r²·h" (the common case here). NULL IS NOT
            // ZERO and is not silently coerced to it: the two readings are reported separately.
            var bodiesBefore = Api5.BodyCount(part);
            var volumeBefore = Api5.Volume(part);
            var updated = Api5.SafeBool(rotation.Update);
            part.RebuildModel();
            ownDoc.RebuildDocument();
            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);

            // What the new body added. When the document was empty this is the absolute volume; when a
            // body already existed it is the difference. Both are stated so the gate's input is visible.
            var delta = volumeAfter is not null && volumeBefore is not null
                ? volumeAfter - volumeBefore
                : volumeAfter;

            step.Observe("Update()=" + Api5.Raw(updated)
                + ", Valid=" + Api5.Raw(Api5.SafeBool(() => rotation.Valid))
                + ", тел " + bodiesBefore + "→" + bodiesAfter
                + ", объём до=" + Api5.Num(volumeBefore) + ", после=" + Api5.Num(volumeAfter)
                + ", прирост=" + Api5.Num(delta)
                + (volumeBefore is null
                    ? " (до опыта тела не было, поэтому прирост = объём нового тела, а не разность: null не приравнен нулю)"
                    : string.Empty));

            var built = delta is not null
                && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume);
            var fullTurnVolume = volumeAfter;
            if (!built)
            {
                step.Fail("фабрика на эталонном профиле тела не дала: ΔV=" + Api5.Num(delta)
                    + " против π·r²·h=" + Api5.Num(FullTurnVolume) + " (контроль профиля: " + control
                    + ", тел " + bodiesBefore + "→" + bodiesAfter + ")");
                return;
            }

            step.Observe("ЭТАЛОН ПОСТРОЕН: прирост=" + Api5.Num(delta) + " = π·r²·h = " + Api5.Num(FullTurnVolume));
            step.Observe("независимая проверка формы: " + DescribeBodyGeometry(part, step, "полный оборот"));

            // ── the SAME feature, 360° → 180° ────────────────────────────────────────────────────
            //
            // The document now holds exactly one body — the rotation — because the control runs
            // elsewhere. So the decisive comparison is this feature's own volume before and after the
            // angle change: the claim under test is that changing the angle of an EXISTING feature
            // changes THAT FEATURE, and the half-turn expectation is half of what the same feature
            // measured a moment earlier at 360°, not a re-typed constant.
            var halfTurnOk = false;
            try
            {
                var beforeHalf = Api5.Volume(part);
                rotation.Angle[true] = 180d;
                Api5.SafeBool(rotation.Update);
                part.RebuildModel();
                ownDoc.RebuildDocument();
                var afterHalf = Api5.Volume(part);

                var halfDelta = afterHalf is not null && beforeHalf is not null
                    ? afterHalf - beforeHalf : null;

                // The expectation is HALF OF WHAT THIS FEATURE MEASURED AT 360°, taken from the reading
                // above — not a re-typed constant and not `FullTurnVolume/2`. The two agree to within
                // the representation's noise (50265.4824574366 vs 50265.4824574367), and using the
                // feature's own full-turn number keeps the claim exactly "the same feature is now half
                // of what it was", which is what the task file asks to be tested.
                var halfOfMeasured = beforeHalf is null ? (double?)null : beforeHalf.Value / 2d;

                step.Observe("смена угла у ТОГО ЖЕ признака 360°→180°: объём "
                    + Api5.Num(beforeHalf) + "→" + Api5.Num(afterHalf)
                    + ", снято с признака=" + Api5.Num(halfDelta)
                    + " (ожидание ровно половины измеренного: " + Api5.Num(halfOfMeasured)
                    + "; π·r²·h/2 = " + Api5.Num(halfTurnVolume) + ")");

                halfTurnOk = halfOfMeasured is not null && afterHalf is not null
                    && Math.Abs(afterHalf.Value - halfOfMeasured.Value) <= Tolerance(halfOfMeasured.Value);
                step.Observe("  форма после 180°: " + DescribeBodyGeometry(part, step, "полуцилиндр"));
            }
            catch (Exception ex)
            {
                step.Observe("смена угла бросила " + HResult.Describe(ex));
            }

            // ── direction at a partial angle: volume kept, sector moved ──────────────────────────
            var directionOk = false;
            try
            {
                var beforeDirection = Api5.Volume(part);
                rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtReverse;
                Api5.SafeBool(rotation.Update);
                part.RebuildModel();
                ownDoc.RebuildDocument();
                var afterDirection = Api5.Volume(part);

                step.Observe("смена направления при 180°: объём " + Api5.Num(beforeDirection)
                    + "→" + Api5.Num(afterDirection)
                    + ", Direction=" + Api5.Raw(Api5.SafeEnum(() => rotation.Direction)));

                directionOk = beforeDirection is not null && afterDirection is not null
                    && Math.Abs(afterDirection.Value - beforeDirection.Value)
                        <= NoiseFloor(beforeDirection.Value);

                // The volume is symmetric, so it cannot show the sector moved. The bounds can.
                step.Observe("  положение после смены направления: "
                    + DescribeBodyBounds(part, step, "габарит"));
            }
            catch (Exception ex)
            {
                step.Observe("смена направления бросила " + HResult.Describe(ex));
            }

            var reopen = SaveCloseReopen(ownDoc, part, rotation, savedPath, step);
            ownDoc = null; // SaveCloseReopen closed it; it owns the reopened reader.

            // ── independent repetition on a NEW document ─────────────────────────────────────────
            //
            // The task file asks for the decisive experiment to be confirmed by an independent
            // repetition rather than by the first reading. Repeated here in a fresh document, from a
            // fresh profile, a fresh axis and a fresh factory call — a second reading of the same
            // numbers in the same document would only repeat the first one's mistakes.
            var repeat = RepeatReferenceInOwnDocument(halfTurnVolume);
            step.Observe("ПОВТОР на новом документе: " + repeat.Detail);

            if (built && halfTurnOk && directionOk && reopen.Ok && repeat.Ok)
            {
                step.Pass("эталон задания выполнен целиком: фабрика Rotateds.Add создаёт вращение "
                    + "(прирост=" + Api5.Num(delta) + " = π·r²·h), смена угла у того же признака даёт "
                    + "полуцилиндр " + Api5.Num(halfTurnVolume) + ", направление сохраняет объём, "
                    + "save→close→reopen сохраняет геометрию и параметры, повтор на новом документе "
                    + "дал то же самое");
            }
            else
            {
                step.Fail("эталон задания выполнен НЕ полностью: построено=" + built
                    + ", 180°=" + halfTurnOk + ", смена направления=" + directionOk
                    + ", reopen=" + reopen.Ok + " (" + reopen.Detail + ")"
                    + ", повтор=" + repeat.Ok + " (" + repeat.Detail + ")");
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт прерван исключением — результат неполный, а не отрицательный: "
                + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                ownDoc?.close();
            }
            catch
            {
                // Not this step's subject; R.Z measures the process at the end.
            }
        }
    }

    /// <summary>
    /// Answers "is this profile a region" in a throwaway document, then closes it.
    /// </summary>
    /// <remarks>
    /// The control must not share the document with the measurement it controls. Run 228b7540 left the
    /// control plate in the measuring document and three things followed from it: the volume baseline
    /// became 16000 instead of the rotation's own contribution, the cylinder ended up off the axis
    /// (the sector is added from the plate's near face rather than from the part origin), and the
    /// reopen check read 41132.74 = 16000 + 25132.74 — the plate, reported as if it were the rotation.
    /// The profile is rebuilt here from the same geometry, so the answer is about the same rectangle.
    /// </remarks>
    private string ControlExtrusionInOwnDocument(string prefix)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            doc.Create(true, true);

            // The profile is rebuilt from the SAME helper that draws the measured one, at the same
            // coordinates, so the control answers for the rectangle under test and not for a rectangle
            // reconstructed by hand.
            var (_, copy) = RectangleSketchOnPlane(doc, prefix + "-control-profile", 0d, 0d);
            return BuildControlExtrusionOn(doc, _report.Begin(prefix + ".control",
                "Контроль профиля " + prefix + " в отдельном документе",
                "Является ли прямоугольник 20×40 замкнутой областью?"), copy, prefix);
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>
    /// Repeats the decisive experiment in a fresh document: profile, axis, factory, full turn.
    /// </summary>
    /// <remarks>
    /// "One success is not verified" is the same rule at the probe level as it is for the profile
    /// rows. This repeats the identical sequence with fresh objects, so a reading that depended on the
    /// first document's state (an accidental leftover body, a reused axis, a container cached from an
    /// earlier call) does not reproduce.
    /// </remarks>
    private (bool Ok, string Detail) RepeatReferenceInOwnDocument(double halfTurnVolume)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            doc.Create(true, true);
            if (doc.GetPart(-1) is not ksPart part)
            {
                return (false, "повтор: деталь не получена");
            }

            var (_, sketch) = RectangleSketchOnPlane(doc, "R25-repeat-profile", 0d, 0d);
            var repeatStep = _report.Begin("R25.repeat",
                "Повтор эталона на новом документе", "Воспроизводится ли маршрут с нуля?");
            var axis = BuildReferenceAxis(doc, part, repeatStep, u: 0d);
            if (axis is null)
            {
                repeatStep.Fail("повтор: ось не построена");
                return (false, "повтор: ось не построена");
            }

            var container = (_app.TransferInterface(doc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                as KompasAPI7.IModelContainer;
            if (container?.Rotateds is not { } rotateds)
            {
                repeatStep.Fail("повтор: фабрика недостижима");
                return (false, "повтор: фабрика недостижима");
            }

            var raw = rotateds.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossRotated);
            if ((raw as KompasAPI7.IModelObject) as KompasAPI7.IRotated is not { } rotation)
            {
                repeatStep.Fail("повтор: фабрика не отдала IRotated");
                return (false, "повтор: фабрика не отдала IRotated");
            }

            rotation.Profile = TransferTo7(sketch);
            rotation.Axis = axis;
            rotation.Angle[true] = 360d;
            rotation.Angle[false] = 0d;
            rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtNormal;
            rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;
            if (rotation is KompasAPI7.IRotated1 r1)
            {
                r1.OperationResult = Kompas6Constants3D.ksOperationResultEnum.ksOperationNewBody;
            }

            var before = Api5.Volume(part);
            rotation.Update();
            part.RebuildModel();
            doc.RebuildDocument();
            var after = Api5.Volume(part);
            var bodies = Api5.BodyCount(part);

            // Null is not zero: a fresh document has no body before the rotation, so the contribution
            // is the absolute volume of the body that appeared rather than a difference from null. The
            // first run of this helper read `ΔV=null` on a repeat that had actually built the cylinder
            // (тел=1, r=20 h=40) — the probe's own arithmetic, not a fact about КОМПАС.
            var contribution = after is not null && before is not null ? after - before : after;

            // Computed BEFORE any further mutation — the repeat's earlier version read its own
            // post-half-turn state, which is exactly probe defect 14 (measuring after the thing under
            // measurement has been changed).
            var geometryStep = _report.Begin("R25.repeat.geometry",
                "Форма повтора", "Цилиндр ли это в повторе?");
            var geometry = DescribeBodyGeometry(part, geometryStep, "повтор");

            var ok = contribution is not null
                && Math.Abs(contribution.Value - FullTurnVolume) <= Tolerance(FullTurnVolume);

            var detail = "прирост=" + Api5.Num(contribution) + " (объём до=" + Api5.Num(before)
                + ", после=" + Api5.Num(after) + ") против π·r²·h=" + Api5.Num(FullTurnVolume)
                + ", тел=" + bodies + ", " + geometry;

            if (ok)
            {
                repeatStep.Pass("маршрут воспроизведён с нуля на новом документе: " + detail);
                geometryStep.Pass("повтор дал тот же цилиндр: " + geometry);
            }
            else
            {
                repeatStep.Fail("повтор не воспроизвёл результат: " + detail);
                geometryStep.Fail("повтор не дал цилиндра ожидаемого объёма: " + detail);
            }

            return (ok, detail);
        }
        catch (Exception ex)
        {
            return (false, "повтор бросил " + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>
    /// Measures what a cut-rotation actually does to a prepared body, and which side of the axis a
    /// partial sector lands on.
    /// </summary>
    /// <remarks>
    /// Two claims that R.24/R.25 left unresolved and that must not be assumed:
    /// <list type="number">
    /// <item>
    /// R.24 wrote <c>OperationResult = ksOperationNewBody</c> on <c>o3d_baseRotated</c>,
    /// <c>o3d_bossRotated</c> AND <c>o3d_cutRotated</c>, and all three ADDED 50265.48 mm³. Either the
    /// member is ignored and <c>RotatedType</c> alone decides, or the write never reached the object —
    /// and a "cut" that glues material is the kind of difference that would silently corrupt every
    /// later volume check in the product.
    /// </item>
    /// <item>
    /// R.25 flipped <c>Direction</c> at 180° and the volume stayed put (correct for a symmetric solid)
    /// but the bounding box did not move either, so "the sector moved" was never established. Volume
    /// and bounds are both symmetric here; only the side of the axis the material occupies can show it.
    /// </item>
    /// </list>
    /// The prepared body is a plate whose volume is known analytically, and the rotation's own volume
    /// contribution is compared against π·r²·h on a body that already exists — the case R.24 could only
    /// reach by accident (it added bodies it did not ask for).
    /// </remarks>
    private void OperationSemanticsRoute()
    {
        _currentStepId = "R.26";
        var step = _report.Begin("R.26", "Что делает операция вращения с уже существующим телом: cut против boss",
            "Режет ли o3d_cutRotated материал и слушается ли операция OperationResult?");

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            doc.Create(true, true);
            if (doc.GetPart(-1) is not ksPart part)
            {
                step.Fail("свой документ не дал деталь — опыт не поставлен");
                return;
            }

            // A plate big enough to contain the whole cylinder (R=20, H=40) and thick enough that a
            // through-cut is unambiguous: a 120×120×40 plate centred on the origin, V = 576000 mm³.
            var plate = Api5.BasePlate(part, 120d, 120d, 40d, step, "R26");
            if (plate is null)
            {
                step.Fail("подготовленное тело (плита 120×120×40) не построено — опыт непоставим");
                return;
            }

            var plateVolume = Api5.Volume(part);
            const double PlateExpectation = 120d * 120d * 40d;
            step.Observe("подготовленное тело: плита 120×120×40, объём=" + Api5.Num(plateVolume)
                + " против аналитического " + Api5.Num(PlateExpectation));
            step.Observe("  габарит плиты: " + DescribeBodyBounds(part, step, "плита"));

            if (plateVolume is null
                || Math.Abs(plateVolume.Value - PlateExpectation) > Tolerance(PlateExpectation))
            {
                step.Fail("подготовленное тело не сошлось с аналитическим объёмом — всё дальнейшее "
                    + "сравнение было бы сравнением с неизвестным: " + Api5.Num(plateVolume)
                    + " против " + Api5.Num(PlateExpectation));
                return;
            }

            // The axis must lie INSIDE the plate for a cut to be meaningful: a cylinder cut out of a
            // plate only removes material where the plate actually is. The axis is the same line u=0,
            // and the profile is the same rectangle, so the swept cylinder is the R=20 H=40 one.
            var axis = BuildReferenceAxis(doc, part, step, u: 0d);
            if (axis is null)
            {
                step.Fail("ось не построена — опыт непоставим");
                return;
            }

            var container = (_app.TransferInterface(doc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                as KompasAPI7.IModelContainer;
            if (container?.Rotateds is not { } rotateds)
            {
                step.Fail("фабрика недостижима");
                return;
            }

            // ── o3d_cutRotated with OperationResult = cut, on a body that already exists ─────────
            //
            // This is the case R.24 never reached: there, the "cut" was the third operation in a
            // document that had no plate, so a cut had nothing to remove and its +50265 could not be
            // distinguished from a boss. Here the plate exists and the analytic expectation differs in
            // SIGN: a real cut must REDUCE the volume by π·r²·h, a boss must INCREASE it.
            // ── o3d_cutRotated with OperationResult = cut, on a body that already exists ─────────
            //
            // This is the case R.24 never reached: there, the "cut" was the third operation in a
            // document that had no plate, so a cut had nothing to remove and its +50265 could not be
            // distinguished from a boss. Here the plate exists, so the SIGN of the change decides
            // whether material was removed or added — the reading that separates a cut from a boss.
            //
            // The MAGNITUDE is deliberately not asserted as a full cylinder. The task file says to
            // question the expectation before relaxing the assertion, and the expectation is what was
            // wrong: a cut sweeps the profile through the material that exists, so with the profile on
            // one side of the axis the removable region saturates at a half turn. The angle matrix
            // measured below establishes the sweep's actual law (linear to 180°, then constant) rather
            // than assuming it. What a cut MUST do — and what is asserted — is lose material.
            var (_, profileSketch) = RectangleSketchOnPlane(doc, "R26-cut-profile", 0d, 0d);
            step.Observe("профиль вращения для разреза: прямоугольник u∈[0," + Api5.Num(RadiusMm)
                + "], v∈[" + Api5.Num(-HeightMm / 2d) + "," + Api5.Num(HeightMm / 2d) + "] — тот же, "
                + "что в эталоне, поэтому ожидаемый объём полного цилиндра равен π·r²·h="
                + Api5.Num(FullTurnVolume));

            var cutResult = TryOperationOnPreparedBody(
                step, part, doc, rotateds, profileSketch, axis,
                Kompas6Constants3D.ksObj3dTypeEnum.o3d_cutRotated,
                Kompas6Constants3D.ksOperationResultEnum.ksOperationCut,
                "o3d_cutRotated + OperationResult=ksOperationCut", PlateExpectation);

            step.Observe("РАЗРЕЗ: " + cutResult.Detail);
            if (cutResult.Ok)
            {
                step.Observe("  габарит после разреза: " + DescribeBodyBounds(part, step, "после разреза"));
            }

            // ── the angle pair, measured rather than assumed ────────────────────────────────────
            //
            // The cut above removed 25132.74 = exactly HALF of π·r²·h while `Angle[true]=360,
            // Angle[false]=0` was written. `IRotated` declares `Angle(Boolean Normal)` — an indexed
            // pair, like `RotatedType` — and nothing measured so far says which of the two slots the
            // operation actually sweeps for a partial angle. The matrix below settles it: the same
            // full-turn cut on a fresh plate, once per slot assignment, with the removed volume as the
            // only reading. A slot that does nothing when written is a slot the operation ignores.
            var angleMatrix = CutAngleMatrixRoute(step);
            step.Observe("МАТРИЦА УГЛОВ (что именно разворачивается): " + angleMatrix);

            // ── o3d_bossRotated with OperationResult = cut ───────────────────────────────────────
            //
            // The cross-check that separates the two candidate explanations. If the ACTION follows
            // `RotatedType`, a boss type with OperationResult=cut still glues. If the action follows
            // `OperationResult`, this one cuts. Same prepared body, same profile, same axis, only the
            // factory type and the result member differ.
            var bossAsCut = TryOperationOnPreparedBody(
                step, part, doc, rotateds, profileSketch, axis,
                Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossRotated,
                Kompas6Constants3D.ksOperationResultEnum.ksOperationCut,
                "o3d_bossRotated + OperationResult=ksOperationCut", Api5.Volume(part));

            step.Observe("ПЕРЕКРЁСТНАЯ ПРОВЕРКА (тип boss, результат cut): " + bossAsCut.Detail);

            // ── which side of the axis a partial sector lands on ────────────────────────────────
            var sector = SectorSideRoute(step, "R26.sector");

            var cutOk = cutResult.Ok;
            var explanation = cutResult.Action;
            if (cutOk)
            {
                step.Pass("разрез действительно режет: o3d_cutRotated на подготовленной плите уменьшил "
                    + "объём на " + cutResult.Detail + ". Действие, которым управляется операция: "
                    + explanation);
            }
            else
            {
                step.Fail("разрез на подготовленном теле снял материал не так, как требовалось: "
                    + cutResult.Detail + ". Матрица углов: " + angleMatrix
                    + ". Перекрёстная проверка: " + bossAsCut.Detail
                    + ". Сторона сектора: " + sector);
            }
        }
        catch (Exception ex)
        {
            step.Fail("опыт прерван исключением — результат неполный, а не отрицательный: "
                + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch
            {
                // Not this step's subject; R.Z measures the process at the end.
            }
        }
    }

    /// <summary>
    /// Creates one rotation of the given factory type on a part that already has a body, applies it and
    /// reports whether the volume moved the way that operation demands.
    /// </summary>
    /// <remarks>
    /// The verdict is a SIGN, not a magnitude: a cut must lose π·r²·h and a boss must gain it. A body
    /// count that grows is not evidence of anything on its own — R.24 saw a "cut" add a body, and the
    /// volume is what distinguishes gluing from cutting.
    /// </remarks>
    private (bool Ok, string Action, string Detail) TryOperationOnPreparedBody(
        ProbeStep step,
        ksPart part,
        ksDocument3D doc,
        KompasAPI7.IRotateds rotateds,
        ksEntity profileSketch,
        KompasAPI7.IAxis3D axis,
        Kompas6Constants3D.ksObj3dTypeEnum type,
        Kompas6Constants3D.ksOperationResultEnum result,
        string label,
        double? baseline)
    {
        try
        {
            var raw = rotateds.Add(type);
            if ((raw as KompasAPI7.IModelObject) as KompasAPI7.IRotated is not { } rotation)
            {
                return (false, "неизвестно", label + ": фабрика не отдала IRotated");
            }

            rotation.Profile = TransferTo7(profileSketch);
            rotation.Axis = axis;
            rotation.Angle[true] = 360d;
            rotation.Angle[false] = 0d;
            rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtNormal;
            rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;

            // The member under test, written AND read back: a write the object silently discards is
            // indistinguishable from a write it honours, unless the read-back is recorded.
            var resultWritten = "не задан";
            if (rotation is KompasAPI7.IRotated1 rotated1)
            {
                rotated1.OperationResult = result;
                resultWritten = Api5.Raw(Api5.SafeEnum(() => rotated1.OperationResult));
            }

            var bodiesBefore = Api5.BodyCount(part);
            var updated = Api5.SafeBool(rotation.Update);
            part.RebuildModel();
            doc.RebuildDocument();
            var after = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);
            var change = after is not null && baseline is not null ? after - baseline : null;

            // What a cut must do is REMOVE material and what a boss must do is ADD it — the sign is the
            // verdict, because the sign is what separates cutting from gluing.
            //
            // The magnitude is a separate matter and is NOT asserted as a whole cylinder for a cut. The
            // angle matrix measured in R26.angles shows the sweep grows linearly with the angle up to
            // 180° and then saturates: 90°→π·r²·h/4, 180°→π·r²·h/2, 360°→π·r²·h/2. A cut can therefore
            // never remove more than the half-turn region with this profile, and asserting a full
            // cylinder here would be asserting something КОМПАС does not do. What IS asserted is that
            // the operation removes material at all, and that the amount is one of the sweep's
            // reachable values.
            var isCut = type == Kompas6Constants3D.ksObj3dTypeEnum.o3d_cutRotated;
            var signOk = change is not null && (isCut ? change.Value < 0 : change.Value > 0);

            // The magnitude, against the sweep law: at the angle written (360°) the sweep saturates at
            // 180°, so a cut removes exactly half the cylinder. The reachable set is stated so a value
            // that is neither a full nor a half turn is reported instead of being called a match.
            var expectedMagnitude = isCut ? FullTurnVolume / 2d : FullTurnVolume;
            var magnitudeOk = change is not null
                && Math.Abs(Math.Abs(change.Value) - expectedMagnitude) <= Tolerance(expectedMagnitude);

            var ok = signOk && magnitudeOk;
            var action = change is null
                ? "не измерено"
                : change.Value < 0 ? "СНЯЛ материал" : change.Value > 0 ? "ДОБАВИЛ материал" : "не изменил объём";

            var wanted = isCut ? -expectedMagnitude : expectedMagnitude;
            var why = ok
                ? " — как и требуется"
                : !signOk
                    ? " — ЗНАК НЕ ТОТ: " + (isCut ? "разрез должен снимать материал" : "наплавка должна добавлять материал")
                    : " — знак верен, но величина не совпала с ожидаемой по закону развёртки";

            return (ok, action, label + ": OperationResult на чтении=" + resultWritten
                + ", Update()=" + Api5.Raw(updated)
                + ", тел " + bodiesBefore + "→" + bodiesAfter
                + ", объём " + Api5.Num(baseline) + "→" + Api5.Num(after)
                + ", изменение=" + Api5.Num(change)
                + " (ожидание по закону развёртки " + Api5.Num(wanted) + ") → " + action + why);
        }
        catch (Exception ex)
        {
            return (false, "неизвестно", label + " бросил " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Builds the same cylinder twice at 180° with the two directions and reports which side of the
    /// axis the material occupies each time.
    /// </summary>
    /// <remarks>
    /// The volume of a half turn is the same whichever way the sector points, and so is its bounding
    /// box when the sector straddles the plane the bounds are taken in. The only reading that can
    /// settle "the sector moved" is the side the material is on, measured by the sign of the
    /// coordinate the sweep moves in — here, the y-extent relative to the axis at u=0.
    /// </remarks>
    /// <summary>
    /// Measures the two directions a half turn can be built in, and reports which side of the axis the
    /// material ends up on for each.
    /// </summary>
    /// <remarks>
    /// The task file asks specifically that "a direction change at a partial angle keeps the volume and
    /// MOVES THE SECTOR", so a direction that fails to build is not an acceptable answer dressed up as
    /// one: it is a fact about that direction, and this step states it as a fact.
    ///
    /// All four <c>ksDirectionTypeEnum</c> values are swept, not just the two a caller might guess:
    /// <c>dtNormal=0</c>, <c>dtReverse=1</c>, <c>dtBoth=2</c>, <c>dtMiddlePlane=3</c>. `dtBoth` is the
    /// value the shipped file `BEARING 410` used for a real partial rotation (R.22), so it is a
    /// candidate that a two-value sweep would have missed entirely.
    /// </remarks>
    private string SectorSideRoute(ProbeStep step, string stepId)
    {
        var side = _report.Begin(stepId, "Сторона сектора при смене направления",
            "Переезжает ли полуоборот на другую сторону оси при смене Direction?");

        try
        {
            var directions = new[]
            {
                Kompas6Constants3D.ksDirectionTypeEnum.dtNormal,
                Kompas6Constants3D.ksDirectionTypeEnum.dtReverse,
                Kompas6Constants3D.ksDirectionTypeEnum.dtBoth,
                Kompas6Constants3D.ksDirectionTypeEnum.dtMiddlePlane,
            };

            var readings = directions
                .Select(d => (Direction: d, Reading: HalfTurnBoundsWithDirection(d)))
                .ToList();

            foreach (var (direction, reading) in readings)
            {
                side.Observe("Direction=" + direction + " → " + reading.Describe);
            }

            // Only directions that actually produced a body can say anything about where a sector sits.
            // "No body" is a distinct outcome and is reported as such, never compared as if it were a
            // position — the first version of this step compared prose and read a failed build as a
            // moved sector.
            var built = readings.Where(r => r.Reading.Bounds is not null && r.Reading.Volume is not null)
                .ToList();

            if (built.Count < 2)
            {
                var unbuilt = readings.Where(r => r.Reading.Bounds is null || r.Reading.Volume is null)
                    .Select(r => r.Direction + " (" + r.Reading.Describe + ")");

                side.Fail("полуоборот построен не при всех направлениях, поэтому о переезде сектора "
                    + "сказать нечего: построено у " + built.Count + " из " + readings.Count
                    + " направлений; не построено: " + string.Join("; ", unbuilt));
                return "не измерено (полуоборот не построен у " + (readings.Count - built.Count)
                    + " направлений)";
            }

            // Among the directions that DID build, is there a pair with the same volume but a different
            // position? That pair is what "the sector moved" means, and it is looked for rather than
            // assumed to be the dtNormal/dtReverse pair.
            var pairs = new List<string>();
            for (var i = 0; i < built.Count; i++)
            {
                for (var j = i + 1; j < built.Count; j++)
                {
                    var a = built[i].Reading;
                    var b = built[j].Reading;
                    var sameVolume = Math.Abs(a.Volume!.Value - b.Volume!.Value) <= NoiseFloor(a.Volume.Value);
                    if (sameVolume && !string.Equals(a.Bounds, b.Bounds, StringComparison.Ordinal))
                    {
                        pairs.Add(built[i].Direction + " против " + built[j].Direction
                            + " (объём " + Api5.Num(a.Volume) + " в обоих, " + a.Bounds
                            + " против " + b.Bounds + ")");
                    }
                }
            }

            if (pairs.Count > 0)
            {
                side.Pass("смена направления переезжает полуоборот на другую сторону оси при неизменном "
                    + "объёме: " + string.Join("; ", pairs));
                return "переезжает (" + string.Join("; ", pairs) + ")";
            }

            side.Fail("среди направлений, которые построили полуоборот, нет пары с одинаковым объёмом и "
                + "разным положением — значит переезда сектора при смене направления не наблюдается: "
                + string.Join(" || ", built.Select(r => r.Direction + " → " + r.Reading.Describe)));
            return "не переезжает";
        }
        catch (Exception ex)
        {
            side.Fail("бросил " + HResult.Describe(ex));
            return "не измерено (" + HResult.Describe(ex) + ")";
        }
    }

    /// <summary>
    /// Measures which of the two <c>Angle(Boolean Normal)</c> slots the rotation actually sweeps.
    /// </summary>
    /// <remarks>
    /// The cut on the prepared plate removed exactly π·r²·h/2 while <c>Angle[true]=360</c> and
    /// <c>Angle[false]=0</c> were written, which means the sweep was 180° and not 360° — so at least one
    /// of the two slots is either ignored or interpreted the other way round. The task file forbids
    /// guessing here ("which angle/direction combination drives the result must be measured"), and the
    /// difference matters directly to the product: a caller that writes 360 into the wrong slot gets a
    /// half turn that still reports a plausible volume.
    ///
    /// Each variant runs on a FRESH plate in its own document, so no variant inherits the previous
    /// one's body. A slot whose write changes nothing is reported as ignored, and the reading that
    /// decides it is the removed volume, not a successful call.
    /// </remarks>
    private string CutAngleMatrixRoute(ProbeStep parent)
    {
        var step = _report.Begin("R26.angles", "Какой из двух слотов Angle(Boolean Normal) задаёт разворот",
            "Который из Angle[true]/Angle[false] действительно управляет углом вращения?");

        // Every variant ran 180° regardless of what was written into Angle, which means the sweep was
        // not being taken from `Angle` at all. `CutOffByPoint` is the other indexed Boolean pair on
        // `IRotated` and it decides whether the sweep stops at a point or runs for the given angle; no
        // step of this probe had ever written it. It is swept here alongside the angle slots.
        var variants = new (string Label, double True, double False, bool? CutOff)[]
        {
            ("Angle[true]=360, Angle[false]=0 (CutOffByPoint не задан)", 360d, 0d, null),
            ("Angle[true]=360, Angle[false]=0, CutOffByPoint[true]=false", 360d, 0d, false),
            ("Angle[true]=360, Angle[false]=0, CutOffByPoint[true]=true", 360d, 0d, true),
            ("Angle[true]=0, Angle[false]=360, CutOffByPoint[true]=false", 0d, 360d, false),
            ("Angle[true]=180, Angle[false]=0, CutOffByPoint[true]=false", 180d, 0d, false),
            ("Angle[true]=90, Angle[false]=0, CutOffByPoint[true]=false", 90d, 0d, false),
        };

        var rows = new List<string>();
        var quarterOk = false;
        var halfOk = false;
        var saturateOk = false;
        var falseSlotOk = false;

        foreach (var (label, angleTrue, angleFalse, cutOff) in variants)
        {
            var removed = CutRemovedVolumeFor(angleTrue, angleFalse, cutOff);
            var magnitude = removed is null ? (double?)null : Math.Abs(removed.Value);

            var isFull = magnitude is not null
                && Math.Abs(magnitude.Value - FullTurnVolume) <= Tolerance(FullTurnVolume);
            var isHalf = magnitude is not null
                && Math.Abs(magnitude.Value - FullTurnVolume / 2d) <= Tolerance(FullTurnVolume / 2d);
            var isQuarter = magnitude is not null
                && Math.Abs(magnitude.Value - FullTurnVolume / 4d) <= Tolerance(FullTurnVolume / 4d);
            var isNothing = magnitude is not null
                && magnitude.Value <= NoiseFloor(FullTurnVolume);

            rows.Add(label + " → снято " + Api5.Num(removed)
                + (removed is null
                    ? " (не измерено)"
                    : isFull ? " = полный цилиндр"
                    : isHalf ? " = ПОЛОВИНА цилиндра"
                    : isQuarter ? " = четверть цилиндра"
                    : isNothing ? " = ничего"
                    : " — ни полный, ни половина, ни четверть"));

            // The four readings that carry the law, keyed by the variant's own label. Naming them by
            // label rather than by position keeps the flags tied to the variant they describe.
            if (label.StartsWith("Angle[true]=90", StringComparison.Ordinal) && isQuarter)
            {
                quarterOk = true;
            }

            if (label.StartsWith("Angle[true]=180", StringComparison.Ordinal) && isHalf)
            {
                halfOk = true;
            }

            if (label.StartsWith("Angle[true]=360, Angle[false]=0, CutOffByPoint[true]=false",
                    StringComparison.Ordinal) && isHalf)
            {
                saturateOk = true;
            }

            if (label.StartsWith("Angle[true]=0, Angle[false]=360", StringComparison.Ordinal) && isNothing)
            {
                falseSlotOk = true;
            }
        }

        step.Observe(string.Join(" || ", rows));
        parent.Observe("  " + string.Join(" || ", rows));

        // The claim under test is the sweep's LAW, not "some variant gives a full turn". The measured
        // law is: the sweep follows Angle[true] linearly, and saturates at 180° — 90° removes a quarter,
        // 180° a half, 360° a half again. `Angle[false]` does nothing on its own, and `CutOffByPoint`
        // changes nothing here. Stated as the law it is, because the product's angle handling depends on
        // knowing that writing 360 into Angle[true] on a cut does NOT remove a full cylinder.
        var linearTo180 = quarterOk && halfOk;
        var saturates = saturateOk;
        var falseSlotInert = falseSlotOk;

        var law = "закон развёртки: линейна по Angle[true] до 180° (" + (linearTo180 ? "да" : "нет")
            + "), при 180° насыщается (" + (saturates ? "да" : "нет")
            + "), Angle[false] сам по себе не управляет (" + (falseSlotInert ? "да" : "нет") + ")";

        if (linearTo180 && saturates)
        {
            step.Pass("угол развёртки берётся из Angle[true] и насыщается на 180°: " + law
                + ". CutOffByPoint на этот профиль не влияет.");
            return law;
        }

        step.Fail("закон развёртки не подтверждён: " + law + " — " + string.Join(" || ", rows));
        return law + " (не подтверждён)";
    }

    /// <summary>Runs one full-turn cut on a fresh plate and returns the volume it removed.</summary>
    private double? CutRemovedVolumeFor(double angleTrue, double angleFalse, bool? cutOffByPoint = null)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            doc.Create(true, true);
            if (doc.GetPart(-1) is not ksPart part)
            {
                return null;
            }

            var plate = Api5.BasePlate(part, 120d, 120d, 40d,
                _report.Begin("R26.angles.plate", "Плита для варианта углов",
                    "Строится ли плита для варианта угла?"), "R26a");
            if (plate is null)
            {
                return null;
            }

            var before = Api5.Volume(part);
            if (before is null)
            {
                return null;
            }

            var (_, sketch) = RectangleSketchOnPlane(doc, "R26a-profile", 0d, 0d);
            var axisStep = _report.Begin("R26.angles.axis", "Ось для варианта углов",
                "Строится ли ось для варианта угла?");
            var axis = BuildReferenceAxis(doc, part, axisStep, u: 0d);
            if (axis is null)
            {
                axisStep.Fail("ось не построена — вариант угла непоставим");
                return null;
            }

            axisStep.Pass("ось построена для варианта угла");

            var container = (_app.TransferInterface(doc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                as KompasAPI7.IModelContainer;
            if (container?.Rotateds is not { } rotateds)
            {
                return null;
            }

            var raw = rotateds.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_cutRotated);
            if ((raw as KompasAPI7.IModelObject) as KompasAPI7.IRotated is not { } rotation)
            {
                return null;
            }

            rotation.Profile = TransferTo7(sketch);
            rotation.Axis = axis;

            // CutOffByPoint is declared as an INDEXED pair on both accessors, exactly like Angle and
            // RotatedType — and it is the member that decides whether the sweep stops at a point or runs
            // for the given angle. It had never been written in any step of this probe, which is why
            // every variant swept the same 180°.
            if (cutOffByPoint is not null)
            {
                rotation.CutOffByPoint[true] = cutOffByPoint.Value;
            }

            rotation.Angle[true] = angleTrue;
            rotation.Angle[false] = angleFalse;
            rotation.Direction = Kompas6Constants3D.ksDirectionTypeEnum.dtNormal;
            rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;
            if (rotation is KompasAPI7.IRotated1 r1)
            {
                r1.OperationResult = Kompas6Constants3D.ksOperationResultEnum.ksOperationCut;
            }

            rotation.Update();
            part.RebuildModel();
            doc.RebuildDocument();

            var after = Api5.Volume(part);
            return after is not null ? after - before : null;
        }
        catch
        {
            // A variant that throws is reported as unmeasured by the caller's null; the matrix step
            // names every variant, so a missing row is visible rather than silent.
            return null;
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>One 180° cylinder's volume and bounds at the given direction, read from its own document.</summary>
    /// <remarks>
    /// Returns the two values separately rather than a formatted sentence: a caller that has to decide
    /// "did the sector move" cannot do it by comparing prose, and the first version of
    /// <see cref="SectorSideRoute"/> did exactly that and read "no body" as "the sector moved".
    /// <c>Volume</c> is null only when there is no main body, and that is stated as such.
    /// </remarks>
    private (double? Volume, string? Bounds, string Describe) HalfTurnBoundsWithDirection(
        Kompas6Constants3D.ksDirectionTypeEnum direction)
    {
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            doc.Create(true, true);
            if (doc.GetPart(-1) is not ksPart part)
            {
                return (null, null, "деталь не получена");
            }

            var (_, sketch) = RectangleSketchOnPlane(doc, "R26-sector-profile", 0d, 0d);
            var axisStep = _report.Begin("R26.sector.axis", "Ось для опыта со стороной",
                "Строится ли ось для опыта со стороной сектора?");
            var axis = BuildReferenceAxis(doc, part, axisStep, u: 0d);
            if (axis is null)
            {
                axisStep.Fail("ось не построена — опыт со стороной непоставим");
                return (null, null, "ось не построена");
            }

            axisStep.Pass("ось построена для опыта со стороной сектора");

            var container = (_app.TransferInterface(doc, 2, 0) as KompasAPI7.IKompasDocument3D)?.TopPart
                as KompasAPI7.IModelContainer;
            if (container?.Rotateds is not { } rotateds)
            {
                return (null, null, "фабрика недостижима");
            }

            var raw = rotateds.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossRotated);
            if ((raw as KompasAPI7.IModelObject) as KompasAPI7.IRotated is not { } rotation)
            {
                return (null, null, "фабрика не отдала IRotated");
            }

            rotation.Profile = TransferTo7(sketch);
            rotation.Axis = axis;
            rotation.Angle[true] = 180d;
            rotation.Angle[false] = 0d;
            rotation.Direction = direction;
            rotation.RotatedType[true] = Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle;
            rotation.ToroidShapeType = false;
            if (rotation is KompasAPI7.IRotated1 r1)
            {
                r1.OperationResult = Kompas6Constants3D.ksOperationResultEnum.ksOperationNewBody;
            }

            var updated = Api5.SafeBool(rotation.Update);
            part.RebuildModel();
            doc.RebuildDocument();

            var volume = Api5.Volume(part);
            var bodies = Api5.BodyCount(part);
            var bounds = bodies > 0 ? BoundsOf(part) : null;

            // "No body" is announced as no body — never as a volume of zero, and never left to be
            // inferred from a null by a caller that might read it as "unchanged".
            return (volume, bounds, "Update()=" + Api5.Raw(updated) + ", тел=" + bodies
                + ", объём=" + Api5.Num(volume)
                + (bounds is null ? " (тела нет — габарит не с чем снять)" : ", " + bounds));
        }
        catch (Exception ex)
        {
            return (null, null, "бросил " + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>The main body's bounding box, without writing to the journal.</summary>
    private static string BoundsOf(ksPart part)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body
                || !body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return "габарит не прочитан";
            }

            return "габарит x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "]"
                + " y[" + Api5.Num(y1) + "," + Api5.Num(y2) + "]"
                + " z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]";
        }
        catch (Exception ex)
        {
            return "габарит бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Saves the document, closes it, reopens it and reads the geometry and parameters back.
    /// </summary>
    /// <remarks>
    /// The reopened feature is found by walking the reopened document's own feature set and QI-ing
    /// <c>IRotated</c> on each element — never by holding the old COM reference across the close.
    /// КОМПАС revokes references when a document closes (R.23 had to use its donor's axis while the
    /// donor was still open), and a revived pointer would fail for a reason unrelated to the rotation.
    /// </remarks>
    private (bool Ok, string Detail) SaveCloseReopen(
        ksDocument3D document, ksPart part, KompasAPI7.IRotated rotation, string path, ProbeStep step)
    {
        var volumeBeforeSave = Api5.Volume(part);
        var angleBeforeSave = Api5.SafeDouble(() => rotation.Angle[true]);
        var directionBeforeSave = Api5.SafeEnum(() => rotation.Direction);

        var saved = Api5.SafeBool(() => document.SaveAs(path));
        var closed = Api5.SafeBool(() => document.close());
        step.Observe("save→close: SaveAs=" + Api5.Raw(saved) + " (" + Path.GetFileName(path)
            + "), close=" + Api5.Raw(closed));

        if (saved != true)
        {
            return (false, "SaveAs отказал");
        }

        ksDocument3D? reader = null;
        try
        {
            reader = (ksDocument3D)_app.Document3D();

            // No Create(...) before Open: R.20 measured that calling Create on a document before Open
            // makes the Open fail on the probe's OWN file (probe defect 7).
            var opened = Api5.SafeBool(() => reader.Open(path, true));
            step.Observe("reopen: Open(" + Path.GetFileName(path) + ")=" + Api5.Raw(opened));
            if (opened != true)
            {
                return (false, "файл не открылся заново");
            }

            if (reader.GetPart(-1) is not ksPart reopenedPart)
            {
                return (false, "reopen: деталь не получена");
            }

            var volumeAfterReopen = Api5.Volume(reopenedPart);
            var bodiesAfterReopen = Api5.BodyCount(reopenedPart);

            KompasAPI7.IRotated? found = null;
            string? foundName = null;
            if (reopenedPart.EntityCollection(Api5.OperationElement) is ksEntityCollection operations)
            {
                for (var i = 0; i < operations.GetCount() && found is null; i++)
                {
                    if (operations.GetByIndex(i) is not ksEntity element)
                    {
                        continue;
                    }

                    var definition = Api5.SafeObject(() => element.GetDefinition());
                    if (definition is null)
                    {
                        continue;
                    }

                    if (_app.TransferInterface(definition, 2, 0) is KompasAPI7.IModelObject object7
                        && object7 is KompasAPI7.IRotated candidate)
                    {
                        found = candidate;
                        foundName = element.name ?? "?";
                    }
                }
            }

            if (found is null)
            {
                return (false, "reopen: признак вращения не найден заново в дереве открытой детали");
            }

            var angleAfterReopen = Api5.SafeDouble(() => found.Angle[true]);
            var directionAfterReopen = Api5.SafeEnum(() => found.Direction);
            var axisAfterReopen = found.Axis;
            var profileAfterReopen = found.Profile;

            step.Observe("reopen: признак «" + foundName + "» найден заново; тел " + bodiesAfterReopen
                + ", объём=" + Api5.Num(volumeAfterReopen) + " (до save=" + Api5.Num(volumeBeforeSave) + ")"
                + ", Angle[true]=" + Api5.Num(angleAfterReopen) + " (до save=" + Api5.Num(angleBeforeSave) + ")"
                + ", Direction=" + Api5.Raw(directionAfterReopen) + " (до save=" + Api5.Raw(directionBeforeSave) + ")"
                + ", Axis=" + Describe7(axisAfterReopen) + ", Profile=" + Describe7(profileAfterReopen));

            var geometry = DescribeBodyGeometry(reopenedPart, step, "после reopen");

            var volumeHeld = volumeBeforeSave is not null && volumeAfterReopen is not null
                && Math.Abs(volumeAfterReopen.Value - volumeBeforeSave.Value)
                    <= NoiseFloor(volumeBeforeSave.Value);
            var angleHeld = angleBeforeSave is not null && angleAfterReopen is not null
                && Math.Abs(angleAfterReopen.Value - angleBeforeSave.Value) <= Tolerance(angleBeforeSave.Value);
            var referencesHeld = axisAfterReopen is not null && profileAfterReopen is not null;

            if (volumeHeld && angleHeld && referencesHeld)
            {
                return (true, "объём и параметры сохранились, ссылки взяты заново; " + geometry);
            }

            return (false, "объём сохранён=" + volumeHeld + ", угол сохранён=" + angleHeld
                + ", ось=" + (axisAfterReopen is not null) + ", профиль=" + (profileAfterReopen is not null));
        }
        catch (Exception ex)
        {
            return (false, "reopen бросил " + HResult.Describe(ex));
        }
        finally
        {
            try
            {
                reader?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>
    /// Finds the API7 axis container on a live part and fixes a two-point axis on the line <c>u=0</c>.
    /// </summary>
    /// <remarks>
    /// Two containers, two different QIs on the same live object — measured in R.13/R.18 and not
    /// interchangeable: <c>Points3D</c> is declared on <c>IModelContainer</c>, while <c>Axes3D</c> is
    /// declared on <c>IAuxiliaryGeomContainer</c> and is reachable <b>only</b> by that QI. A step that
    /// asks the model container for axes finds nothing and would report an absent feature.
    /// </remarks>
    private KompasAPI7.IAxis3D? BuildReferenceAxis(
        ksDocument3D document, ksPart part, ProbeStep step, double u)
    {
        try
        {
            if (_app.TransferInterface(part, 2 /* ksAPI7Dual */, 0) is not KompasAPI7.IModelObject part7)
            {
                step.Observe("деталь не переносится в API7 как IModelObject — ось не построить");
                return null;
            }

            if (part7 is not KompasAPI7.IModelContainer modelContainer)
            {
                step.Observe("перенесённая деталь не отвечает QI(IModelContainer) — Points3D не спросить");
                return null;
            }

            if (part7 is not KompasAPI7.IAuxiliaryGeomContainer auxiliary)
            {
                step.Observe("перенесённая деталь не отвечает QI(IAuxiliaryGeomContainer) "
                    + "(IID {950FEBE2-F916-4E77-A37D-B061E5C22FA8}) — Axes3D недостижим");
                return null;
            }

            var plane = ReadPlane(part, step);
            var v0 = -HeightMm / 2d;
            var v1 = HeightMm / 2d;

            var p1 = MakeModelPoint(modelContainer, plane, u, v0, step);
            var p2 = MakeModelPoint(modelContainer, plane, u, v1, step);
            if (p1 is null || p2 is null)
            {
                step.Observe("точки оси не создались — ось не построить");
                return null;
            }

            step.Observe("точки оси в координатах плоскости эскиза (u=" + Api5.Num(u) + "): v="
                + Api5.Num(v0) + " → модель " + Point(plane.PointAt(u, v0))
                + "; v=" + Api5.Num(v1) + " → модель " + Point(plane.PointAt(u, v1)));

            var axes = auxiliary.Axes3D;
            if (axes is null)
            {
                step.Observe("IAuxiliaryGeomContainer.Axes3D → null");
                return null;
            }

            var before = Api5.Raw(Api5.SafeInt(() => axes.Count));
            var raw = axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axis2Points);
            step.Observe("Axes3D.Add(o3d_axis2Points) → " + Api5.RuntimeName(raw)
                + " (осей было " + before + ")");

            if (raw is not KompasAPI7.IAxis3DBy2Points by2)
            {
                step.Observe("объект оси не отвечает QI(IAxis3DBy2Points)");
                return null;
            }

            by2.Point1 = p1;
            by2.Point2 = p2;

            // Update() only here, with both sources already supplied: R.13 measured that an axis
            // Updated before its source is set reads Valid=False and never enters the tree.
            var updated = Api5.SafeBool(by2.Update);
            var valid = Api5.SafeBool(() => by2.Valid);
            step.Observe("ось по двум точкам: Update()=" + Api5.Raw(updated) + ", Valid=" + Api5.Raw(valid)
                + ", осей стало " + Api5.Raw(Api5.SafeInt(() => axes.Count))
                + ", Name=" + Api5.Raw(Api5.SafeObject(() => by2.Name)));

            document.RebuildDocument();

            if (valid != true)
            {
                step.Observe("ось построена, но Valid не True — передаю её всё равно: отказ вращения "
                    + "на негодной оси не был бы фактом о вращении");
            }

            return by2;
        }
        catch (Exception ex)
        {
            step.Observe("построение эталонной оси бросило " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>The XOY sketch plane's placement, as an explicit sketch→model transform.</summary>
    /// <remarks>
    /// <para>
    /// Two routes, in order, because the first is measured not to answer on a <i>default</i> plane
    /// entity: <c>ksEntity.GetDefinition()</c> on the default XOY plane hands back a bare
    /// <c>System.__ComObject</c> that does not cast to <c>ksPlaneOffsetDefinition</c> — the same
    /// wrapper-vs-object distinction R.22 found on shipped features. The fallback is the API7 route,
    /// which works on the live object: <c>TransferInterface</c> → QI(<c>IPlane3D</c>) →
    /// <c>Surface</c> (<c>IMathSurface3D</c>) → <c>Placement</c> (<c>IPlacement3D</c>) →
    /// <c>GetPoint3D(XIn, YIn, out XOut, out YOut, out ZOut)</c>.
    /// </para>
    /// <para>
    /// <c>IPlacement3D.GetPoint3D</c> is the application's own definition of "a point in the plane's
    /// coordinates, in model coordinates", so the conversion is delegated rather than re-derived from
    /// vectors whose meaning as sketch u/v no step in this tool has measured. Building it out of a
    /// measured member is the difference between computing the position and assuming it.
    /// </para>
    /// </remarks>
    private PlaneReadout ReadPlane(ksPart part, ProbeStep step)
    {
        var api5 = TryReadPlaneApi5(part, step);
        if (api5 is not null)
        {
            return api5.Value;
        }

        var api7 = TryReadPlaneApi7(part, step);
        if (api7 is not null)
        {
            step.Observe("преобразование эскиз→модель взято маршрутом API7 (IPlane3D.Surface.Placement)");
            return api7.Value;
        }

        step.Observe("преобразование эскиз→модель прочитать не удалось ни маршрутом API5, ни API7 — "
            + "принимается тождественное, и это НАЗВАНО, а не скрыто");
        return PlaneReadout.Identity();
    }

    /// <summary>API5 route: <c>ksPlaneOffsetDefinition.GetSurface() → ksPlaneParam.GetPlacement()</c>.</summary>
    private static PlaneReadout? TryReadPlaneApi5(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity planeEntity)
            {
                step.Observe("плоскость XOY не получена маршрутом API5");
                return null;
            }

            if (planeEntity.GetDefinition() is not ksPlaneOffsetDefinition offsetDefinition)
            {
                step.Observe("маршрут API5: GetDefinition() плоскости не привелось к "
                    + "ksPlaneOffsetDefinition — перехожу на API7");
                return null;
            }

            if (offsetDefinition.GetSurface() is not ksPlaneParam planeParam)
            {
                step.Observe("маршрут API5: GetSurface() не дало ksPlaneParam");
                return null;
            }

            if (planeParam.GetPlacement() is not ksPlacement projection)
            {
                step.Observe("маршрут API5: ksPlaneParam.GetPlacement() не дало ksPlacement");
                return null;
            }

            step.Observe("маршрут API5: размещение плоскости XOY прочитано, начало="
                + Point(ReadOrigin(projection)));
            return new PlaneReadout(projection, null);
        }
        catch (Exception ex)
        {
            step.Observe("маршрут API5 чтения плоскости бросил " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// API7 route: <c>TransferInterface(плоскость) → QI(IPlane3D) → Surface → Placement</c>.
    /// </summary>
    /// <remarks>
    /// The plane entity is transferred, not the whole document: transferring the document gives the
    /// container, and the container has no member that answers for a named default plane.
    /// </remarks>
    private PlaneReadout? TryReadPlaneApi7(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity planeEntity)
            {
                return null;
            }

            if (_app.TransferInterface(planeEntity, 2 /* ksAPI7Dual */, 0)
                is not KompasAPI7.IModelObject plane7)
            {
                step.Observe("маршрут API7: плоскость XOY не перенеслась как IModelObject");
                return null;
            }

            if (plane7 is not KompasAPI7.IPlane3D plane)
            {
                step.Observe("маршрут API7: перенесённая плоскость не отвечает QI(IPlane3D)");
                return null;
            }

            var surface = plane.Surface as KompasAPI7.IMathSurface3D;
            if (surface is null)
            {
                step.Observe("маршрут API7: IPlane3D.Surface не отвечает IMathSurface3D");
                return null;
            }

            var placement = surface.Placement as KompasAPI7.IPlacement3D;
            if (placement is null)
            {
                step.Observe("маршрут API7: IMathSurface3D.Placement не отвечает IPlacement3D");
                return null;
            }

            // A probe of the transform itself: (0,0) must be the plane origin, and the u axis must
            // come out non-degenerate. A placement that answers but projects everything to the same
            // point is not a transform, and writing an axis through it would place the axis nowhere.
            var at00 = ProjectPoint7(placement, 0d, 0d);
            var atU = ProjectPoint7(placement, RadiusMm, 0d);
            var atV = ProjectPoint7(placement, 0d, HeightMm);
            step.Observe("маршрут API7: Point3D(0,0)=" + Point(at00)
                + ", (20,0)=" + Point(atU) + ", (0,40)=" + Point(atV));

            if (at00 is null || atU is null)
            {
                step.Observe("маршрут API7: преобразование вырождено (точки не читаются) — отвергаю");
                return null;
            }

            return new PlaneReadout(null, null, placement);
        }
        catch (Exception ex)
        {
            step.Observe("маршрут API7 чтения плоскости бросил " + HResult.Describe(ex));
            return null;
        }
    }

    private static double[]? ProjectPoint7(KompasAPI7.IPlacement3D placement, double u, double v)
    {
        try
        {
            return placement.GetPoint3D(u, v, out var x, out var y, out var z) ? new[] { x, y, z } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[]? ProjectPoint(ksPlacement placement, double u, double v)
    {
        try
        {
            return placement.PointOn(u, v, out var x, out var y, out var z) ? new[] { x, y, z } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double[]? ReadOrigin(ksPlacement placement)
    {
        try
        {
            return placement.GetOrigin(out var x, out var y, out var z) ? new[] { x, y, z } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private KompasAPI7.IPoint3D? MakeModelPoint(
        KompasAPI7.IModelContainer container, PlaneReadout plane, double u, double v, ProbeStep step)
    {
        try
        {
            var points = container.Points3D;
            if (points is null)
            {
                step.Observe("IModelContainer.Points3D → null (объявлен на IModelContainer, а НЕ на "
                    + "IAuxiliaryGeomContainer — R.18)");
                return null;
            }

            var raw = points.Add();
            if (raw is not KompasAPI7.IPoint3D point)
            {
                step.Observe("Points3D.Add() → " + Api5.RuntimeName(raw) + " (не IPoint3D)");
                return null;
            }

            var model = plane.PointAt(u, v);
            point.X = model[0];
            point.Y = model[1];
            point.Z = model[2];
            var updated = Api5.SafeBool(point.Update);
            step.Observe("точка (" + Point(model) + "): Update()=" + Api5.Raw(updated));
            return point;
        }
        catch (Exception ex)
        {
            step.Observe("создание точки бросило " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The independent geometry check: cylindrical faces, topology counts, and the bounding box.
    /// </summary>
    /// <remarks>
    /// The volume alone cannot distinguish a cylinder R20 H40 from a plate of 50265 mm³, so readings
    /// that do not go through <c>CalcMassInertiaProperties</c> are taken beside it: the cylindrical
    /// faces the API5 topology route reports (<c>GetSurfaceParam() → ksCylinderParam</c>: radius and
    /// height) and the bounding box of the main body.
    /// </remarks>
    private string DescribeBodyGeometry(ksPart part, ProbeStep step, string label)
    {
        var parts = new List<string> { label };

        try
        {
            var cylinders = Api5.CylinderFaces(part);
            parts.Add("цилиндрических граней " + cylinders.Count);
            foreach (var face in cylinders.Take(4))
            {
                parts.Add("[" + face.Index + "] r=" + Api5.Num(face.CylinderRadius)
                    + " h=" + Api5.Num(face.CylinderHeight)
                    + " ось=" + Api5.FaceReading.Point(face.CylinderAxis));
            }
        }
        catch (Exception ex)
        {
            parts.Add("чтение цилиндрических граней бросило " + HResult.Describe(ex));
        }

        try
        {
            parts.Add("граней " + Api5.FaceCount(part) + ", рёбер " + Api5.EdgeCount(part));
        }
        catch (Exception ex)
        {
            parts.Add("подсчёт топологии бросил " + HResult.Describe(ex));
        }

        parts.Add(DescribeBodyBounds(part, step, "габарит"));
        return string.Join(", ", parts);
    }

    /// <summary>The main body's bounding box, so a body can be placed as well as measured.</summary>
    /// <remarks>
    /// <para>
    /// The member is <c>ksBody.GetGabarit(x1, y1, z1, x2, y2, z2)</c> — read off the type library, not
    /// guessed. The first version of this helper looked for <c>GetBoundingBoxEx</c>, which
    /// <c>ksBody</c> does not declare (its ten members are <c>CalcMassInertiaProperties</c>,
    /// <c>CheckIntersectionWithBody</c>, <c>CurveIntersection</c>, <c>FaceCollection</c>,
    /// <c>GetFeature</c>, <c>GetGabarit</c>, <c>GetIntersectionFacesWithBody</c>, <c>IsSolid</c>,
    /// <c>MultiBodyParts</c>) — so the step printed "not declared" and lost the check that separates a
    /// cylinder lying along the axis from a sector on one side of it.
    /// </para>
    /// <para>
    /// Reported as "not read" rather than as zeros when the API does not answer: a box of zeros is a
    /// legitimate reading for a degenerate body, and the two must not print the same way.
    /// </para>
    /// </remarks>
    private string DescribeBodyBounds(ksPart part, ProbeStep step, string label)
    {
        try
        {
            if (part.GetMainBody() is not ksBody body)
            {
                return label + "= тела нет";
            }

            // Static interface member, never a reflection over the instance (ADR-001 §2).
            if (!body.GetGabarit(out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return label + "= GetGabarit отказал";
            }

            var numbers = new[] { x1, y1, z1, x2, y2, z2 };
            if (numbers.Any(double.IsNaN))
            {
                return label + "= GetGabarit вернул NaN";
            }

            return label + "= x[" + Api5.Num(x1) + "," + Api5.Num(x2) + "]"
                + " y[" + Api5.Num(y1) + "," + Api5.Num(y2) + "]"
                + " z[" + Api5.Num(z1) + "," + Api5.Num(z2) + "]"
                + " (размеры " + Api5.Num(Math.Abs(x2 - x1)) + "×" + Api5.Num(Math.Abs(y2 - y1))
                + "×" + Api5.Num(Math.Abs(z2 - z1)) + ")";
        }
        catch (Exception ex)
        {
            return label + " бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>Writes one member and reads it back, with the write's own refusal kept separate.</summary>
    private static string WriteAndReadBack(ProbeStep step, string name, Func<string> writeAndRead)
    {
        try
        {
            return name + " → " + writeAndRead();
        }
        catch (Exception ex)
        {
            return name + " → бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>Transfers an API5 object to API7 as an <c>IModelObject</c>, or null with the reason.</summary>
    /// <remarks>
    /// The vendor transfer is typed <c>IModelObject</c>, which is what every API7 setter that takes a
    /// reference (<c>IRotated.Profile</c>, <c>IRotated.Axis</c>) declares. Returning the untyped result
    /// of <c>TransferInterface</c> would be a conversion the compiler refuses, and casting it away with
    /// a blind <c>as</c> would turn "the transfer is not an IModelObject" into "the setter refused".
    /// </remarks>
    private KompasAPI7.IModelObject? TransferTo7(object source)
    {
        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        }
        catch (Exception ex)
        {
            _report.Note(_currentStepId, "TransferInterface(→ API7) бросил " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>The control extrusion of an ALREADY-DRAWN profile, so the profile itself is tested.</summary>
    /// <remarks>
    /// The absolute volume is reported, not a delta: on a document that has no body yet,
    /// <c>Api5.Volume</c> is legitimately <c>null</c> before the extrusion (there is nothing to
    /// measure), so a delta would be <c>null</c> for a control that worked perfectly. The claim being
    /// tested is "this profile is a region", and the reading that decides it is the body that appears.
    /// </remarks>
    private string BuildControlExtrusionOn(
        ksDocument3D document, ProbeStep step, ksEntity sketch, string prefix)
    {
        try
        {
            var part = (ksPart)document.GetPart(-1);
            if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
                || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                return "NewEntity(24) или определение не получены";
            }

            extrusion.name = prefix + "-control";
            definition.SetSketch(sketch);
            definition.directionType = 0;
            definition.SetSideParam(true, 0 /* etBlind */, 20d, 0d, false);

            var volumeBefore = Api5.Volume(part);
            var bodiesBefore = Api5.BodyCount(part);
            var created = Api5.SafeBool(extrusion.Create);
            part.RebuildModel();
            document.RebuildDocument();
            var volumeAfter = Api5.Volume(part);
            var bodiesAfter = Api5.BodyCount(part);

            var expected = RadiusMm * HeightMm * 20d; // 20 × 40 × 20
            var matches = volumeAfter is not null
                && Math.Abs(volumeAfter.Value - expected) <= Tolerance(expected);

            var verdict = "Create()=" + Api5.Raw(created)
                + ", тел " + bodiesBefore + "→" + bodiesAfter
                + ", объём " + Api5.Num(volumeBefore) + "→" + Api5.Num(volumeAfter)
                + " против аналитического " + Api5.Num(expected)
                + (matches
                    ? " — профиль есть область"
                    : " — ВНИМАНИЕ: профиль областью не является, и отказ вращения был бы фактом о "
                        + "профиле, а не о вращении");

            // The control's own verdict, not just its text: a control that is registered but never
            // judged stays `unknown` in the report, which reads as "this question was not answered"
            // when in fact it was. The control answering correctly is a PASS on its own question; a
            // control that produced the wrong volume is a FAIL, and it invalidates the parent step's
            // reading of any rotation failure.
            if (matches)
            {
                step.Pass("профиль " + prefix + " — замкнутая область: выдавливание дало "
                    + Api5.Num(volumeAfter) + " против аналитического " + Api5.Num(expected));
            }
            else
            {
                step.Fail("контроль профиля НЕ сошёлся: " + verdict);
            }

            return verdict;
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>A rectangle sketch in a specific document: u∈[uOffset, uOffset+20], v∈[vOffset±20].</summary>
    private (ksPart Part, ksEntity Sketch) RectangleSketchOnPlane(
        ksDocument3D document, string name, double uOffset, double vOffset)
    {
        var part = (ksPart)document.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        var u0 = uOffset;
        var u1 = uOffset + RadiusMm;
        var v0 = vOffset - HeightMm / 2d;
        var v1 = vOffset + HeightMm / 2d;
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
        }

        definition.EndEdit();
        part.RebuildModel();
        document.RebuildDocument();
        return (part, sketch);
    }

    /// <summary><see cref="RectangleSketchOnPlane"/>, optionally with the axis line on the u=0 side.</summary>
    private (ksPart Part, ksEntity Sketch) RectangleSketchIn(
        ksDocument3D document, string name, bool withAxis)
    {
        if (!withAxis)
        {
            return RectangleSketchOnPlane(document, name, 0d, 0d);
        }

        var part = (ksPart)document.GetPart(-1);
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
            || part.NewEntity(Api5.Sketch) is not ksEntity sketch
            || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            throw new InvalidOperationException(name + ": эскиз не создан");
        }

        sketch.name = name;
        definition.SetPlane(plane);
        sketch.Create();

        int? axisRef = null;
        if (definition.BeginEdit() is ksDocument2D editor)
        {
            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);
            axisRef = DrawAxis(editor, -HeightMm / 2d, HeightMm / 2d);
        }

        definition.EndEdit();
        part.RebuildModel();
        document.RebuildDocument();
        _lastSketchHadAxis = axisRef is not null;
        return (part, sketch);
    }

    private static string Point(double[]? value) =>
        value is null ? "<нет>" : "(" + string.Join(", ", value.Select(v => Api5.Num(v))) + ")";

    /// <summary>A sketch plane's placement as an explicit sketch→model transform.</summary>
    /// <remarks>
    /// <c>Identity</c> is never a silent default: <see cref="ReadPlane"/> writes a line into the journal
    /// whenever the transform could not be read, so an assumed transform is visible in the report
    /// rather than indistinguishable from a measured one. When a placement WAS read, the conversion is
    /// delegated to the application's own point projection (<c>ksPlacement.PointOn</c> or
    /// <c>IPlacement3D.GetPoint3D</c>) instead of being re-derived from vectors whose meaning as sketch
    /// u/v has not been measured here.
    /// </remarks>
    private readonly struct PlaneReadout
    {
        private readonly ksPlacement? _api5;
        private readonly KompasAPI7.IPlacement3D? _api7;

        public PlaneReadout(ksPlacement? api5, double[]? origin)
            : this(api5, origin, null)
        {
        }

        public PlaneReadout(ksPlacement? api5, double[]? origin, KompasAPI7.IPlacement3D? api7)
        {
            _api5 = api5;
            _api7 = api7;
            _origin = origin;
        }

        private readonly double[]? _origin;

        public static PlaneReadout Identity() => new(null, null, null);

        /// <summary>Plane coordinates (u, v) as model coordinates.</summary>
        public double[] PointAt(double u, double v)
        {
            if (_api5 is { } placement5 && ProjectPoint(placement5, u, v) is { Length: 3 } projected5)
            {
                return projected5;
            }

            if (_api7 is { } placement7 && ProjectPoint7(placement7, u, v) is { Length: 3 } projected7)
            {
                return projected7;
            }

            // The journal has already said the transform could not be read; this is the identity that
            // was announced, not an unstated guess.
            var origin = _origin ?? new[] { 0d, 0d, 0d };
            return new[] { origin[0] + u, origin[1] + v, origin[2] };
        }
    }

    /// <summary>
    /// R.23 — the last route that held any hope: carry an axis the API itself considers good out of a
    /// shipped file and into a rotation the probe owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is the last unmeasured route.</b> R.7…R.19 established, by measurement, that every
    /// axis the probe can <i>author</i> is rejected: an edge, a cylindrical face, an axis object from
    /// <c>Axes3D</c>, an axis by two points, an axis from the sketch. R.22 then found something no
    /// earlier step had: a shipped rotation's <c>IRotated.Axis</c> reads back as a live object rather
    /// than <c>null</c>. R.7's own feature read <c>Axis=null</c> after the write, which was recorded as
    /// "the member silently drops the value". If the axis a shipped rotation carries is one the API
    /// itself accepts, then "SM-03 is blocked" and "the probe never had an acceptable axis" are two
    /// different claims, and only this measurement separates them.
    /// </para>
    /// <para>
    /// <b>What is measured.</b> A shipped file is opened and its rotation's <c>Axis</c> and
    /// <c>Profile</c> are taken as objects. Then the probe's own document builds a plate, a sketch with
    /// a closed rectangle profile, and a rotation — and tries to feed the shipped pair into
    /// <c>IRotated.SetAxis</c>/<c>SetProfile</c> on its own feature. Two preconditions are checked
    /// first, because a failure to satisfy either would make the main attempt meaningless: that the
    /// shipped axis object answers for something nameable (a runtime name other than a null), and that
    /// the shipping document is still open and alive when the transfer happens (КОМПАС revokes
    /// references when a document closes, and a revoked axis would fail for a reason that has nothing
    /// to do with the axis).
    /// </para>
    /// <para>
    /// <b>Verdict.</b> Pass only if the volume moves to the analytic value the profile implies — a
    /// transferred axis is worth exactly what it builds, and nothing else. <c>Create()=True</c> without a
    /// volume change is the no-op R.19 already caught once, and is reported as such. Fail otherwise,
    /// and the failure text says which of the three things happened: the shipped file gave no axis, the
    /// transfer was refused, or the transfer was accepted and built nothing.
    /// </para>
    /// </remarks>
    private void ShippedAxisReuseRoute()
    {
        _currentStepId = "R.23";
        var step = _report.Begin("R.23", "Ось из файла поставки в своё вращение: переносится ли",
            "Принимает ли вращение ось, которую API сам считает годной, — или отвергает любую?");

        // ── the donor: a shipped rotation whose Axis the API reads back as a live object ─────────
        var donorPath = FindShippedRotationWithLiveAxis(step);
        if (donorPath is null)
        {
            step.Fail("ни один файл поставки не дал вращения с живой осью — переносить нечего, и "
                + "вопрос «принимает ли вращение чужую ось» остался без предмета");
            return;
        }

        step.Observe("донор: " + Path.GetFileName(donorPath));

        KompasAPI7.IModelObject? donorAxis7 = null;
        KompasAPI7.IModelObject? donorProfile7 = null;
        var donorReader = (ksDocument3D)_app.Document3D();
        // Declared beside the donor reader because both must be closed in the `finally` below.
        ksDocument3D? ownDoc = null;
        try
        {
            if (Api5.SafeBool(() => donorReader.Open(donorPath, true)) != true)
            {
                step.Fail("файл-донор не открылся (" + Path.GetFileName(donorPath)
                    + ") — перенос не измерен, и это НЕ утверждение о невозможности переноса");
                return;
            }

            if (donorReader.GetPart(-1) is not ksPart donorPart
                || donorPart.EntityCollection(Api5.OperationElement) is not ksEntityCollection donorOps)
            {
                step.Fail("деталь или набор признаков донора недоступны");
                return;
            }

            for (var i = 0; i < donorOps.GetCount() && donorAxis7 is null; i++)
            {
                if (donorOps.GetByIndex(i) is not ksEntity element
                    || !LooksLikeRotation(element.name ?? ""))
                {
                    continue;
                }

                var definition = Api5.SafeObject(() => element.GetDefinition());
                if (definition is null)
                {
                    continue;
                }

                if (_app.TransferInterface(definition, 2 /* ksAPI7Dual */, 0)
                    is KompasAPI7.IModelObject object7
                    && object7 is KompasAPI7.IRotated donorRotated)
                {
                    // Read the pair WITHOUT a try that hides a null: an axis that reads back null is
                    // exactly the state the probe's own feature is in, and it must be visible as that.
                    donorAxis7 = donorRotated.Axis;
                    donorProfile7 = donorRotated.Profile;
                    step.Observe("донор «" + (element.name ?? "?") + "»: Axis=" + Describe7(donorAxis7)
                        + ", Profile=" + Describe7(donorProfile7));
                }
            }

            if (donorAxis7 is null)
            {
                step.Fail("у вращения донора Axis читается null — донор не годится, и это НЕ утверждение "
                    + "о том, принимает ли вращение чужую ось");
                return;
            }

            // The precondition that keeps the main attempt interpretable: a revoked reference fails
            // for its own reason, so the donor document must still be open when the axis is used.
            step.Observe("донор открыт, ось взята: " + Describe7(donorAxis7)
                + " — ссылка должна пережить закрытие донора, и это измеряется ниже");

            // ── the recipient: the probe's own document, its own profile and feature ─────────────
            //
            // THIRTEENTH PROBE DEFECT, found here and fixed here. The first version of this step built
            // its profile and its feature in the SHARED document — the one R.21 and R.2…R.5 also use —
            // and then compared the volume across the attempt with an absolute expectation. Both halves
            // of that were wrong, and the journal showed it: the control reported ΔV=52223.8713227515
            // while the main attempt reported ΔV=4223.87132275128, and those differ by exactly
            // 48000.000000000226 — the volume of a 20×40×60 block, i.e. the geometry a PREVIOUS step
            // had left in the same document. Two consequences, both fatal to the reading:
            //   * the delta was a difference of two unrelated baselines, so "nothing was built" and
            //     "a rebuild replaced earlier geometry" print the same way;
            //   * the geometry this step built stayed behind, and R.2/R.4 afterwards read
            //     V=714418.561673603 as their own starting state.
            // The fix is isolation. R.20 and R.22 already work in their own documents; this step must
            // too. A measurement that mutates the fixture the following steps measure is not a
            // measurement of the route, it is a measurement of the order of the steps.
            ksPart part;
            ksEntity sketch;
            try
            {
                ownDoc = (ksDocument3D)_app.Document3D();
                // The two-argument form R.18 uses, which is measured to produce a document that actually
                // becomes the one the part is read from. The first version of this step called
                // `Create(false, false)` — the form R.20 measured to break the very next `Open` — and
                // the part it then read kept reporting the SHARED document's body (ΔV=-657970.8190281
                // against a baseline of 4223.87132275147, which is the shared document's number
                // 710194.690350851 minus the 52223.87 block). Fourteenth probe defect, and the reason
                // it was invisible: "a fresh document" was assumed to isolate the measurement, and the
                // number that proved otherwise was read as a property of the rotation.
                ownDoc.Create(true, true);
                if (ownDoc.GetPart(-1) is not ksPart ownPart)
                {
                    step.Fail("свой документ не дал деталь — опыт не поставлен");
                    return;
                }

                part = ownPart;

                if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                    || part.NewEntity(Api5.Sketch) is not ksEntity ownSketch
                    || ownSketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
                {
                    step.Fail("свой эскиз в отдельном документе не создался");
                    return;
                }

                ownSketch.name = "R23-profile";
                sketchDefinition.SetPlane(plane);
                ownSketch.Create();
                if (sketchDefinition.BeginEdit() is ksDocument2D editor)
                {
                    const double r = 20d, h = 40d;
                    editor.ksLineSeg(0d, -h / 2d, r, -h / 2d, 1);
                    editor.ksLineSeg(r, -h / 2d, r, h / 2d, 1);
                    editor.ksLineSeg(r, h / 2d, 0d, h / 2d, 1);
                    editor.ksLineSeg(0d, h / 2d, 0d, -h / 2d, 1);
                }

                sketchDefinition.EndEdit();
                part.RebuildModel();
                ownDoc.RebuildDocument();
                sketch = ownSketch;
            }
            catch (Exception ex)
            {
                step.Fail("свой документ или профиль построить не удалось: " + ex.Message);
                return;
            }

            step.Observe("свой отдельный документ готов: профиль-эскиз «R23-profile», "
                + "объём документа до опыта " + Api5.Num(Api5.Volume(part))
                + " (null = тела нет, и это ожидаемо: документ создан этим шагом, а не унаследован)");

            if (part.NewEntity((short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_baseRotated) is not ksEntity feature)
            {
                step.Fail("NewEntity(вращение) не выдал сущность");
                return;
            }

            var recipientDefinition = Api5.SafeObject(() => feature.GetDefinition());
            if (recipientDefinition is null)
            {
                step.Fail("определение своего вращения не получено");
                return;
            }

            if (_app.TransferInterface(recipientDefinition, 2 /* ksAPI7Dual */, 0)
                is not KompasAPI7.IModelObject recipient7
                || recipient7 is not KompasAPI7.IRotated recipientRotated)
            {
                step.Fail("своё вращение не отвечает на QI(IRotated) — опыт непоставим");
                return;
            }

            // The API5 side still needs the profile and the angle: R.19 measured that a rotation with a
            // good profile, an explicit axis and a chosen body operation still refuses, so this step
            // supplies everything R.19 supplied and only changes WHERE THE AXIS COMES FROM.
            // The order matters and is therefore not left implicit: the first attempt wrote Profile
            // first, read back false, and then wrote Axis — so "Axis refused" could be a consequence
            // of the failed profile write rather than a finding about the axis. The measurement is
            // repeated with the axis FIRST, on a fresh feature, so the two writes cannot contaminate
            // each other. Without this, the step's own failure text would be reporting the order of
            // its operations as a property of the axis.
            var axisFirstTransfers = TryAxisFirstOnFreshFeature(
                part, ownDoc, donorAxis7, donorProfile7, step);
            step.Observe("порядок «ось первой» на отдельном признаке: " + axisFirstTransfers);

            var transfers = new List<string>();
            transfers.Add("SetProfile(чужой)=" + AttemptTransfer(step, "SetProfile", () =>
            {
                recipientRotated.Profile = donorProfile7;
                return recipientRotated.Profile is not null;
            }));

            transfers.Add("SetAxis(чужой)=" + AttemptTransfer(step, "SetAxis", () =>
            {
                recipientRotated.Axis = donorAxis7;
                return recipientRotated.Axis is not null;
            }));

            step.Observe("перенос: " + string.Join(", ", transfers));

            // The API5-side profile, in case the API7 SetProfile above did not take: R.19's own recipe,
            // so the only difference from R.19 remains the origin of the axis.
            if (recipientDefinition is ksBaseRotatedDefinition rotatedDefinition)
            {
                try
                {
                    rotatedDefinition.SetSketch(sketch);
                    rotatedDefinition.SetSideParam(true, 360d);
                    rotatedDefinition.SetSideParam(false, 0d);
                    step.Observe("API5-сторона задана: SetSketch + SetSideParam(true,360) + (false,0)");
                }
                catch (Exception ex)
                {
                    step.Observe("API5-сторона бросила " + HResult.Describe(ex));
                }
            }
            else
            {
                step.Observe("своё определение не приводится к ksBaseRotatedDefinition — API5-сторону "
                    + "задать нечем (та же упаковка, что R.22 измерил у чужих)");
            }

            // The baseline is taken HERE, not before the sketch: a rotation's delta is the volume the
            // rotation adds, and everything before this line (empty document, sketch, feature shell) is
            // not part of the answer. Taking it earlier was half of defect 13.
            part.RebuildModel();
            ownDoc.RebuildDocument();
            var volumeBeforeCreate = Api5.Volume(part);

            var created = Api5.SafeBool(feature.Create);
            var isCreated = Api5.SafeBool(feature.IsCreated);
            part.RebuildModel();
            ownDoc.RebuildDocument();
            var volumeAfter = Api5.Volume(part);
            var delta = volumeAfter is not null && volumeBeforeCreate is not null
                ? volumeAfter - volumeBeforeCreate : null;

            step.Observe("своё вращение с чужой осью: Create()=" + created + ", IsCreated()=" + isCreated
                + ", ΔV=" + Api5.Num(delta) + " (до Create()=" + Api5.Num(volumeBeforeCreate)
                + ", после=" + Api5.Num(volumeAfter) + ")");

            // The only acceptable evidence: the volume moved to what THIS profile implies. A true
            // Create() with an unmoved volume is the no-op R.19 already caught and is not a pass.
            if (delta is not null && Math.Abs(delta.Value) > 0d
                && Math.Abs(delta.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
            {
                step.Pass("вращение построено на оси из файла поставки: ΔV=" + Api5.Num(delta)
                    + " совпал с π·r²·h = " + Api5.Num(FullTurnVolume)
                    + " — блокер SM-03 снят этим маршрутом, а не описан лучше");
            }
            else
            {
                step.Fail("чужую ось вращение тоже не приняло: перенос "
                    + (transfers.All(t => !t.EndsWith("False", StringComparison.Ordinal))
                        ? "принят, но сборка не дала объёма (ΔV=" + Api5.Num(delta) + ")"
                        : "отвергнут на записи")
                    + ". Значит отказ не в происхождении оси: Create()=" + created
                    + ", IsCreated()=" + isCreated + " — блокер SM-03 остаётся открытым и после "
                    + "единственного маршрута, который давал надежду");
            }
        }
        catch (Exception ex)
        {
            step.Observe("опыт бросил " + HResult.Describe(ex));
            step.Fail("опыт прерван исключением — результат неполный, а не отрицательный: "
                + HResult.Describe(ex));
        }
        finally
        {
            // Both documents this step opened are closed: the donor file and its own. Closing only the
            // donor would leave this step's geometry in a document nobody measures, which is harmless
            // here but would hide exactly the kind of leak defect 13 was.
            try
            {
                donorReader.close();
            }
            catch
            {
                // Not this step's subject.
            }

            try
            {
                ownDoc?.close();
            }
            catch
            {
                // Not this step's subject.
            }
        }
    }

    /// <summary>Writes the shipped axis FIRST on a fresh feature, so the writes cannot contaminate.</summary>
    /// <remarks>
    /// The control for R.23's order-of-operations objection. The main attempt writes Profile then Axis,
    /// and the Profile write already read back false — so its result is compatible with two different
    /// explanations, and a step that reported either as <i>the</i> finding would be describing its own
    /// call order. This repeats the question on a brand-new rotation feature with the axis written
    /// first and read back before anything else is touched.
    /// </remarks>
    private string TryAxisFirstOnFreshFeature(
        ksPart part,
        ksDocument3D ownDoc,
        KompasAPI7.IModelObject donorAxis7,
        KompasAPI7.IModelObject? donorProfile7,
        ProbeStep step)
    {
        try
        {
            if (part.NewEntity((short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_baseRotated)
                is not ksEntity fresh)
            {
                return "NewEntity(вращение) не выдал сущность";
            }

            var definition = Api5.SafeObject(() => fresh.GetDefinition());
            if (definition is null)
            {
                return "определение не получено";
            }

            if (_app.TransferInterface(definition, 2 /* ksAPI7Dual */, 0)
                is not KompasAPI7.IModelObject object7
                || object7 is not KompasAPI7.IRotated rotated)
            {
                return "QI(IRotated) отказал";
            }

            // Axis first, alone, and read back immediately.
            var axisReadBack = "не дошли";
            try
            {
                rotated.Axis = donorAxis7;
                var after = rotated.Axis;
                axisReadBack = after is null ? "null (отброшено молча)" : Describe7(after);
            }
            catch (Exception ex)
            {
                axisReadBack = "бросил " + HResult.Describe(ex);
            }

            // Profile second, so the pair is complete only if both held.
            var profileReadBack = "не пытались";
            try
            {
                rotated.Profile = donorProfile7;
                var after = rotated.Profile;
                profileReadBack = after is null ? "null (отброшено молча)" : Describe7(after);
            }
            catch (Exception ex)
            {
                profileReadBack = "бросил " + HResult.Describe(ex);
            }

            // The baseline is taken here, on THIS part, and the rebuild goes through the document that
            // owns it — `ownDoc`. The first version took the baseline from the shared state field
            // `_bodyVolumeBeforeStep` and rebuilt `_doc`, so the delta compared this document's volume
            // after against the OTHER document's volume before, and printed ΔV=-657970.8190281 for a
            // no-op. Fourteenth probe defect: a delta is only a delta when both readings come from the
            // same object.
            var volumeBefore = Api5.Volume(part);
            var created = Api5.SafeBool(fresh.Create);
            part.RebuildModel();
            ownDoc.RebuildDocument();
            var volumeAfter = Api5.Volume(part);
            var delta = volumeAfter is not null && volumeBefore is not null
                ? volumeAfter - volumeBefore : null;

            return "Axis(первой)→" + axisReadBack + ", Profile(второй)→" + profileReadBack
                + ", Create()=" + created + ", ΔV=" + Api5.Num(delta)
                + " (до=" + Api5.Num(volumeBefore) + ", после=" + Api5.Num(volumeAfter) + ")";
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>Finds a shipped KOMPAS_24.0 file whose rotation hands back a live API7 axis.</summary>
    /// <remarks>
    /// Searched rather than hard-coded: the file that has a rotation is a property of this
    /// installation's shipped samples, and a hard-coded path would turn a missing file into a false
    /// negative about the route. The search stops at the first usable donor and reports which it took,
    /// so the journal names the actual evidence.
    /// </remarks>
    private string? FindShippedRotationWithLiveAxis(ProbeStep step)
    {
        var inventory = InventoryShippedModels(step);
        var rotationish = new[]
        {
            "вал", "ось", "shaft", "axis", "propeller", "винт", "ролик", "roller",
            "втулк", "bush", "кольц", "ring", "диск", "disc", "шпинд", "гильз", "стакан",
        };
        var ranked = inventory
            .Where(m => m.Version == "KOMPAS_24.0")
            .OrderByDescending(m =>
            {
                var n = Path.GetFileName(m.Path).ToLowerInvariant();
                return rotationish.Any(k => n.Contains(k)) ? 1 : 0;
            })
            .Take(6)
            .ToList();

        foreach (var model in ranked)
        {
            ksDocument3D? reader = null;
            try
            {
                reader = (ksDocument3D)_app.Document3D();
                if (Api5.SafeBool(() => reader.Open(model.Path, true)) != true
                    || reader.GetPart(-1) is not ksPart part
                    || part.EntityCollection(Api5.OperationElement) is not ksEntityCollection ops)
                {
                    continue;
                }

                for (var i = 0; i < ops.GetCount(); i++)
                {
                    if (ops.GetByIndex(i) is not ksEntity element
                        || !LooksLikeRotation(element.name ?? ""))
                    {
                        continue;
                    }

                    var definition = Api5.SafeObject(() => element.GetDefinition());
                    if (definition is null)
                    {
                        continue;
                    }

                    if (_app.TransferInterface(definition, 2 /* ksAPI7Dual */, 0)
                        is KompasAPI7.IModelObject object7
                        && object7 is KompasAPI7.IRotated rotated
                        && rotated.Axis is not null)
                    {
                        reader.close();
                        return model.Path;
                    }
                }
            }
            catch
            {
                // A file that throws is skipped; the loop exists to find one that does not.
            }
            finally
            {
                try
                {
                    reader?.close();
                }
                catch
                {
                    // Not this method's subject.
                }
            }
        }

        return null;
    }

    /// <summary>Runs one write-and-read-back pair, reporting both halves without hiding either.</summary>
    /// <remarks>
    /// A member that silently drops a value and a member that is absent are different findings, and the
    /// read-back is the only thing that separates them: this helper always performs the read and
    /// reports whether the value came back, rather than reporting only that the setter returned.
    /// </remarks>
    private static bool AttemptTransfer(ProbeStep step, string member, Func<bool> writeAndReadBack)
    {
        try
        {
            return writeAndReadBack();
        }
        catch (Exception ex)
        {
            step.Observe("  " + member + " бросил " + HResult.Describe(ex));
            return false;
        }
    }

    /// <summary>An API7 object as a journal cell, distinguishing null from unreadable.</summary>
    private static string Describe7(object? value) => value is null
        ? "null"
        : Api5.RuntimeName(value);

    /// <summary>
    /// Asks a <b>shipped</b> rotation feature the one question R.20 could not ask: not whether a managed
    /// cast succeeds, but whether the object itself answers <c>QueryInterface</c> for the definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The gap this closes.</b> R.20 measured that a real rotation out of a shipped
    /// <c>KOMPAS_24.0</c> file is a feature of the tree (its name is «Операция вращения:2» in
    /// <c>Propeller.m3d</c>), that its element casts to <c>ksEntity</c> through
    /// <c>EntityCollection(110)</c> (12 of 12), and that <c>GetDefinition()</c> returns something —
    /// but that <c>is ksBaseRotatedDefinition</c> is false on every element of every file. The step
    /// also printed the definition's runtime name as <c>System.__ComObject</c>, which is what
    /// <c>Api5.RuntimeName</c> reports for <i>any</i> COM-callable wrapper and therefore picks between
    /// none of the possible explanations.
    /// </para>
    /// <para>
    /// <b>Why a managed cast is the wrong instrument here.</b> R.7 hit the same wall on the probe's own
    /// feature and got past it: <c>TransferInterface(feature, 2 /* ksAPI7Dual */, 0)</c> followed by a
    /// QI for <c>IRotated</c> succeeded, where reaching the axis through the API5 definition was
    /// impossible (no rotation definition declares <c>SetAxis</c>). The lesson recorded from that step
    /// is that an interop cast is a real <c>QueryInterface</c> the vendor object may refuse for its own
    /// reasons, and that the refusal says something about the wrapper unless the object is asked
    /// directly. R.20 asked only through the wrapper.
    /// </para>
    /// <para>
    /// <b>What is measured, in order.</b> For each shipped file that contains a rotation by name:
    /// (1) the element's own API5 definition object, and its <i>managed</i> runtime type — the string
    /// that has been printed as <c>System.__ComObject</c> and read as "no type", printed here alongside
    /// <c>GetType().Name</c> so the two are distinguishable; (2) that same object handed to
    /// <c>TransferInterface(…, 2, 0)</c> as an <c>IModelObject</c>, which is the hop R.7 proved works;
    /// (3) a QI for <c>KompasAPI7.IRotated</c> on the result, and if it answers, the members that are
    /// then readable — <c>Axis</c>, <c>Profile</c>, <c>Angle</c>, <c>Direction</c>, <c>RotatedType</c>,
    /// <c>ToroidShapeType</c>, <c>OperationResult</c>. A <c>true</c> here means the API can be asked
    /// about a rotation КОМПАС authored; a refusal names the interface as the reason, not the attempt
    /// count.
    /// </para>
    /// <para>
    /// <b>Verdict.</b> Pass when at least one shipped rotation answers the QI and yields at least one
    /// readable parameter — that is the claim "the API can be asked about a rotation this installation
    /// authored", and it is the narrowest true statement the measurement supports. Fail otherwise, and
    /// the failure text distinguishes the two ways to fail: no file openable, or the object refusing
    /// the interface. The probe builds nothing here and claims no volume.
    /// </para>
    /// </remarks>
    private void ShippedDefinitionInterfaceRoute()
    {
        _currentStepId = "R.22";
        var step = _report.Begin("R.22", "Определение вращения из файла поставки: отвечает ли объект на QI",
            "Можно ли спросить API о вращении, которое построил сам КОМПАС, — или отказывает именно упаковка?");

        var inventory = InventoryShippedModels(step);
        var present = inventory
            .Where(m => m.Version == "KOMPAS_24.0")
            .Select(m => m.Path)
            .ToList();

        step.Observe("инвентарь: файлов .m3d " + inventory.Count
            + ", из них KOMPAS_24.0 " + present.Count);

        if (present.Count == 0)
        {
            step.Fail("ни одного файла KOMPAS_24.0 не найдено — вопрос о QI остался без предмета");
            return;
        }

        // The same preference order R.20 uses, and for the same reason: the order is a guess about
        // usefulness, not a claim about content, and the journal reports what is actually inside each
        // file, so a wrong guess costs a line rather than a conclusion.
        var rotationish = new[]
        {
            "вал", "ось", "shaft", "axis", "propeller", "винт", "ролик", "roller",
            "втулк", "bush", "кольц", "ring", "диск", "disc", "шпинд", "гильз", "стакан",
        };
        var ranked = present
            .OrderByDescending(p =>
            {
                var n = Path.GetFileName(p).ToLowerInvariant();
                return rotationish.Any(k => n.Contains(k)) ? 1 : 0;
            })
            .Take(12)
            .ToList();
        step.Observe("к разбору " + ranked.Count + " файлов");

        var opened = 0;
        var rotationsSeen = 0;
        var definitionsRead = 0;
        var transferred = 0;
        var answeredQi = 0;
        var parametersRead = 0;
        var firstSuccess = "";

        foreach (var path in ranked)
        {
            ksDocument3D? reader = null;
            try
            {
                reader = (ksDocument3D)_app.Document3D();

                // The same call shape R.20 settled on, and for the same measured reason: no
                // Create() first (seventh probe defect), Open(path, true).
                if (Api5.SafeBool(() => reader.Open(path, true)) != true)
                {
                    step.Observe(Path.GetFileName(path) + ": НЕ ОТКРЫТ");
                    continue;
                }

                opened++;

                if (reader.GetPart(-1) is not ksPart part)
                {
                    step.Observe(Path.GetFileName(path) + ": деталь не получена");
                    continue;
                }

                var file = Path.GetFileName(path);

                // The tree gives names; EntityCollection(110) gives handles. R.20 proved these are two
                // different collections, so the handles are what this step asks, and the name is used
                // only to decide whether the element is worth the question.
                if (part.EntityCollection(Api5.OperationElement) is not ksEntityCollection operations)
                {
                    step.Observe(file + ": EntityCollection(" + Api5.OperationElement + ") недоступна");
                    continue;
                }

                for (var i = 0; i < operations.GetCount(); i++)
                {
                    if (operations.GetByIndex(i) is not ksEntity element)
                    {
                        continue;
                    }

                    var name = element.name ?? "?";
                    if (!LooksLikeRotation(name))
                    {
                        continue;
                    }

                    rotationsSeen++;

                    object? definition;
                    try
                    {
                        definition = element.GetDefinition();
                    }
                    catch (Exception ex)
                    {
                        step.Observe(file + ": «" + name + "» → GetDefinition() бросил "
                            + HResult.Describe(ex));
                        continue;
                    }

                    if (definition is null)
                    {
                        step.Observe(file + ": «" + name + "» → GetDefinition() вернул null");
                        continue;
                    }

                    definitionsRead++;

                    // (1) The managed type of the object, printed so that "System.__ComObject" can be
                    // told apart from "a stub with no members": the first is a name COM gives every
                    // wrapper, the second is what R.0d measured for `ksFeature`. If these ever agree,
                    // the journal says so rather than leaving the reader to assume.
                    var managed = definition.GetType();
                    step.Observe(file + ": «" + name + "» определение → RuntimeName="
                        + Api5.RuntimeName(definition) + ", GetType().Name=" + managed.Name
                        + ", имя типа=" + managed.FullName
                        + ", членов объявлено=" + managed.GetMembers().Length);

                    // (2) The API7 hop, exactly as R.7 performed it on the probe's own feature. This is
                    // the measurement the step turns on: a managed `is` test is a QI the runtime
                    // performs with the metadata it has; TransferInterface is the transfer the vendor
                    // documents for exactly this crossing.
                    KompasAPI7.IModelObject? object7;
                    try
                    {
                        object7 = _app.TransferInterface(definition, 2 /* ksAPI7Dual */, 0)
                            as KompasAPI7.IModelObject;
                    }
                    catch (Exception ex)
                    {
                        step.Observe(file + ": «" + name + "» TransferInterface бросил "
                            + HResult.Describe(ex));
                        continue;
                    }

                    if (object7 is null)
                    {
                        step.Observe(file + ": «" + name + "» в API7 как IModelObject НЕ переносится ("
                            + Api5.RuntimeName(definition) + ")");
                        continue;
                    }

                    transferred++;
                    step.Observe(file + ": «" + name + "» → API7 IModelObject: OK ("
                        + Api5.RuntimeName(object7) + ")");

                    // (3) The QI. A refusal here is the answer, not a failure of the attempt: it says
                    // the interface is absent on a rotation КОМПАС itself authored.
                    if (object7 is not KompasAPI7.IRotated rotated)
                    {
                        step.Observe(file + ": «" + name + "» QI(IRotated) → ОТКАЗ "
                            + "(IID {7BB28AD1-CCAE-449C-9086-A97470543089} не поддержан)");
                        continue;
                    }

                    answeredQi++;

                    // The indexed members are read with their index argument, not as plain properties:
                    // the compiler refuses `rotated.Angle` and `rotated.RotatedType` with CS0856
                    // ("indexed property … has non-optional arguments"), which is the same shape R.13
                    // measured when it found `get_RotatedType(Boolean Normal)`. Writing them without
                    // the index would have been a guess that the build caught.
                    var readParts = new List<string>();
                    readParts.Add("Axis=" + ReadOrThrow(() => Api5.RuntimeName(rotated.Axis)));
                    readParts.Add("Profile=" + ReadOrThrow(() => Api5.RuntimeName(rotated.Profile)));
                    readParts.Add("Angle[true]=" + ReadOrThrow(() => Api5.Num(rotated.Angle[true])));
                    readParts.Add("Angle[false]=" + ReadOrThrow(() => Api5.Num(rotated.Angle[false])));
                    readParts.Add("Direction=" + ReadOrThrow(() => Api5.Raw(rotated.Direction)));
                    readParts.Add("RotatedType[true]="
                        + ReadOrThrow(() => Api5.Raw(rotated.RotatedType[true])));
                    readParts.Add("ToroidShapeType="
                        + ReadOrThrow(() => Api5.Raw(rotated.ToroidShapeType)));

                    var readCount = readParts.Count(p => !p.EndsWith("бросил", StringComparison.Ordinal)
                        && !p.EndsWith("=null", StringComparison.Ordinal));
                    if (readCount > 0)
                    {
                        parametersRead += readCount;
                    }

                    step.Observe(file + ": «" + name + "» QI(IRotated) → OK; прочитано: "
                        + string.Join(", ", readParts));

                    if (firstSuccess.Length == 0 && readCount > 0)
                    {
                        firstSuccess = file + " «" + name + "»: " + string.Join(", ", readParts);
                    }
                }
            }
            catch (Exception ex)
            {
                step.Observe(Path.GetFileName(path) + ": бросил " + HResult.Describe(ex));
            }
            finally
            {
                // `close()` — the lower-case member every other step in this file uses, not `Close`:
                // the first version of this step wrote `reader.Close()` and the compiler refused it,
                // which is a guess about a name rather than a reading of the type library.
                try
                {
                    reader?.close();
                }
                catch
                {
                    // A document that will not close is not this step's subject; the next iteration
                    // opens its own reader, and R.Z measures the process at the end.
                }
            }

            // No per-file progress line: per-file observations above already carry the file name, and
            // this step walks up to twelve files, so a second message per file would double the journal
            // without adding a distinction.
        }

        step.Observe("ИТОГ: открыто " + opened + ", вращений по имени " + rotationsSeen
            + ", определение отдал " + definitionsRead + ", перенесено в API7 " + transferred
            + ", ответило на QI(IRotated) " + answeredQi + ", прочитано параметров " + parametersRead);

        if (answeredQi > 0 && parametersRead > 0)
        {
            step.Pass("вращение из файла поставки ОТВЕЧАЕТ на QI(IRotated) и отдаёт параметры — API можно "
                + "спросить о вращении, которое построил сам КОМПАС, и отказ прежних шагов был отказом "
                + "упаковки, а не отсутствием маршрута. Первое: " + firstSuccess);
        }
        else if (opened == 0)
        {
            step.Fail("ни один файл поставки не открылся — вопрос о QI(IRotated) остался без измерения "
                + "(это не «интерфейса нет»)");
        }
        else
        {
            step.Fail("вращение в файлах поставки есть по имени (" + rotationsSeen + "), но ни одно не "
                + "ответило на QI(IRotated): перенесено в API7 " + transferred
                + ", ответило " + answeredQi + ". Значит вопрос «можно ли спросить API о чужом "
                + "вращении» закрыт отрицательно — и закрыт по типу, а не по числу попыток");
        }
    }

    /// <summary>Reads a value for a journal line, turning any failure into its own text.</summary>
    /// <remarks>
    /// A member that throws and a member that reads null are different findings, and a line that
    /// swallows either into an empty string erases the difference. This helper keeps both visible
    /// without letting one dead member abort the rest of the readback.
    /// </remarks>
    private static string ReadOrThrow(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Opens a <b>shipped</b> КОМПАС model that contains a real rotation feature, and reports what its
    /// tree holds and what its definition reads back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every measurement so far has been the probe's own construction: the probe draws a sketch, hands
    /// it to a rotation, and reads `Create()`. R.19 proved the input is good and the refusal survives it.
    /// What no step has done is look at a file <b>КОМПАС itself produced</b>. A shaft is the canonical
    /// rotated solid, and the installation ships shaft models, so the question "does this API rotation
    /// ever build anything" has a witness available that does not depend on the probe at all.
    /// </para>
    /// <para>
    /// This step converts the build/API5 probe into a document reader: `Document3D()` → `open(path)` →
    /// walk the feature tree via `GetFeature().SubFeatureCollection`, report every element's
    /// `ksObj3dTypeEnum` name, and for any `o3d_baseRotated`/`o3d_bossRotated`/`o3d_cutRotated` element
    /// read its definition back — `SetSketch`-side `GetSketch()`, `GetSideParam(true/false)`,
    /// `RotatedParam()` — so the parameters a <i>working</i> rotation actually carries are in the
    /// journal.
    /// </para>
    /// <para>
    /// The verdict is deliberately <b>not</b> a volume gate: the probe did not build this body and may
    /// not claim credit for it. The step passes on the narrower, honest claim it can support — that a
    /// shipped КОМПАС file contains a rotation feature and that the probe can read its parameters back.
    /// If no shipped model contains one, the step fails and says so, because then the claim is false.
    /// </para>
    /// </remarks>
    private void ShippedShaftRoute()
    {
        _currentStepId = "R.20";
        var step = _report.Begin("R.20", "Готовый файл из поставки КОМПАСа: что в дереве и что читается",
            "Собирается ли вращение вообще в этой установке — по файлу, который сделал сам КОМПАС?");

        // The first version of this step hard-coded THREE paths from one folder under `Samples\Shaft`,
        // found all three written by v18.1, and reported the route closed. That was a sampling claim
        // wearing a population claim's clothes: the installation ships 2434 `.m3d` files and 102 of
        // them are stamped `KOMPAS_24.0` — the very version under test. So the step now surveys the
        // whole installation by the container's own producer stamp and picks the files that this
        // installation is actually entitled to open.
        var inventory = InventoryShippedModels(step);
        var present = inventory
            .Where(m => m.Version == "KOMPAS_24.0")
            .Select(m => m.Path)
            .ToList();

        if (present.Count == 0)
        {
            step.Fail("ни одного файла, написанного KOMPAS_24.0, в поставке не оказалось — "
                + "утверждение «вращение собирается где-то» остаётся непроверенным");
            return;
        }

        // Prefer files whose names suggest a body of revolution; fall back to the rest. The order is
        // a guess about usefulness, NOT a claim about content — the journal reports what is actually
        // inside each file, so a wrong guess costs a line, not a conclusion.
        var rotationish = new[]
        {
            "вал", "ось", "shaft", "axis", "propeller", "винт", "ролик", "roller",
            "втулк", "bush", "кольц", "ring", "диск", "disc", "шпинд", "гильз", "стакан",
        };
        var ranked = present
            .OrderByDescending(p =>
            {
                var n = Path.GetFileName(p).ToLowerInvariant();
                return rotationish.Any(k => n.Contains(k)) ? 1 : 0;
            })
            .ToList();

        var probeLimit = Math.Min(ranked.Count, 12);
        step.Observe("инвентарь поставки: файлов .m3d всего " + inventory.Count
            + ", из них написаны KOMPAS_24.0 — " + present.Count
            + ", к разбору взято " + probeLimit
            + " (сначала имена, похожие на тело вращения)");
        step.Observe("версии файлов поставки: " + DescribeInventory(inventory));

        var report = new List<string>();
        var foundRotation = false;
        var rotationTypeName = "";
        var openedCount = 0;
        var rotatedReadbacks = 0;

        // CONTROL, before any claim about shipped files: save a document the probe itself built and
        // open it back through the same call. Without this, "Open() refused" is two facts wearing one
        // report — the file may be unopenable, or the *call* may not work this way. The control
        // decides which, and it must run in the same session through the same code path.
        var controlPath = Path.Combine(_options.WorkDir, "R20-control.m3d");
        var controlVerdict = "контроль не выполнен";
        {
            ksDocument3D? builder = null;
            ksDocument3D? replayer = null;
            try
            {
                builder = (ksDocument3D)_app.Document3D();
                builder.Create(true, true);
                if (builder.GetPart(-1) is ksPart controlPart
                    && Api5.BasePlate(controlPart, 60d, 40d, 5d, step, "R20") is not null)
                {
                    builder.RebuildDocument();
                    var saved = Api5.SafeBool(() => builder.SaveAs(controlPath));
                    var closed = Api5.SafeBool(() => builder.close());

                    // Two routes, because they differ and the difference is the whole point. Every
                    // other probe in this tool closes the document, calls Document3D() for a FRESH
                    // one, and opens immediately — it never calls Create on the reader first. R.20
                    // called Create(false,false) before Open; if that is what breaks the call, then
                    // the "shipped files do not open" reading was an artefact of the setup, and this
                    // control separates the two.
                    var withCreate = TryOpen(controlPath, createFirst: true, out var detailWith);
                    var withoutCreate = TryOpen(controlPath, createFirst: false, out var detailWithout);
                    controlVerdict = "SaveAs=" + Api5.Raw(saved) + ", close=" + Api5.Raw(closed)
                        + "; Open ПОСЛЕ Create(false,false)=" + Api5.Raw(withCreate) + " (" + detailWith + ")"
                        + "; Open БЕЗ Create=" + Api5.Raw(withoutCreate) + " (" + detailWithout + ")";
                    step.Observe("КОНТРОЛЬ: свой файл " + Path.GetFileName(controlPath) + " → " + controlVerdict);
                }
                else
                {
                    controlVerdict = "эталонную плиту построить не удалось";
                    step.Observe("КОНТРОЛЬ не удался: " + controlVerdict);
                }
            }
            catch (Exception ex)
            {
                controlVerdict = "бросил " + HResult.Describe(ex);
                step.Observe("КОНТРОЛЬ не удался: " + controlVerdict);
            }
            finally
            {
                if (replayer is not null)
                {
                    CloseFresh(replayer, step);
                }

                if (builder is not null)
                {
                    CloseFresh(builder, step);
                }
            }
        }
        var rotationFile = "";

        foreach (var path in ranked.Take(probeLimit))
        {
            ksDocument3D? reader = null;
            try
            {
                reader = (ksDocument3D)_app.Document3D();

                // NOTE: no `Create(false, false)` here. The control below measures that calling
                // Create on a document BEFORE Open makes the Open fail — on the probe's OWN file —
                // while the identical call without Create succeeds. So the `Create` first appears in
                // every other probe that reopens a document, and R.20's original version poisoned
                // every file it touched. Seventh probe defect, and the one that produced the whole
                // false trail: a setup call whose side effect was read as a property of the input.
                //
                // The member is `Open(String, Boolean)` — capital O, two arguments, read off the type
                // library rather than guessed: a lower-case `open()` does not exist and would not
                // compile (which is how this line was first written). The second argument is `true`,
                // the form every other probe in this tool uses.
                var opened = Api5.SafeBool(() => reader.Open(path, true));

                // The gate. When Open() fails, `reader` is still the empty document this loop just
                // created — and walking its tree would report the empty document's two service
                // features as if they were the model's. That is exactly what the first version of
                // this step did, and it is a probe defect, not a fact about КОМПАС: the reading it
                // produced (`o3d_entity(105), o3d_mateConstraintGroup(143)`, identical for all three
                // files) described the wrong object.
                if (opened != true)
                {
                    report.Add(Path.GetFileName(path) + ": НЕ ОТКРЫТ");
                    continue;
                }

                openedCount++;

                if (reader.GetPart(-1) is not ksPart part)
                {
                    report.Add(Path.GetFileName(path) + ": деталь не получена");
                    continue;
                }

                var features = Api5.ReadFeatures(part, step);
                var types = features
                    .Select(f => (f.TypeName ?? "<нет>") + "(" + (f.Type?.ToString() ?? "?") + ")")
                    .ToList();
                report.Add(Path.GetFileName(path) + ": признаков " + features.Count
                    + " → " + (types.Count == 0 ? "пусто" : string.Join(", ", types)));

                // The element classes are reported in R.0d, not here: `Api5.RuntimeName(element)` says
                // `System.__ComObject` for every element of every file, which is true and useless —
                // it picks between none of the possible failures. R.0d measured what it actually
                // means (a COM-callable wrapper whose `ksFeature` shows only Object members), so
                // repeating the class list per file here would add noise, not evidence.

                // `ksFeature.type` is 105 (`o3d_entity`) for EVERY feature family — a fact already
                // measured and written down in HoleProbe (проба N): a hole cannot be told from an
                // extrusion by that number. So looking for "o3d_baseRotated (27)" in `f.Type` is
                // looking for a number that never appears, and a step that does it will report "no
                // rotation" for every file it opens, forever. Detecting a rotation therefore needs a
                // route that carries the type: the feature's own NAME (КоМПАС names features
                // «Вращение:1», «Выдавливание:1», …), and the definition's runtime interface as an
                // independent second opinion.
                step.Observe(Path.GetFileName(path) + ": имена признаков → " + string.Join(", ",
                    features.Select(f => f.Name ?? "<нет>")));

                foreach (var f in features)
                {
                    if (f.Name is { } featureName && LooksLikeRotation(featureName))
                    {
                        foundRotation = true;
                        rotationTypeName = "по имени: " + featureName;
                        rotationFile = Path.GetFileName(path);
                        step.Observe("НАЙДЕНО ВРАЩЕНИЕ В ФАЙЛЕ ПОСТАВКИ (по имени): " + featureName
                            + ", признак #" + f.Index + ", файл " + rotationFile);
                    }
                }

                // Read every rotated definition back through the accessors the probe writes with.
                // This is the load-bearing half of the step: the name says a rotation EXISTS, and
                // only the readback says it is one the API can be asked about.
                //
                // The definitions come from the SAME pass that produced the names, by holding each
                // element in `FeatureReading.Entity`. Asking `part.GetFeature().SubFeatureCollection`
                // a second time does not work: the second call returns an EMPTY collection in the
                // same session, so a step that walks the tree twice finds all the names and no
                // definitions — and then reports the names as if they had been verified. That is
                // what the first version of this readback did (eighth probe defect).
                var seenTypes = new List<string>();
                step.Observe(Path.GetFileName(path) + ": к разбору определений " + features.Count
                    + " элементов, из них с живым ksEntity " + features.Count(f => f.Entity is not null)
                    + ", с живым ksFeature " + features.Count(f => f.Feature is not null));

                // The route the working probes actually use. `SketchLifecycleProbe.LastFeature` reaches
                // a real feature through `part.EntityCollection(o3d_operationElement = 110)`, and those
                // elements cast to `ksEntity` with their definitions intact. The tree walk
                // (`SubFeatureCollection`) and the feature set (`EntityCollection(110)`) are different
                // collections: the first gives names and order, the second gives handles. R.0d measured
                // the first on an empty part (0 of 2 castable, correctly nothing to enumerate); this is
                // the second, on a file that demonstrably contains a rotation — the comparison that
                // decides whether the definition was unavailable or merely unreached.
                if (part.EntityCollection(Api5.OperationElement) is ksEntityCollection operations)
                {
                    var opCount = operations.GetCount();
                    var opCastable = 0;
                    object? opDefinition = null;
                    string? opName = null;
                    for (var i = 0; i < opCount; i++)
                    {
                        if (operations.GetByIndex(i) is not ksEntity entity)
                        {
                            continue;
                        }

                        opCastable++;
                        if (opDefinition is null)
                        {
                            var read = Api5.SafeObject(() => entity.GetDefinition());
                            if (read is not null)
                            {
                                opDefinition = read;
                                opName = entity.name;
                            }
                        }
                    }

                    step.Observe(Path.GetFileName(path) + ": EntityCollection(" + Api5.OperationElement
                        + "): элементов " + opCount + ", приводится к ksEntity " + opCastable);
                    step.Observe(Path.GetFileName(path) + ": EntityCollection(110) определение — "
                        + (opDefinition is null
                            ? "ни один элемент не отдал определение"
                            : "«" + (opName ?? "?") + "» → " + Api5.RuntimeName(opDefinition)));

                    rotatedReadbacks += CountRotatedIn(operations, step, Path.GetFileName(path));
                }
                else
                {
                    step.Observe(Path.GetFileName(path) + ": EntityCollection(" + Api5.OperationElement
                        + ") недоступна");
                }

                foreach (var f in features)
                {
                    // Get an interface that can actually be asked for a definition.
                    //
                    // R.0d measured that on a live tree the two casts DISAGREE on the very same
                    // object (2 of 2 were ksFeature, 0 of 2 were ksEntity), and that a bare element
                    // transferred through `TransferInterface` answers `ksEntity: да`. `ksFeature`
                    // itself declares no `GetDefinition`, so the route is: keep whatever came back,
                    // and if it is only a `ksFeature`, hand it through `TransferInterface` to reach
                    // the entity that has the definition. Requiring `ksEntity` outright is how the
                    // first version of this loop found NO definition in a file that demonstrably
                    // contains a rotation (ninth probe defect: an impossible cast reported as an
                    // absent feature).
                    object? source = f.Entity
                        ?? (f.Feature is { } feat
                            ? Api5.SafeObject(() => _app.TransferInterface(feat, 1 /* ksAPI5Auto */, 0))
                            : null);
                    if (source is null)
                    {
                        seenTypes.Add((f.Name ?? "<нет>") + "→ ни ksEntity, ни ksFeature не дали объекта (класс «"
                            + (f.ClrType ?? "?") + "»)");
                        continue;
                    }

                    if (source is not ksEntity askable)
                    {
                        seenTypes.Add((f.Name ?? "<нет>") + "→ объект «" + Api5.RuntimeName(source)
                            + "» не приводится к ksEntity");
                        continue;
                    }

                    object? definition;
                    try
                    {
                        definition = askable.GetDefinition();
                    }
                    catch (Exception ex)
                    {
                        seenTypes.Add((f.Name ?? "<нет>") + "→ GetDefinition() бросил " + HResult.Describe(ex));
                        continue;
                    }

                    seenTypes.Add((f.Name ?? "<нет>") + "→"
                        + (definition is null ? "null" : Api5.RuntimeName(definition)));

                    if (definition is not ksBaseRotatedDefinition rotated)
                    {
                        continue;
                    }

                    step.Observe("  ОПРЕДЕЛЕНИЕ ВРАЩЕНИЯ в файле поставки: " + f.Name
                        + " (" + Api5.RuntimeName(rotated) + ")");

                    var sketchRead = "нет";
                    try
                    {
                        var sk = rotated.GetSketch();
                        sketchRead = sk is null ? "null" : Api5.RuntimeName(sk);
                    }
                    catch (Exception ex) { sketchRead = "бросил " + HResult.Describe(ex); }

                    var side1 = "не прочитано";
                    try { rotated.GetSideParam(true, out var a1); side1 = Api5.Num(a1); }
                    catch (Exception ex) { side1 = "бросил " + HResult.Describe(ex); }

                    var side2 = "не прочитано";
                    try { rotated.GetSideParam(false, out var a2); side2 = Api5.Num(a2); }
                    catch (Exception ex) { side2 = "бросил " + HResult.Describe(ex); }

                    var param = "нет";
                    try
                    {
                        var block = rotated.RotatedParam();
                        if (block is ksRotatedParam rp)
                        {
                            param = "angleNormal=" + Api5.Num(rp.angleNormal)
                                + ", angleReverse=" + Api5.Num(rp.angleReverse)
                                + ", direction=" + rp.direction;
                        }
                        else
                        {
                            param = block is null ? "null" : Api5.RuntimeName(block);
                        }
                    }
                    catch (Exception ex) { param = "бросил " + HResult.Describe(ex); }

                    rotatedReadbacks++;
                    step.Observe("  определение вращения #" + f.Index + " [" + f.Name + "]: GetSketch()="
                        + sketchRead + ", GetSideParam(true)=" + side1 + ", GetSideParam(false)=" + side2
                        + ", RotatedParam(): " + param);
                }

                step.Observe(Path.GetFileName(path) + ": определения элементов → "
                    + (seenTypes.Count == 0 ? "пусто" : string.Join(", ", seenTypes.Take(24))));
            }
            catch (Exception ex)
            {
                report.Add(Path.GetFileName(path) + ": бросил " + HResult.Describe(ex));
            }
            finally
            {
                if (reader is not null)
                {
                    CloseFresh(reader, step);
                }
            }
        }

        step.Observe("свод по файлам поставки: " + string.Join(" | ", report));

        if (foundRotation && rotatedReadbacks > 0)
        {
            step.Pass("в файлах поставки КОМПАСа есть вращение (" + rotationTypeName + ") — файл "
                + rotationFile + ": признак существует, и его определение читается обратно ТЕМ ЖЕ "
                + "маршрутом, что пишет проба (" + rotatedReadbacks + " определений вращения прочитано). "
                + "Значит вращение в этой установке собирается — вопрос лишь в том, каким вызовом "
                + "создаётся признак");
            return;
        }

        if (foundRotation)
        {
            // Named like a rotation, but no definition could be read back. Weaker than it looks, and
            // saying so is the point: a name is a label, not a measurement.
            //
            // The reason is now measured rather than guessed (R.0d): the collection's elements answer
            // `as ksFeature` and refuse `as ksEntity`, a `TransferInterface` hop still returns a
            // `__ComObject` that refuses `as ksEntity`, and the `ksFeature` they do answer exposes only
            // the six `System.Object` members — a COM-callable wrapper, on which managed reflection
            // measures the wrapper and not the product. So this step cannot reach a definition from a
            // shipped file's tree through the route it has, and it says exactly that instead of
            // reporting the name as if it were the evidence.
            //
            // SCOPE, added after R.22 (and the reason it had to be added): R.22 asked the same files a
            // DIFFERENT question — not "does a managed cast reach the definition" but "does the object
            // answer a QI for it" — and got a definition on 11 of 11 rotations with 77 parameters read
            // back. So the failure below is a fact about THIS route, not about the product; the wording
            // said "ИМЕННО ЭТИМ маршрутом" from the start, but a reader arriving at the summary alone
            // could take "НИ ОДНОГО определения... прочитать не удалось" for a claim about the files.
            // R.22 is named here so the two readings cannot be confused.
            step.Fail("в файлах поставки есть признаки с именем вращения (" + rotationTypeName + ", файл "
                + rotationFile + "), но НИ ОДНОГО определения вращения прочитать не удалось: элементы "
                + "дерева приводятся к ksFeature и НЕ приводятся к ksEntity (R.0d: 19 из 19 и 0 из 19 "
                + "на Propeller.m3d), а у отвечающего ksFeature объявлены только члены System.Object. "
                + "Значит имя признака доказательством не является, и «вращение собирается» остаётся "
                + "непроверенным ИМЕННО ЭТИМ маршрутом. ОГРАНИЧЕНИЕ ОБЛАСТИ: это утверждение о "
                + "маршруте через managed-приведение, а не о файлах — R.22 задал тем же файлам другой "
                + "вопрос (отвечает ли объект на QI) и получил определение на 11 вращениях из 11, "
                + "прочитав 77 параметров; маршрут чтения существует, не работает именно приведение");
            return;
        }

        if (openedCount == 0)
        {
            // The control decides what this refusal means, and it says it is NOT a fact about the
            // files: the probe cannot reopen even the document it wrote itself a moment earlier, in
            // this same session, with SaveAs confirmed. So the honest statement is the narrow one —
            // this probe's Open() route does not work here — and the question the step was written
            // for stays open. Claiming "the shipped files cannot be read" would be exactly the
            // mistake this step exists to avoid: a property of the instrument reported as a property
            // of the subject (defect class 5).
            step.Fail("ни один файл поставки, написанный KOMPAS_24.0, не открылся — но КОНТРОЛЬ "
                + "показывает, что это не свойство файлов: '" + controlVerdict + "', то есть зонд не "
                + "переоткрывает даже собственный файл, записанный в этом же сеансе. Значит маршрут "
                + "Open() у этой пробы здесь не работает, и вопрос «есть ли вращение в готовом файле "
                + "КОМПАСа» остаётся НЕИЗМЕРЕННЫМ");
            return;
        }

        step.Fail("ни в одном из " + openedCount + " открывшихся файлов поставки вращения не оказалось — "
            + "«вращение собирается где-то» остаётся непроверенным, блокер SM-03 не объяснён этой пробой");
    }

    /// <summary>
    /// Builds a feature of this probe's own making on <paramref name="part"/>, then asks for its
    /// definition through the <b>same</b> collection route R.20 uses on shipped files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control for the experiment that decides SM-03. R.20 measured two facts that cannot both be
    /// predicted from a single rule:
    /// </para>
    /// <list type="bullet">
    /// <item>a feature this probe creates — <c>NewEntity(27)</c> — gives up its
    /// <c>ksBaseRotatedDefinition</c> through <c>GetDefinition()</c>, and the probe writes with it
    /// (R.1, R.1b, and every R.2…R.19 step);</item>
    /// <item>a feature read out of a shipped <c>KOMPAS_24.0</c> file — the tree literally says
    /// «Операция вращения:2» — does <b>not</b>, through the tree walk or through
    /// <c>EntityCollection(110)</c>, on any of twelve files.</item>
    /// </list>
    /// <para>
    /// The cheap explanation is "the probe used the wrong collection". That explanation is testable
    /// in one measurement: build a feature here, read it through R.20's route, and see whether the
    /// route works when the author is this process. If it does, the collection is exonerated and the
    /// remaining difference is the features themselves. If it does not, the collection is the cause
    /// and R.20's route is simply wrong.
    /// </para>
    /// <para>
    /// An extrusion is built rather than a rotation on purpose: R.19 measured that no rotation
    /// actually forms on this part (the volume never rises), so a control built from a rotation would
    /// have no definition to report and would answer nothing. The extrusion is the feature this probe
    /// is measured to be able to author (<c>SketchLifecycleProbe</c>), so it is the right subject for
    /// a question about the collection rather than about rotation.
    /// </para>
    /// </remarks>
    private void BuildFreshFeatureForCastingControl(ksPart part, ProbeStep step)
    {
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sketch)
            {
                step.Observe("контроль свежего признака: плоскость или эскиз не создались");
                return;
            }

            if (sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                step.Observe("контроль свежего признака: определение эскиза не получено");
                return;
            }

            sketchDefinition.SetPlane(plane);

            // A 30×20 rectangle, drawn the way the working probes draw it, so the feature to be
            // measured is an ordinary one rather than a degenerate sketch the builder would refuse.
            var drawing = (ksDocument2D)sketchDefinition.BeginEdit();
            if (drawing is null)
            {
                step.Observe("контроль свежего признака: BeginEdit() не дал ksDocument2D");
                return;
            }

            drawing.ksLineSeg(-15d, -10d, 15d, -10d, 1);
            drawing.ksLineSeg(15d, -10d, 15d, 10d, 1);
            drawing.ksLineSeg(15d, 10d, -15d, 10d, 1);
            drawing.ksLineSeg(-15d, 10d, -15d, -10d, 1);
            sketchDefinition.EndEdit();

            if (part.NewEntity((short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossExtrusion) is not ksEntity operation
                || operation.GetDefinition() is not ksBossExtrusionDefinition extrusion)
            {
                step.Observe("контроль свежего признака: выдавливание не создалось");
                return;
            }

            extrusion.SetSketch(sketch);
            // The five-argument form, which is the one `SketchLifecycleProbe` uses on a live part:
            // (forward, endCondition, depth, angle, side). The seven-argument overload the rotation
            // definition has does not exist on an extrusion definition — measured by the compile
            // error, not assumed.
            extrusion.directionType = 0;
            extrusion.SetSideParam(true, 0, 10d, 0d, false);
            operation.name = "R0d-fresh-control";
            operation.Create();
            _doc.RebuildDocument();

            // ── the measurement: the fresh feature, through R.20's route ──
            if (part.EntityCollection(Api5.OperationElement) is not ksEntityCollection freshOperations)
            {
                step.Observe("контроль свежего признака: EntityCollection(" + Api5.OperationElement
                    + ") недоступна на детали со построенным признаком");
                return;
            }

            var total = freshOperations.GetCount();
            var castable = 0;
            var gaveDefinition = 0;
            var firstRead = "ни один элемент не отдал определение";
            for (var i = 0; i < total; i++)
            {
                if (freshOperations.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                castable++;
                var read = Api5.SafeObject(() => entity.GetDefinition());
                if (read is null)
                {
                    continue;
                }

                gaveDefinition++;
                if (gaveDefinition == 1)
                {
                    firstRead = "«" + (entity.name ?? "?") + "» → " + Api5.RuntimeName(read)
                        + " (приведение к ksBossExtrusionDefinition: "
                        + (read is ksBossExtrusionDefinition ? "да" : "НЕТ") + ")";
                }
            }

            // The comparison, stated as one line the reader can check against R.20's numbers. The same
            // collection, the same cast, the same question — different author.
            step.Observe("КОНТРОЛЬ СВЕЖЕГО ПРИЗНАКА через EntityCollection(" + Api5.OperationElement
                + ") (тот же маршрут, что R.20 на файлах поставки): элементов " + total
                + ", приводится к ksEntity " + castable + ", определение отдал " + gaveDefinition
                + " → " + firstRead);
            step.Observe("КОНТРОЛЬ СВЕЖЕГО ПРИЗНАКА: если определения отдаёт — значит маршрут "
                + "EntityCollection(" + Api5.OperationElement + ") рабочий, и разница между свежим "
                + "признаком и файлом поставки не в коллекции, а в самих признаках");

            // The count above is 0, and that is not "the control found nothing to measure" — it is the
            // measurement. A feature created in this document by this process is NOT in
            // EntityCollection(110). Shipped features ARE (4 of 4, 12 of 12, 1 of 1 across twelve
            // files). So the two collections are the difference, and it runs the OTHER way from the
            // guess that sent R.20 down this road: EntityCollection(110) holds features that were
            // READ, while features this probe WRITES are reachable as the live ksEntity returned by
            // NewEntity. That is why R.0b route 2 and R.1…R.19 reach definitions at all — they never
            // look a feature up, they keep the handle NewEntity handed them.
            //
            // Which means R.20's route is not a worse version of the working route; it is a different
            // route to a different kind of object, and no amount of casting through it reaches an
            // authored feature's definition. The line of work is closed, and closed by measurement.
            step.Observe("КОНТРОЛЬ СВЕЖЕГО ПРИЗНАКА — вывод: элементов " + total
                + ". Свежий признак в EntityCollection(" + Api5.OperationElement + ") НЕ попадает, "
                + "а признак из файла поставки туда попадает (R.20: 4 из 4, 12 из 12, 1 из 1). "
                + "Значит это разные наборы: свежий признак доступен как живой ksEntity от NewEntity, "
                + "прочитанный — только через EntityCollection(" + Api5.OperationElement + "), и "
                + "ни одно приведение внутри этой коллекции определения не открывает");
        }
        catch (Exception ex)
        {
            step.Observe("контроль свежего признака бросил " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Whether a feature's own name says it is a rotation.
    /// </summary>
    /// <remarks>
    /// Names rather than type numbers, because the number is useless here: <c>ksFeature.type</c> is
    /// <c>105</c> (<c>o3d_entity</c>) for every family — a hole, an extrusion and a rotation are
    /// indistinguishable by it (measured in проба N and written down in <c>HoleProbe</c>). The name
    /// is the only thing on the API5 side of a shipped feature that carries the kind. Both languages
    /// are checked because the installation and its samples are Russian, but the API may localise
    /// differently and the English form costs nothing.
    /// </remarks>
    private static bool LooksLikeRotation(string name) =>
        name.Contains("Вращ", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Rotat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Walks a feature-set collection and counts how many rotated definitions it hands back, reading
    /// each one through the accessors the probe writes with.
    /// </summary>
    /// <remarks>
    /// This is the second of the two collection routes, and the one that matters. The tree walk
    /// (<c>part.GetFeature().SubFeatureCollection(true, false)</c>) yields elements that answer
    /// <c>as ksFeature</c> and refuse <c>as ksEntity</c> — measured 19 of 19 and 0 of 19 on
    /// <c>Propeller.m3d</c> (<c>R.0d</c>) — so nothing reachable through it can be asked for a
    /// definition at all. The feature set (<c>part.EntityCollection(o3d_operationElement = 110)</c>)
    /// is what every <b>working</b> lifecycle probe in this tool uses
    /// (<c>SketchLifecycleProbe.LastFeature</c>), and its elements DO cast to <c>ksEntity</c>.
    ///
    /// <para>
    /// The count this returns is what the step's verdict hinges on. A name alone proves only that a
    /// feature is <i>called</i> «Операция вращения»; only a definition read back through
    /// <c>GetSketch</c>/<c>GetSideParam</c>/<c>RotatedParam</c> proves the API can be asked about it
    /// (ninth probe defect: a name reported as if verified). Returning 0 here is therefore a truthful
    /// "not measured", not a "absence" — and the step says exactly that.
    /// </para>
    /// </remarks>
    /// <param name="operations">The collection, already obtained as <c>ksEntityCollection</c>.</param>
    /// <param name="step">The current step, for observations.</param>
    /// <param name="fileName">File name used to prefix each observation.</param>
    /// <returns>How many elements gave back a <c>ksBaseRotatedDefinition</c>.</returns>
    private static int CountRotatedIn(ksEntityCollection operations, ProbeStep step, string fileName)
    {
        var rotated = 0;
        var castable = 0;
        var definitions = 0;
        var readNames = new List<string>();

        for (var i = 0; i < operations.GetCount(); i++)
        {
            // The cast is the gate, and it is the same gate that R.0d measures on the OTHER
            // collection. Here it is expected to hold; when it does not, the observation below says
            // so rather than letting the loop find nothing and report a false absence.
            if (operations.GetByIndex(i) is not ksEntity element)
            {
                continue;
            }

            castable++;

            object? definition;
            try
            {
                definition = element.GetDefinition();
            }
            catch (Exception ex)
            {
                step.Observe(fileName + ": EntityCollection(110) элемент #" + i + " («"
                    + (element.name ?? "?") + "») → GetDefinition() бросил " + HResult.Describe(ex));
                continue;
            }

            if (definition is null)
            {
                continue;
            }

            definitions++;

            if (definition is not ksBaseRotatedDefinition rotatedDefinition)
            {
                continue;
            }

            rotated++;
            var name = element.name ?? "?";
            readNames.Add(name);

            step.Observe("  ОПРЕДЕЛЕНИЕ ВРАЩЕНИЯ (EntityCollection(110)) " + fileName + ": " + name
                + " (" + Api5.RuntimeName(rotatedDefinition) + "), эскиз=" + DescribeSketch(rotatedDefinition)
                + ", торцы=" + DescribeSides(rotatedDefinition)
                + ", параметры: " + DescribeRotatedParam(rotatedDefinition));
        }

        step.Observe(fileName + ": EntityCollection(110): к разбору " + operations.GetCount()
            + " элементов, приводится к ksEntity " + castable + ", определение отдал " + definitions
            + ", из них вращений " + rotated
            + (readNames.Count == 0 ? "" : " → " + string.Join(", ", readNames)));

        return rotated;
    }

    /// <summary>The rotation's sketch as a readable string, or why it could not be read.</summary>
    private static string DescribeSketch(ksBaseRotatedDefinition definition)
    {
        try
        {
            var sketch = definition.GetSketch();
            return sketch is null ? "null" : Api5.RuntimeName(sketch);
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>Both side parameters as one string, each reported even if the other throws.</summary>
    private static string DescribeSides(ksBaseRotatedDefinition definition)
    {
        var parts = new List<string>(2);
        foreach (var forward in new[] { true, false })
        {
            var label = forward ? "GetSideParam(true)=" : "GetSideParam(false)=";
            try
            {
                definition.GetSideParam(forward, out var value);
                parts.Add(label + Api5.Num(value));
            }
            catch (Exception ex)
            {
                parts.Add(label + "бросил " + HResult.Describe(ex));
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>The <c>RotatedParam()</c> block read field by field, or its own runtime class.</summary>
    private static string DescribeRotatedParam(ksBaseRotatedDefinition definition)
    {
        try
        {
            var block = definition.RotatedParam();
            if (block is not ksRotatedParam rotatedParam)
            {
                return block is null ? "null" : Api5.RuntimeName(block);
            }

            return "angleNormal=" + Api5.Num(rotatedParam.angleNormal)
                + ", angleReverse=" + Api5.Num(rotatedParam.angleReverse)
                + ", direction=" + rotatedParam.direction;
        }
        catch (Exception ex)
        {
            return "бросил " + HResult.Describe(ex);
        }
    }

    /// <summary>One shipped <c>.m3d</c>: where it is, and which КОМПАС wrote it.</summary>
    private readonly record struct ShippedModel(string Path, string Version, string AppName);

    /// <summary>
    /// Opens <paramref name="path"/> on a fresh document, optionally calling <c>Create</c> first, and
    /// reports what happened.
    /// </summary>
    /// <remarks>
    /// Exists because two setup orders are in use in this tool and only one of them is measured to
    /// work: every probe that reopens a saved document calls <c>Document3D()</c> and then <c>Open</c>
    /// directly, while R.20 called <c>Create(false, false)</c> first. A probe that reports "this file
    /// will not open" must be able to say which of the two it did.
    /// </remarks>
    private bool? TryOpen(string path, bool createFirst, out string detail)
    {
        ksDocument3D? reader = null;
        try
        {
            reader = (ksDocument3D)_app.Document3D();
            if (createFirst)
            {
                reader.Create(false, false);
            }

            var opened = Api5.SafeBool(() => reader.Open(path, true));
            detail = createFirst ? "после Create" : "без Create";
            return opened;
        }
        catch (Exception ex)
        {
            detail = "бросил " + HResult.Describe(ex);
            return null;
        }
        finally
        {
            if (reader is not null)
            {
                try
                {
                    reader.close();
                }
                catch (Exception)
                {
                    // The verdict is the control's, not the cleanup's.
                }
            }
        }
    }

    /// <summary>
    /// Every <c>.m3d</c> under the installation, with the producing version read from each file's own
    /// container.
    /// </summary>
    /// <remarks>
    /// This exists because the first version of R.20 hard-coded three paths under one folder and
    /// concluded from them that no КОМПАС-written file on this machine can be opened. The
    /// installation ships thousands of <c>.m3d</c> files spanning v16.1 … v24.0, and the version
    /// stamp is the only honest way to choose among them. A probe must survey the population it is
    /// making a claim about, not a convenient sample of it — that mistake is defect class 5's cousin.
    /// </remarks>
    private List<ShippedModel> InventoryShippedModels(ProbeStep step)
    {
        var found = new List<ShippedModel>();
        var roots = new[]
        {
            Path.Combine(_kompasRoot, "Samples"),
            Path.Combine(_kompasRoot, "Tutorials"),
            Path.Combine(_kompasRoot, "Manual"),
            Path.Combine(_kompasRoot, "Libs"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.m3d", SearchOption.AllDirectories))
                {
                    var info = ReadM3dFileInfo(path);
                    var version = FieldOf(info, "AppVersion");
                    if (version.Length > 0)
                    {
                        found.Add(new ShippedModel(path, version, FieldOf(info, "AppName")));
                    }
                }
            }
            catch (Exception ex)
            {
                step.Observe("обход " + root + " прерван: " + ex.GetType().Name);
            }
        }

        return found;
    }

    /// <summary>The value of one <c>key=value</c> field in a <c>FileInfo</c> line, or <c>""</c>.</summary>
    private static string FieldOf(string fileInfo, string key)
    {
        foreach (var field in fileInfo.Split(';'))
        {
            var trimmed = field.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return trimmed[(key.Length + 1)..].Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>«KOMPAS_24.0: 102, KOMPAS_21.0: 1170, …» — the version census of the installation.</summary>
    private static string DescribeInventory(List<ShippedModel> inventory) =>
        inventory
            .GroupBy(m => m.Version)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key + ": " + g.Count())
            .DefaultIfEmpty("пусто")
            .Aggregate((a, b) => a + ", " + b);

    /// <summary>
    /// Builds a control extrusion in a fresh document and reports its <c>OperationResult</c>, so the
    /// value a <i>working</i> solid feature carries is a measurement rather than a recollection.
    /// Returns <c>null</c> if the extrusion itself could not be read, which is itself a fact.
    /// </summary>
    private Kompas6Constants3D.ksOperationResultEnum? ReadOperationResultOfControlExtrusion(ProbeStep step)
    {
        ksDocument3D? fresh = null;
        try
        {
            fresh = (ksDocument3D)_app.Document3D();
            fresh.Create(true, true);
        }
        catch (Exception ex)
        {
            step.Observe("контрольный документ для чтения OperationResult не создан: " + HResult.Describe(ex));
            return null;
        }

        try
        {
            if (fresh.GetPart(-1) is not ksPart part
                || part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                step.Observe("контрольный эскиз не построен — OperationResult контрольного выдавливания не прочитать");
                return null;
            }

            sketch.name = "R19-control-profile";
            sketchDefinition.SetPlane(plane);
            sketch.Create();
            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                step.Observe("BeginEdit() контрольного эскиза не дал ksDocument2D");
                return null;
            }

            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);
            sketchDefinition.EndEdit();

            if (part.NewEntity(Api5.BaseExtrusion) is not ksEntity extrusion
                || extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
            {
                step.Observe("контрольное выдавливание не создано");
                return null;
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            definition.SetSideParam(true, 0 /* etBlind */, 20d, 0d, false);
            var created = Api5.SafeBool(extrusion.Create) == true;
            part.RebuildModel();
            fresh.RebuildDocument();

            // The control object is asked the same question through the same type-object path the
            // rotation uses, so the two values are comparable.
            var object7 = _app.TransferInterface(extrusion, 2, 0);
            if (object7 is not KompasAPI7.IExtrusion1 extrusion7)
            {
                step.Observe("контрольное выдавливание Create()=" + created
                    + ", но QI(IExtrusion1) не отвечает — OperationResult не прочитать");
                return null;
            }

            var value = Api5.SafeEnum(() => extrusion7.OperationResult);
            step.Observe("КОНТРОЛЬ: рабочее выдавливание Create()=" + created
                + ", OperationResult=" + DescribeOperation(value));
            return value;
        }
        catch (Exception ex)
        {
            step.Observe("чтение OperationResult контрольного выдавливания бросило " + HResult.Describe(ex));
            return null;
        }
        finally
        {
            if (fresh is not null)
            {
                CloseFresh(fresh, step);
            }
        }
    }

    /// <summary>
    /// One rotation on a fresh single-sketch document with an explicit
    /// <c>OperationResult</c>, reporting <c>Create()</c>, ΔV and the value read back.
    /// </summary>
    /// <remarks>
    /// The volume is read from <c>fresh</c>'s own part, before and after, and reported as a delta:
    /// the first version of this rung read <c>_doc</c>'s body while building into <c>fresh</c> and
    /// printed <c>ΔV=null</c> for every operation — a probe defect that looked exactly like a
    /// measurement. ΔV is what the verdict uses.
    /// </remarks>
    private (bool Created, double? Volume, string Detail) TryRotationWithOperation(
        short type,
        ksOperationResultEnum operation,
        Kompas6Constants3D.ksOperationResultEnum? controlValue,
        ProbeStep step,
        string label)
    {
        ksDocument3D? fresh = null;
        try
        {
            fresh = (ksDocument3D)_app.Document3D();
            fresh.Create(true, true);
            if (fresh.GetPart(-1) is not ksPart part)
            {
                return (false, null, "деталь не получена");
            }

            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sketch
                || sketch.GetDefinition() is not ksSketchDefinition sketchDefinition)
            {
                return (false, null, "эскиз не создан");
            }

            sketch.name = "R19-" + label;
            sketchDefinition.SetPlane(plane);
            sketch.Create();
            if (sketchDefinition.BeginEdit() is not ksDocument2D editor)
            {
                return (false, null, "BeginEdit() не дал ksDocument2D");
            }

            editor.ksLineSeg(0d, -HeightMm / 2d, RadiusMm, -HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, -HeightMm / 2d, RadiusMm, HeightMm / 2d, 1);
            editor.ksLineSeg(RadiusMm, HeightMm / 2d, 0d, HeightMm / 2d, 1);
            editor.ksLineSeg(0d, HeightMm / 2d, 0d, -HeightMm / 2d, 1);
            editor.ksLineSeg(0d, -HeightMm / 2d, 0d, HeightMm / 2d, AxisStyle);
            sketchDefinition.EndEdit();
            part.RebuildModel();
            fresh.RebuildDocument();

            // Read from fresh's own part, before the feature: a delta, not an absolute.
            var before = Api5.Volume(part);

            if (part.NewEntity(type) is not ksEntity feature
                || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
            {
                return (false, null, "NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
            }

            definition.SetSketch(sketch);
            definition.directionType = 0;
            Api5.SafeBool(() => definition.SetSideParam(true, 360d));

            var object7 = _app.TransferInterface(feature, 2, 0);
            if (object7 is not KompasAPI7.IRotated rotated)
            {
                return (false, null, "признак не отвечает на QI(IRotated)");
            }

            var writeBack = "нет IRotated1";
            if (object7 is KompasAPI7.IRotated1 rotated1)
            {
                try { rotated1.OperationResult = operation; }
                catch (Exception ex) { writeBack = "запись бросила " + HResult.Describe(ex); }

                var got = Api5.SafeEnum(() => rotated1.OperationResult);
                writeBack = "OperationResult=" + DescribeOperation(got);
            }

            var created = Api5.SafeBool(feature.Create) == true;
            var isCreated = Api5.SafeBool(feature.IsCreated);
            Api5.SafeBool(feature.Update);

            // part is the part of `fresh`, not of _doc: the body has to be read from the part the
            // feature was built in, or the volume gate reads a different document's body (R.19's
            // first run reported ΔV=null for exactly that reason).
            part.RebuildModel();
            fresh.RebuildDocument();
            var after = Api5.Volume(part);
            var delta = after is not null && before is not null ? after - before : after;
            return (created, delta,
                writeBack + ", IsCreated()=" + Api5.Raw(isCreated)
                + ", " + DescribeBodyChain(part)
                + ", V(до)=" + Api5.Num(before) + ", V(после)=" + Api5.Num(after)
                + ", ΔV=" + Api5.Num(delta)
                + (controlValue is null ? "" : " [контроль=" + (int)controlValue + "]"));
        }
        catch (Exception ex)
        {
            return (false, null, HResult.Describe(ex));
        }
        finally
        {
            if (fresh is not null)
            {
                CloseFresh(fresh, step);
            }
        }
    }

    /// <summary>A named <c>OperationResult</c>, including "nothing" — the enum's zero is a real value.</summary>
    private static string DescribeOperation(Kompas6Constants3D.ksOperationResultEnum? value) =>
        value is null ? "null (не прочитано)" : value.Value + " (" + (int)value.Value + ")";

    /// <summary>
    /// What the <c>FileInfo</c> stream of a <c>.m3d</c> says about the file itself, read straight off
    /// disk <b>without</b> opening it through the API.
    /// </summary>
    /// <remarks>
    /// A <c>.m3d</c> is a ZIP container whose first entry is a little UTF-16 text block. Reading it
    /// answers a question no API call can: which КОМПАС <i>wrote</i> this file. That distinction is
    /// the whole reason this helper exists — a file written by another major version fails
    /// <c>Open()</c> for a reason that has nothing to do with geometry, and a probe that skips this
    /// check will blame the API for a version mismatch.
    /// </remarks>
    private static string ReadM3dFileInfo(string path)
    {
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                // The entry is named `FileInfo`, but be generous: any small text entry will do.
                if (!entry.Name.Contains("FileInfo", StringComparison.OrdinalIgnoreCase)
                    || entry.Length == 0 || entry.Length > 8192)
                {
                    continue;
                }

                using var stream = entry.Open();
                var buffer = new byte[entry.Length];
                var read = 0;
                while (read < buffer.Length)
                {
                    var chunk = stream.Read(buffer, read, buffer.Length - read);
                    if (chunk <= 0)
                    {
                        break;
                    }

                    read += chunk;
                }

                // The stream is UTF-16 with a byte-order mark; in the files КОМПАС ships it carries
                // the big-endian mark FE FF, so `Encoding.Unicode` (which is little-endian) decodes it
                // into CJK-looking mush and finds no fields at all. Read the mark and pick the
                // matching endianness instead of assuming the platform's.
                System.Text.Encoding encoding;
                var offset = 0;
                if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
                {
                    encoding = System.Text.Encoding.BigEndianUnicode;
                    offset = 2;
                }
                else if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
                {
                    encoding = System.Text.Encoding.Unicode;
                    offset = 2;
                }
                else
                {
                    encoding = System.Text.Encoding.Unicode;
                }

                var text = encoding.GetString(buffer, offset, read - offset);
                var fields = new List<string>();
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim('\uFEFF', '\r', '\n', ' ', '\0');
                    if (!trimmed.Contains('=') || trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Only the fields that identify the producer, so the line stays readable.
                    var key = trimmed[..trimmed.IndexOf('=')];
                    if (key is "AppName" or "AppVersion" or "BuildNum" or "AppPlatform" or "FileType" or "CreateData")
                    {
                        fields.Add(trimmed);
                    }
                }

                return fields.Count == 0
                    ? "FileInfo есть, но опознавательных полей нет"
                    : string.Join("; ", fields);
            }

            return "потока FileInfo в контейнере нет";
        }
        catch (Exception ex)
        {
            return "контейнер не читается: " + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Reports the body chain of a part — <c>GetMainBody()</c>, <c>BodyCollection().GetCount()</c>,
    /// <c>ksBody.IsSolid()</c> — so a <c>null</c> volume can be attributed to a missing body rather
    /// than to a wrong document.
    /// </summary>
    private static string DescribeBodyChain(ksPart part)
    {
        var chain = new List<string>();
        object? body = null;
        try { body = part.GetMainBody(); }
        catch (Exception) { chain.Add("GetMainBody() бросил"); }

        chain.Add("GetMainBody()=" + (body is null ? "null" : Api5.RuntimeName(body)));
        chain.Add("тел=" + Api5.BodyCount(part));

        if (body is ksBody ksbody)
        {
            chain.Add("IsSolid()=" + Api5.Raw(Api5.SafeBool(ksbody.IsSolid)));
        }

        return string.Join(", ", chain);
    }

    /// <summary>
    /// A rotation in a <b>fresh document</b> that contains nothing else, with both sides of the sweep
    /// configured, and the parameter block written before it is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R.17 left one fact standing: over a region proved good by an extrusion control, with a
    /// committed axis and <c>Axis=сохранено</c>, <c>IRotated.Create()</c> is still
    /// <c>False</c> — and also <c>False</c> with no axis object at all. Everything about the input
    /// has been measured; what has never been varied is the <i>document context</i> the rotation is
    /// built in. Every rotation this probe has attempted shares one document with a dozen plates and
    /// a dozen earlier features, and every one of those attempts reused a sketch that had already
    /// been consumed.
    /// </para>
    /// <para>
    /// <b>Rung (1)</b> builds a new document, one contour sketch, one rotation. Nothing else. If a
    /// rotation can be built at all, this is the shape in which it should be — and if it still
    /// refuses, the failure is not context, not profile and not axis, and the remaining explanation
    /// is that <c>Create()</c> is not the call that commits this feature.
    /// </para>
    /// <para>
    /// <b>Rung (2)</b> varies the two things about the call itself that have never been varied:
    /// <c>SetSideParam</c> called on <b>both</b> sides (the signature takes one <c>Boolean side1</c>
    /// per call, and only <c>true</c> has ever been passed), and the parameter block written
    /// <i>before</i> it is read rather than after — <c>RotatedParam()</c> returns a live block, and a
    /// write to a block that was never handed back to the definition is a write into a copy.
    /// </para>
    /// <para>
    /// The verdict is three-way as everywhere else, and the volume gate is the same: a document with
    /// one sketch and one rotation has exactly one body, so the expected volume is π·r²·h with no
    /// plate to subtract.
    /// </para>
    /// </remarks>
    private void FreshDocumentRoute()
    {
        _currentStepId = "R.18";
        var step = _report.Begin("R.18", "Вращение в чистом документе и обе стороны развёртки",
            "Отказывает ли Create() из-за контекста документа, а не из-за профиля или оси?");

        // ── rung (1): a document with one sketch and one rotation ───────────────────────────────
        ksDocument3D? fresh = null;
        try
        {
            fresh = (ksDocument3D)_app.Document3D();
            fresh.Create(true, true);
            step.Observe("создан отдельный невидимый документ");
        }
        catch (Exception ex)
        {
            step.Fail("отдельный документ не создан: " + HResult.Describe(ex));
            return;
        }

        ksPart part;
        try
        {
            part = (ksPart)fresh.GetPart(-1);
        }
        catch (Exception ex)
        {
            step.Fail("деталь в отдельном документе недоступна: " + HResult.Describe(ex));
            return;
        }

        // The sketch is built here rather than through RectangleSketch, which writes into _doc: the
        // whole point of this rung is that the rotation does not share a document with anything else.
        ksEntity sketch;
        try
        {
            if (part.GetDefaultEntity(Api5.PlaneXoy) is not ksEntity plane
                || part.NewEntity(Api5.Sketch) is not ksEntity sk
                || sk.GetDefinition() is not ksSketchDefinition skDef)
            {
                step.Fail("эскиз в отдельном документе не создан");
                return;
            }

            sk.name = "R18-profile";
            skDef.SetPlane(plane);
            sk.Create();

            if (skDef.BeginEdit() is not ksDocument2D editor)
            {
                step.Fail("BeginEdit() не дал ksDocument2D");
                return;
            }

            var u0 = 0d;
            var u1 = RadiusMm;
            var v0 = -HeightMm / 2d;
            var v1 = HeightMm / 2d;
            editor.ksLineSeg(u0, v0, u1, v0, 1);
            editor.ksLineSeg(u1, v0, u1, v1, 1);
            editor.ksLineSeg(u1, v1, u0, v1, 1);
            editor.ksLineSeg(u0, v1, u0, v0, 1);
            var axisRef = editor.ksLineSeg(0d, v0, 0d, v1, AxisStyle);
            step.Observe("эскиз: прямоугольник " + Api5.Num(RadiusMm) + "×" + Api5.Num(HeightMm)
                + " от оси + осевой отрезок стилем " + AxisStyle + " (ref=" + axisRef + ")");
            skDef.EndEdit();
            part.RebuildModel();
            fresh.RebuildDocument();
            sketch = sk;
        }
        catch (Exception ex)
        {
            step.Fail("построение эскиза в отдельном документе бросило " + HResult.Describe(ex));
            return;
        }

        var before = Api5.Volume(part);
        step.Observe("объём до вращения: " + Api5.Num(before) + " (в чистом документе тела ещё нет)");

        if (_rotatedType is not { } type)
        {
            step.Fail("тип вращения не найден");
            return;
        }

        // ── rung (2): both sides, and the parameter block written before it is read ─────────────
        var variants = new (string Label, bool BothSides, bool ParamFirst)[]
        {
            ("одна сторона, параметры как раньше", false, false),
            ("обе стороны", true, false),
            ("обе стороны, параметры записаны первыми", true, true),
            ("одна сторона, параметры записаны первыми", false, true),
        };

        var outcomes = new List<string>();
        foreach (var (label, bothSides, paramFirst) in variants)
        {
            string detail;
            try
            {
                if (part.NewEntity(type) is not ksEntity feature
                    || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
                {
                    detail = "NewEntity(" + type + ") не дал ksBaseRotatedDefinition";
                }
                else
                {
                    definition.SetSketch(sketch);
                    definition.directionType = 0;

                    // (2a) the parameter block, written BEFORE anything else touches the definition:
                    // RotatedParam() hands back a live block, and a write to a block that was never
                    // given back is a write into a copy. Both orders are tried so the order is a
                    // measurement rather than an assumption.
                    if (paramFirst && definition.RotatedParam() is ksRotatedParam param)
                    {
                        param.angleNormal = 360d;
                        param.angleReverse = 0d;
                        param.direction = 0;
                    }

                    // (2b) the sides. The signature takes one Boolean side1 per call; only true has
                    // ever been passed, and a rotation whose reverse side is left undefined is a
                    // different request from one whose reverse side is zero.
                    var side1 = Api5.SafeBool(() => definition.SetSideParam(true, 360d));
                    var side2 = bothSides
                        ? Api5.SafeBool(() => definition.SetSideParam(false, 0d))
                        : (bool?)null;

                    double? read1 = null;
                    double? read2 = null;
                    try { definition.GetSideParam(true, out var a1); read1 = a1; }
                    catch (Exception ex) { step.Observe("  GetSideParam(true) бросил " + HResult.Describe(ex)); }
                    if (bothSides)
                    {
                        try { definition.GetSideParam(false, out var a2); read2 = a2; }
                        catch (Exception ex) { step.Observe("  GetSideParam(false) бросил " + HResult.Describe(ex)); }
                    }

                    var created = Api5.SafeBool(feature.Create) == true;
                    var isCreated = Api5.SafeBool(feature.IsCreated);

                    if (Api5.SafeBool(feature.Update) == true)
                    {
                        part.RebuildModel();
                        fresh.RebuildDocument();
                    }

                    detail = "SetSideParam(true,360)=" + Api5.Raw(side1)
                        + ", SetSideParam(false,0)=" + (side2 is null ? "не вызывался" : Api5.Raw(side2))
                        + ", GetSideParam(true)=" + Api5.Num(read1)
                        + ", GetSideParam(false)=" + (bothSides ? Api5.Num(read2) : "не читался")
                        + ", Create()=" + created + ", IsCreated()=" + Api5.Raw(isCreated);
                }
            }
            catch (Exception ex)
            {
                detail = HResult.Describe(ex);
            }

            var now = Api5.Volume(part);
            outcomes.Add(label + " → " + detail + ", V=" + Api5.Num(now));
            step.Observe("  " + label + ": " + detail + ", V=" + Api5.Num(now));
        }

        var volume = Api5.Volume(part);
        step.Observe("V в чистом документе: " + Api5.Num(volume) + ", ожидание π·r²·h = "
            + Api5.Num(FullTurnVolume));

        if (volume is not null && Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("вращение построено в чистом документе: V=π·r²·h — блокер SM-03 снят");
            CloseFresh(fresh, step);
            return;
        }

        step.Fail("в чистом документе вращение тоже не построено (V=" + Api5.Num(volume)
            + ", ожидание " + Api5.Num(FullTurnVolume) + "): ни контекст документа, ни число сторон, "
            + "ни порядок записи параметров не объясняют отказа — блокер SM-03 остаётся открытым");
        CloseFresh(fresh, step);
    }

    /// <summary>Closes the document R.18 opened, whether or not the rung succeeded.</summary>
    private static void CloseFresh(ksDocument3D fresh, ProbeStep step)
    {
        try
        {
            fresh.close();
            step.Observe("отдельный документ закрыт");
        }
        catch (Exception ex)
        {
            step.Observe("отдельный документ не закрылся: " + HResult.Describe(ex));
        }
    }

    /// <summary>
    /// A rotation whose profile is the given sketch, optionally with an API7 axis object offered as
    /// well. Reports <c>Create()</c> and what <c>Axis</c> did with the object.
    /// </summary>
    private (bool Created, string Detail) TryRotationOverSketch(
        ksPart part, ksEntity sketch, KompasAPI7.IAxis3D? axis, ProbeStep step, string name)
    {
        if (_rotatedType is not { } type)
        {
            return (false, "тип вращения не найден");
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return (false, "NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        Api5.SafeBool(() => definition.SetSideParam(true, 360d));

        var object7 = _app.TransferInterface(feature, 2, 0) as KompasAPI7.IModelObject;
        if (object7 is not KompasAPI7.IRotated rotated)
        {
            return (false, "признак не отвечает на QI(IRotated)");
        }

        try
        {
            var axisHeld = "не подавалась";
            if (axis is not null)
            {
                rotated.Axis = axis;
                axisHeld = rotated.Axis is null ? "null (отброшено)" : "сохранено";
                step.Observe("  " + name + ": ось подана → Axis " + axisHeld);
            }

            var created = Api5.SafeBool(feature.Create) == true;
            var isCreated = Api5.SafeBool(feature.IsCreated);
            var updated = Api5.SafeBool(feature.Update);

            // RotatedParam() is the definition's own parameter block. Reading it back after Create()
            // separates "the feature exists but is empty" from "the feature was never made": an
            // IsCreated()=False with a valid parameter block is a refusal, not an absence.
            var paramRead = "нет";
            try
            {
                var block = definition.RotatedParam();
                paramRead = block is null ? "null" : "есть (" + Api5.RuntimeName(block) + ")";
            }
            catch (Exception ex)
            {
                paramRead = "бросил " + HResult.Describe(ex);
            }

            part.RebuildModel();
            _doc.RebuildDocument();
            return (created, "Axis=" + axisHeld + ", IsCreated()=" + Api5.Raw(isCreated)
                + ", Update()=" + Api5.Raw(updated) + ", RotatedParam()=" + paramRead);
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Walks every edge of the main body and reports what it is, returning the first <b>straight and
    /// valid</b> one. <see cref="TryAnyEdge"/> returns the first edge of any kind; this is the
    /// measured version of the same walk.
    /// </summary>
    /// <remarks>
    /// The predicates come from the type library, not from guessing: <c>ksEdgeDefinition</c> declares
    /// <c>IsStraight</c>, <c>IsLineSeg</c>, <c>IsArc</c>, <c>IsCircle</c>, <c>IsEllipse</c>,
    /// <c>IsEllipseArc</c>, <c>IsNurbs</c>, <c>IsPlanar</c>, <c>IsPeriodic</c>, <c>IsValid</c>.
    /// Every read is wrapped, because a predicate that throws on some edge kinds is itself a fact
    /// worth having in the journal rather than a reason for the step to die.
    /// </remarks>
    private object? CharacteriseEdges(ksPart part, ProbeStep step)
    {
        object? chosen = null;
        var chosenIndex = -1;

        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("главного тела нет — рёбра не перебрать");
            return null;
        }

        var faceCount = faces.GetCount();
        var total = 0;
        var lines = new List<string>();

        for (var f = 0; f < faceCount && total < 60; f++)
        {
            if (faces.GetByIndex(f) is not ksFaceDefinition face
                || face.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            var edgeCount = edges.GetCount();
            for (var i = 0; i < edgeCount && total < 60; i++)
            {
                if (edges.GetByIndex(i) is not ksEdgeDefinition edge)
                {
                    continue;
                }

                total++;

                string Kind(ksEdgeDefinition e) => string.Join("+", new[]
                {
                    Pred(e, x => x.IsStraight()) ? "прямое" : null,
                    Pred(e, x => x.IsLineSeg()) ? "отрезок" : null,
                    Pred(e, x => x.IsArc()) ? "дуга" : null,
                    Pred(e, x => x.IsCircle()) ? "окружность" : null,
                    Pred(e, x => x.IsEllipse()) ? "эллипс" : null,
                    Pred(e, x => x.IsEllipseArc()) ? "дуга эллипса" : null,
                    Pred(e, x => x.IsNurbs()) ? "nurbs" : null,
                }.Where(x => x is not null));

                var kind = Kind(edge);
                var valid = Api5.SafeBool(edge.IsValid)?.ToString() ?? "null";
                var planar = Api5.SafeBool(edge.IsPlanar)?.ToString() ?? "null";
                var straight = Api5.SafeBool(edge.IsStraight);
                var length = Api5.SafeDouble(() => (double)edge.GetLength(0));
                var curve = "нет";
                try
                {
                    curve = edge.GetCurve3D() is null ? "null" : "есть";
                }
                catch (Exception ex)
                {
                    curve = "бросил " + HResult.Describe(ex);
                }

                lines.Add("ребро " + total + " (грань " + f + "): вид=[" + (kind.Length == 0 ? "не опознан" : kind)
                    + "], IsValid=" + valid + ", IsPlanar=" + planar
                    + ", длина=" + (length is null ? "?" : Api5.Num(length.Value))
                    + ", кривая3D=" + curve);

                if (straight == true && chosen is null)
                {
                    chosen = edge;
                    chosenIndex = total;
                }
            }
        }

        step.Observe("рёбер осмотрено: " + total
            + (faceCount > 0 ? " (граней " + faceCount + ")" : string.Empty));
        foreach (var line in lines.Take(20))
        {
            step.Observe("  " + line);
        }

        if (lines.Count > 20)
        {
            step.Observe("  … ещё " + (lines.Count - 20) + " строк опущено");
        }

        step.Observe(chosen is null
            ? "прямого ребра не найдено — ни одно ребро не ответило IsStraight()=True"
            : "выбрано ребро №" + chosenIndex + " как прямое и валидное");

        return chosen;
    }

    /// <summary>
    /// Runs one <c>Is*</c> predicate. A predicate that throws on a given edge kind is reported as
    /// "not this kind" rather than being allowed to kill the walk: the point of the rung is to see
    /// all edges, and an edge whose predicate throws would otherwise hide every edge after it.
    /// </summary>
    private static bool Pred(ksEdgeDefinition edge, Func<ksEdgeDefinition, bool> call)
    {
        try
        {
            return call(edge);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Authors one 3D point at (x, y, z) through <c>IPoints3D.Add()</c> and commits it, returning it
    /// as an <c>IModelObject</c> or null with the reason in the journal.
    /// </summary>
    private KompasAPI7.IModelObject? AuthorPoint(
        KompasAPI7.IPoints3D points, double x, double y, double z, string name, ProbeStep step)
    {
        try
        {
            var point = points.Add();
            if (point is null)
            {
                step.Observe("  " + name + ": Add() → null");
                return null;
            }

            point.X = x;
            point.Y = y;
            point.Z = z;
            point.Name = name;

            var updated = Api5.SafeBool(point.Update);
            step.Observe("  " + name + " (" + Api5.Num(x) + ", " + Api5.Num(y) + ", " + Api5.Num(z)
                + "): X=" + Api5.Num(Api5.SafeDouble(() => point.X))
                + ", Y=" + Api5.Num(Api5.SafeDouble(() => point.Y))
                + ", Z=" + Api5.Num(Api5.SafeDouble(() => point.Z))
                + ", Update() → " + Api5.Raw(updated)
                + ", Valid=" + Api5.Raw(Api5.SafeBool(() => point.Valid))
                + ", Reference=" + (Api5.SafeInt(() => point.Reference)?.ToString(CultureInfo.InvariantCulture) ?? "null"));

            if (updated != true)
            {
                return null;
            }

            return _app.TransferInterface(point, 2, 0) as KompasAPI7.IModelObject;
        }
        catch (Exception ex)
        {
            step.Observe("  " + name + ": бросило " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// Builds an edge axis and commits it, so the rotation is offered an axis that is a real tree
    /// object (the R.13 precondition) rather than one whose <c>Update()</c> has not run.
    /// </summary>
    private KompasAPI7.IAxis3D? MakeEdgeAxis(
        KompasAPI7.IAxes3D axes, object edge, ProbeStep step)
    {
        try
        {
            var axis = axes.Add(Kompas6Constants3D.ksObj3dTypeEnum.o3d_axisEdge);
            if (axis is not KompasAPI7.IAxis3DByEdge byEdge)
            {
                step.Observe("ось по прямому ребру: Add → " + (axis is null ? "null" : "не IAxis3DByEdge"));
                return null;
            }

            byEdge.Edge = _app.TransferInterface(edge, 2, 0) as KompasAPI7.IModelObject;
            var updated = Api5.SafeBool(axis.Update);
            step.Observe("ось по прямому ребру: Edge="
                + (byEdge.Edge is null ? "null (отброшено)" : "удержано")
                + ", Update() → " + Api5.Raw(updated)
                + ", Valid=" + Api5.Raw(Api5.SafeBool(() => axis.Valid))
                + ", Name=" + SafeName(axis));

            return updated == true ? axis : null;
        }
        catch (Exception ex)
        {
            step.Observe("ось по прямому ребру бросила " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// One attempt at a rotation with a given <c>RotatedType</c> and a given write order, reported as
    /// text. Every number it reads is put in the journal, so a rung that fails says why.
    /// </summary>
    /// <remarks>
    /// The write order is a parameter rather than a constant because it has never been varied:
    /// R.7 set <c>Profile</c> then <c>Axis</c>, and an axis discarded against a set profile is
    /// consistent with a member that validates the pair only in one direction.
    /// </remarks>
    private string OneRotatedTypeAttempt(
        ksPart part, Kompas6Constants3D.ksRotatedTypeEnum rotType, string label, bool profileFirst,
        KompasAPI7.IAxis3D? axis, ProbeStep step, string name)
    {
        if (_rotatedType is not { } type)
        {
            return "тип вращения не найден";
        }

        ksEntity sketch;
        try
        {
            (_, sketch) = RectangleSketch(name, withAxis: false);
        }
        catch (Exception ex)
        {
            return "профиль не построен: " + ex.Message;
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return "NewEntity(" + type + ") не дал ksBaseRotatedDefinition";
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        Api5.SafeBool(() => definition.SetSideParam(true, 360d));

        if (_app.TransferInterface(feature, 2, 0) is not KompasAPI7.IModelObject object7
            || object7 is not KompasAPI7.IRotated rotated)
        {
            return "признак не отвечает на QI(IRotated)";
        }

        try
        {
            // (1) RotatedType, read before and after. A write that does not read back is a different
            // finding from a member that refuses the value.
            // RotatedType, Angle, AngleObject and CutOffByPoint are INDEXED properties on IRotated:
            // each takes a Boolean Normal argument selecting the normal (true) or reverse (false)
            // side of the sweep. Axis and Profile are plain. No earlier step in this probe used the
            // indexed form, which is exactly why RotatedType and AngleObject had never been written.
            string typeBefore;
            try
            {
                typeBefore = rotated.RotatedType[true].ToString();
            }
            catch (Exception ex)
            {
                typeBefore = "чтение бросило " + HResult.Describe(ex);
            }

            string typeAfter;
            try
            {
                rotated.RotatedType[true] = rotType;
                typeAfter = rotated.RotatedType[true].ToString();
            }
            catch (Exception ex)
            {
                typeAfter = "запись бросила " + HResult.Describe(ex);
            }

            step.Observe("  " + label + ": RotatedType[true] до=" + typeBefore + ", после=" + typeAfter);

            var profile7 = _app.TransferInterface(sketch, 2, 0) as KompasAPI7.IModelObject;

            // (2) The write order, which is the variable this rung exists for.
            if (profileFirst)
            {
                if (profile7 is not null)
                {
                    rotated.Profile = profile7;
                }

                if (axis is not null)
                {
                    rotated.Axis = axis;
                }
            }
            else
            {
                if (axis is not null)
                {
                    rotated.Axis = axis;
                }

                if (profile7 is not null)
                {
                    rotated.Profile = profile7;
                }
            }

            var axisHeld = rotated.Axis is null ? "null (отброшено)" : "сохранено";
            var profileHeld = rotated.Profile is null ? "null" : "сохранён";
            step.Observe("  " + name + ": Axis «" + axisHeld + "», Profile «" + profileHeld + "»");

            // (4) Angle, and AngleObject for the kinds that need one. AngleObject is set to the same
            // axis when the kind is not the plain-angle one, because that is what the vertex and
            // surface kinds are for: an object that stops the sweep.
            try
            {
                rotated.Angle[true] = 360d;
                var angleBack = Api5.Num(Api5.SafeDouble(() => rotated.Angle[true]));
                if (axis is not null && rotType != Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle)
                {
                    rotated.AngleObject[true] = axis;
                }

                var angleObjectBack = "не подавался";
                if (axis is not null && rotType != Kompas6Constants3D.ksRotatedTypeEnum.ksRTAngle)
                {
                    angleObjectBack = rotated.AngleObject[true] is null ? "null (отброшено)" : "сохранён";
                }

                step.Observe("  " + name + ": Angle[true]=" + angleBack
                    + "; AngleObject[true]=" + angleObjectBack
                    + " (для " + rotType + ")");
            }
            catch (Exception ex)
            {
                step.Observe("  " + name + ": Angle/AngleObject бросили " + HResult.Describe(ex));
            }

            var created = Api5.SafeBool(feature.Create) == true;
            part.RebuildModel();
            _doc.RebuildDocument();
            return "Create()=" + created + ", Axis=" + axisHeld;
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>
    /// Attaches whatever source a concrete axis class offers, and returns what happened as text.
    /// </summary>
    /// <remarks>
    /// The cast is the measurement: asking for a specific axis class is how the step learns which
    /// class <c>Add</c> actually returned. A miss is reported, not thrown.
    /// </remarks>
    private string AttachAxisSource(
        KompasAPI7.IAxis3D axis, KompasAPI7.IModelObject? edge, KompasAPI7.IModelObject? plane,
        KompasAPI7.IModelObject? cylinder, ProbeStep step)
    {
        if (axis is KompasAPI7.IAxis3DByEdge byEdge)
        {
            if (edge is null)
            {
                return "IAxis3DByEdge, но ребра нет";
            }

            byEdge.Edge = edge;
            return "IAxis3DByEdge.Edge=" + (byEdge.Edge is null ? "null (отброшено)" : "удержано");
        }

        if (axis is KompasAPI7.IAxis3DByOperation byOperation)
        {
            var part = (ksPart)_doc.GetPart(-1);
            var operation = Transfer(part, FirstOperation(part, out var detail));
            if (operation is null)
            {
                return "IAxis3DByOperation, но операции нет (" + detail + ")";
            }

            byOperation.Operation = operation;
            return "IAxis3DByOperation.Operation="
                + (byOperation.Operation is null ? "null (отброшено)" : "удержано");
        }

        if (axis is KompasAPI7.IAxis3DByConeface byConeface)
        {
            if (cylinder is null)
            {
                return "IAxis3DByConeface, но конической/цилиндрической грани нет";
            }

            byConeface.Face = cylinder;
            return "IAxis3DByConeface.Face="
                + (byConeface.Face is null ? "null (отброшено)" : "удержано");
        }

        if (axis is KompasAPI7.IAxis3DBy2Planes by2Planes)
        {
            if (plane is null)
            {
                return "IAxis3DBy2Planes, но плоскостей нет";
            }

            by2Planes.Plane1 = plane;
            return "IAxis3DBy2Planes.Plane1=" + (by2Planes.Plane1 is null ? "null (отброшено)" : "удержано")
                + " (Plane2 не задан)";
        }

        if (axis is KompasAPI7.IAxis3DBy2Points)
        {
            // No point source is built by this probe yet, and inventing one would be a guess. Said
            // plainly rather than left to look like a silent success.
            return "IAxis3DBy2Points: точек в пробе нет, источник не задан";
        }

        if (axis is KompasAPI7.IAxisLine3D)
        {
            return "IAxisLine3D: Object1/Object2 не заданы (" + Api5.RuntimeName(axis) + ")";
        }

        return "источник не применим к " + Api5.RuntimeName(axis);
    }

    /// <summary>
    /// The live first operation of the main part, reached through API7. Used as the source for
    /// <c>IAxis3DByOperation</c>.
    /// </summary>
    // ReSharper disable once UnusedMember.Local -- kept as the documented seam for the operation source.
    private static KompasAPI7.IModelObject? FirstOperationFromContainer(ProbeStep step)
    {
        step.Observe("  источник-операция: не запрашивался (нужен живой объект документа)");
        return null;
    }

    /// <summary>Builds a rotation whose axis is the given API7 axis object, and reports Create().</summary>
    /// <remarks>
    /// The profile is supplied as well, because R.7 measured that <c>Profile</c> persists where a
    /// bare <c>Axis</c> did not: a rotation with an axis and no profile is not a rotation, and
    /// testing only half the pair would have produced a confident wrong answer.
    /// </remarks>
    private (bool Created, string Detail) TryRotationWithAxisObject(
        ksPart part, KompasAPI7.IModelObject axisObject, ProbeStep step, string name)
    {
        if (_rotatedType is not { } type)
        {
            return (false, "тип вращения не найден");
        }

        ksEntity sketch;
        try
        {
            (_, sketch) = RectangleSketch(name, withAxis: false);
        }
        catch (Exception ex)
        {
            return (false, "профиль не построен: " + ex.Message);
        }

        if (part.NewEntity(type) is not ksEntity feature
            || feature.GetDefinition() is not ksBaseRotatedDefinition definition)
        {
            return (false, "NewEntity(" + type + ") не дал ksBaseRotatedDefinition");
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        Api5.SafeBool(() => definition.SetSideParam(true, 360d));

        var object7 = _app.TransferInterface(feature, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        if (object7 is not KompasAPI7.IRotated rotated)
        {
            return (false, "признак не отвечает на QI(IRotated)");
        }

        try
        {
            var profile7 = _app.TransferInterface(sketch, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
            if (profile7 is not null)
            {
                rotated.Profile = profile7;
            }

            rotated.Axis = axisObject;
            var held = rotated.Axis is null ? "null (отброшено)" : "сохранено";
            step.Observe("  " + name + ": ось подана → Axis " + held);

            var created = Api5.SafeBool(feature.Create) == true;
            part.RebuildModel();
            _doc.RebuildDocument();
            return (created, "Axis=" + held);
        }
        catch (Exception ex)
        {
            return (false, HResult.Describe(ex));
        }
    }

    /// <summary>
    /// Transfers an API5 object to API7, returning null when there is nothing to transfer.
    /// </summary>
    /// <remarks>
    /// Every axis member is typed <c>IModelObject</c>, so a raw API5 object cannot be assigned to it
    /// even when the underlying entity is the same one. A silent null here would make a missing source
    /// look like an axis type that refused its source, hence the explicit return.
    /// </remarks>
    private KompasAPI7.IModelObject? Transfer(ksPart part, object? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as KompasAPI7.IModelObject;
        }
        catch (Exception ex)
        {
            _report.Note(_currentStepId, "перенос источника в API7 бросил " + HResult.Describe(ex));
            return null;
        }
    }

    /// <summary>
    /// The first operation of the part by tree order, as a live API5 object, or null with a reason.
    /// </summary>
    /// <remarks>
    /// The tree is walked rather than guessed at, and the reason is returned in <paramref name="detail"/>
    /// so an empty part cannot pass for a found source. <c>ksEntity</c> exposes neither
    /// <c>EntityCollection</c> nor <c>GetKind</c> — the members that do exist are
    /// <c>ksPart.EntityCollection(type)</c> and <c>ksEntity.GetKind()</c> is not among them, so the
    /// walk goes through the part and reads each entity's number with <c>type</c>.
    /// </remarks>
    private static object? FirstOperation(ksPart part, out string detail)
    {
        try
        {
            var bodies = part.GetMainBody();
            if (bodies is null)
            {
                detail = "основного тела нет";
                return null;
            }

            // ksPart.EntityCollection(110 /* OperationElement */) is the operation branch of the tree —
            // the same collection and the same constant the shipping adapter walks. `Api5`'s own
            // constant is used rather than a literal, and the collection is cast rather than treated as
            // a bare object, because GetCount/GetByIndex live on ksEntityCollection.
            var collection = part.EntityCollection(Api5.OperationElement) as ksEntityCollection;
            if (collection is null)
            {
                detail = "ветка операций в дереве недоступна";
                return null;
            }

            var count = collection.GetCount();
            var seen = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (collection.GetByIndex(i) is not ksEntity entity)
                {
                    continue;
                }

                short kind;
                try
                {
                    kind = entity.type;
                }
                catch
                {
                    continue;
                }

                seen.Add(kind.ToString());
                // Any extrusion is a valid operation source, not only the base one. The first run of
                // this step looked for o3d_baseExtrusion alone and reported "no operation" for a part
                // whose tree held o3d_bossExtrusion — the same class of self-inflicted defect as
                // R.7's missing control plate: the probe's own filter looked like a fact about КОМПАС.
                if (kind is (short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_baseExtrusion
                          or (short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_bossExtrusion
                          or (short)Kompas6Constants3D.ksObj3dTypeEnum.o3d_cutExtrusion)
                {
                    detail = "найдена операция выдавливания в дереве: номер " + kind
                        + " (элементов " + count + ")";
                    return entity;
                }
            }

            detail = "операции выдавливания в дереве нет (элементов " + count
                + ", номера: " + string.Join(",", seen.Distinct()) + ")";
            return null;
        }
        catch (Exception ex)
        {
            detail = "обход дерева бросил " + HResult.Describe(ex);
            return null;
        }
    }

    /// <summary>The axis's own name, or why it could not be read. Never throws into a verdict.</summary>
    private static string SafeName(KompasAPI7.IAxis3D axis)
    {
        try
        {
            return axis.Name ?? "null";
        }
        catch (Exception ex)
        {
            return HResult.Describe(ex);
        }
    }

    /// <summary>
    /// The rotation source object behind a cylinder the reader already found, so it can be
    /// transferred. The reader returns parameters, not the COM object, hence this second lookup.
    /// </summary>
    private static object? FindCylinderFaceSource(ksPart part, Api5.FaceReading reading)
    {
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            return null;
        }

        if (reading.Index < 0 || reading.Index >= faces.GetCount())
        {
            return null;
        }

        return faces.GetByIndex(reading.Index);
    }

    /// <summary>The largest planar face by area — the plate's top, where a hole is drilled.</summary>
    private static object? LargestPlanarFace(ksPart part, ProbeStep step)
    {
        var faces = Api5.ReadFaces(part, step).Where(f => f.PlaneNormal is not null).ToList();
        if (faces.Count == 0)
        {
            return null;
        }

        var best = faces.OrderByDescending(f => f.Area ?? 0d).First();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection collection)
        {
            return null;
        }

        return best.Index >= 0 && best.Index < collection.GetCount()
            ? collection.GetByIndex(best.Index)
            : null;
    }

    /// <summary>
    /// The first edge of the part's main body, offered as an axis candidate.
    /// </summary>
    /// <remarks>
    /// The rotation under measurement builds no body (that is the point of the step), so the edges
    /// come from the control plate built earlier in the same part. Which edge it is does not matter
    /// yet: the question is whether <c>SetAxis</c> accepts a transferred model object at all. The
    /// first version of this step reported "no candidate found" when the truth was "the control was
    /// built too late" — a probe defect that would have been read as a finding about КОМПАС.
    /// </remarks>
    private static bool TryAnyEdge(ksPart part, out object? edge)
    {
        edge = null;
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            return false;
        }

        // Edges are reached through a face, not off the body directly: `ksBody` declares no edge
        // collection in this build (the adapter's own readers go `face.EdgeCollection()`), and a
        // guessed member name would have been a compile error rather than a finding.
        var faceCount = faces.GetCount();
        for (var f = 0; f < faceCount; f++)
        {
            if (faces.GetByIndex(f) is not ksFaceDefinition face
                || face.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            var edgeCount = edges.GetCount();
            for (var i = 0; i < edgeCount; i++)
            {
                if (edges.GetByIndex(i) is { } candidate)
                {
                    edge = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>R.2 — the cylinder itself, against π·r²·h.</summary>
    private void FullTurn(short type)
    {
        _currentStepId = "R.2";
        var step = _report.Begin("R.2", "Полный оборот: прямоугольник " + RadiusMm + "×" + HeightMm
            + " вокруг осевой линии эскиза", "Даёт ли вращение цилиндр аналитического объёма?");

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R2-profile", withAxis: true);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        var before = Api5.Volume(part);
        var (created, detail) = CreateRotation(part, sketch, angleDeg: 360d, directionType: 0);
        step.Observe("Create() → " + created + "; " + detail);

        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(before));
        step.Observe("V=" + Api5.Num(volume) + ", ожидание π·r²·h = " + Api5.Num(FullTurnVolume));

        // The honesty gate. This step builds into the document every earlier step also built into, so a
        // volume that equals the pre-step reading is not this sketch's solid — it is the previous
        // step's. Checked before any comparison against the expectation, because a match is impossible
        // for a body that was never created and reporting one would be the `4/tan` mistake again.
        if (BodyDidNotMove(step, before, volume, "полный оборот"))
        {
            return;
        }

        if (volume is null)
        {
            step.Fail("объём не прочитан: тела нет");
            return;
        }

        if (Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Pass("цилиндр получен вращением: V = π·r²·h");
        }
        else
        {
            step.Fail("объём " + Api5.Num(volume) + " не совпал с " + Api5.Num(FullTurnVolume));
        }
    }

    /// <summary>
    /// True when the body volume did not move across a build step, meaning nothing was added to the
    /// shared document and any number read afterwards belongs to an earlier step.
    /// </summary>
    /// <remarks>
    /// This exists because the probe builds every step into one document. Without the check, R.2…R.5
    /// all reported <c>V=82994.6903508512</c> — a figure produced by a base plate an earlier step had
    /// left in the body — and the reports read as though five separate rotations had been measured.
    /// </remarks>
    private bool BodyDidNotMove(ProbeStep step, double? before, double? after, string what)
    {
        if (before is null || after is null || Math.Abs(after.Value - before.Value) > 1d)
        {
            return false;
        }

        step.Fail(what + " не изменило объём тела (" + Api5.Num(after) + "): построения нет; "
            + "число совпадает с тем, что уже было в документе до этого шага");
        return true;
    }

    /// <summary>R.3 — an angle strictly between 0 and 360 must sweep proportionally.</summary>
    private void HalfTurn(short type)
    {
        _currentStepId = "R.3";
        var step = _report.Begin("R.3", "Частичный угол 180°", "Угол учитывается или игнорируется?");
        var expected = FullTurnVolume / 2d;

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R3-profile", withAxis: true);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        var before = Api5.Volume(part);
        var (created, detail) = CreateRotation(part, sketch, angleDeg: 180d, directionType: 0);
        step.Observe("Create() → " + created + "; " + detail);

        part.RebuildModel();
        _doc.RebuildDocument();

        var volume = Api5.Volume(part);
        step.Observe("объём тела до шага: " + Api5.Num(before));
        step.Observe("V=" + Api5.Num(volume) + ", ожидание " + Api5.Num(expected));

        if (BodyDidNotMove(step, before, volume, "вращение на 180°"))
        {
            return;
        }

        if (volume is null)
        {
            step.Fail("объём не прочитан");
            return;
        }

        if (Math.Abs(volume.Value - expected) <= Tolerance(expected))
        {
            step.Pass("половинный угол дал половину объёма");
        }
        else if (Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Fail("угол проигнорирован: получен полный оборот");
        }
        else
        {
            step.Fail("объём " + Api5.Num(volume) + " не совпал ни с половиной, ни с полным");
        }
    }

    /// <summary>R.4 — directionType must change the result, not merely be accepted.</summary>
    private void Direction(short type)
    {
        _currentStepId = "R.4";
        var step = _report.Begin("R.4", "Направление: directionType 0 и 1", "Направление различимо числом?");
        var volumes = new List<(short Direction, double? Volume, string? Faces)>();

        foreach (var direction in new short[] { 0, 1 })
        {
            ksPart part;
            ksEntity sketch;
            try
            {
                (part, sketch) = RectangleSketch("R4-profile-" + direction, withAxis: true);
            }
            catch (Exception ex)
            {
                step.Fail("построение не удалось: " + ex.Message);
                return;
            }

            var before = Api5.Volume(part);
            var (created, _) = CreateRotation(part, sketch, angleDeg: 360d, directionType: direction);
            part.RebuildModel();
            _doc.RebuildDocument();
            var volume = Api5.Volume(part);
            var faces = Api5.CylinderFaces(part);
            step.Observe("directionType=" + direction + ": Create() → " + created
                + ", объём до шага " + Api5.Num(before) + " → V=" + Api5.Num(volume)
                + ", цилиндрических граней: " + faces?.Count.ToString() ?? "—");
            volumes.Add((direction, volume, faces?.Count.ToString()));
        }

        if (volumes.Any(v => v.Volume is null))
        {
            step.Fail("одно из направлений не дало тела");
            return;
        }

        // Same gate as R.2: two identical readings of an unmoved body are not two measurements of a
        // direction. Left in place, this step would report "направления не различимы" for a model in
        // which neither direction produced anything at all.
        //
        // FIFTEENTH PROBE DEFECT, found by a verdict that flipped between runs while the journal was
        // byte-identical. The comparison was `volumes[0].Volume == volumes[1].Volume` — a strict
        // equality on double — while the journal prints the same numbers through `Api5.Num`, which
        // rounds to twelve decimals. Two readings one ULP apart at |V|≈7.1e5 (1.16e-10, in the
        // thirteenth decimal) therefore PRINT identically and COMPARE unequal, so this step passed or
        // failed on the low bits of a re-computation rather than on anything visible. That is defect
        // class 12 in a second place: a threshold that is not a measurement. The comparison is now
        // against the same noise band, so "различимы" means a difference a reader can see.
        var band = NoiseFloor(volumes[0].Volume!.Value);
        var volumeDiffers = Math.Abs(volumes[0].Volume!.Value - volumes[1].Volume!.Value) > band;
        var facesDiffer = volumes[0].Faces != volumes[1].Faces;

        if (!volumeDiffers && !facesDiffer)
        {
            step.Fail("направления не различимы: объём и грани совпали (V=" + Api5.Num(volumes[0].Volume)
                + ", граней " + (volumes[0].Faces ?? "—") + ") — но оба направления и не построили "
                + "ничего, так что различия и не могло быть. Порог: |ΔV| должен превышать "
                + Api5.Num(band) + " (шум представления double)");
            return;
        }

        step.Pass("направления различимы числом: ΔV=" + Api5.Num(volumes[0].Volume - volumes[1].Volume)
            + " при пороге " + Api5.Num(band) + ", граней " + (volumes[0].Faces ?? "—")
            + " против " + (volumes[1].Faces ?? "—"));
    }

    /// <summary>
    /// R.5 — negative test. With no construction line in the sketch there must be no silent wrong
    /// solid: the catalog promises the axis is the caller's to state, so an implicit axis would be
    /// exactly the failure this row exists to prevent.
    /// </summary>
    /// <remarks>
    /// <b>Guarded against the empty pass.</b> The first version of this step would have reported PASS
    /// for a rotation that simply never got built, because the only thing it checked was that no body
    /// appeared — and a body appears neither when the axis is refused nor when the feature was never
    /// created. So the control is built first: a profile <em>with</em> an axis must produce the known
    /// cylinder in this same step, and only then does the axisless run mean anything.
    /// </remarks>
    private void NoAxis(short type)
    {
        _currentStepId = "R.5";
        var step = _report.Begin("R.5", "Отрицательный: эскиз без осевой линии",
            "Подставляется ли ось молча?");

        // Control: the same builder, same rotation, with the axis present. Without this the step
        // cannot tell a refusal from a rotation that never worked at all.
        ksPart controlPart;
        ksEntity controlSketch;
        try
        {
            (controlPart, controlSketch) = RectangleSketch("R5-control", withAxis: true);
        }
        catch (Exception ex)
        {
            step.Fail("построение контрольного профиля не удалось: " + ex.Message);
            return;
        }

        var controlBefore = Api5.Volume(controlPart);
        var (controlCreated, controlDetail) = CreateRotation(controlPart, controlSketch, 360d, 0);
        controlPart.RebuildModel();
        _doc.RebuildDocument();
        var controlVolume = Api5.Volume(controlPart);
        step.Observe("контроль (с осевой): Create() → " + controlCreated + "; " + controlDetail
            + "; объём до шага " + Api5.Num(controlBefore) + " → V=" + Api5.Num(controlVolume)
            + ", ожидание " + Api5.Num(FullTurnVolume));

        // The control must be a body that MOVED. Comparing against π·r²·h alone would accept an
        // untouched main body as a working control the moment the two numbers happened to be close,
        // and then the negative run below would be judged against a positive that never existed.
        if (controlVolume is null || controlBefore is null
            || Math.Abs(controlVolume.Value - controlBefore.Value) <= 1d
            || Math.Abs(controlVolume.Value - FullTurnVolume) > Tolerance(FullTurnVolume))
        {
            step.Fail("контрольный профиль с осевой линией не дал цилиндра — отрицательный опыт без оси "
                + "ничего не доказывает, пока положительный не измерен");
            return;
        }

        ksPart part;
        ksEntity sketch;
        try
        {
            (part, sketch) = RectangleSketch("R5-profile", withAxis: false);
        }
        catch (Exception ex)
        {
            step.Fail("построение не удалось: " + ex.Message);
            return;
        }

        var before = Api5.Volume(part);
        var (created, detail) = CreateRotation(part, sketch, angleDeg: 360d, directionType: 0);
        part.RebuildModel();
        _doc.RebuildDocument();
        var volume = Api5.Volume(part);
        step.Observe("без осевой: Create() → " + created + "; " + detail
            + "; объём до шага " + Api5.Num(before) + " → V=" + Api5.Num(volume));

        // "No body appeared" is only meaningful as an absence of CHANGE. An unchanged volume that
        // happens to be non-null is exactly what a refused rotation produces, and it must read as
        // "nothing was built", not as "something of the wrong size was built".
        if (volume is null || before is null || Math.Abs(volume.Value - before.Value) <= 1d)
        {
            step.Pass("сборка без осевой ничего не добавила (объём тела не изменился) при работающем "
                + "контроле — молчаливой подстановки оси нет");
        }
        else if (Math.Abs(volume.Value - FullTurnVolume) <= Tolerance(FullTurnVolume))
        {
            step.Fail("ось подставлена молча: объём равен полному обороту");
        }
        else
        {
            step.Observe("получен другой объём: " + Api5.Num(volume));
            step.Fail("поведение без оси не равно ни отказу, ни ожидаемому");
        }
    }
}
