using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe H-2 — the inputs of the fillet feature ITSELF through <c>IFillet.BaseObjects</c>, on a live feature.</summary>
/// <remarks>
/// <b>The question.</b> MEASURED: probe H measured the recomputation of a feature from a SUBSTITUTED
/// input — the remembered plate corner edges (<c>cornerEdges.Take(2)</c>) went into
/// <c>BaseObjects</c>; its geometry is therefore correct but its "APPLIES" label is not. The decisive
/// question is different: does <c>BaseObjects</c> of a live fillet return the very objects the feature
/// would accept BACK? If yes, an addressing route exists and <c>edit</c> can go through it; if no, it
/// is proven that the feature input cannot be addressed and the verdict stays <c>blocked_api</c>, but
/// with a precise formulation.
/// <b>Order of the experiments and why it is this one.</b>
/// <list type="number">
/// <item>Plate 100×80×10, four vertical R3 corners (reference 79922.74333882307).</item>
/// <item>Save, close, reopen. This is order §2: "Do not use COM objects or public references saved
/// before the fillet." After reopen we certainly hold no object captured at creation — so everything
/// we read comes from the LIVE feature.</item>
/// <item>Read <c>BaseObjects</c> without mutation: VARIANT/SAFEARRAY type, length, available
/// interfaces, usability of each element. Three cases are told apart explicitly — empty value, a
/// different array type, a marshalling error.</item>
/// <item>Control: check that the <c>BaseObjects</c> elements have geometric meaning at all — compare
/// their number and, where possible, the type against the expectation.</item>
/// <item>Reduce 4→2 and 4→3 using EXACTLY the objects from <c>BaseObjects</c>, without Clear and
/// without searching the edges of the final body. This checks COMPOSITION, not count: a set of three
/// is distinguishable from "broke and rebuilt four".</item>
/// <item>If the reduction is confirmed — save → close → reopen → read back.</item>
/// </list>
/// <b>Why 4→3, not only 4→2.</b> MEASURED: references 79961.37166941153 (two corners) and
/// 79942.05750411731 (three) are already measured on this rig. Three is "the same size minus one", a
/// set that cannot be produced by a mere coincidence of the number: if after the write the volume
/// lands on four corners the write was ignored; on two — something other than what we asked for
/// happened; only the three-corner reference proves that OUR set was accepted. That is the
/// distinction of composition from count.
/// <b>What the probe does NOT do.</b> It does not call <c>Clear()</c> before writing from
/// <c>BaseObjects</c> (that would substitute the subject: the order forbids prior emptying), does not
/// search the final body edges and does not touch the MCP public reference registry — a direct probe
/// must work without it.
/// History: docs/decisions/probes.md#fillet-base-objects</remarks>
internal sealed class FilletBaseObjectsProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;
    private const double Radius3 = 3d;

    private const short Fillet = 34;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private KompasObject _app = null!;
    private IApplication? _app7;

    public FilletBaseObjectsProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Writes the report from inside a run, for the <c>--keep</c> diagnostic path.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        var stem = "fillet-base-objects";
        report.Flush(
            Path.Combine(options.ReportDir, stem + ".json"),
            Path.Combine(options.ReportDir, stem + ".md"));
    }

    public void Run()
    {
        Launch();
        if (_app is null)
        {
            return;
        }

        try
        {
            // The decisive line goes first and on its own document: everything after it refines it.
            var live = BuildLiveFillet();
            if (live is not null)
            {
                ReadBaseObjects(live);
                ReduceUsingBaseObjects(live);
            }

            // ── differentiating controls. Without them "the volume moved to the three-corner
            //    reference" proves no more than probe H did: a recomputation from a substituted input
            //    gives the SAME picture. Each runs on its own document (its own plate) so that no
            //    state is inherited from another.
            SubstituteOneEdge();
            EnlargeUsingBaseObjects();

            // ── Addressing on a model with TWO fillets of one radius: a mandatory acceptance
            //    criterion (order §"checking addressing on a model with two fillets of equal radius:
            //    only the selected feature changes"). A single feature with no neighbour cannot tell
            //    "addressing" from "the only candidate".
            AddressAmongTwoFillets();

            // Reconciliation with probe H — not a retelling but a check against the numbers of THIS
            // run: it recomputes whether what was attributed to probe H follows from what we got.
            ReconcileWithProbeH();
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("H2.X", "Необработанное исключение пробы H-2");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Finish();
        }
    }

    // ════════════════════════════════════════════════════════ preparing the live feature ══

    /// <summary>Plate with R3 fillets on four vertical corners, SAVED, CLOSED and REOPENED. Returns the
    /// feature obtained after the reopen.</summary>
    /// <param name="label">Scenario label: each control has its own document and file, because reducing
    /// the set is irreversible and on a shared document the experiments would measure one another.</param>
    /// <param name="filledCorners">How many of the four corners to fillet at the FIRST creation of the
    /// feature. Default is all four (reference 79922.74333882307). The substitution control needs three:
    /// the fourth corner then stays free and its edge can be offered as the one to add. Building a set of
    /// four and then looking for a "free corner" is impossible — at r=3 on all corners no free corner
    /// remains at all.</param>
    private LiveFillet? BuildLiveFillet(string label = "H2.1", int filledCorners = 4)
    {
        var step = _report.Begin(
            label + ":live",
            "Живое скругление после save→close→reopen",
            "Можно ли получить IFillet, не держа ни одного объекта, захваченного при создании?");

        ksDocument3D? doc = null;
        var path = Path.Combine(_options.WorkDir, $"h2-{label.Replace(':', '-')}.m3d");
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, label) is null)
            {
                step.Fail("Пластина не построена.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Corner edges BEFORE the fillet — only for creating the feature. Afterwards they are not
            // needed and will NOT enter any measurement: after reopen we do not have them anyway.
            var corners = VerticalCornerEdges(part, step);
            step.Data["corner_edges_before_create"] = corners.Count;
            if (corners.Count != 4)
            {
                step.Fail($"Вертикальных угловых рёбер {corners.Count}, нужно ровно 4 — эталон не годится.");
                Close(doc);
                return null;
            }

            // The FIRST `filledCorners` of the four corners are filleted. The order in which edges are
            // returned is non-deterministic, but that does not matter for the reference: only the COUNT
            // of corners matters, and the volume does not depend on which corner is chosen (the formula
            // is symmetric). The substitution control takes 3 — the fourth corner then stays free and
            // its edge can be offered as the one to add.
            var toFillet = corners.Take(filledCorners).ToList();
            step.Data["corners_to_fillet"] = toFillet.Count;

            var created = CreateFillet(step, part, toFillet, Radius3);
            if (created is null)
            {
                Close(doc);
                return null;
            }

            // Rebuild — as in probe H: on the document part, not on the feature (a ksPart has no
            // Document member; the compiler confirmed this).
            doc.RebuildDocument();
            var expectedCreate = PlateVolume - filledCorners * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeAfterCreate = Api5.Volume(part);
            step.Data["volume_after_create"] = Api5.Num(volumeAfterCreate);
            step.Data["expected_at_create"] = Api5.Num(expectedCreate);
            if (volumeAfterCreate is not double vc || Math.Abs(vc - expectedCreate) > Tolerance(expectedCreate))
            {
                step.Fail($"Скругление {filledCorners} углов не дало эталон: {Api5.Num(volumeAfterCreate)} против {Api5.Num(expectedCreate)}.");
                Close(doc);
                return null;
            }

            // ── this is where the link to creation is broken ──
            doc.SaveAs(path);
            Close(doc);
            doc = null;
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Open(path) != true)
            {
                step.Fail("Повторно открыть сохранённый документ не удалось.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();
            var reopenedPart = (ksPart)doc.GetPart(-1);
            var volumeAfterReopen = Api5.Volume(reopenedPart);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);

            var container = Container(step, doc);
            if (container is null)
            {
                Close(doc);
                return null;
            }

            var filletCount = Api5.SafeInt(() => container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 1)
            {
                step.Fail($"Скруглений в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 1 — сопоставление неоднозначно.");
                Close(doc);
                return null;
            }

            if (container.Fillets[0] is not IFillet live)
            {
                step.Fail("Элемент Fillets[0] не отдаёт IFillet.");
                Close(doc);
                return null;
            }

            if (volumeAfterReopen is not double vr || Math.Abs(vr - expectedCreate) > Tolerance(expectedCreate))
            {
                step.Fail($"После reopen объём {Api5.Num(volumeAfterReopen)} не на эталоне {filledCorners} углов {Api5.Num(expectedCreate)} — признак не выжил, мерить нечего.");
                Close(doc);
                return null;
            }

            step.Data["saved_path"] = path;
            step.Observe($"Признак выжил reopen: V={Api5.Num(volumeAfterReopen)} при эталоне {Api5.Num(expectedCreate)}; " +
                         "ни один объект, захваченный при создании, в дальнейших шагах не используется.");

            // The input count is read HERE, not only in step H2.2: controls H2.4/H2.5 are other
            // documents with their own features, and `BaseObjectCount` from the first feature does not
            // apply to them. INVARIANT: leaving the field empty would make both controls refuse with
            // "inputs not read" — a harness refusal easily mistaken for a product refusal.
            var inputs = Api5.SafeObject(() => live.BaseObjects);
            var inputLength = inputs is null ? null : LengthOf(inputs);
            step.Data["base_objects_at_prep"] = inputLength;
            step.Data["base_objects_type_at_prep"] = inputs is null ? "null" : Api5.RuntimeName(inputs);

            step.Pass("Живое скругление получено после reopen; связь с созданием разорвана.");
            return new LiveFillet
            {
                Doc = doc,
                Part = reopenedPart,
                Container = container,
                Fillet = live,
                BaseObjectCount = inputLength,
            };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Подготовка живого признака не удалась: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    // ════════════════════════════════════════ live feature on a model with TWO fillets ══

    /// <summary>Plate 100×80×10 with <b>TWO independent R3 fillets on different corners</b>, saved,
    /// closed and reopened. Returns the container holding exactly two features.</summary>
    /// <remarks>
    /// <b>Why a second feature.</b> The acceptance criterion requires checking addressing on a model
    /// with two fillets of EQUAL radius: only the selected one changes. On a model with one feature
    /// "addressing" and "the only candidate" are indistinguishable, and the route looks working
    /// whether or not it can choose.
    /// <b>Why two SEPARATE features, not one with two edges.</b> One feature with a set of two edges is
    /// a single object, and writing into it says nothing about choosing BETWEEN objects. Two objects in
    /// <c>container.Fillets</c> are required.
    /// <b>The radius is equal on purpose.</b> If the radii differed, matching could be done by radius;
    /// the order explicitly forbids carrying such a simplification into the product, and an experiment
    /// with different radii would not test what is required.
    /// </remarks>
    private LiveFillet? BuildLiveTwoFillets(string label)
    {
        var step = _report.Begin(
            label + ":live",
            "Живые ДВА скругления R3 после save→close→reopen",
            "Даёт ли документ два независимых признака одного радиуса после reopen?");

        ksDocument3D? doc = null;
        var path = Path.Combine(_options.WorkDir, $"h2-{label.Replace(':', '-')}-two.m3d");
        try
        {
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Create(true, true) != true)
            {
                step.Fail("Документ не создан.");
                return null;
            }

            var part = (ksPart)doc.GetPart(-1);
            if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, label) is null)
            {
                step.Fail("Пластина не построена.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // All four corner edges — TWO different corners are taken from them, each into its own feature.
            var corners = VerticalCornerEdges(part, step);
            step.Data["corner_edges_before_create"] = corners.Count;
            if (corners.Count != 4)
            {
                step.Fail($"Вертикальных угловых рёбер {corners.Count}, нужно ровно 4 — эталон не годится.");
                Close(doc);
                return null;
            }

            // Corners are chosen BY COORDINATES, not by positions in the collection: the order in which
            // they are returned is non-deterministic, and two "first" elements would easily turn out to
            // be the same corner.
            var allCorners = new[] { "(-50,-40)", "(-50,40)", "(50,-40)", "(50,40)" };
            var byCorner = new Dictionary<string, ksEntity>(StringComparer.Ordinal);
            foreach (var edge in corners)
            {
                if (CornerOf(edge) is string corner && !byCorner.ContainsKey(corner))
                {
                    byCorner[corner] = edge;
                }
            }

            if (byCorner.Count != 4)
            {
                step.Fail($"Различных углов среди рёбер {byCorner.Count}, нужно 4 — два признака не построить.");
                Close(doc);
                return null;
            }

            var firstCorner = allCorners[0];
            var secondCornerA = allCorners[1];
            var secondCornerB = allCorners[3];
            step.Data["first_fillet_corners"] = new[] { firstCorner };
            step.Data["second_fillet_corners"] = new[] { secondCornerA, secondCornerB };
            step.Data["first_fillet_radius"] = Radius3;
            step.Data["second_fillet_radius"] = Radius3;

            var firstFeature = CreateFillet(step, part, new[] { byCorner[firstCorner] }, Radius3);
            if (firstFeature is null)
            {
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // The second feature is on TWO corners. The different set sizes are needed so that the
            // mutation is VISIBLE and ATTRIBUTABLE: for a feature with one input, removing an input
            // would mean emptying the set, and that is a different experiment.
            var secondFeature = CreateFillet(step, part, new[] { byCorner[secondCornerA], byCorner[secondCornerB] }, Radius3);
            if (secondFeature is null)
            {
                Close(doc);
                return null;
            }

            doc.RebuildDocument();

            // Reference: three of the four corners (1 + 2, without overlap).
            var expectedThree = PlateVolume - 3 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeAfterCreate = Api5.Volume(part);
            step.Data["volume_after_create"] = Api5.Num(volumeAfterCreate);
            step.Data["expected_three_corners"] = Api5.Num(expectedThree);
            if (volumeAfterCreate is not double vc || Math.Abs(vc - expectedThree) > Tolerance(expectedThree))
            {
                step.Fail($"Скругления 1+2 угла не дали эталон: {Api5.Num(volumeAfterCreate)} против {Api5.Num(expectedThree)}.");
                Close(doc);
                return null;
            }

            // ── the link to creation is broken ──
            doc.SaveAs(path);
            Close(doc);
            doc = null;
            doc = (ksDocument3D)_app.Document3D();
            if (doc.Open(path) != true)
            {
                step.Fail("Повторно открыть сохранённый документ не удалось.");
                Close(doc);
                return null;
            }

            doc.RebuildDocument();
            var reopenedPart = (ksPart)doc.GetPart(-1);
            var volumeAfterReopen = Api5.Volume(reopenedPart);
            step.Data["volume_after_reopen"] = Api5.Num(volumeAfterReopen);

            var container = Container(step, doc);
            if (container is null)
            {
                Close(doc);
                return null;
            }

            var filletCount = Api5.SafeInt(() => container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 2)
            {
                step.Fail($"Скруглений в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 2 — адресация неоднозначна.");
                Close(doc);
                return null;
            }

            if (container.Fillets[0] is not IFillet live)
            {
                step.Fail("Элемент Fillets[0] не отдаёт IFillet.");
                Close(doc);
                return null;
            }

            step.Data["saved_path"] = path;
            step.Observe($"Два признака выжили reopen: V={Api5.Num(volumeAfterReopen)} при эталоне " +
                         $"{Api5.Num(expectedThree)}; первый на угол {firstCorner}, второй на углы " +
                         $"{secondCornerA} и {secondCornerB}, оба R{Radius3.ToString(CultureInfo.InvariantCulture)}.");

            var inputs = Api5.SafeObject(() => live.BaseObjects);
            var inputLength = inputs is null ? null : LengthOf(inputs);
            step.Data["first_base_objects_at_prep"] = inputLength;
            step.Data["first_base_objects_type_at_prep"] = inputs is null ? "null" : Api5.RuntimeName(inputs);

            step.Pass("Две живые скругления получены после reopen; связь с созданием разорвана.");
            return new LiveFillet
            {
                Doc = doc,
                Part = reopenedPart,
                Container = container,
                Fillet = live,
                BaseObjectCount = inputLength,
            };
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Подготовка двух скруглений не удалась: " + ex.Message);
            if (doc is not null)
            {
                Close(doc);
            }

            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════ reading BaseObjects ══

    /// <summary>What exactly <c>BaseObjects</c> of a live feature returns: type, length, interfaces,
    /// usability. It distinguishes an empty value, a different array type and a marshalling error —
    /// order §2 forbids reading a mismatch with <c>object[]</c> as an empty set.</summary>
    private void ReadBaseObjects(LiveFillet live)
    {
        var step = _report.Begin(
            "H2.2",
            "Чтение BaseObjects живого скругления: тип, длина, интерфейсы",
            "Отдаёт ли живой признак свои входы — и в каком именно виде?");

        object? raw;
        try
        {
            raw = live.Fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data["read_failure"] = HResult.Describe(ex);
            step.Unknown("Чтение BaseObjects отвергнуто: " + ex.GetType().Name + ": " + ex.Message +
                         " — это ОТКАЗ ЧТЕНИЯ, а не пустой набор.");
            return;
        }

        if (raw is null)
        {
            step.Data["raw_type"] = "null";
            step.Fail("BaseObjects вернул null: живой признак не отдаёт свои входы этим членом. " +
                      "Пустое значение, а не ошибка — это самостоятельный результат.");
            return;
        }

        step.Data["raw_type"] = Api5.RuntimeName(raw);

        // The array type is distinguished explicitly: object[] is expected but not the only possible one.
        var length = LengthOf(raw);
        step.Data["length"] = length;
        step.Observe($"BaseObjects вернул {Api5.RuntimeName(raw)} длиной " +
                     (length?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано") + ".");

        if (length is not int count || count == 0)
        {
            step.Fail("BaseObjects вернул пустой набор: признак не удерживает ни одного входа этим " +
                      "членом. (Значение прочитано и не является ошибкой — это ответ «входов нет».)");
            return;
        }

        live.BaseObjectCount = count;

        // Usability of each element: what object it is and whether it answers the interfaces by which
        // the feature accepts inputs. A direct cast to object[] already exists in the project; here we
        // check the SEMANTICS of the objects, not the way to obtain them (order §5).
        var details = new List<string>();
        var usable = 0;
        for (var i = 0; i < count; i++)
        {
            var element = ElementAt(raw, i);
            if (element is null)
            {
                details.Add($"[{i}] null");
                continue;
            }

            var name = Api5.RuntimeName(element);
            var asModelObject = TryQi<IModelObject>(element);
            var asEntity5 = element as ksEntity;
            var kind = DescribeEdge(element);

            if (asModelObject is not null)
            {
                usable++;
            }

            details.Add($"[{i}] {name} → IModelObject={(asModelObject is not null)}, " +
                        $"ksEntity={(asEntity5 is not null)}, {kind}");
        }

        step.Data["elements"] = details;
        step.Data["usable_as_input"] = usable;
        step.Observe($"Входов {count}, из них приводятся к IModelObject: {usable}.");

        if (usable == 0)
        {
            step.Fail($"Ни один из {count} входов не приводится к IModelObject: предъявить их признаку " +
                      "обратно этим маршрутом нельзя. Признак удерживает входы, но не отдаёт их в " +
                      "пригодном для записи виде.");
            return;
        }

        step.Pass($"BaseObjects живого признака читается: {count} входов, {usable} пригодны к предъявлению.");
    }

    // ══════════════════════════════════════════════════════ reduction by the feature's inputs ══

    /// <summary>The reduction itself: build a subset from the objects READ FROM <c>BaseObjects</c> and
    /// write it back. Neither <c>Clear()</c> nor a search for final-body edges — either operation would
    /// substitute the subject (order §2).</summary>
    private void ReduceUsingBaseObjects(LiveFillet live)
    {
        var step = _report.Begin(
            "H2.3",
            "Сокращение 4→3 входами, прочитанными ИЗ BaseObjects",
            "Принимает ли признак назад свои же входы, если их стало меньше?");

        if (live.BaseObjectCount is not int count || count == 0)
        {
            step.Unknown("Входов для сокращения нет: шаг чтения не дал пригодных объектов. " +
                         "Сокращать нечего — это следствие отказа чтения, а не самостоятельный отказ.");
            return;
        }

        object? raw;
        try
        {
            raw = live.Fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data["read_failure"] = HResult.Describe(ex);
            step.Unknown("BaseObjects не перечитался перед сокращением: " + ex.Message);
            return;
        }

        var volumeBefore = Api5.Volume(live.Part);
        step.Data["volume_before"] = Api5.Num(volumeBefore);

        // Three of FOUR (not two): a set of three is distinguishable from "broke and rebuilt four" and
        // from a foreign set of two. MEASURED: the three-corner reference 79942.05750411731.
        var keep = Math.Min(3, count);
        var subset = new List<object>(keep);
        for (var i = 0; i < keep; i++)
        {
            var element = ElementAt(raw, i);
            if (element is not null)
            {
                subset.Add(element);
            }
        }

        step.Data["requested_subset"] = subset.Count;
        step.Data["source_count"] = count;
        step.Observe($"Пишем подмножество из {subset.Count} объектов, прочитанных из BaseObjects " +
                     $"(всего их {count}). Clear() НЕ вызывается, рёбра конечного тела НЕ ищутся.");

        try
        {
            live.Fillet.BaseObjects = subset.ToArray();
            step.Data["write"] = "ok";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            step.Data["write"] = HResult.Describe(ex);
            step.Unknown("Запись подмножества из BaseObjects отвергнута: " + ex.Message +
                         " — отказ записи, а не «не применилось».");
            return;
        }

        var updated = Applied(() => live.Fillet.Update());
        step.Data["update"] = updated;
        Rebuild(live);

        var volumeAfter = Api5.Volume(live.Part);
        step.Data["volume_after"] = Api5.Num(volumeAfter);

        var expected1 = PlateVolume - 1 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected2 = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected3 = PlateVolume - 3 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
        step.Data["expected_1_corner"] = Api5.Num(expected1);
        step.Data["expected_2_corners"] = Api5.Num(expected2);
        step.Data["expected_3_corners"] = Api5.Num(expected3);
        step.Data["expected_4_corners"] = Api5.Num(expected4);

        step.Observe($"Объём: было {Api5.Num(volumeBefore)} → стало {Api5.Num(volumeAfter)}; " +
                     $"набор из {subset.Count} дал бы {Api5.Num(expected3)}, сохранение прежнего — {Api5.Num(expected4)}, " +
                     $"схлопывание до пластины — {Api5.Num(PlateVolume)}.");

        if (Matches(volumeAfter, expected3))
        {
            step.Pass($"Сокращение входами ИЗ BaseObjects ПРИМЕНЯЕТСЯ: объём перешёл на эталон {subset.Count} рёбер. " +
                      "Маршрут адресации входов существующего признака найден.");
        }
        else if (Matches(volumeAfter, expected4))
        {
            step.Fail("Запись подмножества из BaseObjects НЕ применяется: объём остался на четырёх рёбрах. " +
                      "Признак принимает запись без эффекта — как ksFilletDefinition.radius на FL04r.");
        }
        else if (Matches(volumeAfter, PlateVolume))
        {
            step.Fail("Набор схлопнулся до пластины: прочитанные из BaseObjects объекты признак как входы " +
                      "НЕ удерживает. Это тот же тупик, что и на живых рёбрах конечного тела.");
        }
        else if (Matches(volumeAfter, expected2) || Matches(volumeAfter, expected1))
        {
            step.Fail($"Объём {Api5.Num(volumeAfter)} соответствует НЕ нашему набору из {subset.Count} " +
                      "рёбер: принят не тот состав, который мы предъявили.");
        }
        else
        {
            step.Fail($"Объём {Api5.Num(volumeAfter)} не совпал ни с набором из {subset.Count} " +
                      $"({Api5.Num(expected3)}), ни с прежним ({Api5.Num(expected4)}), ни с пластиной " +
                      $"({Api5.Num(PlateVolume)}).");
        }
    }

    // ═════════════════════════════════════════════════════════ differentiating controls ══
    /// <summary><b>Replacing one edge with another at an UNCHANGED set size (1→1).</b> This is the only
    /// experiment that tells "the feature accepted our composition" from "the feature recomputed what
    /// it was obliged to be".</summary>
    /// <remarks>
    /// <b>Why it is indispensable.</b> In the 4→3 experiment the volume moved to the three-corner
    /// reference — but a feature from which one edge was removed is OBLIGED to recompute exactly that
    /// way, whether or not it understood our inputs. That is precisely the substitution that gave probe
    /// H its false label. Substitution keeps the count unchanged, so the volume here distinguishes
    /// NOTHING: a set of one corner gives 79980.68583470577 for ANY choice of corner (the formula is
    /// symmetric). Only the COMPOSITION distinguishes.
    /// <b>Why one corner, not three or four.</b> A set of four leaves no free corner at r=3, so there is
    /// nothing to offer as the one to add; a set of three destroys the edges of the filleted corners on
    /// the final body, so a "free corner edge" would be sought among what no longer exists. One corner
    /// gives a clean experiment: exactly one corner is filleted (reference 79980.68583470577), three stay
    /// free.
    /// <b>What exactly measures the composition.</b> The plate corners have coordinates (±50, ±40). The
    /// composition witness is the coordinates of the cylinder-face axes (<c>CylinderAxisCorners</c>): if
    /// the fillet moved, the old corner must disappear from that set and the new one must appear. No
    /// other quantity shows this here.
    /// <b>Where the "free" edge comes from.</b> From a walk of the final body AFTER reopen, with the
    /// same selection as at creation. A filleted corner has no edge on the body — there is a cylinder —
    /// so the "straight + vertical + in a corner + full thickness" selection does not pass it. Thus for a
    /// one-fillet reference the walk must return EXACTLY THREE edges — one per free corner — and that is
    /// checked by itself. INVARIANT: edge pointers must not be remembered before creation; after
    /// save→close→open they are dead, and offering a dead pointer would be an experiment about
    /// marshalling, not addressing. The free edge is identified by corner coordinate, not by position in
    /// the collection: MEASURED: the return order is non-deterministic (6 runs — 6 orders).
    /// History: docs/decisions/probes.md#fillet-base-objects</remarks>
    private void SubstituteOneEdge()
    {
        var step = _report.Begin(
            "H2.4",
            "КОНТРОЛЬ: замена одного ребра при неизменном размере набора (1→1)",
            "Признак принимает НАШ состав или пересчитывает то, чем был обязан быть?");

        // One corner: three stay free, and substituting the corner does not touch the already filleted edges.
        var live = BuildLiveFillet("H2.4", filledCorners: 1);
        if (live is null)
        {
            step.Unknown("Живой признак для контроля не подготовлен — опыт не состоялся.");
            return;
        }

        try
        {
            var count = live.BaseObjectCount;
            if (count != 1)
            {
                step.Unknown($"Входов {count?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, нужен 1 — заменять нечем.");
                return;
            }

            // The corners that ARE filleted now (one) and the plate corners absent from the set (three).
            var filletedCorners = CylinderAxisCorners(live.Part, step);
            step.Data["corners_rounded_before"] = filletedCorners;
            if (filletedCorners.Count != 1)
            {
                step.Unknown($"Скруглённых углов прочитано {filletedCorners.Count}, ожидался 1 — контроль несостоятелен.");
                return;
            }

            var allCorners = new[] { "(-50,-40)", "(-50,40)", "(50,-40)", "(50,40)" };
            var free = allCorners.Where(c => !filletedCorners.Contains(c)).ToArray();
            step.Data["corners_free_before"] = free;
            if (free.Length != 3)
            {
                step.Unknown($"Свободных углов {free.Length}, ожидалось 3 — заменять нечем.");
                return;
            }

            // The free edge is taken FROM THE BODY AFTER REOPEN, by a walk with the same selection as at
            // creation. Why not "remember an edge before creation": pointers to objects of the previous
            // document are dead after save→close→open, and offering the feature a dead pointer is an
            // experiment about marshalling, not addressing. Why not "walk all four": a filleted corner
            // already has no edge on the body (there is a cylinder in its place), so for a reference with
            // ONE fillet the walk must return THREE edges — one per free corner.
            var plateCorners = VerticalCornerEdges(live.Part, step);
            step.Data["free_corner_edges_on_reopened_body"] = plateCorners.Count;
            if (plateCorners.Count != 3)
            {
                step.Unknown($"Свободных угловых рёбер на теле после reopen {plateCorners.Count}, ожидалось 3 " +
                             "(четыре угла минус один скруглённый) — заменять нечем.");
                return;
            }

            // Which edge is free is determined by coordinates, not by position in the collection: the
            // return order is non-deterministic (MEASURED: 6 runs — 6 orders). The FIRST free one is
            // taken; which corner ended up in the substitution is recorded.
            var foreign = plateCorners.FirstOrDefault(e => CornerOf(e) is string corner && free.Contains(corner));
            var foreignCorner = foreign is null ? null : CornerOf(foreign);
            if (foreign is null || foreignCorner is null)
            {
                step.Unknown("Свободное угловое ребро не опознано по координатам — замена не состоялась.");
                return;
            }

            step.Data["substituting_corner"] = foreignCorner;

            // ── Replace ONE input: offer an edge of a free corner instead of the previous one. The set
            //    size does NOT change (1→1), the volume does NOT change — only the composition changes.
            //    That is exactly what distinguishes addressing from recomputation from a substituted input.
            object? raw;
            try
            {
                raw = live.Fillet.BaseObjects;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                step.Data["read_failure"] = HResult.Describe(ex);
                step.Unknown("BaseObjects не перечитался перед заменой: " + ex.Message);
                return;
            }

            // Which corner is REMOVED: the one filleted now (it is also the single input).
            var droppedCorner = filletedCorners.Count == 1 ? filletedCorners[0] : null;
            step.Data["dropped_corner"] = droppedCorner ?? "не опознан";

            var transferred = ToApi7(step, foreign);
            if (transferred is null)
            {
                step.Unknown("Свободное ребро не перенеслось в API7 — заменять нечем.");
                return;
            }

            var replacement = new[] { transferred };
            step.Data["requested_size"] = replacement.Length;
            step.Data["source_count"] = count;

            var volumeBefore = Api5.Volume(live.Part);
            step.Data["volume_before"] = Api5.Num(volumeBefore);

            try
            {
                live.Fillet.BaseObjects = replacement;
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись замены отвергнута: " + ex.Message);
                return;
            }

            var updated = Applied(() => live.Fillet.Update());
            step.Data["update"] = updated;
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var cornersAfter = CylinderAxisCorners(live.Part, step);
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["corners_rounded_after"] = cornersAfter;

            // Reference at one corner. Before and after the substitution it is THE SAME: the volume at
            // 1→1 distinguishes nothing — that is the point of the experiment.
            var expected1 = PlateVolume - 1 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            step.Data["expected_one_corner"] = Api5.Num(expected1);

            // Did the corner we did NOT offer disappear from the composition?
            var removed = filletedCorners.Where(c => !cornersAfter.Contains(c)).ToArray();
            var added = cornersAfter.Where(c => !filletedCorners.Contains(c)).ToArray();
            step.Data["corners_left"] = removed;
            step.Data["corners_joined"] = added;

            var volumeHeld = Matches(volumeAfter, expected1);
            var sizeHeld = cornersAfter.Count == filletedCorners.Count;
            var foreignArrived = cornersAfter.Contains(foreignCorner!);
            var droppedLeft = droppedCorner is null || !cornersAfter.Contains(droppedCorner);
            var compositionChanged = removed.Length > 0 || added.Length > 0;

            step.Observe($"Состав: было {string.Join(", ", filletedCorners)}; стало {string.Join(", ", cornersAfter)}. " +
                         $"Предъявлено вместо {droppedCorner ?? "?"}: {foreignCorner}. " +
                         $"Ушли: {string.Join(", ", removed)}; пришли: {string.Join(", ", added)}. " +
                         $"Объём: {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)} " +
                         $"(набор из одного угла даёт {Api5.Num(expected1)} при ЛЮБОМ углу — объём здесь не различает).");

            if (volumeHeld && sizeHeld && foreignArrived && droppedLeft && compositionChanged)
            {
                step.Pass($"Замена ПРИМЕНЯЕТСЯ: состав изменился при неизменном размере набора — скругление " +
                          $"переехало на угол {foreignCorner}, а угол {droppedCorner}, который мы сняли, ушёл. " +
                          "Это не пересчёт по подставленному входу: признак принял именно НАШ состав.");
            }
            else if (!compositionChanged && volumeHeld)
            {
                step.Fail("Состав НЕ изменился при неизменном размере набора: признак удержал прежний угол и " +
                          "проигнорировал предъявленное ребро. Значит в опыте 4→3 он пересчитался по СВОЕМУ " +
                          "составу, а не по нашему — маршрут адресации этим не доказан.");
            }
            else if (volumeHeld && sizeHeld && !foreignArrived)
            {
                step.Fail($"Объём удержан, размер набора прежний, но предъявленный угол {foreignCorner} НЕ " +
                          $"скруглён: состав изменился не туда — пришли {string.Join(", ", added)}, " +
                          $"ушли {string.Join(", ", removed)}.");
            }
            else if (!volumeHeld)
            {
                step.Fail($"Набор не удержал один угол: объём {Api5.Num(volumeAfter)} против эталона " +
                          $"{Api5.Num(expected1)}.");
            }
            else
            {
                step.Fail($"Замена не состоялась по составу: скруглённых углов после записи {cornersAfter.Count} " +
                          $"при прежнем размере {filletedCorners.Count}; ушли {string.Join(", ", removed)}, " +
                          $"пришли {string.Join(", ", added)}, ожидалось прибытие {foreignCorner}.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary><b>Repeatability of the route on a fresh document.</b> Order §2 asks for an enlargement
    /// experiment, but a clean enlargement of the SET cannot be built on this reference: the plate has
    /// exactly four vertical corners, all four already filleted at r=3. So what is meaningful is checked
    /// here — <b>a second independent reduction run on its own document</b>: the route must reproduce,
    /// not fire once.</summary>
    /// <remarks>LIMIT: the substitution is not passed off as satisfying the requirement: the enlargement
    /// experiment stays unfulfilled on this reference and that is recorded explicitly. Enlargement must
    /// not be declared measured from this experiment.</remarks>
    private void EnlargeUsingBaseObjects()
    {
        var step = _report.Begin(
            "H2.5",
            "Повторяемость маршрута на свежем документе (расширение набора невыполнимо)",
            "Воспроизводится ли маршрут, или он сработал один раз?");

        step.Observe("Расширение набора НА ЭТОМ ЭТАЛОНЕ невыполнимо: у пластины 100×80×10 ровно четыре " +
                     "вертикальных угловых ребра, и все четыре уже в наборе при r=3 по всем углам. " +
                     "Опыт «4→5» потребовал бы другого эталона и был бы несравним с числами этой серии. " +
                     "Здесь измеряется то, что доступно, — повторяемость уже найденного маршрута.");

        // The source set is four corners: the repeat checks a DIFFERENT reduction than H2.3 (which went
        // from four to three). Two identical experiments would not tell "the route works" from "it fired once".
        var live = BuildLiveFillet("H2.5");
        if (live is null)
        {
            step.Unknown("Живой признак для повтора не подготовлен — опыт не состоялся.");
            return;
        }

        try
        {
            var count = live.BaseObjectCount;
            if (count != 4)
            {
                step.Unknown($"Входов {count?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, нужно 4.");
                return;
            }

            object? raw;
            try
            {
                raw = live.Fillet.BaseObjects;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                step.Data["read_failure"] = HResult.Describe(ex);
                step.Unknown("BaseObjects не перечитался: " + ex.Message);
                return;
            }

            // Reduction 4→2: a different quantity than in H2.3, hence a different reference.
            const int keep = 2;
            var subset = new List<object>();
            for (var i = 0; i < keep; i++)
            {
                if (ElementAt(raw, i) is object element)
                {
                    subset.Add(element);
                }
            }

            step.Data["requested_subset"] = subset.Count;
            step.Data["source_count"] = count;

            try
            {
                live.Fillet.BaseObjects = subset.ToArray();
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись отвергнута на повторе: " + ex.Message);
                return;
            }

            step.Data["update"] = Applied(() => live.Fillet.Update());
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var expected2 = PlateVolume - keep * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var expected4 = PlateVolume - 4 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["expected_2_corners"] = Api5.Num(expected2);
            step.Data["expected_4_corners"] = Api5.Num(expected4);

            step.Observe($"Объём {Api5.Num(volumeAfter)}: набор из {keep} дал бы {Api5.Num(expected2)}, " +
                         $"прежний из четырёх — {Api5.Num(expected4)}.");

            if (Matches(volumeAfter, expected2))
            {
                step.Pass($"Маршрут воспроизвёлся на свежем документе при ДРУГОМ сокращении: объём {Api5.Num(volumeAfter)} = эталон двух рёбер.");
            }
            else if (Matches(volumeAfter, expected4))
            {
                step.Fail("На повторе объём остался на четырёх рёбрах: маршрут сработал один раз и не воспроизводится.");
            }
            else
            {
                step.Fail($"На повторе объём {Api5.Num(volumeAfter)} не совпал ни с эталоном {keep} рёбер " +
                          $"({Api5.Num(expected2)}), ни с прежним ({Api5.Num(expected4)}): маршрут невоспроизводим.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary><b>Addressing on a model with TWO fillets of one radius.</b> A mandatory acceptance
    /// criterion: only the selected feature changes. A single feature with no neighbour cannot tell
    /// "addressing" from "the only candidate" — on a model with one fillet any route looks working.</summary>
    /// <remarks>
    /// <b>Why the reference is different, not four corners.</b> TWO independent features of one radius
    /// are needed on one plate. From the four vertical corners two fillets are made: the first on ONE
    /// corner, the second on TWO. Both have radius R3, the document is one. Different set sizes are
    /// needed for distinguishability: for a feature with one input, removing an input would mean
    /// emptying the set, and that is a different experiment.
    /// <b>What is measured here.</b> Not the volume (it is symmetric) but the DISTRIBUTION of fillets
    /// over corners: which corners are occupied before the write and which after. The feature with TWO
    /// inputs is mutated; the feature with one input serves as a WITNESS: if it changes, addressing is
    /// not found. If only the mutated one changed, addressing by feature is confirmed.
    /// <b>Why this is not the same experiment as H2.4.</b> H2.4 holds ONE feature and changes a corner
    /// inside it: it tells "accepted our composition" from "recomputed its own". Here it is checked that
    /// the route does not confuse TWO features. Two different questions.
    /// <b>Why feature correspondence is not taken by index.</b> The order explicitly forbids matching
    /// arbitrary fillets by position in the collection. Each feature is identified by ITS OWN input
    /// edges: the corner of an input edge is read via a transfer to API7, and by it the feature gets a
    /// name. The same technique is applied to the witness on re-read.
    /// </remarks>
    private void AddressAmongTwoFillets()
    {
        var step = _report.Begin(
            "H2.7",
            "АДРЕСАЦИЯ: два скругления R3 на одной пластине — меняется только выбранное",
            "Не задевает ли запись во входы одного признака соседний признак того же радиуса?");

        var live = BuildLiveTwoFillets("H2.7");
        if (live is null)
        {
            step.Unknown("Модель с двумя скруглениями не подготовлена — опыт не состоялся.");
            return;
        }

        try
        {
            var filletCount = Api5.SafeInt(() => live.Container.Fillets.Count);
            step.Data["fillets_count"] = filletCount;
            if (filletCount != 2)
            {
                step.Unknown($"Признаков в контейнере {filletCount?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано"}, ожидалось 2.");
                return;
            }

            if (live.Container.Fillets[0] is not IFillet a || live.Container.Fillets[1] is not IFillet b)
            {
                step.Unknown("Не оба элемента Fillets отдают IFillet — адресовать нечем.");
                return;
            }

            // ── Which write corresponds to which feature is NOT assumed by index. The order explicitly
            //    forbids matching arbitrary fillets by position in the collection. So each feature is
            //    identified by ITS OWN input edges.
            //
            //    INVARIANT: first MEASURE which route of reading an element works at all, and record the
            //    result rather than implying it — a failed identification must not look like "addressing
            //    not confirmed".
            var aInputs = InputsOf(a, step, "a");
            var bInputs = InputsOf(b, step, "b");
            if (aInputs.Count == 0 || bInputs.Count == 0)
            {
                step.Unknown("Один из признаков не отдал входы через BaseObjects — адресовать нечем.");
                return;
            }

            // Identification by the stable IModelObject.Reference — the very one already measured in H2.2
            // (Reference=1073741872…75, Type=ksObjectEdge). Unlike the index it does not depend on the
            // collection's return order.
            var aRefs = RefsOfInputs(aInputs);
            var bRefs = RefsOfInputs(bInputs);
            step.Data["fillets_0_input_refs"] = aRefs;
            step.Data["fillets_1_input_refs"] = bRefs;
            step.Data["fillets_0_input_count"] = aInputs.Count;
            step.Data["fillets_1_input_count"] = bInputs.Count;

            // ── IMPORTANT MEASUREMENT closing one identification route. MEASURED: the feature's input
            //    references are NOT equal to the final-body edge references: inputs are 1073742065–2067,
            //    the single surviving corner edge of the body is 1073742080. These are different objects
            //    in different contexts, and an input CANNOT be linked to a corner by Reference.
            //
            //    Therefore the feature's composition is measured NOT through body corners but through its
            //    OWN inputs: their references are stable and re-read. Against body corners only the count
            //    and distribution of fillets is compared — via CylinderAxisCorners, which is read from
            //    faces, not from inputs.
            var bodyCornerEdges = VerticalCornerEdges(live.Part, step);
            step.Data["body_free_corner_edges"] = bodyCornerEdges.Count;
            step.Data["body_corner_refs"] = bodyCornerEdges
                .Select(e => ReferenceOf(e) is int r && CornerOf(e) is string c ? $"{r}={c}" : null)
                .Where(s => s is not null)
                .ToList();

            if (aRefs.Count == 0 || bRefs.Count == 0)
            {
                step.Unknown("Входы признаков не отдали устойчивых ссылок — состав перечитывать нечем.");
                return;
            }

            // The radii must match: the experiment is about addressing at an EQUAL radius — otherwise
            // matching would go by radius, exactly the simplification the order forbids carrying into the
            // product.
            var aRadius = ReadRadius(a, step, "a");
            var bRadius = ReadRadius(b, step, "b");
            step.Data["fillets_0_radius"] = aRadius;
            step.Data["fillets_1_radius"] = bRadius;
            if (aRadius is null || bRadius is null || Math.Abs(aRadius.Value - bRadius.Value) > 1e-9)
            {
                step.Unknown("Радиусы признаков не совпали или не прочитаны — опыт не различает адресацию.");
                return;
            }

            // The feature with TWO inputs is mutated. The one-corner feature is left alone and serves as
            // a witness: if it changes anyway, the write touched the wrong object.
            var (victim, victimInputs, victimTag, witness, witnessInputs, witnessTag) =
                aInputs.Count >= 2
                    ? (a, aInputs, "Fillets[0]", b, bInputs, "Fillets[1]")
                    : bInputs.Count >= 2
                        ? (b, bInputs, "Fillets[1]", a, aInputs, "Fillets[0]")
                        : (null, null, null, null, null, null);

            if (victim is null || victimInputs is null || witness is null || witnessInputs is null)
            {
                step.Unknown($"Ни у одного признака нет двух входов ({aInputs.Count} и {bInputs.Count}) — " +
                             "снять один вход, не опустошая набор, нечем.");
                return;
            }

            step.Data["mutated_fillet"] = victimTag;
            step.Data["witness_fillet"] = witnessTag;
            step.Data["mutated_inputs_before"] = victimInputs.Count;
            step.Data["witness_inputs_before"] = witnessInputs.Count;

            var victimRefsBefore = RefsOfInputs(victimInputs);
            var witnessRefsBefore = RefsOfInputs(witnessInputs);
            step.Data["mutated_refs_before"] = victimRefsBefore;
            step.Data["witness_refs_before"] = witnessRefsBefore;

            // Composition of each feature BEFORE the write — by its own inputs.
            var cornersBefore = CylinderAxisCorners(live.Part, step);
            step.Data["corners_rounded_before_total"] = cornersBefore;

            var volumeBefore = Api5.Volume(live.Part);
            step.Data["volume_before"] = Api5.Num(volumeBefore);

            var reduced = victimInputs.Take(victimInputs.Count - 1).ToList();
            step.Data["written_to_mutated"] = reduced.Count;

            try
            {
                victim.BaseObjects = reduced.ToArray();
                step.Data["write"] = "ok";
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                step.Data["write"] = HResult.Describe(ex);
                step.Unknown("Запись во входы выбранного признака отвергнута: " + ex.Message +
                             " — отказ записи, а не «задело соседа».");
                return;
            }

            step.Data["update"] = Applied(() => victim.Update());
            Rebuild(live);

            var volumeAfter = Api5.Volume(live.Part);
            var cornersAfter = CylinderAxisCorners(live.Part, step);
            step.Data["volume_after"] = Api5.Num(volumeAfter);
            step.Data["corners_rounded_after_total"] = cornersAfter;

            var lost = cornersBefore.Where(c => !cornersAfter.Contains(c)).ToArray();
            var gained = cornersAfter.Where(c => !cornersBefore.Contains(c)).ToArray();
            step.Data["corners_lost"] = lost;
            step.Data["corners_gained"] = gained;

            // The witness is re-read: its inputs must stay exactly the same — by reference.
            var witnessAfter = InputsOf(witness, step, "witness_after");
            var witnessRefsAfter = RefsOfInputs(witnessAfter);
            step.Data["witness_inputs_after"] = witnessAfter.Count;
            step.Data["witness_refs_after"] = witnessRefsAfter;

            var witnessUntouched =
                witnessAfter.Count == witnessInputs.Count &&
                witnessRefsBefore.OrderBy(r => r).SequenceEqual(witnessRefsAfter.OrderBy(r => r));
            step.Data["witness_untouched"] = witnessUntouched;

            // The mutated feature is re-read: exactly one input must leave its set.
            var victimAfter = InputsOf(victim, step, "mutated_after");
            var victimRefsAfter = RefsOfInputs(victimAfter);
            step.Data["mutated_inputs_after"] = victimAfter.Count;
            step.Data["mutated_refs_after"] = victimRefsAfter;

            // Expectation: exactly one corner left the body, the volume is the two-corner reference.
            var expectedAfter = PlateVolume - 2 * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;
            var volumeMatchesTwoCorners = Matches(volumeAfter, expectedAfter);
            var lostOneCorner = lost.Length == 1 && gained.Length == 0;
            var victimLostExactlyOne = victimRefsAfter.Count == victimRefsBefore.Count - 1;

            step.Observe($"Мутируемый: {victimTag}, входов {victimInputs.Count}→{victimAfter.Count} " +
                         $"(ссылки {string.Join(",", victimRefsBefore)} → {string.Join(",", victimRefsAfter)}). " +
                         $"Свидетель: {witnessTag}, входов {witnessInputs.Count}→{witnessAfter.Count} " +
                         $"(ссылки {string.Join(",", witnessRefsBefore)} → {string.Join(",", witnessRefsAfter)}). " +
                         $"С тела ушли: {string.Join(", ", lost)}; пришли: {string.Join(", ", gained)}. " +
                         $"Объём: {Api5.Num(volumeBefore)} → {Api5.Num(volumeAfter)}.");

            if (witnessUntouched && victimLostExactlyOne && lostOneCorner && volumeMatchesTwoCorners)
            {
                step.Pass($"Адресация подтверждена: запись во входы {victimTag} сняла ровно один ЕГО вход " +
                          $"({victimRefsBefore.Count}→{victimRefsAfter.Count}) и НЕ задела {witnessTag} того же " +
                          "радиуса — ссылки свидетеля совпали до и после. С тела ушёл ровно один угол.");
            }
            else if (!witnessUntouched)
            {
                step.Fail($"Запись задела НЕ только выбранный признак: свидетель {witnessTag} изменился " +
                          $"({witnessInputs.Count}→{witnessAfter.Count} входов; ссылки " +
                          $"{string.Join(",", witnessRefsBefore)} → {string.Join(",", witnessRefsAfter)}). " +
                          "На модели с двумя скруглениями одного радиуса это неоднозначная адресация — " +
                          "переносить в продукт без защиты нельзя.");
            }
            else if (!victimLostExactlyOne)
            {
                step.Fail($"Сам {victimTag} изменился не так, как предъявлено: входов " +
                          $"{victimRefsBefore.Count}→{victimRefsAfter.Count}, ожидалось −1.");
            }
            else if (lost.Length > 1 || gained.Length > 0)
            {
                step.Fail($"С тела ушло не то, что предъявлено: ушли {string.Join(", ", lost)}, " +
                          $"пришли {string.Join(", ", gained)}; ожидалось снятие одного угла.");
            }
            else if (!volumeMatchesTwoCorners)
            {
                step.Fail($"Геометрия не соответствует снятию одного угла: объём {Api5.Num(volumeAfter)}, " +
                          $"ожидался {Api5.Num(expectedAfter)}.");
            }
            else
            {
                step.Fail($"Адресация не подтверждена: ушли {string.Join(", ", lost)}, пришли " +
                          $"{string.Join(", ", gained)}.");
            }
        }
        finally
        {
            Close(live.Doc);
        }
    }

    /// <summary>Stable <c>IModelObject.Reference</c> identifiers of the feature's input elements. Unlike
    /// the index they do not depend on the collection's return order and are re-read after a mutation —
    /// that is why they serve as the measure of the feature's COMPOSITION in the addressing
    /// experiment.</summary>
    /// <remarks><b>What these references do NOT do.</b> They do not link a feature input to a
    /// final-body corner: MEASURED: input references are 1073742065–2067, the surviving corner edge of
    /// the body is 1073742080. These are different objects in different contexts; trying to link them by
    /// Reference returned 0 and 0. LIMIT: the same reference is meaningful within its own context and is
    /// not carried into another.</remarks>
    private static List<int> RefsOfInputs(IReadOnlyList<object> inputs)
    {
        var refs = new List<int>();
        foreach (var input in inputs)
        {
            if (TryQi<IModelObject>(input) is IModelObject modelObject)
            {
                try
                {
                    refs.Add(modelObject.Reference);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    // An element without a readable reference is skipped: that is an answer, not a crash.
                }
            }
        }

        return refs;
    }

    /// <summary>Stable reference of a body edge obtained as a <c>ksEntity</c> (an API5 object).</summary>
    private int? ReferenceOf(ksEntity edge)
    {
        try
        {
            var transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is IModelObject modelObject)
            {
                return modelObject.Reference;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The feature's inputs via <c>BaseObjects</c>: objects usable to be offered back.</summary>
    private static List<object> InputsOf(IFillet fillet, ProbeStep step, string tag)
    {
        var inputs = new List<object>();
        object? raw;
        try
        {
            raw = fillet.BaseObjects;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[$"{tag}_read_failure"] = HResult.Describe(ex);
            return inputs;
        }

        var length = raw is null ? null : LengthOf(raw);
        if (length is not int count)
        {
            step.Data[$"{tag}_type"] = raw is null ? "null" : Api5.RuntimeName(raw);
            return inputs;
        }

        step.Data[$"{tag}_type"] = Api5.RuntimeName(raw!);
        for (var i = 0; i < count; i++)
        {
            if (ElementAt(raw!, i) is object element)
            {
                inputs.Add(element);
            }
        }

        return inputs;
    }

    /// <summary>The feature's radius in API7: <c>IFillet.Radius1</c> — the measured member (dispid 3, see
    /// <c>Api7Bridge.Api7Fillet.Read</c>). Needed to be sure the two features really have one radius —
    /// otherwise the addressing experiment would degenerate into matching by radius, which the order
    /// explicitly forbids carrying into the product.</summary>
    private static double? ReadRadius(IFillet fillet, ProbeStep step, string tag)
    {
        try
        {
            return fillet.Radius1;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Data[$"{tag}_radius_failure"] = HResult.Describe(ex);
            return null;
        }
    }

    /// <summary><b>Reconciliation with probe H on the numbers of THIS run.</b> Not a retelling and not a
    /// reference to a document: the method recomputes whether the claim attributed to probe H follows
    /// from what was JUST obtained, and where that claim stops being a consequence.</summary>
    /// <remarks>The reconciliation is COMPUTED, not narrated: it takes the numbers of the 4→3 and 1→1 experiments
    /// from this same report and checks the distinguishing power of each on its own.
    /// The claim under test: "a write to <c>BaseObjects</c> applies to an existing feature". The 4→3
    /// experiment does NOT confirm it, even when the volume lands exactly on the three-corner reference:
    /// a feature from which one edge was removed must recompute the same way even if it did not parse
    /// our inputs at all. The 1→1 experiment confirms it, not by volume but by composition: at an
    /// unchanged set size the volume before and after is THE SAME, and the only thing that differs is
    /// which corner is filleted.
    /// Hence the boundary: if the 1→1 experiment did not show a change of composition, the 4→3
    /// experiment must be read as "recomputed its own composition", and the addressing route as NOT
    /// found. That link is what makes the reconciliation substantive rather than decorative.</remarks>
    private void ReconcileWithProbeH()
    {
        var step = _report.Begin(
            "H2.6",
            "Сверка с пробой H: что именно из доказанного следует",
            "Подтверждает ли опыт 4→3 запись входов, или для этого нужен опыт 1→1?");

        var reduce = FindStep("H2.3");
        var substitute = FindStep("H2.4");
        if (reduce is null || substitute is null)
        {
            step.Unknown("Опытов H2.3/H2.4 в отчёте нет — сверять не с чем.");
            return;
        }

        var reduceApplied = reduce.Verdict == Verdict.Pass;
        var substituteChangedComposition = substitute.Verdict == Verdict.Pass;

        step.Data["reduce_4_to_3_alone_sufficient"] = false;
        step.Data["reduce_4_to_3_verdict"] = reduce.Verdict.ToString();
        step.Data["substitute_1_to_1_verdict"] = substitute.Verdict.ToString();

        // The numbers of both sides come from THIS run, not from probe H's document.
        step.Data["this_run_expected_3_corners"] = Api5.Num(ExpectedCorners(3));
        step.Data["this_run_expected_1_corner"] = Api5.Num(ExpectedCorners(1));
        step.Data["this_run_reduce_volume_after"] = reduce.Data.TryGetValue("volume_after", out var v3) ? v3 : null;
        step.Data["this_run_substitute_volume_before"] = substitute.Data.TryGetValue("volume_before", out var vb) ? vb : null;
        step.Data["this_run_substitute_volume_after"] = substitute.Data.TryGetValue("volume_after", out var va) ? va : null;
        step.Data["this_run_substitute_corners_before"] = substitute.Data.TryGetValue("corners_rounded_before", out var cb) ? cb : null;
        step.Data["this_run_substitute_corners_after"] = substitute.Data.TryGetValue("corners_rounded_after", out var ca) ? ca : null;

        var volumeUnchangedInSubstitute =
            substitute.Data.TryGetValue("volume_before", out var before) &&
            substitute.Data.TryGetValue("volume_after", out var after) &&
            before is not null && after is not null &&
            string.Equals(before.ToString(), after.ToString(), StringComparison.Ordinal);
        step.Data["substitute_volume_literally_unchanged"] = volumeUnchangedInSubstitute;

        if (substituteChangedComposition && reduceApplied)
        {
            step.Pass(
                "Опыт 4→3 объёмом не различает: признак, у которого сняли ребро, ОБЯЗАН дать эталон трёх. " +
                "Различает опыт 1→1 — объём там НЕ меняется (" +
                $"{(volumeUnchangedInSubstitute ? "буквально один и тот же" : "сравнить не удалось")}), " +
                "а состав меняется. Маршрут записи входов подтверждён именно составом, а не объёмом. " +
                "Проба H мерила другое: она подставляла запомненные рёбра до создания признака, и её " +
                "числа — пересчёт по подставленному входу.");
        }
        else if (reduceApplied && !substituteChangedComposition)
        {
            step.Fail(
                "Опыт 4→3 дал эталон трёх, но опыт 1→1 НЕ показал смены состава. Следовательно 4→3 читается " +
                "как пересчёт по СВОЕМУ составу, а запись входов не доказана: маршрут адресации не найден.");
        }
        else
        {
            step.Unknown(
                "Ни сокращение, ни замена не прошли: об адресации входов этим прогоном сказать нечего.");
        }
    }

    /// <summary>Reference volume of the plate with <paramref name="corners"/> filleted corners at R3.</summary>
    private static double ExpectedCorners(int corners) =>
        PlateVolume - corners * (1 - Math.PI / 4) * Radius3 * Radius3 * PlateThickness;

    /// <summary>A step of the already-collected report by id — for reconciliation between experiments.</summary>
    private ProbeStep? FindStep(string id) =>
        _report.Steps.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    /// <summary>The coordinates of the corners WHERE A CYLINDRICAL FILLET FACE SITS — "which corners are
    /// filleted", read from the final body, not from the feature. This is the measure of COMPOSITION:
    /// the volume is the same for any set of four corners, but the corners differ.</summary>
    /// <remarks>Signature of a fillet's cylindrical face: an axis at distance r from the plate corner.
    /// The corners (±50, ±40) at R3 give axes at (±47, ±37). The source of the axis point is not a guess
    /// but the measured path <see cref="Api5.ReadFaces"/>: <c>GetSurface() → ksSurface →
    /// GetSurfaceParam() → ksCylinderParam → GetPlacement() → GetOrigin()</c>. LIMIT: a guess about an
    /// API member must not enter a measurement.</remarks>
    private static List<string> CylinderAxisCorners(ksPart part, ProbeStep step)
    {
        var corners = new List<string>();
        foreach (var reading in Api5.CylinderFaces(part))
        {
            var point = reading.CylinderOrigin;
            if (point is null || point.Length < 3)
            {
                step.Observe($"Цилиндрическая грань [{reading.Index}] без читаемого размещения — угол не опознан.");
                continue;
            }

            var x = point[0];
            var y = point[1];

            // The fillet axis is r away from the corner along both axes. Only lattice axes (±47, ±37)
            // are accepted: otherwise foreign cylinders would enter the "filleted corners".
            if (Math.Abs(Math.Abs(x) - (PlateWidth / 2d - Radius3)) > 1e-3 ||
                Math.Abs(Math.Abs(y) - (PlateHeight / 2d - Radius3)) > 1e-3)
            {
                continue;
            }

            var sx = Math.Sign(x) * (PlateWidth / 2d);
            var sy = Math.Sign(y) * (PlateHeight / 2d);
            var label = $"({sx.ToString("0.####", CultureInfo.InvariantCulture)},{sy.ToString("0.####", CultureInfo.InvariantCulture)})";
            if (!corners.Contains(label))
            {
                corners.Add(label);
            }
        }

        return corners;
    }

    /// <summary>Coordinate label of a corner edge: "(50,-40)" and the like.</summary>
    private static string? CornerOf(ksEntity edge)
    {
        try
        {
            if (edge.GetDefinition() is not ksEdgeDefinition definition)
            {
                return null;
            }

            return CornerOfDefinition(definition);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Coordinate label of a corner from an edge definition — shared by a body edge and an edge
    /// obtained another way. Called only where the definition was already obtained by a verified
    /// route.</summary>
    private static string? CornerOfDefinition(ksEdgeDefinition definition)
    {
        if (definition.GetVertex(true) is not ksVertexDefinition v0 || definition.GetVertex(false) is not ksVertexDefinition v1
            || !v0.GetPoint(out var ax, out var ay, out _) || !v1.GetPoint(out var bx, out var by, out _))
        {
            return null;
        }

        var x = (ax + bx) / 2d;
        var y = (ay + by) / 2d;
        return $"({x.ToString("0.####", CultureInfo.InvariantCulture)},{y.ToString("0.####", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Transfer of an edge into API7 the same way probe H does it.</summary>
    private object? ToApi7(ProbeStep step, ksEntity edge)
    {
        try
        {
            var transferred = _app.TransferInterface(edge, (int)ksAPITypeEnum.ksAPI7Dual, 0);
            if (transferred is null)
            {
                step.Data["transfer_edge"] = "null";
            }

            return transferred;
        }
        catch (Exception ex)
        {
            step.Data["transfer_edge"] = HResult.Describe(ex);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════ mechanics ══

    /// <summary>Plate 100×80×10, four vertical R3 corners. MEASURED: reference 79922.74333882307. The
    /// created feature is returned — it is needed ONLY for the subsequent save of the document. Rebuild
    /// is done by the caller: a <c>ksPart</c> has no <c>Document</c> member but does have a document (the
    /// compiler established this, not a guess).</summary>
    private static ksEntity? CreateFillet(ProbeStep step, ksPart part, IReadOnlyList<ksEntity> edges, double radius)
    {
        var feature = (ksEntity)part.NewEntity(Fillet);
        if (feature.GetDefinition() is not ksFilletDefinition definition)
        {
            step.Fail("Определение скругления не отдало ksFilletDefinition.");
            return null;
        }

        definition.radius = radius;
        definition.tangent = false;
        if (definition.array() is not ksEntityCollection array)
        {
            step.Fail("array() не отдал ksEntityCollection.");
            return null;
        }

        var added = 0;
        foreach (var edge in edges)
        {
            if (array.Add(edge))
            {
                added++;
            }
        }

        if (added != edges.Count || !feature.Create())
        {
            step.Fail($"Скругление не создано: рёбер принято {added} из {edges.Count}.");
            return null;
        }

        return feature;
    }

    /// <summary>The four vertical corner edges of the final body — the selection criterion is TAKEN FROM
    /// PROBE H UNCHANGED (<c>Api5.SafeBool(edge.IsStraight)</c> + vertices + the check "vertical, in a
    /// corner, full thickness"). It is already measured working there, and a second, "own" selection
    /// would give a second reference incomparable with the earlier numbers.</summary>
    private static List<ksEntity> VerticalCornerEdges(ksPart part, ProbeStep step)
    {
        var chosen = new List<ksEntity>();
        var seen = new HashSet<IntPtr>();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            step.Observe("Тело или коллекция граней недоступны — рёбер не видно.");
            return chosen;
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            if (faces.GetByIndex(f) is not object faceObject)
            {
                continue;
            }

            var face = faceObject as ksFaceDefinition
                       ?? ((faceObject as ksEntity)?.GetDefinition() as ksFaceDefinition);
            if (face?.EdgeCollection() is not ksEdgeCollection edges)
            {
                continue;
            }

            for (var e = 0; e < edges.GetCount(); e++)
            {
                var edgeObject = edges.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition
                           ?? ((edgeObject as ksEntity)?.GetDefinition() as ksEdgeDefinition);
                if (edge is null || Api5.SafeBool(edge.IsStraight) != true)
                {
                    continue;
                }

                if (edge.GetVertex(true) is not ksVertexDefinition v0 || edge.GetVertex(false) is not ksVertexDefinition v1
                    || !v0.GetPoint(out var ax, out var ay, out var az) || !v1.GetPoint(out var bx, out var by, out var bz))
                {
                    continue;
                }

                var vertical = Math.Abs(ax - bx) < 1e-6 && Math.Abs(ay - by) < 1e-6;
                var corner = Math.Abs(Math.Abs(ax) - PlateWidth / 2d) < 1e-3 && Math.Abs(Math.Abs(ay) - PlateHeight / 2d) < 1e-3;
                var spans = Math.Abs(Math.Max(az, bz) - PlateThickness) < 1e-3 && Math.Abs(Math.Min(az, bz)) < 1e-3;
                if (!(vertical && corner && spans))
                {
                    continue;
                }

                var entity = edgeObject as ksEntity ?? edge.GetEntity() as ksEntity ?? edge.GetOwnerEntity() as ksEntity;
                if (entity is null)
                {
                    continue;
                }

                var pointer = Marshal.GetIUnknownForObject(entity);
                try
                {
                    if (seen.Add(pointer))
                    {
                        chosen.Add(entity);
                    }
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }

        return chosen;
    }

    /// <summary>The set length, whatever type it came back as. The distinction is mandatory: <c>object[]</c>
    /// is only one option, and a mismatch with it does NOT mean emptiness (order §2).</summary>
    private static int? LengthOf(object raw) => raw switch
    {
        object[] array => array.Length,
        Array array => array.Length,
        _ => null,
    };

    /// <summary>Set element by index — allowing for the fact that it may be any <see cref="Array"/>.</summary>
    private static object? ElementAt(object raw, int index) => raw switch
    {
        object[] array when index < array.Length => array[index],
        Array array when index < array.Length => array.GetValue(index),
        _ => null,
    };

    /// <summary>The meaning of an element as an edge: does it answer the interfaces by which the feature accepts inputs.</summary>
    private static string DescribeEdge(object element)
    {
        var asModelObject = TryQi<IModelObject>(element);
        if (asModelObject is null)
        {
            return "не IModelObject";
        }

        try
        {
            var index = asModelObject.Reference;
            var type = asModelObject.Type;
            return $"Reference={index}, Type={type}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return "IModelObject без читаемых Reference/Type";
        }
    }

    /// <summary>QI on an object: the fact that an element came back from the set does not mean it answers
    /// the interface (MEASURED on rotations: a plain cast does not work, QI does — see R.22).</summary>
    private static T? TryQi<T>(object element) where T : class
    {
        try
        {
            return element as T;
        }
        catch (Exception ex) when (ex is InvalidCastException or COMException)
        {
            return null;
        }
    }

    private IModelContainer? Container(ProbeStep step, ksDocument3D doc)
    {
        try
        {
            var document7 = _app.TransferInterface(doc, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;
            var container = document7?.TopPart as IModelContainer;
            step.Data["qi_imodelcontainer"] = container is not null;
            if (container is null)
            {
                step.Unknown("Документ/TopPart не дали IModelContainer.");
            }

            return container;
        }
        catch (Exception ex)
        {
            step.Data["qi_imodelcontainer"] = "исключение: " + ex.Message;
            return null;
        }
    }

    private static void Rebuild(LiveFillet live)
    {
        if (live.Container is IPart7 part7)
        {
            part7.RebuildModel(true);
        }

        live.Doc.RebuildDocument();
    }

    private double Tolerance(double expected) =>
        Math.Max(Math.Abs(expected), 1d) * 8 * 2.2204460492503131e-16;

    private bool Matches(double? measured, double expected) =>
        measured is double m && Math.Abs(m - expected) <= Tolerance(expected);

    /// <summary>Reduces a three-valued state to "applied / not applied": <c>null</c> from "not asked"
    /// here means NOT "written", so the default is false.</summary>
    private static bool Applied(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }

    private static void Close(ksDocument3D doc)
    {
        try
        {
            doc.close();
        }
        catch (Exception)
        {
            // Closing after a step crash is cleanup, not measurement: an orphaned document is visible at
            // step H2.Z.
        }
    }

    private ProbeStep? Bridge()
    {
        var step = _report.Begin("H2.0", "Мост API5→API7", "Тот же сеанс отдаёт IApplication?");
        try
        {
            _app7 = _app.GetType().InvokeMember(
                "ksGetApplication7", System.Reflection.BindingFlags.InvokeMethod, null, _app, null) as IApplication;
            step.Data["app7"] = _app7 is not null;
            if (_app7 is null)
            {
                step.Fail("ksGetApplication7 не отдал IApplication.");
                return null;
            }

            step.Pass("Мост построен.");
            return step;
        }
        catch (Exception ex)
        {
            step.Data["app7_error"] = ex.Message;
            step.Fail("ksGetApplication7 бросил: " + ex.Message);
            return null;
        }
    }

    private void Launch()
    {
        var step = _report.Begin("H2.1:app", "Свой невидимый экземпляр", null);
        try
        {
            dynamic? raw = Activator.CreateInstance(Type.GetTypeFromProgID("KOMPAS.Application.5")!);
            _app = (KompasObject)raw!;
            _app.Visible = false;
            step.Data["pid"] = Environment.ProcessId;
            step.Pass("Экземпляр поднят невидимо.");
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.ToString());
            step.Fail("Экземпляр не поднят: " + ex.Message);
        }

        Bridge();
    }

    private void Finish()
    {
        var step = _report.Begin("H2.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app?.Quit();
            step.Data["quit"] = true;
            step.Pass("Сеанс завершён.");
        }
        catch (Exception ex)
        {
            step.Data["quit"] = "исключение: " + ex.Message;
            step.Fail("Сеанс не завершён: " + ex.Message);
        }
    }

    private sealed class LiveFillet
    {
        public required ksDocument3D Doc { get; init; }

        public required ksPart Part { get; init; }

        public required IModelContainer Container { get; init; }

        public required IFillet Fillet { get; init; }

        public int? BaseObjectCount { get; set; }
    }
}
