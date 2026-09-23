using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter;

namespace KompasMcp.P0Probe;

/// <summary>
/// P2.6 — three questions that block real features, each settled by a cheap decisive experiment on
/// a live КОМПАС-3D v24 instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>A — which body an extrusion acts on.</b> The adapter requires <c>target_body_ref</c> for
/// boss/cut and then never passes it to COM (Api5Session.Geometry.cs refuses a missing ref but has
/// no way to honour one). The vendor interop declares <c>chooseType</c>, <c>ChooseBodies()</c> and
/// <c>ChooseParts()</c> on both extrusion definitions, so the question is not "does the API have a
/// knob" but "does turning that knob change <em>which body's volume moves</em>". Measured on a
/// two-body part by comparing <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c> per body
/// before and after — the same read the adapter performs, so the numbers stay comparable with
/// P0.7/P2.1.
/// </para>
/// <para>
/// <b>B — deleting entities out of an existing sketch.</b> <c>kompas_edit_sketch</c> modes
/// <c>replace</c> and <c>delete_entities</c> are not implemented (<c>replace</c> appends). API5
/// declares <c>ksDeleteObj(ref)</c>, <c>ksExistObj</c>, <c>ksFindObj</c>, <c>ksNewGroup</c>/
/// <c>ksAddObjGroup</c>/<c>ksClearGroup</c>/<c>ksDestroyObjects</c> on <c>ksDocument2D</c> and
/// nothing of the sort on <c>ksSketchDefinition</c>. Every candidate route is run, and the verdict
/// comes from whether the extruded redrawn profile equals the NEW figure analytically — never from
/// a return code. The historical scripts in <c>reference/</c> were read first: none of them ever
/// deletes anything (each builds a fresh sketch), so there is no idiom to copy.
/// </para>
/// <para>
/// <b>C — the raw keys of the existing <c>kompas_probe_units</c> path</b> for a known 100 mm
/// segment and a known R10 circle (acceptance row G08: length 100, circumference 20π, area 100π),
/// including which readings come from an edge of a real body and which only from a sketch entity.
/// Key names mirror <c>Api5Session.Exchange.UnitProbe</c> (<c>edge{n}_length_selector_{bits}</c>,
/// <c>gabarit_*_mm</c>) so this report and that tool's output can be compared line by line.
/// </para>
/// <para>
/// Structure follows <see cref="BoreProbe"/>: local copies of the construction helpers, so the
/// existing steps cannot move under this one; signatures from the vendor binary or live
/// QueryInterface (<see cref="ComDiscovery"/>); and an affirmative return from COM is data, not a
/// result.
/// </para>
/// </remarks>
internal static class TargetBodyProbe
{
    private const int MixMmKg = 1 | 16;
    private const string EndTypeEnum = "ksEndTypeEnum";

    // --- A: the two-body part -------------------------------------------------------------------
    private const double PlateX = 100d;
    private const double PlateY = 80d;
    private const double Thickness = 10d;
    private const double BlobX = 150d;
    private const double BlobRadius = 12d;
    private const double HoleRadius = 5d;

    /// <summary>Volume a Ø10 through-cut removes from a 10 mm wall: π·5²·10.</summary>
    private static readonly double HoleVolume = Math.PI * HoleRadius * HoleRadius * Thickness;

    // --- B: the sketch to be cleared ------------------------------------------------------------
    private const double RectU1 = -60d;
    private const double RectV1 = -20d;
    private const double RectU2 = -20d;
    private const double RectV2 = 20d;
    private const double OldCircleU = 60d;
    private const double OldCircleR = 10d;
    private const double NewCircleR = 10d;
    private const double NewProfileDepth = 5d;

    /// <summary>What a clean redraw must produce: π·10²·5.</summary>
    private static readonly double CleanVolume = Math.PI * NewCircleR * NewCircleR * NewProfileDepth;

    /// <summary>Rect 40×40 plus a Ø20 circle, extruded 5 mm, if both contours add material.</summary>
    private static readonly double DirtySumVolume = ((RectU2 - RectU1) * (RectV2 - RectV1) + Math.PI * OldCircleR * OldCircleR) * NewProfileDepth;

    /// <summary>The same, if the circle is taken as an inner hole.</summary>
    private static readonly double DirtyDifferenceVolume = ((RectU2 - RectU1) * (RectV2 - RectV1) - Math.PI * OldCircleR * OldCircleR) * NewProfileDepth;

    // --- C: known geometry for the unit keys ----------------------------------------------------
    private const double KnownLineMm = 100d;
    private const double KnownRadiusMm = 10d;
    private static readonly double KnownCircumference = 2d * Math.PI * KnownRadiusMm;
    private static readonly double KnownDiskArea = Math.PI * KnownRadiusMm * KnownRadiusMm;

    /// <summary>Agreement threshold for a volume comparison, in mm³ (the acceptance tolerance).</summary>
    private const double Tol = 0.01d;

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    private static short TypeOf(string constant, int fallback) => EntityTypes.Value(constant, (short)fallback);

    private static short Blind() => VendorConstants.Value(EndTypeEnum, "etBlind", 0);

    private static short ThroughAll() => VendorConstants.Value(EndTypeEnum, "etThroughAll", 1);

    // -----------------------------------------------------------------------------------------
    // Step
    // -----------------------------------------------------------------------------------------

    public static void Measure(ProbeReport report, KompasObject app)
    {
        var sw = Stopwatch.StartNew();
        var step = report.Begin(
            "P2.6",
            "Целевое тело операции выдавливания, очистка существующего эскиза и ключи сырого чтения единиц",
            "На какое тело реально режет вырезание, когда цель не указана, и задаётся ли тело явно (A); удаляются ли объекты из готового эскиза и чем (B); какие ключи и числа даёт путь kompas_probe_units на известной геометрии 100 мм и R10 (C)?");

        var a = new TargetResult();
        var b = new List<ClearRoute>();
        var c = new UnitKeys();

        try
        {
            DeclaredSurface(step);
        }
        catch (Exception ex)
        {
            step.Errors.Add("объявленная поверхность API5: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
        }

        try
        {
            a = MeasureTargetBody(step, app);
        }
        catch (Exception ex)
        {
            a.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Errors.Add("A: " + a.Failure);
            step.Observe("A: шаг не доигран — " + a.Failure);
        }

        try
        {
            b = MeasureSketchClearing(step, app);
        }
        catch (Exception ex)
        {
            step.Errors.Add("B: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe("B: шаг не доигран — " + Unwrap(ex).Message);
        }

        try
        {
            c = MeasureUnitKeys(step, app);
        }
        catch (Exception ex)
        {
            c.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Errors.Add("C: " + c.Failure);
            step.Observe("C: шаг не доигран — " + c.Failure);
        }

        Conclude(step, a, b, c);
        step.Duration = sw.Elapsed;
    }

    /// <summary>
    /// What the vendor interop declares for the three questions, recorded before any measurement so
    /// that a later "the API has no X" is confirmed or corrected by this run rather than by memory.
    /// </summary>
    private static void DeclaredSurface(ProbeStep step)
    {
        var assembly = typeof(KompasObject).Assembly;

        foreach (var typeName in new[]
                 {
                     "ksCutExtrusionDefinition", "ksBossExtrusionDefinition", "ksBaseExtrusionDefinition",
                     "ksChooseBodies", "ksChooseMng", "ksBodyCollection", "ksEntityCollection",
                     "ksSketchDefinition", "ksDocument2D", "ksBody", "ksEntity",
                     "ksCurve3D", "ksEdgeDefinition", "ksCircle3dParam",
                 })
        {
            var type = assembly.GetType("Kompas6API5." + typeName, throwOnError: false);
            if (type is null)
            {
                step.Data["declared:" + typeName] = new[] { "<типа нет в Interop.Kompas6API5>" };
                step.Observe($"API5 объявляет {typeName}: нет такого типа.");
                continue;
            }

            var interesting = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Contains("Choose", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Destroy", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Clear", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Exist", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Find", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Group", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Add", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("GetCount", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("Length", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("radius", StringComparison.OrdinalIgnoreCase)
                         || m.Name.Contains("cut", StringComparison.OrdinalIgnoreCase))
                .Select(Members.Signature)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            step.Data["declared:" + typeName] = interesting;
            step.Observe($"API5, {typeName} (GUID {type.GUID:N}): " + (interesting.Length == 0 ? "<ничего из перечисленного>" : string.Join(" | ", interesting)));

            var chooseTypeProperty = type.GetProperty("chooseType", BindingFlags.Public | BindingFlags.Instance);
            var cutProperty = type.GetProperty("cut", BindingFlags.Public | BindingFlags.Instance);
            if (chooseTypeProperty is not null || cutProperty is not null)
            {
                step.Observe($"  {typeName}: chooseType {{{DescribeAccessors(chooseTypeProperty)}}} : {chooseTypeProperty?.PropertyType.Name ?? "—"}; cut {{{DescribeAccessors(cutProperty)}}} : {cutProperty?.PropertyType.Name ?? "—"}.");
            }
        }

        // The meaning of a bare Int32 selector can only come from a vendor enum; a guessed name is
        // exactly the mistake this repository has already paid for twice.
        var enums = ChooseEnumSurvey();
        step.Data["vendor_enums_choose_bodies"] = enums.ToArray();
        foreach (var line in enums)
        {
            step.Observe("Вендорский enum: " + line);
        }

        // B turns on "can every object in a sketch be listed". The 2D surface has no fragment
        // enumeration in API5 — checked against the loaded interop rather than asserted, because
        // the whole point of this step is that the answer decides what a `replace` can promise.
        var twoD = assembly.GetType("Kompas6API5.ksDocument2D", throwOnError: false);
        var sketch = assembly.GetType("Kompas6API5.ksSketchDefinition", throwOnError: false);
        var absent = new[]
        {
            "ksFirstObj", "ksNextObj", "ksPrevObj", "ksGetObjCount", "ksGetObjByRole", "ksEnumObjects",
            "Delete", "Clear", "DeleteAll", "ksClearSketch", "GetSegmentContainer", "SketchEntities",
        }.SelectMany(name => new[]
        {
            twoD is null || twoD.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is null
                ? $"ksDocument2D.{name} → нет"
                : $"ksDocument2D.{name} → ЕСТЬ",
            sketch is null || sketch.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) is null
                ? $"ksSketchDefinition.{name} → нет"
                : $"ksSketchDefinition.{name} → ЕСТЬ",
        }).ToArray();
        step.Data["api5_absent_enumeration_members"] = absent;
        step.Observe("Проверка предпосылки B (перечисление объектов эскиза): " + string.Join("; ", absent));
        step.Observe("То есть ref на объект фрагмента API5 даёт только сам вызов отрисовки (ksLineSeg/ksCircle) в текущем сеансе BeginEdit, либо ksFindObj по точке; "
            + "перебрать все объекты готового эскиза нечем — это ограничивает режим delete_entities перечислением, которое есть у самого адаптера.");
    }

    private static string DescribeAccessors(PropertyInfo? property) => property is null
        ? "нет"
        : (property.CanRead ? "get" : string.Empty) + (property.CanRead && property.CanWrite ? "/" : string.Empty) + (property.CanWrite ? "set" : string.Empty);

    /// <summary>Enums in the constants interop whose name mentions choosing or bodies.</summary>
    private static List<string> ChooseEnumSurvey()
    {
        var lines = new List<string>();
        var directory = KompasInteropResolver.ResolvedDirectory;
        foreach (var fileName in new[] { "Interop.Kompas6Constants3D.dll", "Interop.Kompas6Constants.dll", "Interop.Kompas6Constants2D.dll" })
        {
            var path = directory is null ? null : Path.Combine(directory, fileName);
            if (path is null || !File.Exists(path))
            {
                lines.Add($"{fileName}: файл не найден");
                continue;
            }

            try
            {
                var assembly = Assembly.LoadFrom(path);
                foreach (var type in assembly.GetExportedTypes().Where(t => t.IsEnum).OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (!type.Name.Contains("Choose", StringComparison.OrdinalIgnoreCase)
                        && !type.Name.Contains("Body", StringComparison.OrdinalIgnoreCase)
                        && !type.Name.Contains("AddMode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var members = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                        .Select(f => $"{f.Name}={f.GetRawConstantValue()}")
                        .OrderBy(s => s, StringComparer.Ordinal);
                    lines.Add($"{fileName} :: {type.Name}: {string.Join(", ", members)}");
                }
            }
            catch (Exception ex)
            {
                lines.Add($"{fileName}: {ex.GetType().Name}");
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("<ни одного enum с Choose/Body/AddMode в загруженных константных библиотеках не найдено>");
        }

        return lines;
    }

    // =========================================================================================
    // A — which body the operation acts on
    // =========================================================================================

    private sealed class TargetResult
    {
        public string BuildRoute = "-";
        public bool TwoBodiesAchieved;
        public string? Failure;

        /// <summary>Index in the before-snapshot whose volume moved when nothing was chosen.</summary>
        public int DefaultChangedIndex = -1;
        public string DefaultEvidence = "-";
        public bool DefaultCutCreated;

        /// <summary>Which body moved when the contour sat over the NON-main body and nothing was declared.</summary>
        public int FarBodyChangedIndex = -1;
        public string FarBodyEvidence = "-";

        /// <summary>Declaring a body that the contour does not touch suppressed the automatic cut.</summary>
        public bool ContradictionSuppressed;
        public string ContradictionEvidence = "-";

        /// <summary>The call order that produced the honouring, assembled from the measured rows.</summary>
        public string MinimalSequence = "-";

        /// <summary>Declaring the body the contour does touch cut exactly it.</summary>
        public CutAttempt? AgreedCut;
        public string NewBodyEvidence = "-";

        public readonly List<CutAttempt> Variants = new();
        public CutAttempt? Working;
        public string OrderEvidence = "-";
    }

    /// <summary>How (or whether) a run declares the body the operation must act on.</summary>
    private sealed class Choice
    {
        /// <summary>none | collection | bodycollection | choosemng.</summary>
        public string Via = "none";

        /// <summary>The definition's own <c>chooseType</c> (vendor <c>ksChooseType</c>).</summary>
        public int? ChooseType;

        /// <summary><c>ChooseBodies().ChooseBodiesType</c> (vendor <c>ksChooseBodiesType</c>).</summary>
        public int? ChooseBodiesType;

        /// <summary>Index in the before-snapshot of the body to offer; -1 = offer nothing.</summary>
        public int AddBodyIndex = -1;

        public bool BeforeSketch = true;

        /// <summary>
        /// Whether a hole is expected at all. The contradiction rows declare a body the contour does
        /// not touch, so there the correct outcome is <em>nothing removed</em> — without this flag
        /// those rows would print as a failed expectation.
        /// </summary>
        public bool ExpectRemoved = true;
    }

    private static TargetResult MeasureTargetBody(ProbeStep step, KompasObject app)
    {
        var result = new TargetResult();

        var setup = BuildTwoBody(step, app, "p6", result);
        if (setup is null || !setup.Ok)
        {
            result.Failure = "двухтелесная модель не построена: " + (setup?.Failure ?? "<нет попытки>");
            step.Data["a_two_bodies_achieved"] = false;
            step.Data["a_two_body_failure"] = result.Failure;
            step.Observe("A: " + result.Failure);
            setup?.DisposeQuietly();
            return result;
        }

        result.TwoBodiesAchieved = true;
        step.Observe($"A: построено маршрутом «{result.BuildRoute}»; тел {setup.Bodies.Count}:");
        foreach (var body in setup.Bodies)
        {
            step.Observe("   " + body.Describe());
        }
        step.Data["a_bodies_before_default_cut"] = setup.Bodies.Select(b => b.Describe()).ToArray();

        // ---- A.1/A.1b the production question: what acts when nothing is declared? -------------
        // Two profiles, one under each body. A plate alone cannot tell "the main body" from "the
        // body under the contour", because for the main body those are the same thing — which is
        // exactly the ambiguity the adapter's target_body_ref has to resolve.
        var farIndex = setup.Bodies.Count > 1 ? 1 : 0;
        var attempts = new List<CutAttempt>();
        foreach (var profileIndex in new[] { 0, farIndex })
        {
            var fresh = profileIndex == 0 ? setup : BuildTwoBody(step, app, "p6auto" + profileIndex.ToString(CultureInfo.InvariantCulture), null);
            if (fresh is null || !fresh.Ok)
            {
                step.Observe($"A.1 цель не указана, профиль под телом {profileIndex}: модель не построена ({fresh?.Failure ?? "<нет попытки>"}).");
                fresh?.DisposeQuietly();
                continue;
            }

            var attempt = RunCut(fresh, profileIndex, new Choice { Via = "none" }, $"A.1 цель НЕ указана, профиль под телом {profileIndex}");
            fresh.DisposeQuietly();
            attempts.Add(attempt);
            result.Variants.Add(attempt);
            step.Observe($"{attempt.Label} → {attempt.Evidence}");
            if (profileIndex == 0)
            {
                result.DefaultChangedIndex = attempt.ChangedIndex;
                result.DefaultEvidence = attempt.Evidence;
                result.DefaultCutCreated = attempt.Created;
            }
            else
            {
                result.FarBodyChangedIndex = attempt.ChangedIndex;
                result.FarBodyEvidence = attempt.Evidence;
            }
        }

        step.Data["a_default_route"] = result.DefaultEvidence;
        step.Data["a_default_changed_index"] = result.DefaultChangedIndex;
        step.Data["a_far_profile_changed_index"] = result.FarBodyChangedIndex;
        step.Data["a_far_profile_route"] = result.FarBodyEvidence;

        // ---- A.2/A.3 the declared selectors, without and with the document choose manager ------
        var sweeps = new (string Via, string Prefix)[]
        {
            ("collection", "A.2 ChooseBodies() без добавления"),
            ("choosemng", "A.3 GetChooseMng().Choose()"),
        };
        foreach (var (via, prefix) in sweeps)
        {
            foreach (var chooseType in new[] { 0, 1, 2 })
            {
                var tag = $"{prefix} chooseType={chooseType}";
                var fresh = BuildTwoBody(step, app, "p6" + (via == "collection" ? "cb" : "cm") + chooseType.ToString(CultureInfo.InvariantCulture), null);
                if (fresh is null || !fresh.Ok)
                {
                    result.Variants.Add(new CutAttempt { Label = tag, ChooseTypeRequested = chooseType, IntendedIndex = farIndex, Failure = "модель не построена: " + (fresh?.Failure ?? "<нет попытки>"), Evidence = "не построено" });
                    step.Observe($"{tag}: {result.Variants[^1].Failure}.");
                    fresh?.DisposeQuietly();
                    continue;
                }

                var variant = RunCut(fresh, farIndex, new Choice { Via = via, ChooseType = chooseType, AddBodyIndex = via == "choosemng" ? farIndex : -1 }, tag);
                fresh.DisposeQuietly();
                result.Variants.Add(variant);
                ReportVariant(step, variant, prefix);
            }
        }

        // ---- A.5 the decisive pair: declared target CONTRADICTS the profile --------------------
        // If choosing is honoured, declaring body 0 while the contour sits over body 1 must NOT cut
        // body 1; declaring body 1 must cut it. Anything else means the knob is inert and the
        // "successful" A.2/A.3 rows above were automatic behaviour all along.
        var decisive = new (string Label, int Declared, int Profile, int? ChooseType, int? BodiesType)[]
        {
            ("A.5a заявлено тел0, профиль под телом 1", 0, farIndex, 3, 2),
            ("A.5b заявлено тел1, профиль под телом 1", farIndex, farIndex, 3, 2),
            ("A.5c заявлено тел0, профиль под телом 1, chooseType=1", 0, farIndex, 1, 2),
            ("A.5d заявлено тел1, профиль под телом 1, chooseType=3, ChooseBodiesType=3 (ksAllBodies)", farIndex, farIndex, 3, 3),
        };
        foreach (var (label, declared, profile, chooseType, bodiesType) in decisive)
        {
            var fresh = BuildTwoBody(step, app, "p6d" + declared.ToString(CultureInfo.InvariantCulture) + profile.ToString(CultureInfo.InvariantCulture) + Raw(bodiesType), null);
            if (fresh is null || !fresh.Ok)
            {
                step.Observe($"{label}: модель не построена ({fresh?.Failure ?? "<нет попытки>"}).");
                fresh?.DisposeQuietly();
                continue;
            }

            var variant = RunCut(fresh, profile, new Choice
            {
                Via = "bodycollection",
                ChooseType = chooseType,
                ChooseBodiesType = bodiesType,
                AddBodyIndex = declared,
                ExpectRemoved = declared == profile,
            }, label);
            fresh.DisposeQuietly();
            result.Variants.Add(variant);
            ReportVariant(step, variant, label);

            // The contradiction row proves honouring by REFUSING the automatic cut; the agreeing
            // row proves the same call shape still cuts when the declaration matches the contour.
            var nothingRemoved = variant.RemovedVolume is not double moved || Math.Abs(moved) <= Tol;
            var declaredCut = variant.IntendedOnly && variant.RemovedVolume is double removed && Math.Abs(removed - HoleVolume) <= Math.Max(Tol, 1e-6 * HoleVolume);
            if (declared != profile && nothingRemoved)
            {
                result.ContradictionSuppressed = true;
                result.ContradictionEvidence = variant.Evidence;
            }

            if (declared == profile && declaredCut)
            {
                result.AgreedCut ??= variant;
            }
        }

        result.Working = result.ContradictionSuppressed && result.AgreedCut is not null ? result.AgreedCut : null;
        result.MinimalSequence = result.Working is null
            ? "<маршрут не найден: см. строки A.5a..A.5d>"
            : "cut = (ksEntity)part.NewEntity(o3d_cutExtrusion=26); def = (ksCutExtrusionDefinition)cut.GetDefinition(); "
                + "def.chooseType = 3 /*ksChBodies из вендорского ksChooseType; 1=ksChBodiesAndParts тоже соблюдается, 0 отбрасывается в 1*/; "
                + "cb = def.ChooseBodies() /*отвечает Kompas6API5.ksChooseBodies*/; cb.ChooseBodiesType = 2 /*ksManualEditing*/; "
                + "((ksBodyCollection)cb.BodyCollection()).Add(((ksBodyCollection)part.BodyCollection()).GetByIndex(N)) → true /*сырой элемент коллекции, без распаковки; как ksEntity элемент не отвечает*/; "
                + "def.SetSketch(sketch); def.directionType = 2; def.SetSideParam(true, etThroughAll, 1, 0, false); def.SetSideParam(false, etThroughAll, 1, 0, false); cut.Create(); doc.RebuildDocument() — "
                + "меняется только тело N (A.5b), а если заявлено тело, которого под контуром нет, не меняется ни одно (A.5a). Порядок «выбор до/после SetSketch» на результат не влияет (A.4). "
                + "Для добавки отдельно: cb.ChooseBodiesType = 0 /*ksNewBody*/ + Add(любое тело) → признак создаёт НОВОЕ тело даже при пересечении с существующим (A.6: тел 2→3, V нового = π·10²·10).";

        // ---- A.6 can a boss be sent to a NEW body explicitly? ----------------------------------
        result.NewBodyEvidence = BossToNewBody(step, app);

        // ---- A.4 must the choice be made before or after SetSketch? ---------------------------
        var orderBase = result.AgreedCut ?? result.Variants.FirstOrDefault(v => v.Created && v.ChooseTypeRequested is not null);
        if (orderBase?.ChooseTypeRequested is int orderedType)
        {
            var late = BuildTwoBody(step, app, "p6late", null);
            if (late is not null && late.Ok)
            {
                var afterSketch = RunCut(late, farIndex, new Choice
                {
                    Via = "bodycollection",
                    ChooseType = orderedType,
                    ChooseBodiesType = orderBase.ChooseBodiesTypeRequested,
                    AddBodyIndex = farIndex,
                    BeforeSketch = false,
                }, "A.4 выбор ПОСЛЕ SetSketch");
                late.DisposeQuietly();
                result.OrderEvidence = $"«{orderBase.Label}»: «сначала выбор, потом SetSketch» → {orderBase.Evidence}; «SetSketch, потом выбор» → {afterSketch.Evidence}";
            }
            else
            {
                late?.DisposeQuietly();
                result.OrderEvidence = "порядок вызовов не проверен: двухтелесная модель для этого варианта не построена";
            }
        }
        else
        {
            result.OrderEvidence = "порядок вызовов не проверялся: ни один вариант с явным выбором не дошёл до Create";
        }

        step.Observe("A.4: " + result.OrderEvidence);

        step.Data["a_build_route"] = result.BuildRoute;
        step.Data["a_explicit_target_achievable"] = result.Working is not null;
        step.Data["a_working_variant"] = result.Working is null ? "<нет>" : result.Working.Label;
        step.Data["a_minimal_sequence"] = result.MinimalSequence;
        step.Data["a_contradiction_suppressed"] = result.ContradictionSuppressed;
        step.Data["a_boss_new_body"] = result.NewBodyEvidence;
        step.Data["a_call_order"] = result.OrderEvidence;
        step.Data["a_variants"] = result.Variants.Select(v => $"{v.Label}: {v.Evidence}").ToArray();
        return result;
    }

    private static void ReportVariant(ProbeStep step, CutAttempt variant, string prefix)
    {
        step.Observe($"{variant.Label} → {variant.Evidence}");
        foreach (var line in variant.CollectionEvidence)
        {
            step.Observe("   " + line);
        }

        step.Observe($"   {prefix}: chooseType запрошен {Raw(variant.ChooseTypeRequested)} → после сеттера {Raw(variant.ChooseTypeAfterSet)} → после Create {Raw(variant.ChooseTypeAfterCreate)}; "
            + $"ChooseBodiesType запрошен {Raw(variant.ChooseBodiesTypeRequested)} → {Raw(variant.ChooseBodiesTypeAfterSet)}; добавление: {variant.AddEvidence}; счётчик цели: {Raw(variant.CollectionCountAfterAdd)}; cut={On(variant.CutProperty)}");
    }

    /// <summary>
    /// A.6: a boss whose profile overlaps an existing body, declared as
    /// <c>ChooseBodiesType = ksNewBody</c>. Whether the body count grows is the measurement.
    /// </summary>
    private static string BossToNewBody(ProbeStep step, KompasObject app)
    {
        var setup = BuildTwoBody(step, app, "p6nb", null);
        if (setup is null || !setup.Ok || setup.Part is null || setup.Doc is null)
        {
            setup?.DisposeQuietly();
            return "модель не построена: " + (setup?.Failure ?? "<нет попытки>");
        }

        try
        {
            var before = ReadBodies(setup.Part);
            var xy = (ksEntity)setup.Part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));

            // A Ø20 cylinder right through the plate: overlapping, so an automatic boss would fuse.
            var overlap = CircleSketch(setup.Part, xy, 0d, 0d, 10d, "p6 overlapping boss profile");
            var boss = (ksEntity)setup.Part.NewEntity(TypeOf("o3d_bossExtrusion", KompasObjectTypes.BossExtrusion));
            if (boss.GetDefinition() is not ksBossExtrusionDefinition definition)
            {
                return "определение добавки не ksBossExtrusionDefinition";
            }

            var notes = new List<string>();
            notes.Add($"chooseType := {SetValue(definition, 3)} прочитано {Raw(SafeInt(() => definition.chooseType))}");
            var collection = SafeObject(definition.ChooseBodies);
            if (collection is not null)
            {
                var answered = AnsweredInterfaces(collection);
                notes.Add($"ChooseBodies() → [{string.Join(", ", answered.Select(a => a.FullName))}]");
                notes.Add($"ChooseBodiesType := {WriteIntProperty(collection, answered, "ChooseBodiesType", 0)} прочитано {Raw(ReadIntProperty(collection, answered, "ChooseBodiesType"))}");
                var bodies = SafeObject(() => InvokeGetBodyCollection(collection, answered));
                if (bodies is not null)
                {
                    var bodyAnswered = AnsweredInterfaces(bodies);
                    var added = AddFirstBody(bodies, bodyAnswered, before, 0);
                    notes.Add($"BodyCollection() → [{string.Join(", ", bodyAnswered.Select(a => a.FullName))}]; Add → {added}; счётчик {Raw(CollectionCount(bodies, bodyAnswered))}");
                }
                else
                {
                    notes.Add("ChooseBodies().BodyCollection() → null");
                }
            }

            definition.SetSketch(overlap);
            definition.directionType = 0;
            definition.SetSideParam(true, Blind(), Thickness, 0d, false);
            var created = SafeBool(boss.Create);
            SafeBool(setup.Doc.RebuildDocument);
            var after = ReadBodies(setup.Part);
            var summary = $"Create={On(created)}, тел {before.Count}→{after.Count}; " + string.Join("; ", notes)
                + "; " + string.Join(" | ", after.Select(b => $"тел{b.Index} V={Raw(b.Volume)} центр {Point(b.Center)}"));
            step.Observe("A.6 (добавка с ChooseBodiesType=ksNewBody(0) поверх существующего тела): " + summary);
            step.Data["a_boss_new_body_bodies"] = after.Select(b => b.Describe()).ToArray();
            return summary;
        }
        catch (Exception ex)
        {
            return "A.6 упало: " + ex.GetType().Name + ": " + Unwrap(ex).Message;
        }
        finally
        {
            setup.DisposeQuietly();
        }
    }

    // -----------------------------------------------------------------------------------------
    // The two-body part
    // -----------------------------------------------------------------------------------------

    private sealed class TwoBodySetup
    {
        public ksDocument3D? Doc;
        public ksPart? Part;
        public bool Ok;
        public string? Failure;
        public List<BodyInfo> Bodies = new();
        public ksEntity? CutPlane;

        public void DisposeQuietly() => CloseQuietly(Doc);
    }

    /// <summary>
    /// Builds a part with two disjoint solids and records which route produced them. All three
    /// routes are measured, because "how do you even get a second body" is itself an open question
    /// for the adapter.
    /// </summary>
    private static TwoBodySetup? BuildTwoBody(ProbeStep step, KompasObject app, string tag, TargetResult? result)
    {
        var setup = new TwoBodySetup();
        try
        {
            setup.Doc = (ksDocument3D)app.Document3D();
            if (!setup.Doc.Create(true, true))
            {
                setup.Failure = "Document3D().Create(true,true) = false";
                return setup;
            }

            setup.Part = (ksPart)setup.Doc.GetPart(-1);
            var xy = (ksEntity)setup.Part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            var attempts = new List<string>();

            var plateSketch = RectSketch(setup.Part, xy, -PlateX / 2d, -PlateY / 2d, PlateX / 2d, PlateY / 2d, tag + " plate profile");
            var plate = (ksEntity)setup.Part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
            if (plate.GetDefinition() is not ksBaseExtrusionDefinition plateDefinition)
            {
                setup.Failure = "определение плиты не ksBaseExtrusionDefinition";
                return setup;
            }

            plateDefinition.SetSketch(plateSketch);
            plateDefinition.directionType = 0;
            plateDefinition.SetSideParam(true, Blind(), Thickness, 0d, false);
            if (!plate.Create() || SafeBool(setup.Doc.RebuildDocument) != true)
            {
                setup.Failure = "плита не создана";
                return setup;
            }

            setup.Bodies = ReadBodies(setup.Part);
            attempts.Add($"плита (NewEntity(24)): тел {setup.Bodies.Count}");

            // Route 1: a boss extrusion whose contour does not touch the plate.
            var blobSketch = CircleSketch(setup.Part, xy, BlobX, 0d, BlobRadius, tag + " blob profile");
            var blob = (ksEntity)setup.Part.NewEntity(TypeOf("o3d_bossExtrusion", KompasObjectTypes.BossExtrusion));
            var bossRoute = false;
            if (blob.GetDefinition() is ksBossExtrusionDefinition blobDefinition)
            {
                blobDefinition.SetSketch(blobSketch);
                blobDefinition.directionType = 0;
                blobDefinition.SetSideParam(true, Blind(), Thickness, 0d, false);
                var created = SafeBool(blob.Create);
                SafeBool(setup.Doc.RebuildDocument);
                setup.Bodies = ReadBodies(setup.Part);
                attempts.Add($"добавка (NewEntity(25)) изолированным кругом R{BlobRadius:0} в x={BlobX:0}: Create={On(created)}, тел {setup.Bodies.Count}");
                bossRoute = setup.Bodies.Count >= 2;
            }
            else
            {
                attempts.Add("добавка: определение не ksBossExtrusionDefinition");
            }

            // Route 2: a second base extrusion, if the boss merged into the plate or refused.
            if (!bossRoute && setup.Bodies.Count < 2)
            {
                var secondSketch = CircleSketch(setup.Part, xy, BlobX + 60d, 0d, BlobRadius, tag + " second base profile");
                var second = (ksEntity)setup.Part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
                if (second.GetDefinition() is ksBaseExtrusionDefinition secondDefinition)
                {
                    secondDefinition.SetSketch(secondSketch);
                    secondDefinition.directionType = 0;
                    secondDefinition.SetSideParam(true, Blind(), Thickness, 0d, false);
                    var created = SafeBool(second.Create);
                    SafeBool(setup.Doc.RebuildDocument);
                    setup.Bodies = ReadBodies(setup.Part);
                    attempts.Add($"второе базовое выдавливание (NewEntity(24)): Create={On(created)}, тел {setup.Bodies.Count}");
                }
            }

            // Route 3: split the plate with a through-slot, so the two halves are separate solids.
            if (setup.Bodies.Count < 2)
            {
                var slotSketch = RectSketch(setup.Part, xy, -6d, -PlateY, 6d, PlateY, tag + " slot profile");
                var slot = (ksEntity)setup.Part.NewEntity(TypeOf("o3d_cutExtrusion", KompasObjectTypes.CutExtrusion));
                if (slot.GetDefinition() is ksCutExtrusionDefinition slotDefinition)
                {
                    slotDefinition.SetSketch(slotSketch);
                    slotDefinition.directionType = 2;
                    slotDefinition.SetSideParam(true, ThroughAll(), 1d, 0d, false);
                    slotDefinition.SetSideParam(false, ThroughAll(), 1d, 0d, false);
                    var created = SafeBool(slot.Create);
                    SafeBool(setup.Doc.RebuildDocument);
                    setup.Bodies = ReadBodies(setup.Part);
                    attempts.Add($"рассечение плиты сквозным пазом: Create={On(created)}, тел {setup.Bodies.Count}");
                }
            }

            if (setup.Bodies.Count < 2)
            {
                setup.Failure = "два изолированных тела не получены; попытки: " + string.Join("; ", attempts);
                step.Observe("A.0: " + setup.Failure);
                return setup;
            }

            setup.CutPlane = OffsetPlane(setup.Part, xy, Thickness + 10d, true);
            if (setup.CutPlane is null)
            {
                setup.Failure = "плоскость смещения для реза не создана; попытки: " + string.Join("; ", attempts);
                return setup;
            }

            setup.Ok = true;
            if (result is not null)
            {
                result.BuildRoute = string.Join("; ", attempts);
            }

            step.Observe("A.0 маршруты построения двух тел: " + string.Join("; ", attempts));
            return setup;
        }
        catch (Exception ex)
        {
            setup.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Observe("A.0: построение упало — " + setup.Failure);
            return setup;
        }
    }

    // -----------------------------------------------------------------------------------------
    // One cut, measured per body
    // -----------------------------------------------------------------------------------------

    private sealed class CutAttempt
    {
        public required string Label { get; init; }
        public bool Created;
        public bool Rebuilt;
        public int ChangedIndex = -1;
        public int IntendedIndex = -1;
        public string Evidence = "-";
        public readonly List<string> CollectionEvidence = new();
        public string AddEvidence = "-";
        public string CollectionInterfaces = "-";
        public string CollectionMembers = "-";
        public int? CollectionCountAfterAdd;
        public int? ChooseTypeRequested;
        public int? ChooseTypeAfterSet;
        public int? ChooseTypeAfterCreate;
        public int? ChooseBodiesTypeRequested;
        public int? ChooseBodiesTypeAfterSet;
        public int? ChooseBodiesTypeRead;
        public bool? CutProperty;
        public double? RemovedVolume;
        public string? Failure;

        /// <summary>Exactly one body moved, and it is the one that was declared.</summary>
        public bool IntendedOnly => ChangedIndex >= 0 && ChangedIndex == IntendedIndex;
    }

    /// <summary>
    /// Cuts a Ø10 through-hole under the centre of body <paramref name="profileIndex"/>, declaring a
    /// target only if <paramref name="choice"/> says to. The verdict comes from per-body volumes
    /// before and after, never from a return code.
    /// </summary>
    private static CutAttempt RunCut(TwoBodySetup setup, int profileIndex, Choice choice, string label)
    {
        var attempt = new CutAttempt
        {
            Label = label,
            ChooseTypeRequested = choice.ChooseType,
            ChooseBodiesTypeRequested = choice.ChooseBodiesType,
        };
        var part = setup.Part;
        var doc = setup.Doc;
        if (part is null || doc is null || setup.CutPlane is null)
        {
            attempt.Failure = "сеанс не готов";
            attempt.Evidence = attempt.Failure;
            return attempt;
        }

        if (profileIndex < 0 || profileIndex >= setup.Bodies.Count)
        {
            attempt.Failure = $"индекс профиля {profileIndex} вне диапазона (тел {setup.Bodies.Count})";
            attempt.Evidence = attempt.Failure;
            return attempt;
        }

        try
        {
            var before = ReadBodies(part);
            var profileBody = before[profileIndex];
            attempt.IntendedIndex = choice.AddBodyIndex >= 0 ? choice.AddBodyIndex : profileIndex;
            var centre = profileBody.Center ?? new double[] { 0d, 0d, 0d };

            var circle = CircleSketch(part, setup.CutPlane, centre[0], centre[1], HoleRadius, "p6 cut profile");
            var cut = (ksEntity)part.NewEntity(TypeOf("o3d_cutExtrusion", KompasObjectTypes.CutExtrusion));
            if (cut.GetDefinition() is not ksCutExtrusionDefinition definition)
            {
                attempt.Failure = "определение вырезания не ksCutExtrusionDefinition";
                attempt.Evidence = attempt.Failure;
                return attempt;
            }

            attempt.CutProperty = SafeBool(() => definition.cut);

            if (choice.BeforeSketch && choice.Via != "none")
            {
                ApplyChoose(part, doc, definition, choice, before, attempt);
            }

            var sketchAccepted = SafeBool(() => definition.SetSketch(circle));

            if (!choice.BeforeSketch && choice.Via != "none")
            {
                ApplyChoose(part, doc, definition, choice, before, attempt);
            }

            definition.directionType = 2;
            definition.SetSideParam(true, ThroughAll(), 1d, 0d, false);
            definition.SetSideParam(false, ThroughAll(), 1d, 0d, false);

            attempt.Created = SafeBool(cut.Create) == true;
            attempt.Rebuilt = SafeBool(doc.RebuildDocument) == true;

            // Read the selectors back from a freshly obtained definition object: reading through
            // the same RCW cannot distinguish "the document stored it" from "the wrapper kept my
            // value" (the rule P2.1 established).
            if (cut.GetDefinition() is ksCutExtrusionDefinition reread)
            {
                attempt.ChooseTypeAfterCreate = SafeInt(() => reread.chooseType);
                attempt.CutProperty = SafeBool(() => reread.cut);
            }

            var after = ReadBodies(part);
            var changes = new List<string>();
            var changed = new List<int>();
            for (var i = 0; i < before.Count; i++)
            {
                var match = Nearest(after, before[i]);
                var removed = (before[i].Volume ?? 0d) - (match?.Volume ?? 0d);
                changes.Add($"тел{i} (центр {Point(before[i].Center)}): {Raw(before[i].Volume)} → {Raw(match?.Volume)}, ΔV={Raw(removed)}");
                if (Math.Abs(removed) > Tol)
                {
                    changed.Add(i);
                }
            }

            if (changed.Count == 1)
            {
                var hit = changed[0];
                attempt.RemovedVolume = (before[hit].Volume ?? 0d) - (Nearest(after, before[hit])?.Volume ?? 0d);
            }

            attempt.ChangedIndex = changed.Count == 1 ? changed[0] : -1;
            var amount = attempt.RemovedVolume is double removedVolume && Math.Abs(removedVolume - HoleVolume) <= Math.Max(Tol, 1e-6 * HoleVolume);
            var nothingRemoved = attempt.RemovedVolume is not double moved || Math.Abs(moved) <= Tol;
            var expectation = choice.ExpectRemoved
                ? amount
                    ? "снятое совпало с 250π"
                    : "ОЖИДАНИЕ (250π) НЕ ВЫПОЛНЕНО"
                : nothingRemoved
                    ? "материал не снят — заявленное тело подавило автоматический рез, что и ожидалось"
                    : "АВТОМАТИЧЕСКИЙ РЕЗ НЕ ПОДАВЛЕН — выбор не соблюдён";
            attempt.Evidence = $"Create={On(attempt.Created)}, rebuild={On(attempt.Rebuilt)}, SetSketch={On(sketchAccepted)}, изменилось тел: {changed.Count} [{string.Join(",", changed)}] при профиле под телом {profileIndex} и заявленной цели тел{attempt.IntendedIndex}; "
                + $"снято {Raw(attempt.RemovedVolume)} мм³ при ожидании 250π={Raw(HoleVolume)} ({expectation}); "
                + $"chooseType после Create={Raw(attempt.ChooseTypeAfterCreate)}, cut={On(attempt.CutProperty)}; "
                + string.Join(" | ", changes);
            if (attempt.Failure is not null)
            {
                attempt.Evidence += "; сбои: " + attempt.Failure;
            }

            return attempt;
        }
        catch (Exception ex)
        {
            attempt.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            attempt.Evidence = attempt.Failure;
            return attempt;
        }
    }

    /// <summary>
    /// Declares the target body according to <paramref name="choice"/>: the definition's
    /// <c>chooseType</c>, then <c>ChooseBodies()</c> — either its own <c>Add</c>, or the
    /// <c>BodyCollection()</c> it exposes — or the document choose manager.
    /// </summary>
    private static void ApplyChoose(ksPart part, ksDocument3D doc, ksCutExtrusionDefinition definition, Choice choice, List<BodyInfo> before, CutAttempt attempt)
    {
        if (choice.ChooseType is int chooseType)
        {
            var set = SafeBool(() =>
            {
                definition.chooseType = chooseType;
                return true;
            });
            attempt.ChooseTypeAfterSet = SafeInt(() => definition.chooseType);
            attempt.CollectionEvidence.Add($"chooseType := {chooseType} → сеттер {On(set)}, прочитано {Raw(attempt.ChooseTypeAfterSet)}");
        }

        var declared = choice.AddBodyIndex >= 0 && choice.AddBodyIndex < before.Count ? before[choice.AddBodyIndex] : before[0];
        var candidates = BodyCandidates(part, declared);

        if (choice.Via == "choosemng")
        {
            var manager = SafeObject(() => doc.GetChooseMng());
            if (manager is null)
            {
                attempt.AddEvidence = "GetChooseMng() → null";
                attempt.CollectionEvidence.Add(attempt.AddEvidence);
                return;
            }

            // One QI sweep per object, then every call goes through the interfaces it answered:
            // the sweep is ~1000 cross-process QueryInterfaces and must not be paid per candidate.
            var managerAnswered = AnsweredInterfaces(manager);
            attempt.CollectionInterfaces = string.Join(", ", managerAnswered.Select(i => i.FullName));
            var cleared = InvokeNoArgument(manager, managerAnswered, "UnChooseAll");
            var lines = candidates.Select(c => $"{c.Label}: Choose → {InvokeAnswered(manager, managerAnswered, "Choose", c.Candidate)}");
            attempt.AddEvidence = $"UnChooseAll → {cleared}; " + string.Join(" ; ", lines);
            attempt.CollectionCountAfterAdd = CollectionCount(manager, managerAnswered);
            attempt.CollectionEvidence.Add("GetChooseMng(): " + attempt.AddEvidence + $"; GetCount после: {Raw(attempt.CollectionCountAfterAdd)}");
            return;
        }

        object? collection;
        try
        {
            collection = definition.ChooseBodies();
        }
        catch (Exception ex)
        {
            attempt.AddEvidence = "ChooseBodies() бросил " + ex.GetType().Name + ": " + Unwrap(ex).Message;
            attempt.CollectionEvidence.Add(attempt.AddEvidence);
            return;
        }

        if (collection is null)
        {
            attempt.AddEvidence = "ChooseBodies() → null";
            attempt.CollectionEvidence.Add(attempt.AddEvidence);
            return;
        }

        var discovered = ComDiscovery.Probe(collection);
        attempt.CollectionInterfaces = string.Join(", ", discovered.Select(i => i.FullName + " [" + i.AssemblyName + "]"));
        attempt.CollectionMembers = string.Join(" | ", discovered.SelectMany(i => i.Members).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal));
        var answered = discovered
            .Select(i => (Interface: i, Type: ComDiscovery.LookupType(i.FullName)))
            .Where(p => p.Type is not null)
            .Select(p => (p.Interface.FullName, p.Type!))
            .ToList();
        attempt.ChooseBodiesTypeRead = ReadIntProperty(collection, answered, "ChooseBodiesType");
        attempt.CollectionEvidence.Add($"ChooseBodies() → интерфейс(ы) [{attempt.CollectionInterfaces}]; члены: {attempt.CollectionMembers}");
        attempt.CollectionEvidence.Add($"ChooseBodiesType прочитан по умолчанию: {Raw(attempt.ChooseBodiesTypeRead)}");

        if (choice.ChooseBodiesType is int bodiesType)
        {
            attempt.ChooseBodiesTypeAfterSet = WriteIntProperty(collection, answered, "ChooseBodiesType", bodiesType);
            attempt.CollectionEvidence.Add($"ChooseBodiesType := {bodiesType} → прочитано {Raw(attempt.ChooseBodiesTypeAfterSet)}");
        }

        if (choice.Via != "bodycollection")
        {
            // A.2 kept for the record: the ChooseBodies object itself, which exposes only
            // BodyCollection() and ChooseBodiesType — so there is nowhere to Add.
            var ownLines = candidates.Select(c => $"{c.Label}: Add → {InvokeAnswered(collection, answered, "Add", c.Candidate)}");
            attempt.AddEvidence = string.Join(" ; ", ownLines);
            attempt.CollectionCountAfterAdd = CollectionCount(collection, answered);
            attempt.CollectionEvidence.Add("добавление в сам объект ChooseBodies(): " + attempt.AddEvidence);
            return;
        }

        var bodies = SafeObject(() => InvokeGetBodyCollection(collection, answered));
        if (bodies is null)
        {
            attempt.AddEvidence = "ChooseBodies().BodyCollection() → null или бросил исключение";
            attempt.CollectionEvidence.Add(attempt.AddEvidence);
            return;
        }

        var bodyAnswered = AnsweredInterfaces(bodies);
        attempt.CollectionEvidence.Add($"ChooseBodies().BodyCollection() → [{string.Join(", ", bodyAnswered.Select(a => a.FullName))}]; счётчик до Add: {Raw(CollectionCount(bodies, bodyAnswered))}");
        var addLines = new List<string>();
        foreach (var (label, candidate) in candidates)
        {
            var result = InvokeAnswered(bodies, bodyAnswered, "Add", candidate);
            var count = CollectionCount(bodies, bodyAnswered);
            addLines.Add($"{label}: Add → {result} (счётчик после попытки {Raw(count)})");
            if (result.StartsWith("True", StringComparison.OrdinalIgnoreCase) || result.StartsWith("true", StringComparison.Ordinal))
            {
                // One accepted form is enough; offering the rest would stack bodies into the choice.
                break;
            }
        }

        attempt.AddEvidence = string.Join(" ; ", addLines);
        attempt.CollectionCountAfterAdd = CollectionCount(bodies, bodyAnswered);
        attempt.CollectionEvidence.Add("добавление тела в ChooseBodies().BodyCollection(): " + attempt.AddEvidence);
        attempt.CollectionEvidence.Add($"итоговый счётчик коллекции: {Raw(attempt.CollectionCountAfterAdd)}");
    }

    /// <summary>Writes the boss definition's own <c>chooseType</c> and reports the setter's answer.</summary>
    private static string SetValue(ksBossExtrusionDefinition definition, int value) =>
        On(SafeBool(() =>
        {
            definition.chooseType = value;
            return true;
        }));

    /// <summary>Offers body <paramref name="bodyIndex"/> to a choose collection and reports the answer.</summary>
    private static string AddFirstBody(object collection, List<(string FullName, Type Type)> answered, List<BodyInfo> bodies, int bodyIndex)
    {
        if (bodyIndex < 0 || bodyIndex >= bodies.Count)
        {
            return $"<индекс {bodyIndex} вне диапазона (тел {bodies.Count})>";
        }

        var candidate = bodies[bodyIndex].Element as ksEntity ?? bodies[bodyIndex].Element;
        return InvokeAnswered(collection, answered, "Add", candidate);
    }

    /// <summary>Calls the no-argument <c>BodyCollection()</c> the choose object exposes.</summary>
    private static object? InvokeGetBodyCollection(object target, List<(string FullName, Type Type)> answered)
    {
        foreach (var (_, type) in answered)
        {
            var method = type.GetMethod("BodyCollection", BindingFlags.Public | BindingFlags.Instance);
            if (method is null || method.GetParameters().Length != 0)
            {
                continue;
            }

            try
            {
                var value = method.Invoke(target, Array.Empty<object>());
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // Try the next answered interface.
            }
        }

        return null;
    }

    private static string InvokeNoArgument(object target, List<(string FullName, Type Type)> answered, string methodName)
    {
        foreach (var (fullName, type) in answered)
        {
            var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (method is null || method.GetParameters().Length != 0)
            {
                continue;
            }

            try
            {
                return $"{Members.Value(method.Invoke(target, Array.Empty<object>())!)} (через {fullName})";
            }
            catch (Exception ex)
            {
                return $"{Unwrap(ex).GetType().Name}: {Trim(Unwrap(ex).Message)} (через {fullName})";
            }
        }

        return $"<метод {methodName}() не объявлен ни на одном поддержанном интерфейсе>";
    }

    private static int? WriteIntProperty(object target, List<(string FullName, Type Type)> answered, string propertyName, int value)
    {
        foreach (var (_, type) in answered)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || !property.CanWrite)
            {
                continue;
            }

            try
            {
                property.SetValue(target, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
                return ReadIntProperty(target, answered, propertyName);
            }
            catch (Exception)
            {
                // Try the next answered interface.
            }
        }

        return ReadIntProperty(target, answered, propertyName);
    }

    /// <summary>The vendor interfaces an object answers, resolved to types. One sweep per object.</summary>
    private static List<(string FullName, Type Type)> AnsweredInterfaces(object target) =>
        ComDiscovery.Probe(target)
            .Select(i => (i.FullName, Type: ComDiscovery.LookupType(i.FullName)))
            .Where(p => p.Type is not null)
            .Select(p => (p.FullName, p.Type!))
            .ToList();

    /// <summary>The object variants to offer a choose collection — which one it accepts is the question.</summary>
    private static List<(string Label, object? Candidate)> BodyCandidates(ksPart part, BodyInfo target)
    {
        var candidates = new List<(string, object?)>
        {
            ($"элемент BodyCollection[{target.Index}] как есть", target.Element),
            ($"BodyCollection[{target.Index}] как ksEntity", target.Element as ksEntity),
        };

        if (target.Element is ksEntity targetEntity)
        {
            candidates.Add(($"((ksEntity)BodyCollection[{target.Index}]).GetDefinition()", SafeObject(targetEntity.GetDefinition)));
        }

        // Bodies are also model-tree entities of type o3d_body=115; that is the other thing a
        // collection of this kind is likely to accept.
        if (SafeObject(() => part.EntityCollection(TypeOf("o3d_body", KompasObjectTypes.Body))) is ksEntityCollection bodyEntities)
        {
            var count = SafeInt(bodyEntities.GetCount) ?? 0;
            for (var i = 0; i < count && i < 4; i++)
            {
                var index = i;
                var element = SafeObject(() => bodyEntities.GetByIndex(index));
                if (element is null)
                {
                    continue;
                }

                candidates.Add(($"EntityCollection(o3d_body=115)[{i}] как есть", element));
                candidates.Add(($"EntityCollection(o3d_body=115)[{i}] как ksEntity", element as ksEntity));
            }
        }

        return candidates;
    }

    /// <summary>Calls a one-argument method found among the interfaces the object answered.</summary>
    private static string InvokeAnswered(object target, List<(string FullName, Type Type)> answered, string methodName, object? argument)
    {
        if (argument is null)
        {
            return $"кандидат null — нечего передавать в {methodName}";
        }

        foreach (var (fullName, type) in answered)
        {
            MethodInfo? method;
            try
            {
                method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            }
            catch (AmbiguousMatchException)
            {
                method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1);
            }

            if (method?.GetParameters().Length != 1)
            {
                continue;
            }

            try
            {
                return $"{Members.Value(method.Invoke(target, new[] { argument }))} (через {fullName})";
            }
            catch (Exception ex)
            {
                return $"{Unwrap(ex).GetType().Name}: {Trim(Unwrap(ex).Message)} (через {fullName})";
            }
        }

        return $"<метод {methodName}(Object) не объявлен ни на одном поддержанном интерфейсе>";
    }

    private static int? CollectionCount(object collection, List<(string FullName, Type Type)> answered)
    {
        foreach (var (_, type) in answered)
        {
            var method = type.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Instance);
            if (method is null || method.GetParameters().Length != 0)
            {
                continue;
            }

            try
            {
                return Convert.ToInt32(method.Invoke(collection, Array.Empty<object>()) ?? 0, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Try the next answered interface.
            }
        }

        return null;
    }

    private static int? ReadIntProperty(object target, List<(string FullName, Type Type)> answered, string propertyName)
    {
        foreach (var (_, type) in answered)
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                continue;
            }

            try
            {
                var value = property.GetValue(target);
                return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Keep looking.
            }
        }

        return null;
    }

    private static object? SafeObject(Func<object?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Trim(string value) => value.Length <= 140 ? value : value[..140] + "…";

    // -----------------------------------------------------------------------------------------
    // Bodies: identity, volume, box
    // -----------------------------------------------------------------------------------------

    private sealed class BodyInfo
    {
        public required int Index { get; init; }
        public object? Element;
        public double? Volume;
        public double[]? Min;
        public double[]? Max;

        public double[]? Center => Min is null || Max is null ? null : new[] { (Min[0] + Max[0]) / 2d, (Min[1] + Max[1]) / 2d, (Min[2] + Max[2]) / 2d };

        public string Describe() =>
            $"тело {Index}: объём {Raw(Volume)} мм³, габарит {Box(Min, Max)}, центр {Point(Center)}, элемент {RuntimeName(Element)}";
    }

    /// <summary>
    /// Per-body volume read exactly the way the adapter reads it —
    /// <c>CalcMassInertiaProperties(ST_MIX_MM|ST_MIX_KG).v()</c> — so the numbers compare with
    /// P0.7 and P2.1.
    /// </summary>
    private static List<BodyInfo> ReadBodies(ksPart part)
    {
        var list = new List<BodyInfo>();
        try
        {
            var collection = (ksBodyCollection)part.BodyCollection();
            var count = collection.GetCount();
            for (var i = 0; i < count; i++)
            {
                var element = collection.GetByIndex(i);
                var body = element as ksBody ?? (element as ksEntity)?.GetDefinition() as ksBody;
                var info = new BodyInfo { Index = i, Element = element };
                if (body is not null)
                {
                    info.Volume = BodyVolume(body);
                    var box = new double[6];
                    if (SafeBool(() => body.GetGabarit(out box[0], out box[1], out box[2], out box[3], out box[4], out box[5])) == true)
                    {
                        info.Min = new[] { box[0], box[1], box[2] };
                        info.Max = new[] { box[3], box[4], box[5] };
                    }
                }

                list.Add(info);
            }
        }
        catch (Exception)
        {
            // An empty list is the honest answer; the caller reports "no bodies".
        }

        return list;
    }

    private static double? BodyVolume(ksBody body)
    {
        try
        {
            if (body.CalcMassInertiaProperties((uint)MixMmKg) is not ksMassInertiaParam properties)
            {
                return null;
            }

            var value = typeof(ksMassInertiaParam).GetProperty("v")?.GetValue(properties);
            return value is null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BodyInfo? Nearest(List<BodyInfo> bodies, BodyInfo reference) =>
        reference.Center is null
            ? null
            : bodies.Where(b => b.Center is not null).OrderBy(b => Length(b.Center, reference.Center) ?? double.MaxValue).FirstOrDefault();

    // =========================================================================================
    // B — deleting entities out of an existing sketch
    // =========================================================================================

    private enum ClearMode
    {
        /// <summary>Refs returned by the draw call, deleted in the same BeginEdit session.</summary>
        SameSessionRefs,

        /// <summary>Refs returned by the draw call, deleted after EndEdit and a NEW BeginEdit.</summary>
        NextSessionRefs,

        /// <summary>Nothing known in advance: locate objects with ksFindObj, then ksDeleteObj.</summary>
        FindByPoint,

        /// <summary>Draw inside a group, then clear or destroy the group.</summary>
        Group,

        /// <summary>Point the dependent feature at a fresh sketch instead of touching the old one.</summary>
        SwapSketchOnFeature,
    }

    private sealed class ClearRoute
    {
        public required ClearMode Mode { get; init; }
        public required string Label { get; init; }
        public string Evidence = "-";
        public bool Deleted;
        public bool CleanSingleProfile;
        public double? Volume;
        public int BodyCount;
        public string BoxAfter = "-";
        public string Leftovers = "-";
        public bool? SurvivedReopen;
        public string ReopenEvidence = "-";
        public string? SavedDocument;
        public string? Failure;
    }

    private static List<ClearRoute> MeasureSketchClearing(ProbeStep step, KompasObject app)
    {
        var routes = new List<ClearRoute>();

        // Control: what a sketch with two disjoint contours produces. Without this the "dirty"
        // baseline is a guess and every deletion result below is uninterpretable.
        var control = DirtyControl(step, app);
        routes.Add(control);
        step.Observe($"B [{control.Label}] → {control.Evidence}");

        foreach (var mode in new[] { ClearMode.SameSessionRefs, ClearMode.NextSessionRefs, ClearMode.FindByPoint, ClearMode.Group, ClearMode.SwapSketchOnFeature })
        {
            var route = RunClearRoute(step, app, mode);
            routes.Add(route);
            step.Observe($"B [{route.Label}] → {route.Evidence}");
            step.Observe($"   профиль после перерисовки: объём {Raw(route.Volume)} мм³ при ожидании 500π={Raw(CleanVolume)}, тел {route.BodyCount}, габарит {route.BoxAfter}; остатки старого контура: {route.Leftovers}");
            step.Observe($"   save→close→reopen: {route.ReopenEvidence}");
        }

        step.Data["b_routes"] = routes.Select(r => $"{r.Label}: удалено={On(r.Deleted)} чисто={On(r.CleanSingleProfile)} V={Raw(r.Volume)} тел={r.BodyCount} остатки=«{r.Leftovers}» reopen={r.ReopenEvidence}").ToArray();
        step.Data["b_verdict_clean_and_persisted"] = routes.Where(r => r.CleanSingleProfile && r.SurvivedReopen == true).Select(r => r.Label).ToArray();

        var best = routes.FirstOrDefault(r => r.CleanSingleProfile && r.SurvivedReopen == true);
        step.Data["b_minimal_sequence"] = best?.Mode switch
        {
            ClearMode.FindByPoint =>
                "def = (ksSketchDefinition)sketchEntity.GetDefinition(); ed = (ksDocument2D)def.BeginEdit(); "
                + "ref = ed.ksFindObj(x, y, 1e-3) для каждой точки старого контура (перебирать, пока молчит); "
                + "ed.ksDeleteObj(ref) — ответ 1 = успех, 0 = отказ (соглашение снято измерением: ksExistObj до=1, после удаления=0); "
                + "ed.ksCircle(…)/ksLineSeg(…) нового профиля; def.EndEdit(); затем NewEntity(o3d_baseExtrusion=24)+SetSketch+SetSideParam+Create",
            ClearMode.SameSessionRefs =>
                "ed = (ksDocument2D)def.BeginEdit(); ref = ed.ksLineSeg/ksCircle (возвращают ref объекта); ed.ksDeleteObj(ref) → 1 = успех; "
                + "в том же сеансе нарисовать новый профиль; def.EndEdit(); Create()",
            _ => "<нет>",
        } ?? "ни один маршрут не дал чистого профиля, сохраняющегося после reopen";

        var byRef = routes.FirstOrDefault(r => r.Mode == ClearMode.NextSessionRefs);
        var byGroup = routes.FirstOrDefault(r => r.Mode == ClearMode.Group);
        var bySwap = routes.FirstOrDefault(r => r.Mode == ClearMode.SwapSketchOnFeature);
        step.Data["b_negative_rows"] = new[]
        {
            $"ref переживают EndEdit: {On(byRef?.Deleted)} (объём после перерисовки {Raw(byRef?.Volume)} вместо {Raw(CleanVolume)})",
            $"групповой ksClearGroup/ksDestroyObjects очищает эскиз: {On(byGroup?.Deleted)} (объём {Raw(byGroup?.Volume)})",
            $"переключение SetSketch на существующем признаке: {On(bySwap?.Deleted)} (объём {Raw(bySwap?.Volume)}; EntityCollection(24) не даёт признаков — см. P2.3)",
        };
        return routes;
    }

    /// <summary>Rect + circle in one sketch, extruded: the measurement of "dirty".</summary>
    private static ClearRoute DirtyControl(ProbeStep step, KompasObject app)
    {
        var route = new ClearRoute { Mode = ClearMode.SameSessionRefs, Label = "КОНТРОЛЬ: эскиз «прямоугольник + окружность» без правок" };
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                route.Failure = "документ не создан";
                route.Evidence = route.Failure;
                return route;
            }

            var part = (ksPart)doc.GetPart(-1);
            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
            sketch.name = "p6 dirty profile";
            var definition = (ksSketchDefinition)sketch.GetDefinition();
            definition.SetPlane(xy);
            sketch.Create();
            var editor = (ksDocument2D)definition.BeginEdit();
            var refs = DrawRectAndCircle(editor);
            definition.EndEdit();
            SafeBool(doc.RebuildDocument);

            var before = ReadBodies(part);
            var beforeVolume = before.Sum(b => b.Volume ?? 0d);
            var extruded = TryExtrudeBase(part, sketch, NewProfileDepth);
            SafeBool(doc.RebuildDocument);
            var after = ReadBodies(part);
            route.BodyCount = after.Count;
            route.Volume = after.Sum(b => b.Volume ?? 0d) - beforeVolume;
            route.BoxAfter = PartBox(part);
            route.Evidence = $"отрисовка вернула refs [{string.Join(",", refs)}]; выдавливание Create={On(extruded)}; тел {before.Count}→{after.Count}; объём {Raw(route.Volume)} мм³ при ожиданиях: "
                + $"сумма контуров {Raw(DirtySumVolume)}, круг как отверстие {Raw(DirtyDifferenceVolume)}, только круг {Raw(CleanVolume)}";
            step.Data["b_dirty_refs"] = refs.Select(r => Raw(r)).ToArray();
            step.Data["b_dirty_volume"] = Raw(route.Volume);
            step.Data["b_dirty_gabarit"] = route.BoxAfter;
            step.Data["b_dirty_bodies"] = after.Count;
        }
        catch (Exception ex)
        {
            route.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            route.Evidence = route.Failure;
        }
        finally
        {
            CloseQuietly(doc);
        }

        return route;
    }

    private static ClearRoute RunClearRoute(ProbeStep step, KompasObject app, ClearMode mode)
    {
        var label = mode switch
        {
            ClearMode.SameSessionRefs => "R1 ksDeleteObj по ref из вызова отрисовки, тот же сеанс BeginEdit",
            ClearMode.NextSessionRefs => "R2 ksDeleteObj по ref из прошлого сеанса, после EndEdit→BeginEdit",
            ClearMode.FindByPoint => "R3 ksFindObj по известным точкам → ksDeleteObj (сеанс, который ничего не рисовал)",
            ClearMode.Group => "R4 ksNewGroup/ksAddObjGroup + ksClearGroup(deleteTmp=true) и ksDestroyObjects",
            ClearMode.SwapSketchOnFeature => "R5 новый эскиз + SetSketch существующего признака (вместо очистки старого)",
            _ => mode.ToString(),
        };
        var route = new ClearRoute { Mode = mode, Label = label };
        ksDocument3D? doc = null;
        ksSketchDefinition? sketchDefinition = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                route.Failure = "документ не создан";
                route.Evidence = route.Failure;
                return route;
            }

            var part = (ksPart)doc.GetPart(-1);
            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
            sketch.name = "p6 to be cleared (" + mode + ")";
            var definition = (ksSketchDefinition)sketch.GetDefinition();
            definition.SetPlane(xy);
            sketch.Create();
            sketchDefinition = definition;

            var notes = new List<string>();

            switch (mode)
            {
                case ClearMode.SameSessionRefs:
                {
                    var editor = (ksDocument2D)definition.BeginEdit();
                    var refs = DrawRectAndCircle(editor);
                    notes.Add($"refs из вызовов отрисовки: [{string.Join(",", refs.Select(r => Raw(r)))}]");
                    notes.Add($"ksExistObj до удаления: [{string.Join(",", refs.Select(r => Raw(SafeInt(() => editor.ksExistObj(r)))))}]");
                    var deleted = refs.Select(r => SafeInt(() => editor.ksDeleteObj(r))).ToArray();
                    var after = refs.Select(r => SafeInt(() => editor.ksExistObj(r))).ToArray();
                    notes.Add($"ksDeleteObj: [{string.Join(",", deleted.Select(Raw))}]; ksExistObj после: [{string.Join(",", after.Select(Raw))}]");
                    notes.Add("соглашение об ответе снято измерением: 1 = успех/существует, 0 = отказ/отсутствует (обратное по сравнению с Boolean)");
                    route.Deleted = deleted.All(d => d == 1) && after.All(e => e == 0);
                    editor.ksCircle(0d, 0d, NewCircleR, 1);
                    definition.EndEdit();
                    break;
                }

                case ClearMode.NextSessionRefs:
                {
                    var first = (ksDocument2D)definition.BeginEdit();
                    var refs = DrawRectAndCircle(first);
                    definition.EndEdit();
                    SafeBool(doc.RebuildDocument);
                    var editor = (ksDocument2D)definition.BeginEdit();
                    notes.Add($"refs из первого сеанса: [{string.Join(",", refs.Select(r => Raw(r)))}]; между сеансами был EndEdit");
                    notes.Add($"ksExistObj в новом сеансе: [{string.Join(",", refs.Select(r => Raw(SafeInt(() => editor.ksExistObj(r)))))}]");
                    var deleted = refs.Select(r => SafeInt(() => editor.ksDeleteObj(r))).ToArray();
                    var after = refs.Select(r => SafeInt(() => editor.ksExistObj(r))).ToArray();
                    notes.Add($"ksDeleteObj в новом сеансе: [{string.Join(",", deleted.Select(Raw))}]; ksExistObj после: [{string.Join(",", after.Select(Raw))}]");
                    notes.Add("ref, полученные в прошлом сеансе BeginEdit, в новом не действуют (проверка того и другого — в массивах выше)");
                    route.Deleted = deleted.All(d => d == 1) && refs.All(r => SafeInt(() => editor.ksExistObj(r)) == 0) && deleted.Any(d => d == 1);
                    editor.ksCircle(0d, 0d, NewCircleR, 1);
                    definition.EndEdit();
                    break;
                }

                case ClearMode.FindByPoint:
                {
                    var first = (ksDocument2D)definition.BeginEdit();
                    var drawn = DrawRectAndCircle(first);
                    definition.EndEdit();
                    SafeBool(doc.RebuildDocument);
                    var probes = ProbePoints();
                    var editor = (ksDocument2D)definition.BeginEdit();
                    var found = new List<int>();
                    for (var round = 0; round < 4; round++)
                    {
                        var thisRound = new List<string>();
                        foreach (var (x, y) in probes)
                        {
                            foreach (var limit in new[] { 1e-3d, 1d })
                            {
                                var reference = SafeInt(() => editor.ksFindObj(x, y, limit)) ?? 0;
                                if (reference == 0 || found.Contains(reference))
                                {
                                    continue;
                                }

                                found.Add(reference);
                                var deleted = SafeInt(() => editor.ksDeleteObj(reference));
                                var exists = SafeInt(() => editor.ksExistObj(reference));
                                thisRound.Add($"({Num(x)},{Num(y)}) limit={Num(limit)} → ref {reference}: ksDeleteObj→{Raw(deleted)}, ksExistObj→{Raw(exists)}");
                            }
                        }

                        notes.Add($"раунд {round}: {thisRound.Count} — {string.Join("; ", thisRound)}");
                        if (thisRound.Count == 0)
                        {
                            break;
                        }
                    }

                    var stillFound = probes.Count(p => SafeInt(() => editor.ksFindObj(p.X, p.Y, 1d)) is int r && r != 0);
                    notes.Add($"найдено {found.Count} объектов; после цикла ksFindObj отвечает на {stillFound} из {probes.Length} контрольных точек");
                    route.Deleted = found.Count > 0 && stillFound == 0;
                    editor.ksCircle(0d, 0d, NewCircleR, 1);
                    definition.EndEdit();
                    notes.Add($"справка: ksFindObj(0,0,1) рядом с центром новой окружности до отрисовки → {Raw(SafeInt(() => editor.ksFindObj(0d, 0d, 1d)))}");
                    break;
                }

                case ClearMode.Group:
                {
                    var editor = (ksDocument2D)definition.BeginEdit();
                    var groupId = SafeInt(() => editor.ksNewGroup(0)) ?? 0;
                    var refs = DrawRectAndCircle(editor);
                    var added = refs.Select(r => SafeInt(() => editor.ksAddObjGroup(groupId, r))).ToArray();
                    var ended = SafeInt(editor.ksEndGroup);
                    var cleared = SafeInt(() => editor.ksClearGroup(groupId, true));
                    var destroyed = SafeInt(() => editor.ksDestroyObjects(groupId));
                    var exists = refs.Select(r => SafeInt(() => editor.ksExistObj(r))).ToArray();
                    notes.Add($"ksNewGroup(0)→{Raw(groupId)}; refs [{string.Join(",", refs.Select(r => Raw(r)))}]; ksAddObjGroup→[{string.Join(",", added.Select(Raw))}]; ksEndGroup→{Raw(ended)}; "
                        + $"ksClearGroup(g,true)→{Raw(cleared)}; ksDestroyObjects(g)→{Raw(destroyed)}; ksExistObj после: [{string.Join(",", exists.Select(Raw))}]");
                    route.Deleted = exists.All(e => e == 0);
                    editor.ksCircle(0d, 0d, NewCircleR, 1);
                    definition.EndEdit();
                    break;
                }

                default:
                {
                    var first = (ksDocument2D)definition.BeginEdit();
                    var refs = DrawRectAndCircle(first);
                    definition.EndEdit();
                    SafeBool(doc.RebuildDocument);
                    notes.Add($"исходный эскиз: {refs.Length} объектов, refs [{string.Join(",", refs.Select(r => Raw(r)))}]");
                    var dirty = TryExtrudeBase(part, sketch, NewProfileDepth);
                    SafeBool(doc.RebuildDocument);
                    notes.Add($"грязный контур выдавлен: Create={On(dirty)}, объём {Raw(SumVolume(part))} при ожиданиях {Raw(DirtySumVolume)}/{Raw(DirtyDifferenceVolume)}");

                    var clean = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
                    clean.name = "p6 replacement profile";
                    var cleanDefinition = (ksSketchDefinition)clean.GetDefinition();
                    cleanDefinition.SetPlane(xy);
                    clean.Create();
                    var cleanEditor = (ksDocument2D)cleanDefinition.BeginEdit();
                    var cleanRefs = cleanEditor.ksCircle(0d, 0d, NewCircleR, 1);
                    cleanDefinition.EndEdit();
                    notes.Add($"новый эскиз: ksCircle → {Raw(cleanRefs)}");

                    var swapped = SwapSketchOnExistingExtrusion(part, clean);
                    SafeBool(part.Update);
                    SafeBool(doc.RebuildDocument);
                    notes.Add("переключение признака: " + swapped);
                    route.Deleted = swapped.Contains("→ true", StringComparison.Ordinal);
                    break;
                }
            }

            // The common cleanliness measurement. R5 already has a body (the dirty one) and only
            // re-points it, so for R5 the absolute volume is the evidence; for the others the model
            // was empty before this extrusion, so the delta is the evidence.
            var bodiesBefore = ReadBodies(part);
            var beforeVolume = bodiesBefore.Sum(b => b.Volume ?? 0d);
            bool extruded;
            if (mode == ClearMode.SwapSketchOnFeature)
            {
                extruded = bodiesBefore.Count > 0;
            }
            else
            {
                extruded = TryExtrudeBase(part, sketch, NewProfileDepth);
                SafeBool(doc.RebuildDocument);
            }

            var afterBodies = ReadBodies(part);
            var totalVolume = afterBodies.Sum(b => b.Volume ?? 0d);
            route.Volume = mode == ClearMode.SwapSketchOnFeature ? totalVolume : totalVolume - beforeVolume;
            route.BodyCount = afterBodies.Count;
            route.BoxAfter = PartBox(part);
            route.CleanSingleProfile = Math.Abs(route.Volume.GetValueOrDefault() - CleanVolume) <= Math.Max(Tol, 1e-6 * CleanVolume)
                && route.BodyCount == 1;
            route.Leftovers = mode == ClearMode.SwapSketchOnFeature
                ? "<не применимо: старый эскиз не менялся, оцените объём>"
                : FindLeftovers(sketchDefinition, ProbePoints());
            route.Evidence = $"выдавливание нового профиля {On(extruded)}; объём {Raw(route.Volume)} мм³ при ожидании 500π={Raw(CleanVolume)} (неудалённый профиль дал бы {Raw(DirtySumVolume)} или {Raw(DirtyDifferenceVolume)}); тел {route.BodyCount}; габарит {route.BoxAfter}; "
                + string.Join(" || ", notes);

            // Persistence: a route that only lives inside one editing session is not a route.
            if (!route.CleanSingleProfile)
            {
                route.SurvivedReopen = null;
                route.ReopenEvidence = "не проверялось: профиль не чистый";
                return route;
            }

            var path = WorkFile($"P2_6_Clear_{mode}.m3d");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var saved = SafeBool(() => doc.SaveAs(path));
            step.Observe($"B [{label}]: SaveAs → {On(saved)}; документ закрывается перед повторным открытием.");
            CloseQuietly(doc);
            var reopenedDoc = (ksDocument3D)app.Document3D();
            if (saved == true && reopenedDoc.Open(path, true))
            {
                var reopenedPart = (ksPart)reopenedDoc.GetPart(-1);
                var volume = SumVolume(reopenedPart);
                var sketchEntities = SafeObject(() => reopenedPart.EntityCollection(TypeOf("o3d_sketch", KompasObjectTypes.Sketch))) as ksEntityCollection;
                var sketchCount = sketchEntities is null ? 0 : (SafeInt(sketchEntities.GetCount) ?? 0);
                string leftovers;
                ksSketchDefinition? reopenedDefinition = null;
                if (sketchCount > 0 && SafeObject(() => sketchEntities!.GetByIndex(0)) is ksEntity firstSketch)
                {
                    reopenedDefinition = firstSketch.GetDefinition() as ksSketchDefinition;
                    leftovers = FindLeftovers(reopenedDefinition, ProbePoints());
                }
                else
                {
                    leftovers = $" эскизов через EntityCollection(o3d_sketch=5): {sketchCount} — контрольные точки после reopen не снимаются";
                }

                route.SurvivedReopen = Math.Abs(volume.GetValueOrDefault() - CleanVolume) <= Math.Max(Tol, 1e-6 * CleanVolume);
                route.ReopenEvidence = $"объём после reopen {Raw(volume)} при ожидании {Raw(CleanVolume)}, габарит {PartBox(reopenedPart)}, эскизов в дереве {sketchCount}, контрольные точки старого контура: {leftovers}";
                route.SavedDocument = path;
                step.Artifacts.Add(path);
                CloseQuietly(reopenedDoc);
            }
            else
            {
                CloseQuietly(reopenedDoc);
                route.SurvivedReopen = false;
                route.ReopenEvidence = $"SaveAs={On(saved)}, повторное открытие не удалось";
            }

            return route;
        }
        catch (Exception ex)
        {
            route.Failure = ex.GetType().Name + ": " + Unwrap(ex).Message;
            route.Evidence = route.Failure;
            return route;
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    /// <summary>Are the old profile's objects still reachable? This is what "replace" must kill.</summary>
    private static string FindLeftovers(ksSketchDefinition? definition, (double X, double Y)[] probes)
    {
        if (definition is null)
        {
            return "<определения эскиза нет>";
        }

        try
        {
            var editor = (ksDocument2D?)definition.BeginEdit();
            if (editor is null)
            {
                return "<BeginEdit не удался>";
            }

            var hits = new List<string>();
            foreach (var (x, y) in probes)
            {
                var reference = SafeInt(() => editor.ksFindObj(x, y, 1d)) ?? 0;
                if (reference != 0)
                {
                    hits.Add($"({Num(x)},{Num(y)}) → ref {reference}");
                }
            }

            SafeBool(definition.EndEdit);
            return hits.Count == 0
                ? "остатков нет: ksFindObj молчит во всех контрольных точках старого контура"
                : "остатки: " + string.Join(", ", hits);
        }
        catch (Exception ex)
        {
            return "<" + ex.GetType().Name + ": " + Trim(Unwrap(ex).Message) + ">";
        }
    }

    /// <summary>Points that lie on the OLD profile and nowhere on the new circle.</summary>
    private static (double X, double Y)[] ProbePoints() => new[]
    {
        ((RectU1 + RectU2) / 2d, RectV1),
        ((RectU1 + RectU2) / 2d, RectV2),
        (RectU1, (RectV1 + RectV2) / 2d),
        (RectU2, (RectV1 + RectV2) / 2d),
        (OldCircleU + OldCircleR, 0d),
        (OldCircleU, OldCircleR),
    };

    private static int[] DrawRectAndCircle(ksDocument2D editor) => new[]
    {
        editor.ksLineSeg(RectU1, RectV1, RectU2, RectV1, 1),
        editor.ksLineSeg(RectU2, RectV1, RectU2, RectV2, 1),
        editor.ksLineSeg(RectU2, RectV2, RectU1, RectV2, 1),
        editor.ksLineSeg(RectU1, RectV2, RectU1, RectV1, 1),
        editor.ksCircle(OldCircleU, 0d, OldCircleR, 1),
    };

    private static bool TryExtrudeBase(ksPart part, ksEntity sketch, double depth)
    {
        var extrusion = (ksEntity)part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
        if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            return false;
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, Blind(), depth, 0d, false);
        return SafeBool(extrusion.Create) == true;
    }

    /// <summary>R5: re-point every base extrusion of the part at the replacement sketch.</summary>
    private static string SwapSketchOnExistingExtrusion(ksPart part, ksEntity replacementSketch)
    {
        try
        {
            var features = (ksEntityCollection)part.EntityCollection(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
            var count = SafeInt(features.GetCount) ?? 0;
            var lines = new List<string>();
            var anyOk = false;
            for (var i = 0; i < count; i++)
            {
                var index = i;
                if (SafeObject(() => features.GetByIndex(index)) is not ksEntity entity)
                {
                    lines.Add($"[{i}] не ksEntity");
                    continue;
                }

                var ok = SafeBool(() =>
                {
                    if (entity.GetDefinition() is not ksBaseExtrusionDefinition definition)
                    {
                        return false;
                    }

                    if (!definition.SetSketch(replacementSketch))
                    {
                        return false;
                    }

                    return entity.Update();
                }) == true;
                anyOk |= ok;
                lines.Add($"[{i}] SetSketch(новый) + Update → {On(ok)}");
            }

            return $"{count} признаков типа 24 в EntityCollection: {string.Join("; ", lines)}; хоть один переключён: {anyOk}";
        }
        catch (Exception ex)
        {
            return "не удалось: " + ex.GetType().Name + ": " + Trim(Unwrap(ex).Message);
        }
    }

    private static double? SumVolume(ksPart part) => ReadBodies(part).Sum(b => b.Volume ?? 0d);

    private static string PartBox(ksPart part)
    {
        try
        {
            if (part.GetGabarit(true, true, out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return Box(new[] { x1, y1, z1 }, new[] { x2, y2, z2 });
            }
        }
        catch (Exception)
        {
            // fall through
        }

        return "<GetGabarit=false>";
    }

    // =========================================================================================
    // C — the raw keys of the kompas_probe_units path
    // =========================================================================================

    private sealed class UnitKeys
    {
        public string? Failure;
        public readonly Dictionary<string, string> Raw = new(StringComparer.Ordinal);
        public bool LineMmConfirmed;
        public bool CircumferenceConfirmed;
        public bool AreaConfirmed;
        public string BodyRoute = "-";
        public string SketchRoute = "-";
        public int EdgeCountBody;
        public int EdgeCountSketch;
    }

    private static UnitKeys MeasureUnitKeys(ProbeStep step, KompasObject app)
    {
        var keys = new UnitKeys();

        // ---- C.1 edges of a real body ----------------------------------------------------------
        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                keys.Failure = "документ C.1 не создан";
                return keys;
            }

            var part = (ksPart)doc.GetPart(-1);
            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));

            // Plate [0..100]×[0..80]×[0..10] gives a straight edge of exactly 100 mm; a Ø20 boss
            // standing on it gives a circular edge of R10 and a planar disk face of 100π.
            var plate = RectSketch(part, xy, 0d, 0d, PlateX, PlateY, "p6 units plate profile");
            var plateEntity = (ksEntity)part.NewEntity(TypeOf("o3d_baseExtrusion", KompasObjectTypes.BaseExtrusion));
            if (plateEntity.GetDefinition() is ksBaseExtrusionDefinition plateDefinition)
            {
                plateDefinition.SetSketch(plate);
                plateDefinition.directionType = 0;
                plateDefinition.SetSideParam(true, Blind(), Thickness, 0d, false);
                plateEntity.Create();
            }

            SafeBool(doc.RebuildDocument);

            // The disk must STAND on the plate: a sketch on XY with a blind 5 mm extrusion would
            // sit entirely inside the 10 mm plate and produce no new face and no circular edge,
            // which is what the first P2.6 run measured. Offsetting the plane to the top face
            // makes the cylinder real and gives the R10 edges and the 100π disk face.
            var topPlane = OffsetPlane(part, xy, Thickness, true) ?? xy;
            var diskSketch = CircleSketch(part, topPlane, 50d, 40d, KnownRadiusMm, "p6 units disk profile");
            var disk = (ksEntity)part.NewEntity(TypeOf("o3d_bossExtrusion", KompasObjectTypes.BossExtrusion));
            if (disk.GetDefinition() is ksBossExtrusionDefinition diskDefinition)
            {
                diskDefinition.SetSketch(diskSketch);
                diskDefinition.directionType = 0;
                // Height 7, not 5: with h = 5 the cylinder's lateral area 2πrh equals its base
                // area πr² (both 100π), so a face reading of 314.159 could not be attributed.
                // Deliberately unequal so the 100π row of G08 is unambiguous.
                diskDefinition.SetSideParam(true, Blind(), 7d, 0d, false);
                disk.Create();
            }

            SafeBool(doc.RebuildDocument);
            step.Data["c_body_count"] = ReadBodies(part).Count;
            step.Data["c_part_gabarit"] = PartBox(part);
            step.Data["c_gabarit_keys"] = new[] { "gabarit_x_mm", "gabarit_y_mm", "gabarit_z_mm" };

            if (part.GetMainBody() is not ksBody body)
            {
                keys.Failure = "GetMainBody() → null — рёбер тела нет";
                return keys;
            }

            var faces = (ksFaceCollection)body.FaceCollection();
            var faceCount = faces.GetCount();
            var seen = new HashSet<IntPtr>();
            var readings = new List<EdgeReading>();
            var faceTable = new List<string>();
            double? diskArea = null;
            double? lateralArea = null;
            var index = 0;
            for (var f = 0; f < faceCount; f++)
            {
                if (AsDefinition<ksFaceDefinition>(faces.GetByIndex(f)) is not ksFaceDefinition face)
                {
                    continue;
                }

                var faceArea = SafeDouble(() => face.GetArea((uint)KompasUnits.LengthMm));
                var faceAreaCm2 = SafeDouble(() => face.GetArea((uint)KompasUnits.Centimetres));
                var faceIsPlanar = SafeBool(face.IsPlanar);
                var faceIsCylinder = SafeBool(face.IsCylinder);

                // Collected per FACE, not per edge: an edge belongs to several faces and the
                // de-duplication below would hide the disk face behind the cylindrical one. With
                // height 7 the disk (πr²=100π) and the lateral surface (2πrh=140π) differ, so the
                // two rows cannot be confused for each other.
                faceTable.Add($"грань {f}: planar={On(faceIsPlanar)} cylinder={On(faceIsCylinder)}; GetArea(1)={Raw(faceArea)} мм², GetArea(0)={Raw(faceAreaCm2)} см²");
                if (faceIsPlanar == true && faceArea is double pa && Math.Abs(pa - KnownDiskArea) <= Math.Max(Tol, 1e-6 * KnownDiskArea))
                {
                    diskArea ??= pa;
                }

                if (faceIsCylinder == true && faceArea is double ca)
                {
                    lateralArea ??= ca;
                }

                var edges = (ksEdgeCollection)face.EdgeCollection();
                for (var e = 0; e < edges.GetCount(); e++)
                {
                    if (AsDefinition<ksEdgeDefinition>(edges.GetByIndex(e)) is not ksEdgeDefinition edge)
                    {
                        continue;
                    }

                    var pointer = ReferenceId(edge);
                    if (!seen.Add(pointer))
                    {
                        continue;
                    }

                    index++;
                    var reading = ReadEdge(edge, index);
                    reading.FaceAreaMm2 = faceArea;
                    reading.FaceIsPlanar = faceIsPlanar;
                    readings.Add(reading);
                    AddEdgeKeys(keys.Raw, reading);
                }
            }

            keys.EdgeCountBody = index;
            step.Data["c_edge_count_from_body"] = index;
            step.Data["c_face_count"] = faceCount;
            step.Observe($"C.1 из тела: граней {faceCount}, уникальных рёбер {index}:");
            foreach (var reading in readings)
            {
                step.Observe("   " + reading.Describe());
            }

            var straight = readings.FirstOrDefault(r => r.Straight == true && r.EdgeLengthMm is double l && Math.Abs(l - KnownLineMm) <= 1e-6);
            var circle = readings.FirstOrDefault(r => r.IsCircle == true && r.RadiusFromParam is double rr && Math.Abs(rr - KnownRadiusMm) <= 1e-6);
            keys.LineMmConfirmed = straight is not null;
            keys.CircumferenceConfirmed = circle?.CurveLengthMm is double cl && Math.Abs(cl - KnownCircumference) <= 1e-6;
            keys.BodyRoute = $"прямое ребро 100 мм: {(straight is null ? "<не найдено>" : straight.DescribeShort())}; окружность R10: {(circle is null ? "<не найдено>" : circle.DescribeShort())}";
            step.Observe("C.1 " + keys.BodyRoute);
            step.Observe($"C.1 таблица граней ({faceTable.Count}):");
            foreach (var row in faceTable)
            {
                step.Observe("   " + row);
            }

            keys.AreaConfirmed = diskArea is double da && Math.Abs(da - KnownDiskArea) <= Math.Max(Tol, 1e-6 * KnownDiskArea);
            var expectedLateral = 2d * Math.PI * KnownRadiusMm * 7d;
            step.Data["c_face_table"] = faceTable.ToArray();
            step.Data["c_disk_face_area_mm2"] = Raw(diskArea);
            step.Data["c_disk_face_area_expected"] = Raw(KnownDiskArea);
            step.Data["c_lateral_face_area_mm2"] = Raw(lateralArea);
            step.Data["c_lateral_face_area_expected"] = Raw(expectedLateral);
            step.Data["c_circumference_from_curve_mm"] = Raw(circle?.CurveLengthMm);
            step.Data["c_circumference_from_edge_mm"] = Raw(circle?.EdgeLengthMm);
            step.Data["c_circumference_expected"] = Raw(KnownCircumference);
            step.Data["c_line_length_from_edge_mm"] = Raw(straight?.EdgeLengthMm);
            step.Data["c_line_metric_length_mm"] = Raw(straight?.MetricLengthOverRange);
            step.Data["c_radius_from_get_radius_mm"] = Raw(circle?.RadiusFromGetRadius);
            step.Data["c_radius_from_property_mm"] = Raw(circle?.RadiusFromParam);
            step.Data["c_radius_from_bbox_mm"] = Raw(circle?.RadiusFromBox);
            step.Data["c_radius_from_circumference_mm"] = Raw(circle?.RadiusFromLength);
            step.Observe($"C.1 площадь плоской грани-диска GetArea(1) = {Raw(diskArea)} мм² при ожидании 100π={Raw(KnownDiskArea)} (боковая цилиндрическая грань при этом {Raw(lateralArea)} мм² против 2πrh={Raw(expectedLateral)} — числа различаются, путаницы «диск или боковая» нет); "
                + $"длина окружности из тела: {Raw(circle?.CurveLengthMm)} мм при 20π={Raw(KnownCircumference)}; радиус из get_radius()={Raw(circle?.RadiusFromGetRadius)}, из L/2π={Raw(circle?.RadiusFromLength)}, из габарита={Raw(circle?.RadiusFromBox)}.");
            step.Observe("C.1 ключи, которые пишет kompas_probe_units (имена — как в Api5Session.Exchange.UnitProbe): "
                + string.Join(", ", keys.Raw.Where(p => p.Key.StartsWith("edge", StringComparison.Ordinal) || p.Key.StartsWith("gabarit", StringComparison.Ordinal)).Select(p => p.Key + "=" + p.Value)));
        }
        catch (Exception ex)
        {
            keys.Failure = "C.1: " + ex.GetType().Name + ": " + Unwrap(ex).Message;
            step.Errors.Add(keys.Failure);
        }
        finally
        {
            CloseQuietly(doc);
        }

        // ---- C.2 sketch-only entities: same calls, no body in the model ------------------------
        doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                step.Observe("C.2: документ не создан, эскизное чтение не снято.");
                return keys;
            }

            var part = (ksPart)doc.GetPart(-1);
            var xy = (ksEntity)part.GetDefaultEntity(TypeOf("o3d_planeXOY", 1));
            var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
            sketch.name = "p6 units sketch only";
            var definition = (ksSketchDefinition)sketch.GetDefinition();
            definition.SetPlane(xy);
            sketch.Create();
            var editor = (ksDocument2D)definition.BeginEdit();
            var lineRef = editor.ksLineSeg(0d, 0d, KnownLineMm, 0d, 1);
            var circleRef = editor.ksCircle(0d, 150d, KnownRadiusMm, 1);
            definition.EndEdit();
            SafeBool(doc.RebuildDocument);
            step.Observe($"C.2 эскиз без тела: ksLineSeg вернул {Raw(lineRef)}, ksCircle вернул {Raw(circleRef)} — это те самые значения, по которым R1 ищет ksDeleteObj.");
            step.Data["c_sketch_refs"] = new[] { Raw(lineRef), Raw(circleRef) };
            step.Observe($"C.2 GetMainBody() при отсутствии тела → {(part.GetMainBody() is null ? "null (ни граней, ни рёбер тела недоступны)" : "нечто есть, см. ниже")}");

            var collection = SafeObject(() => part.EntityCollection(TypeOf("o3d_edge", KompasObjectTypes.Edge))) as ksEntityCollection;
            var count = collection is null ? 0 : (SafeInt(collection.GetCount) ?? 0);
            keys.EdgeCountSketch = count;
            step.Data["c_sketch_edge_collection_count"] = count;
            step.Observe($"C.2 EntityCollection(o3d_edge=7) в модели без тела: объектов {count}.");

            var sketchReadings = new List<EdgeReading>();
            var index = 0;
            for (var i = 0; i < count; i++)
            {
                var elementIndex = i;
                var element = SafeObject(() => collection!.GetByIndex(elementIndex));
                if (AsDefinition<ksEdgeDefinition>(element) is not ksEdgeDefinition edge)
                {
                    step.Observe($"   объект {i}: не отвечает ksEdgeDefinition ({RuntimeName(element)}).");
                    continue;
                }

                index++;
                var reading = ReadEdge(edge, 1000 + index);
                sketchReadings.Add(reading);
                AddEdgeKeys(keys.Raw, reading);
                step.Observe("   " + reading.Describe());
            }

            var sketchLine = sketchReadings.FirstOrDefault(r => r.EdgeLengthMm is double l && Math.Abs(l - KnownLineMm) <= 1e-6);
            var sketchCircle = sketchReadings.FirstOrDefault(r => r.IsCircle == true && r.RadiusFromParam is double rr && Math.Abs(rr - KnownRadiusMm) <= 1e-6);
            keys.SketchRoute = $"из EntityCollection(7): объектов {count}, из них рёбер {index}; прямое 100 мм {(sketchLine is null ? "НЕ найдено" : "найдено (" + sketchLine.DescribeShort() + ")")}; "
                + $"окружность R10 {(sketchCircle is null ? "НЕ найдена" : "найдена (" + sketchCircle.DescribeShort() + ")")}; flag sketchEdge: [{string.Join(",", sketchReadings.Select(r => On(r.SketchEdge)).Distinct(StringComparer.Ordinal))}]";
            step.Observe("C.2 " + keys.SketchRoute);
            step.Data["c_sketch_line_reading"] = sketchLine?.DescribeShort() ?? "<нет>";
            step.Data["c_sketch_circle_reading"] = sketchCircle?.DescribeShort() ?? "<нет>";
            step.Data["c_sketch_area_available"] = sketchReadings.Any(r => r.FaceAreaMm2 is not null)
                ? "есть граневые площади"
                : "у эскизных рёбер грани нет — площадь 100π из эскиза неоткуда взять напрямую";
        }
        catch (Exception ex)
        {
            step.Errors.Add("C.2: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
            step.Observe("C.2 не доигран: " + ex.GetType().Name + ": " + Unwrap(ex).Message);
        }
        finally
        {
            CloseQuietly(doc);
        }

        step.Data["c_raw_keys"] = keys.Raw.Select(p => p.Key + " = " + p.Value).ToArray();
        step.Data["c_body_route"] = keys.BodyRoute;
        step.Data["c_sketch_route"] = keys.SketchRoute;
        step.Data["c_note_adapter_key_mismatch"] =
            "Api5Session.Exchange.UnitProbe записывает ключи edge{1..4}_length_selector_{0..3} и observation edge_count, а сверку масштаба делает по "
            + "edge_length_selector_1 / edge_length_selector_0 (без индекса) — имени, которое запись не создаёт. Это наблюдение по коду адаптера, не измерение COM.";
        return keys;
    }

    private static void AddEdgeKeys(Dictionary<string, string> raw, EdgeReading reading)
    {
        foreach (var selector in new uint[] { 0, 1, 2, 3 })
        {
            raw[$"edge{reading.Index}_length_selector_{selector}"] = SelectorValue(reading, selector);
        }

        raw[$"edge{reading.Index}_sketch_edge"] = On(reading.SketchEdge);
        raw[$"edge{reading.Index}_kind"] = reading.Kind;
        raw[$"curve{reading.Index}_length_selector_0"] = Raw(reading.CurveLengthCm);
        raw[$"curve{reading.Index}_length_selector_1"] = Raw(reading.CurveLengthMm);
        raw[$"curve{reading.Index}_length_selector_2"] = Raw(reading.CurveLengthDm);
        raw[$"curve{reading.Index}_length_selector_3"] = Raw(reading.CurveLengthM);
        raw[$"curve{reading.Index}_metric_length_param_range"] = Raw(reading.MetricLengthOverRange);
        raw[$"curve{reading.Index}_param_min"] = Raw(reading.ParamMin);
        raw[$"curve{reading.Index}_param_max"] = Raw(reading.ParamMax);
        raw[$"curve{reading.Index}_radius_from_get_radius"] = Raw(reading.RadiusFromGetRadius);
        raw[$"curve{reading.Index}_radius_from_property"] = Raw(reading.RadiusFromParam);
        raw[$"curve{reading.Index}_radius_from_bbox"] = Raw(reading.RadiusFromBox);
        raw[$"curve{reading.Index}_radius_from_circumference"] = Raw(reading.RadiusFromLength);
        raw[$"curve{reading.Index}_is_closed"] = On(reading.Closed);
        raw[$"face{reading.Index}_area_selector_1"] = Raw(reading.FaceAreaMm2);
        raw[$"curve{reading.Index}_gabarit"] = Box(reading.BoxMin, reading.BoxMax);
    }

    private static string SelectorValue(EdgeReading reading, uint selector) => selector switch
    {
        0 => Raw(reading.EdgeLengthCm),
        1 => Raw(reading.EdgeLengthMm),
        2 => Raw(reading.EdgeLengthDm),
        3 => Raw(reading.EdgeLengthM),
        _ => "null",
    };

    private sealed class EdgeReading
    {
        public required int Index { get; init; }
        public bool? Straight;
        public bool? IsCircle;
        public bool? IsArc;
        public bool? SketchEdge;
        public bool? Closed;
        public bool? FaceIsPlanar;
        public double? EdgeLengthCm;
        public double? EdgeLengthMm;
        public double? EdgeLengthDm;
        public double? EdgeLengthM;
        public double? CurveLengthCm;
        public double? CurveLengthMm;
        public double? CurveLengthDm;
        public double? CurveLengthM;
        public double? MetricLengthOverRange;
        public double? ParamMin;
        public double? ParamMax;
        public double? RadiusFromParam;
        public double? RadiusFromGetRadius;
        public double? RadiusFromBox;
        public double? RadiusFromLength;
        public double? FaceAreaMm2;
        public double[]? BoxMin;
        public double[]? BoxMax;
        public string CurveParamClrType = "-";
        public string Error = "-";

        public string Kind => IsCircle == true ? "circle" : IsArc == true ? "arc" : Straight == true ? "straight" : "other";

        public string Describe() =>
            $"ребро {Index} ({Kind}): sketchEdge={On(SketchEdge)} closed={On(Closed)}; "
            + $"edge.GetLength 0/1/2/3 = {Raw(EdgeLengthCm)}/{Raw(EdgeLengthMm)}/{Raw(EdgeLengthDm)}/{Raw(EdgeLengthM)}; "
            + $"curve.GetLength 0/1/2/3 = {Raw(CurveLengthCm)}/{Raw(CurveLengthMm)}/{Raw(CurveLengthDm)}/{Raw(CurveLengthM)}; "
            + $"GetMetricLength(paramMin..paramMax)={Raw(MetricLengthOverRange)} при param∈[{Raw(ParamMin)}..{Raw(ParamMax)}]; "
            + $"радиус: get_radius()={Raw(RadiusFromGetRadius)}, radius={Raw(RadiusFromParam)}, из габарита={Raw(RadiusFromBox)}, из L/2π={Raw(RadiusFromLength)}; "
            + $"габарит кривой {Box(BoxMin, BoxMax)}; GetArea(1) грани={Raw(FaceAreaMm2)} (planar={On(FaceIsPlanar)}); GetCurveParam() → {CurveParamClrType}"
            + (Error == "-" ? string.Empty : " ; ошибки: " + Error);

        public string DescribeShort() =>
            $"ребро {Index}: edge.GetLength(1)={Raw(EdgeLengthMm)}, curve.GetLength(1)={Raw(CurveLengthMm)}, GetMetricLength={Raw(MetricLengthOverRange)}, get_radius()={Raw(RadiusFromGetRadius)}, radius(L/2π)={Raw(RadiusFromLength)}";
    }

    private static EdgeReading ReadEdge(ksEdgeDefinition edge, int index)
    {
        var reading = new EdgeReading { Index = index };
        var errors = new List<string>();
        reading.Straight = SafeBool(edge.IsStraight);
        reading.IsCircle = SafeBool(edge.IsCircle);
        reading.IsArc = SafeBool(edge.IsArc);
        reading.SketchEdge = SafeBool(() => edge.sketchEdge);
        foreach (var selector in new uint[] { 0, 1, 2, 3 })
        {
            var value = SafeDouble(() => edge.GetLength(selector));
            switch (selector)
            {
                case 0: reading.EdgeLengthCm = value; break;
                case 1: reading.EdgeLengthMm = value; break;
                case 2: reading.EdgeLengthDm = value; break;
                case 3: reading.EdgeLengthM = value; break;
            }
        }

        object? curveObject;
        try
        {
            curveObject = edge.GetCurve3D();
        }
        catch (Exception ex)
        {
            errors.Add("GetCurve3D " + ex.GetType().Name);
            reading.Error = string.Join("; ", errors);
            return reading;
        }

        if (curveObject is not ksCurve3D curve)
        {
            errors.Add("GetCurve3D() → " + RuntimeName(curveObject) + ", не ksCurve3D");
            reading.Error = string.Join("; ", errors);
            return reading;
        }

        reading.Closed = SafeBool(curve.IsClosed);
        reading.ParamMin = SafeDouble(curve.GetParamMin);
        reading.ParamMax = SafeDouble(curve.GetParamMax);
        foreach (var selector in new uint[] { 0, 1, 2, 3 })
        {
            var value = SafeDouble(() => curve.GetLength(selector));
            switch (selector)
            {
                case 0: reading.CurveLengthCm = value; break;
                case 1: reading.CurveLengthMm = value; break;
                case 2: reading.CurveLengthDm = value; break;
                case 3: reading.CurveLengthM = value; break;
            }
        }

        if (reading.ParamMin is double p0 && reading.ParamMax is double p1)
        {
            reading.MetricLengthOverRange = SafeDouble(() => curve.GetMetricLength(p0, p1));
        }

        var box = new double[6];
        if (SafeBool(() => curve.GetGabarit(out box[0], out box[1], out box[2], out box[3], out box[4], out box[5])) == true)
        {
            reading.BoxMin = new[] { box[0], box[1], box[2] };
            reading.BoxMax = new[] { box[3], box[4], box[5] };
            reading.RadiusFromBox = Math.Max(Math.Abs(box[3] - box[0]), Math.Abs(box[4] - box[1])) / 2d;
        }

        object? curveParam;
        try
        {
            curveParam = curve.GetCurveParam();
        }
        catch (Exception ex)
        {
            errors.Add("GetCurveParam " + ex.GetType().Name);
            curveParam = null;
        }

        if (curveParam is null)
        {
            errors.Add("GetCurveParam() → null");
        }
        else
        {
            // Deliberately no ComDiscovery.Probe here: one sweep is ~1000 cross-process
            // QueryInterfaces, and this runs per edge. The cast below is itself a QI, and the type
            // name is the report.
            reading.CurveParamClrType = RuntimeName(curveParam);
            if (curveParam is ksCircle3dParam circleParam)
            {
                reading.RadiusFromParam = SafeDouble(() => circleParam.radius);

                // The task asks for the raw accessor by name, so it is called the way COM sees it
                // rather than through the C# property sugar (same vtable slot, different spelling).
                reading.RadiusFromGetRadius = SafeDouble(() => (double)typeof(ksCircle3dParam)
                    .GetMethod("get_radius", BindingFlags.Public | BindingFlags.Instance)!.Invoke(circleParam, Array.Empty<object>())!);
            }
            else
            {
                errors.Add("GetCurveParam() не отвечает ksCircle3dParam");
            }
        }

        var lengthForRadius = reading.CurveLengthMm ?? reading.EdgeLengthMm;
        reading.RadiusFromLength = lengthForRadius is double l && l > 0 ? l / (2d * Math.PI) : (double?)null;
        reading.Error = errors.Count == 0 ? "-" : string.Join("; ", errors);
        return reading;
    }

    // =========================================================================================
    // Verdict
    // =========================================================================================

    private static void Conclude(ProbeStep step, TargetResult a, List<ClearRoute> b, UnitKeys c)
    {
        var cleanPersisted = b.Where(r => r.CleanSingleProfile && r.SurvivedReopen == true).Select(r => r.Label).ToList();
        var cleanAny = b.Where(r => r.CleanSingleProfile).Select(r => r.Label).ToList();

        step.Data["a_verdict"] = a.Failure is not null
            ? "A НЕ ИЗМЕРЕНО: " + a.Failure
            : a.Working is not null
                ? "явное целевое тело достижимо: " + a.Working.Label
                : "явное целевое тело не достигнуто: " + a.ContradictionEvidence;
        step.Data["b_verdict_clean_and_persisted"] = cleanPersisted.ToArray();
        step.Data["b_verdict_clean_not_persisted"] = cleanAny.Except(cleanPersisted, StringComparer.Ordinal).ToArray();
        step.Data["c_verdict"] = new[]
        {
            "линия 100 мм = " + On(c.LineMmConfirmed),
            "длина окружности 20π = " + On(c.CircumferenceConfirmed),
            "площадь диска 100π = " + On(c.AreaConfirmed),
        };

        var aSummary = a.Failure is not null
            ? $"A не измерено: {a.Failure}."
            : $"A: когда цель НЕ указана, режется то тело, под которым контур: профиль под телом 0 изменил тело {a.DefaultChangedIndex}, профиль под телом 1 — тело {a.FarBodyChangedIndex} (объёма второго тела это не коснулось). "
                + $"Явное задание цели: {(a.Working is null ? "НЕ работает" : $"работает — «{a.Working.Label}»")} (проверено противоречием: заявлено тело 0 при контуре над телом 1 → {"автоматический рез подавлен: " + a.ContradictionEvidence}). "
                + $"Добавка с ChooseBodiesType=ksNewBody: {a.NewBodyEvidence}. Порядок вызовов: {a.OrderEvidence}. Минимальная последовательность: {a.MinimalSequence}";

        var bSummary = cleanPersisted.Count > 0
            ? $"B: очистка существующего эскиза работает — {string.Join("; ", cleanPersisted)}: после удаления и новой отрисовки объём = 500π и это переживает save→close→reopen; "
                + "соглашение ksDocument2D обратное Boolean (1 = успех/существует, 0 = отказ/отсутствует), а ref объекта живёт только в том сеансе BeginEdit, где получен (R2); "
                + "перебрать все объекты фрагмента нечем — ksFirstObj/ksNextObj/ksGetObjCount в interop нет, поэтому delete_entities опирается на точки, известные адаптеру (ksFindObj)"
            : cleanAny.Count > 0
                ? $"B: чистый профиль получен ({string.Join("; ", cleanAny)}), но reopen его не подтверждает"
                : $"B: ни один объявленный в API5 маршрут (ksDeleteObj по ref из отрисовки, по ref из прошлого сеанса, через ksFindObj, через группу, переключение эскиза у признака) не дал чистого одноконтурного профиля. Контроль грязного эскиза: {b.FirstOrDefault()?.Evidence ?? "<нет>"}";

        var cSummary = c.Failure is not null
            ? $"C не измерено: {c.Failure}"
            : $"C: из тела (рёбер {c.EdgeCountBody}) длина 100 мм = {On(c.LineMmConfirmed)}, длина окружности 20π = {On(c.CircumferenceConfirmed)}, площадь диска 100π = {On(c.AreaConfirmed)}; {c.BodyRoute}; из эскиза без тела: {c.SketchRoute}";

        step.Observe("ИТОГ " + aSummary);
        step.Observe("ИТОГ " + bSummary);
        step.Observe("ИТОГ " + cSummary);

        var aOk = a.Working is not null;
        var bOk = cleanPersisted.Count > 0;
        var cOk = c.Failure is null && c.LineMmConfirmed && c.CircumferenceConfirmed && c.AreaConfirmed;

        if (aOk && bOk && cOk)
        {
            step.Pass($"A, B и C измерены и пригодны для построения инструментов. {aSummary} {bSummary}. {cSummary}.");
            return;
        }

        step.Verdict = Verdict.Fail;
        step.Conclusion = $"Раздельный итог трёх вопросов — A: {(aOk ? "PASS" : "NOT AVAILABLE")}; B: {(bOk ? "PASS" : "FAIL")}; C: {(cOk ? "PASS" : "FAIL")}. {aSummary} {bSummary}. {cSummary}.";
    }

    // =========================================================================================
    // Shared construction + recording helpers (local copies on purpose, as in P2.5)
    // =========================================================================================

    private static ksEntity? OffsetPlane(ksPart part, ksEntity basePlane, double offsetMm, bool direction)
    {
        var plane = (ksEntity)part.NewEntity(TypeOf("o3d_planeOffset", KompasObjectTypes.PlaneOffset));
        if (plane.GetDefinition() is not ksPlaneOffsetDefinition definition)
        {
            return null;
        }

        definition.SetPlane(basePlane);
        definition.offset = offsetMm;
        definition.direction = direction;
        return plane.Create() ? plane : null;
    }

    private static ksEntity RectSketch(ksPart part, ksEntity plane, double u1, double v1, double u2, double v2, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksLineSeg(u1, v1, u2, v1, 1);
        editor.ksLineSeg(u2, v1, u2, v2, 1);
        editor.ksLineSeg(u2, v2, u1, v2, 1);
        editor.ksLineSeg(u1, v2, u1, v1, 1);
        definition.EndEdit();
        return sketch;
    }

    private static ksEntity CircleSketch(ksPart part, ksEntity plane, double u, double v, double radius, string name)
    {
        var sketch = (ksEntity)part.NewEntity(TypeOf("o3d_sketch", KompasObjectTypes.Sketch));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksCircle(u, v, radius, 1);
        definition.EndEdit();
        return sketch;
    }

    private static TInterface? AsDefinition<TInterface>(object? element)
        where TInterface : class
    {
        if (element is TInterface direct)
        {
            return direct;
        }

        try
        {
            return element is ksEntity entity ? entity.GetDefinition() as TInterface : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IntPtr ReferenceId(object? value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            var pointer = Marshal.GetIUnknownForObject(value);
            Marshal.Release(pointer);
            return pointer;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    private static bool? SafeBool(Func<bool> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? SafeInt(Func<int> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CloseQuietly(ksDocument3D? doc)
    {
        if (doc is null)
        {
            return;
        }

        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // The failure that matters was recorded by the caller.
        }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException! : ex;

    private static string RuntimeName(object? value) => value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    private static string On(bool? value) => value switch
    {
        null => "<исключение>",
        true => "true",
        false => "false",
    };

    private static string On(bool value) => value ? "true" : "false";

    private static string Raw(double? value) => value is double d ? (double.IsFinite(d) ? Zero(d).ToString("R", CultureInfo.InvariantCulture) : "NaN") : "null";

    private static string Raw(double value) => double.IsFinite(value) ? Zero(value).ToString("R", CultureInfo.InvariantCulture) : "NaN";

    private static string Raw(int? value) => value is int i ? Raw(i) : "null";

    /// <summary>Distinct overload so an <see cref="int"/> argument is not ambiguous with the double form.</summary>
    private static string Raw(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static double Zero(double value) => value == 0d ? 0d : value;

    private static string Num(double? value) => value is double d ? Zero(d).ToString("0.###", CultureInfo.InvariantCulture) : "null";

    private static string Point(double[]? value) => value is null ? "<нет>" : $"({string.Join(", ", value.Select(v => Zero(v).ToString("0.####", CultureInfo.InvariantCulture)))})";

    private static string Box(double[]? min, double[]? max) => min is null || max is null
        ? "<нет>"
        : $"x∈[{Num(min[0])}..{Num(max[0])}], y∈[{Num(min[1])}..{Num(max[1])}], z∈[{Num(min[2])}..{Num(max[2])}]";

    private static double? Length(double[]? from, double[]? to)
    {
        if (from is null || to is null)
        {
            return null;
        }

        var dx = from[0] - to[0];
        var dy = from[1] - to[1];
        var dz = from[2] - to[2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
