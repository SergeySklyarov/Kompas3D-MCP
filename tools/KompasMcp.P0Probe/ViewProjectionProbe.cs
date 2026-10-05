using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Kompas6API5;
using KompasAPI7;

namespace KompasMcp.P0Probe;

/// <summary>VIEW — measurement of the view/projection route (<c>ksViewProjectionCollection</c> /
/// <c>ksViewProjection</c>) before any MCP tool touches it.</summary>
/// <remarks>INVARIANT: a step PASSES only on a value actually READ back; two equal nulls are not a
/// measurement, and a value that could not be read leaves the step `unknown`, never `pass`.
/// INVARIANT: projection changes are judged against the document's own change flag read from API7
/// (<c>IKompasDocument.Changed</c>, help page ikompasdocument_changed), not a server-side revision.
/// LIMIT: it drives the DOCUMENT's projection collection, not the UI camera of a user's window.
/// History: docs/decisions/probes.md#view-projection</remarks>
internal static class ViewProjectionProbe
{
    /// <summary>Predefined projection types by <c>ksViewProjectionType</c> (help page
    /// ksviewprojectiontype.html): the numbers the collection actually reports, not the API5
    /// <c>ProjectionType</c> numbers, which do not match.</summary>
    private const int KsFront = 1;
    private const int KsRear = 2;
    private const int KsUp = 3;
    private const int KsIsometric = 7;

    private const int ImageResolution = 100;

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("VIEW.0", "Управление проекцией отображения");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС: шаг пропущен.";
            return;
        }

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            // options.ViewVisible selects the mode the order demands: the product's invisible
            // document (default) versus a visible document in a visible window.
            var invisible = !options.ViewVisible;
            if (!doc.Create(invisible, true))
            {
                var failed = report.Begin("VIEW.0", "Создание документа под замер проекций");
                failed.Fail($"Document3D().Create(invisible={invisible}, true) вернул false.");
                return;
            }

            if (options.ViewVisible)
            {
                MakeWindowVisible(app, report);
            }

            BuildPlate(doc);
            report.Begin("VIEW.0", "Документ под замер проекций построен")
                .Pass($"Несимметричное тело построено (пластина с вырезом у угла), режим visible={options.ViewVisible}.");

            var collection = ReadCollection(report, doc);
            if (collection is null)
            {
                return;
            }

            var before = ReadCurrentProjection(report, collection);
            var api7 = ReadChangedAvailability(report, doc, app);
            var baseChanged = EstablishBaseState(report, doc, api7);
            ReadState(report, api7, "VIEW.2a", "Состояние документа до смены вида");

            MeasureApplyAndReadBack(report, doc, collection, options, api7, baseChanged);
            MeasureProjectionDiscrimination(report, doc, collection);
            MeasureSnapshots(report, doc, collection);
            MeasureRestore(report, doc, collection, before, api7);
        }
        finally
        {
            try
            {
                doc?.close();
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine("[VIEW] doc.close() бросил: " + ex.Message);
            }
        }
    }

    /// <summary>Raise the application window so the "visible document in a visible window" mode of
    /// the order is measured, not assumed.</summary>
    /// <remarks>DOC: the document's own visibility mode is read-only (<c>ksDocument3D.invisibleMode</c>);
    /// a visible window is a VISIBLE document (<c>Create(invisible:false)</c>) plus the application's
    /// <c>Visible</c>, presented by <c>SetActive()</c> and redrawn by <c>ksRefreshActiveWindow()</c>.
    /// History: docs/decisions/probes.md#view-projection</remarks>
    private static void MakeWindowVisible(KompasObject app, ProbeReport report)
    {
        var step = report.Begin("VIEW.0v", "Видимый режим окна для снимков");
        try
        {
            Members.SetProp(typeof(KompasObject), app, "Visible", true);
            var activated = (app.ActiveDocument3D() as ksDocument3D)?.SetActive();
            var refresh = app.ksRefreshActiveWindow();
            step.Data["set_active"] = activated;
            step.Data["refresh_active_window"] = refresh;
            step.Pass($"Окно поднято: Visible=true, SetActive={activated}, ksRefreshActiveWindow={refresh}.");
        }
        catch (Exception ex) when (ex is COMException or MissingMemberException or TargetInvocationException)
        {
            step.Unknown("Сделать окно видимым не удалось: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>A 100×80×10 plate with a rectangular notch cut out of ONE corner — deliberately
    /// asymmetric, because a symmetric plate projects to the same outline from several views and
    /// would make "the projection changes the image" unprovable.</summary>
    /// <remarks>The notch is a second extrusion with the <c>cut</c> flag, reusing the P2 route
    /// (<c>o3d_baseExtrusion</c> + <c>ksBaseExtrusionDefinition</c>) so the probe carries no
    /// geometry route of its own that could fail and be mistaken for a fact about projections.
    /// History: docs/decisions/probes.md#view-projection</remarks>
    private static void BuildPlate(ksDocument3D doc)
    {
        var part = (ksPart)doc.GetPart(-1);

        var sketch = (ksEntity)part.NewEntity((short)EntityTypes.Value("o3d_sketch", 24));
        var sketchDef = (ksSketchDefinition)sketch.GetDefinition();
        sketchDef.SetPlane((ksEntity)part.GetDefaultEntity((short)EntityTypes.Value("o3d_planeXOY", 28)));
        sketch.Create();

        var edit = (ksDocument2D)sketchDef.BeginEdit();
        edit.ksLineSeg(-50, -40, 50, -40, 1);
        edit.ksLineSeg(50, -40, 50, 40, 1);
        edit.ksLineSeg(50, 40, -50, 40, 1);
        edit.ksLineSeg(-50, 40, -50, -40, 1);
        sketchDef.EndEdit();

        Extrude(part, sketch, 10d, cut: false);
        part.Update();
        doc.RebuildDocument();

        // The notch: 30×30 remove at the (+X,+Y) corner of the top face.
        var corner = (ksEntity)part.NewEntity((short)EntityTypes.Value("o3d_sketch", 24));
        var cornerDef = (ksSketchDefinition)corner.GetDefinition();
        cornerDef.SetPlane((ksEntity)part.GetDefaultEntity((short)EntityTypes.Value("o3d_planeXOY", 28)));
        corner.Create();
        var cutter = (ksDocument2D)cornerDef.BeginEdit();
        cutter.ksLineSeg(50, 20, 50, 40, 1);
        cutter.ksLineSeg(50, 40, 30, 40, 1);
        cutter.ksLineSeg(30, 40, 30, 20, 1);
        cutter.ksLineSeg(30, 20, 50, 20, 1);
        cornerDef.EndEdit();

        Extrude(part, corner, 10d, cut: true);
        part.Update();
        doc.RebuildDocument();
    }

    private static void Extrude(ksPart part, ksEntity sketch, double depth, bool cut)
    {
        var extrusion = (ksEntity)part.NewEntity((short)EntityTypes.Value("o3d_baseExtrusion", 45));
        if (extrusion.GetDefinition() is not ksBaseExtrusionDefinition definition)
        {
            throw new InvalidOperationException("GetDefinition() выдал не ksBaseExtrusionDefinition (маршрут P2).");
        }

        definition.SetSketch(sketch);
        definition.directionType = 0;
        definition.SetSideParam(true, VendorConstants.Value("ksEndTypeEnum", "etBlind", 0), depth, 0d, cut);
        if (!extrusion.Create())
        {
            throw new InvalidOperationException($"Create() выдавливания (cut={cut}) вернул false.");
        }
    }

    /// <summary>VIEW.1 — read the collection and the predefined projections it carries. The help page says
    /// the collection "is filled automatically with the projections defined in the document", so the
    /// count and the per-entry type are MEASURED, not assumed from the enum.</summary>
    private static ksViewProjectionCollection? ReadCollection(ProbeReport report, ksDocument3D doc)
    {
        var step = report.Begin(
            "VIEW.1",
            "Коллекция проекций: состав и типы предопределённых",
            "Что реально лежит в GetViewProjectionCollection() у этой детали и совпадают ли типы с ProjectionType?");

        try
        {
            var collection = (ksViewProjectionCollection)doc.GetViewProjectionCollection();
            if (collection is null)
            {
                step.Fail("GetViewProjectionCollection() вернул null.");
                return null;
            }

            var count = collection.GetCount();
            step.Data["count"] = count;
            step.Observe($"GetCount() = {count}");

            var types = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var projection = (ksViewProjection)collection.GetByIndex(i);
                if (projection is null)
                {
                    types.Add(new { index = i, read = "null" });
                    continue;
                }

                var type = SafeType(projection);
                var name = SafeName(projection);
                var isCurrent = SafeIsCurrent(projection);
                types.Add(new { index = i, type, name, is_current = isCurrent });
                step.Observe($"  [{i}] type={type} name='{name}' current={isCurrent}");
            }

            step.Data["projections"] = types;
            step.Data["scheme"] = ReadScheme(collection);
            step.Observe($"viewProjectionScheme = {step.Data["scheme"]}");

            if (count <= 0)
            {
                step.Unknown("Коллекция проекций пуста: применять нечего документированным перебором.");
                return null;
            }

            step.Pass($"Коллекция читается: {count} проекций; схема {step.Data["scheme"]}.");
            return collection;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Маршрут коллекции не сработал: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>VIEW.2 — read the CURRENT projection before any change: which entry answers IsCurrent, and
    /// what type it reports. This is the value a restore would have to reproduce.</summary>
    private static CurrentProjection ReadCurrentProjection(ProbeReport report, ksViewProjectionCollection collection)
    {
        var step = report.Begin(
            "VIEW.2",
            "Текущая проекция до изменения",
            "Можно ли прочитать текущий вид документированным способом (IsCurrent + GetViewProjectonType)?");

        var result = new CurrentProjection();
        try
        {
            var count = collection.GetCount();
            var found = 0;
            for (var i = 0; i < count; i++)
            {
                var projection = (ksViewProjection)collection.GetByIndex(i);
                if (projection is null || SafeIsCurrent(projection) != true)
                {
                    continue;
                }

                found++;
                result.Index = i;
                result.Type = SafeType(projection);
                result.Name = SafeName(projection);
                result.Found = true;
                step.Observe($"[{i}] IsCurrent=true, GetViewProjectonType()={result.Type}, name='{result.Name}'");
            }

            step.Data["current_index"] = result.Index;
            step.Data["current_type"] = result.Type;
            step.Data["current_name"] = result.Name;
            step.Data["current_entries"] = found;

            if (found == 0)
            {
                step.Unknown("Ни одна проекция не ответила IsCurrent=true: прочитать текущий вид этим способом нельзя.");
            }
            else if (found > 1)
            {
                step.Unknown($"IsCurrent=true ответили {found} проекции: признак текущей неоднозначен.");
            }
            else
            {
                step.Pass($"Текущая проекция читается: индекс {result.Index}, тип {result.Type}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Чтение текущей проекции не сработало: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    /// <summary>VIEW.C — whether the API7 document of the same window answers <c>Changed</c> at all.
    /// The API5 <c>ksDocument3D</c> has no such flag, so this names the bridge the state steps use;
    /// if it is unavailable, those steps stay `unknown` instead of comparing two nulls.</summary>
    private static ChangedProbe ReadChangedAvailability(ProbeReport report, ksDocument3D doc, KompasObject app)
    {
        var step = report.Begin(
            "VIEW.C",
            "Признак изменённости: доступен ли IKompasDocument.Changed",
            "Отвечает ли документ API7 того же окна свойством Changed (справка ikompasdocument_changed)?");

        var probe = new ChangedProbe();
        try
        {
            var transferred = app.TransferInterface(doc, Api7DualTransfer, 0) as IKompasDocument;
            if (transferred is null)
            {
                step.Unknown("TransferInterface(документ → API7) не дал IKompasDocument: признак читать нечем.");
                return probe;
            }

            probe.Document = transferred;
            var value = ReadChanged(transferred);
            step.Data["changed_initial"] = value;
            step.Observe($"Документ API7 получен; Changed сразу после постройки = {Fmt(value)}.");
            if (value is null)
            {
                step.Unknown("Свойство Changed бросило исключение: значение не прочитано.");
                return probe;
            }

            probe.Available = true;
            step.Pass($"IKompasDocument.Changed читается (после постройки тела: {Fmt(value)}).");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Fail($"Перенос документа в API7 не сработал: {ex.GetType().Name}: {ex.Message}");
        }

        return probe;
    }

    /// <summary>VIEW.2b — establish the base state the order demands: build the body, SAVE the
    /// document into the run sandbox, THEN read <c>Changed</c>. It must be false, or the base is
    /// dirty and every later comparison is meaningless.</summary>
    /// <remarks>DOC: <c>ikompasdocument_changed</c> — "TRUE — документ изменен с момента последнего
    /// сохранения, FALSE — документ не изменялся". Save-then-read is the only way to reach a known
    /// false; without it the first read would be true by construction and prove nothing.
    /// History: docs/decisions/probes.md#view-projection</remarks>
    private static bool? EstablishBaseState(ProbeReport report, ksDocument3D doc, ChangedProbe api7)
    {
        if (!api7.Available || api7.Document is null)
        {
            return null;
        }

        var step = report.Begin(
            "VIEW.2b",
            "База: сохранение документа и чтение признака изменённости",
            "Даёт ли сохранение документа в песочницу прогона Changed=false, то есть годную базу для сравнения?");

        var path = WorkFile("VIEW_base.m3d");
        bool saved;
        try
        {
            saved = doc.SaveAs(path);
        }
        catch (COMException ex)
        {
            step.Fail($"SaveAs в песочницу бросил: {ex.Message}");
            return null;
        }

        step.Data["saved_path"] = path;
        step.Data["save_as_returned"] = saved;
        step.Observe($"SaveAs(«{Path.GetFileName(path)}») → {saved}.");

        var afterSave = ReadChanged(api7.Document);
        step.Data["changed_after_save"] = afterSave;
        step.Observe($"Changed сразу после сохранения = {Fmt(afterSave)}.");

        if (!saved)
        {
            step.Unknown("SaveAs вернул false: база не подтверждена, и признак после сохранения может остаться true.");
            return afterSave;
        }

        if (afterSave == false)
        {
            step.Pass("После сохранения Changed=false: база годна для сравнения.");
            return afterSave;
        }

        step.Unknown($"После сохранения Changed={Fmt(afterSave)}, а не false: база не годится, и оценка «двигает ли смена вида» остаётся непрочитанной.");
        return afterSave;
    }

    /// <summary>VIEW.3 — apply the requested projection through the documented route and read the type BACK
    /// from the API. The verdict needs the read-back: SetCurrent returning TRUE is not the result.</summary>
    private static void MeasureApplyAndReadBack(
        ProbeReport report,
        ksDocument3D doc,
        ksViewProjectionCollection collection,
        ProbeOptions options,
        ChangedProbe api7,
        bool? baseChanged)
    {
        var wanted = options.ViewType;
        var step = report.Begin(
            "VIEW.3",
            $"Применение проекции типа {wanted} и обратное чтение",
            $"Можно ли применить стандартную проекцию типа {wanted} к окну документа и подтвердить её типом, прочитанным из API?");

        step.Data["requested_type"] = wanted;
        step.Data["requested_name"] = options.ViewName;

        try
        {
            var target = FindByType(collection, wanted);
            if (target is null)
            {
                // The order demands this be measured, not guessed: if the collection carries no entry
                // of the requested type, then the projection a TOOL would have to switch to cannot be
                // reached by enumerating the collection, and the tool must not silently do nothing.
                var all = AllTypes(collection);
                step.Unknown($"Проекция типа {wanted} в коллекции не найдена. Типы в коллекции: [{string.Join(", ", all)}].");
                return;
            }

            step.Data["found_type"] = SafeType(target);
            step.Data["found_name"] = SafeName(target);

            var applied = target.SetCurrent();
            collection.refresh();
            var afterType = ReadCurrentType(collection);
            step.Data["set_current_returned"] = applied;
            step.Data["type_after"] = afterType;

            if (afterType == wanted)
            {
                step.Pass($"Проекция применена и прочитана обратно: GetViewProjectonType()={afterType}.");
            }
            else if (applied)
            {
                step.Unknown($"SetCurrent() вернул true, но обратное чтение дало тип {afterType}, а не {wanted}: "
                             + "утверждать применение по одному возврату нельзя.");
            }
            else
            {
                step.Fail($"SetCurrent() на типе {wanted} вернул false (обратное чтение: тип {afterType}).");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Применение проекции не сработало: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (api7.Available)
        {
            ReadState(report, api7, "VIEW.3a", "Состояние документа после смены вида", baseChanged);
        }
    }

    /// <summary>VIEW.2a / VIEW.3a — the document's own change flag around the view change. The order
    /// requires this measured rather than reasoned: if a projection switch bumps <c>Changed</c>, a
    /// read-only tool that restores the view still leaves the document dirty.</summary>
    private static void ReadState(ProbeReport report, ChangedProbe api7, string id, string title, bool? baseline = null)
    {
        var step = report.Begin(
            id,
            title,
            baseline is null
                ? "Что говорит признак изменённости документа до смены вида?"
                : "Двигает ли смена вида признак изменённости документа?");

        if (!api7.Available || api7.Document is null)
        {
            step.Unknown("IKompasDocument.Changed недоступен: признак не прочитан, и PASS здесь был бы ложным.");
            return;
        }

        var current = ReadChanged(api7.Document);
        step.Data["changed"] = current;
        step.Observe($"Changed = {Fmt(current)}");

        if (current is null)
        {
            step.Unknown("Признак изменённости не прочитан: сравнивать нечего, и PASS здесь был бы ложным.");
            return;
        }

        if (baseline is null)
        {
            // A read value with no baseline is still a measurement: the state before the change is
            // recorded, and only the comparison needs two reads.
            step.Data["note"] = "База не подтверждена (VIEW.2b): значение записано, сравнение не делается.";
            step.Pass($"Признак изменённости прочитан до смены вида: Changed={Fmt(current)} (сравнение без базы не делается).");
            return;
        }

        step.Data["changed_before"] = baseline;
        var moved = baseline != current;
        step.Data["changed_moved"] = moved;
        step.Pass(moved
            ? $"Смена вида ИЗМЕНИЛА признак: Changed {Fmt(baseline)}→{Fmt(current)}."
            : $"Смена вида признак НЕ изменила: Changed={Fmt(current)} до и после.");
    }

    /// <summary>VIEW.3b — the projection must change the IMAGE, not only the API state. Snapshots of
    /// three projections on the asymmetric body are decoded and compared pixel by pixel; identical
    /// images would mean the route proves nothing about what is drawn.</summary>
    private static void MeasureProjectionDiscrimination(
        ProbeReport report,
        ksDocument3D doc,
        ksViewProjectionCollection collection)
    {
        var step = report.Begin(
            "VIEW.3b",
            "Разные проекции дают разные снимки (по пикселям)",
            "Меняет ли применённая проекция изображение, или меняется только состояние API?");

        // Front and rear are deliberately both captured: they usually share a raster extent, which
        // gives at least one SAME-SIZE pair and therefore a true pixel-count comparison, not only a
        // difference in dimensions.
        var wanted = new (int Type, string Label)[]
        {
            (KsFront, "front"), (KsRear, "rear"), (KsUp, "up"), (KsIsometric, "iso"),
        };
        var captured = new Dictionary<int, PngImage>();
        var hashes = new Dictionary<int, string>();

        foreach (var (type, label) in wanted)
        {
            var projection = FindByType(collection, type);
            if (projection is null)
            {
                step.Unknown($"Проекция типа {type} ({label}) в коллекции не найдена — снимок не снят.");
                return;
            }

            projection.SetCurrent();
            collection.refresh();
            var bytes = Snapshot(doc);
            if (bytes is null || bytes.Length == 0)
            {
                step.Unknown($"Снимок проекции {type} ({label}) не получен: сравнивать нечего.");
                return;
            }

            var path = WorkFile($"VIEW_proj_{label}_{type}.png");
            File.WriteAllBytes(path, bytes);
            step.Artifacts.Add(path);
            step.Data[$"sha256_{label}"] = Sha256(bytes);

            var image = PngPixels.TryDecode(bytes, out var failure);
            if (image is null)
            {
                step.Unknown($"PNG проекции {type} ({label}) не разобран: {failure}.");
                return;
            }

            captured[type] = image;
            hashes[type] = Sha256(bytes);
            step.Observe($"Проекция {type} ({label}): {bytes.Length} байт, {image.Width}×{image.Height}, sha256 {hashes[type][..12]}…");
        }

        var pairs = new (int A, int B)[]
        {
            (KsFront, KsRear),
            (KsFront, KsUp),
            (KsFront, KsIsometric),
            (KsUp, KsIsometric),
        };

        // The kernel sizes the raster to the model's extent IN THE CURRENT VIEW, so two projections
        // may legitimately differ in dimensions. A size difference is itself proof the drawing
        // changed; pixels are compared only when the dimensions agree.
        var allDiffer = true;
        var anyPixelCompared = false;
        foreach (var (a, b) in pairs)
        {
            var (differing, coords) = PngPixels.Compare(captured[a], captured[b]);
            if (differing < 0)
            {
                step.Data[$"diff_{a}_vs_{b}"] = "габариты не совпали";
                step.Observe($"Проекции {a} vs {b}: габариты снимков не совпали "
                             + $"({captured[a].Width}×{captured[a].Height} против {captured[b].Width}×{captured[b].Height}) — "
                             + "изображение заведомо разное, пиксели не сравниваются.");
                continue;
            }

            anyPixelCompared = true;
            step.Data[$"diff_{a}_vs_{b}"] = differing;
            step.Observe($"Проекции {a} vs {b}: различается {differing} пикселей из {captured[a].Width * captured[a].Height}"
                         + (coords is null ? string.Empty : $"; первые: {coords}"));
            allDiffer &= differing > 0;
        }

        step.Data["pixel_pair_compared"] = anyPixelCompared;

        if (allDiffer && anyPixelCompared)
        {
            step.Pass("Проекции дают разные снимки; на совпавших по габариту парах различие подтверждено ПО ПИКСЕЛЯМ.");
        }
        else if (allDiffer)
        {
            step.Pass("Проекции дают разные снимки, но ни одна пара не совпала по габариту: различие доказано размером, "
                      + "а не поштучным сравнением пикселей.");
        }
        else
        {
            step.Unknown("Хотя бы одна пара проекций дала совпадающие снимки: проекция меняет состояние API, но не изображение.");
        }
    }

    /// <summary>VIEW.4 — snapshots of the SAME projection, compared byte for byte and by pixels. The
    /// order names this explicitly; a snapshot that changes between identical requests makes two
    /// views incomparable, and the earlier measurement said they were identical.</summary>
    /// <remarks>MEASURED before this revision: two consecutive snapshots had the same length but
    /// differed bytewise, with the cause unknown and the bytes not kept. So the control now runs
    /// BEFORE any projection change, the files are kept, and the difference is classified into
    /// "service blocks only" versus "pixels differ".
    /// History: docs/decisions/probes.md#view-projection</remarks>
    private static void MeasureSnapshots(
        ProbeReport report,
        ksDocument3D doc,
        ksViewProjectionCollection collection)
    {
        var step = report.Begin(
            "VIEW.4",
            "Два снимка одной проекции — побайтовое совпадение и пиксели",
            "Даёт ли фиксированная проекция один и тот же снимок, или изображение всё равно плавает?");

        try
        {
            var first = Snapshot(doc);
            var second = Snapshot(doc);
            if (first is null || second is null)
            {
                step.Unknown("Снимок не получен (пустой массив байт): сравнивать нечего.");
                return;
            }

            var firstPath = WorkFile("VIEW_same_first.png");
            var secondPath = WorkFile("VIEW_same_second.png");
            File.WriteAllBytes(firstPath, first);
            File.WriteAllBytes(secondPath, second);
            step.Artifacts.Add(firstPath);
            step.Artifacts.Add(secondPath);

            var firstHash = Sha256(first);
            var secondHash = Sha256(second);
            step.Data["first_bytes"] = first.Length;
            step.Data["second_bytes"] = second.Length;
            step.Data["first_sha256"] = firstHash;
            step.Data["second_sha256"] = secondHash;

            var equal = first.AsSpan().SequenceEqual(second);
            step.Data["byte_equal"] = equal;
            step.Observe($"Первый: {first.Length} байт, sha256 {firstHash[..12]}…; второй: {second.Length} байт, sha256 {secondHash[..12]}…; побайтово: {equal}.");

            if (!equal)
            {
                ClassifyDifference(step, first, second);
            }

            // And the same check across a projection switch, to separate "redraw noise" from
            // "the switch itself perturbs the frame". The target is deliberately a DIFFERENT
            // projection from the one already current, or the control would compare a frame with
            // itself and prove nothing.
            var currentType = ReadCurrentType(collection);
            var switchTo = currentType == KsFront ? KsUp : KsFront;
            var target = FindByType(collection, switchTo);
            if (target is not null)
            {
                target.SetCurrent();
                collection.refresh();
                var third = Snapshot(doc);
                if (third is not null && third.Length > 0)
                {
                    var thirdPath = WorkFile("VIEW_after_change.png");
                    File.WriteAllBytes(thirdPath, third);
                    step.Artifacts.Add(thirdPath);
                    step.Data["after_change_bytes"] = third.Length;
                    step.Data["after_change_sha256"] = Sha256(third);
                    step.Data["after_change_equal_to_first"] = third.AsSpan().SequenceEqual(first);
                    step.Observe($"После смены проекции: {third.Length} байт, sha256 {Sha256(third)[..12]}…");
                }
            }

            if (equal)
            {
                step.Pass($"{first.Length} байт совпали побайтово на фиксированной проекции.");
            }
            else
            {
                step.Unknown("Снимки одной проекции разошлись побайтово: причина классифицирована ниже, но «сравнимы побайтово» не подтверждено.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            step.Fail($"Снятие снимка не сработало: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Classify a bytewise difference: does it touch PIXELS, or only service blocks
    /// (timestamps/metadata) that leave the drawing identical?</summary>
    private static void ClassifyDifference(ProbeStep step, byte[] first, byte[] second)
    {
        var a = PngPixels.TryDecode(first, out var failureA);
        var b = PngPixels.TryDecode(second, out var failureB);
        if (a is null || b is null)
        {
            step.Data["classification"] = "не разобрано: " + (failureA ?? failureB);
            step.Observe($"Классификация невозможна: {failureA ?? failureB}.");
            return;
        }

        var (differing, coords) = PngPixels.Compare(a, b);
        step.Data["pixel_differing"] = differing;
        step.Data["pixel_total"] = a.Width * a.Height;
        if (differing == 0)
        {
            step.Data["classification"] = "служебные блоки PNG: пиксели идентичны";
            step.Observe($"Пиксели идентичны ({a.Width}×{a.Height}): различие — в служебных блоках, а не в изображении.");
        }
        else
        {
            step.Data["classification"] = "пиксельное различие";
            step.Observe($"Пиксельное различие: {differing} из {a.Width * a.Height}"
                         + (coords is null ? string.Empty : $"; первые: {coords}"));
        }
    }

    /// <summary>VIEW.5 — restore: put the projection that was current BEFORE the change back and read it.
    /// The order requires this to be named whether it works or not, and the change flag is read
    /// again afterwards to say whether the restore left the document dirty.</summary>
    private static void MeasureRestore(
        ProbeReport report,
        ksDocument3D doc,
        ksViewProjectionCollection collection,
        CurrentProjection before,
        ChangedProbe api7)
    {
        var step = report.Begin(
            "VIEW.5",
            "Возврат прежнего вида после снимка",
            "Восстанавливается ли прежняя проекция документированным способом, и читается ли она обратно?");

        try
        {
            collection.refresh();
            if (!before.Found)
            {
                step.Unknown("Прежняя проекция не была прочитана в VIEW.2 — возвращать не к чему.");
                return;
            }

            var previous = FindByType(collection, before.Type);
            if (previous is null)
            {
                step.Unknown($"Проекция типа {before.Type} (прежний вид) в коллекции не найдена — вернуть нечем.");
                return;
            }

            var restored = previous.SetCurrent();
            collection.refresh();
            var typeAfter = ReadCurrentType(collection);
            step.Data["restore_returned"] = restored;
            step.Data["type_after_restore"] = typeAfter;
            step.Data["expected_type"] = before.Type;

            if (api7.Available && api7.Document is not null)
            {
                var changedAfterRestore = ReadChanged(api7.Document);
                step.Data["changed_after_restore"] = changedAfterRestore;
                step.Observe($"Changed после возврата вида = {Fmt(changedAfterRestore)}.");
            }

            if (typeAfter == before.Type)
            {
                step.Pass($"Прежний вид возвращён и прочитан обратно: тип {typeAfter}.");
            }
            else
            {
                step.Unknown($"Возврат не подтверждён: ожидался тип {before.Type}, прочитан {typeAfter}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            step.Fail($"Возврат вида не сработал: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    /// <summary>API5 → API7 transfer mode, taken from the vendor enum rather than copied as a literal:
    /// a wrong transfer mode does not error but yields an object representing a different entity.</summary>
    private static int Api7DualTransfer
    {
        get
        {
            var member = typeof(Kompas6Constants.ksAPITypeEnum).GetField("ksAPI7Dual", BindingFlags.Public | BindingFlags.Static);
            return member is null ? 1 : Convert.ToInt32(member.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Byte-mode snapshot through the documented API5 raster route, so the comparison is on the
    /// same carrier the tool returns.</summary>
    /// <remarks>INVARIANT: the returned bytes must carry the PNG signature. The kernel does NOT reject
    /// a format outside its own list (measured: a bad code wrote TIFF for a "PNG" request), so the
    /// carrier is verified here rather than trusted. History: docs/decisions/probes.md#raster-format-codes</remarks>
    private static byte[]? Snapshot(ksDocument3D doc)
    {
        var parameter = (ksRasterFormatParam)doc.RasterFormatParam();
        parameter.Init();
        parameter.format = RasterFormatCodes.Png;
        parameter.colorBPP = 24;
        parameter.extResolution = ImageResolution;
        parameter.returnResultAsArrayBytes = true;
        if (!doc.SaveAsToRasterFormat(string.Empty, parameter))
        {
            return null;
        }

        var bytes = parameter.resultArrayBytes switch
        {
            byte[] typed => typed,
            Array array when array.Rank == 1 => array.Cast<object>().Select(Convert.ToByte).ToArray(),
            _ => null,
        };

        if (bytes is null)
        {
            return null;
        }

        // PNG magic: 89 50 4E 47 0D 0A 1A 0A. A mismatch means the kernel wrote another format and
        // the caller would compare the wrong carrier; null keeps that out of the measurements.
        byte[] magic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        for (var i = 0; i < magic.Length; i++)
        {
            if (i >= bytes.Length || bytes[i] != magic[i])
            {
                return null;
            }
        }

        return bytes;
    }

    /// <summary>Read <c>IKompasDocument.Changed</c>; null means "not read", which is different from
    /// false and must never be compared as if it were.</summary>
    private static bool? ReadChanged(IKompasDocument document)
    {
        try
        {
            return document.Changed;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static ksViewProjection? FindByType(ksViewProjectionCollection collection, int type)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = (ksViewProjection)collection.GetByIndex(i);
            if (projection is not null && SafeType(projection) == type)
            {
                return projection;
            }
        }

        return null;
    }

    private static IReadOnlyList<int> AllTypes(ksViewProjectionCollection collection)
    {
        var types = new List<int>();
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = (ksViewProjection)collection.GetByIndex(i);
            if (projection is not null)
            {
                types.Add(SafeType(projection));
            }
        }

        return types;
    }

    private static string Fmt(bool? value) => value is null ? "<не прочитано>" : value.Value ? "true" : "false";

    private static int ReadCurrentType(ksViewProjectionCollection collection)
    {
        var count = collection.GetCount();
        for (var i = 0; i < count; i++)
        {
            var projection = (ksViewProjection)collection.GetByIndex(i);
            if (projection is not null && SafeIsCurrent(projection) == true)
            {
                return SafeType(projection);
            }
        }

        return int.MinValue;
    }

    private static int SafeType(ksViewProjection projection)
    {
        try
        {
            return projection.GetViewProjectonType();
        }
        catch (COMException)
        {
            return int.MinValue;
        }
    }

    private static string? SafeName(ksViewProjection projection)
    {
        try
        {
            return projection.name;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool? SafeIsCurrent(ksViewProjection projection)
    {
        try
        {
            return projection.IsCurrent();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static object? ReadScheme(ksViewProjectionCollection collection)
    {
        try
        {
            // The interop property is named viewProjectionScheme (P0.2 metadata); the getter is not
            // callable explicitly, only the property is.
            return collection.viewProjectionScheme;
        }
        catch (COMException)
        {
            return null;
        }
    }

    internal sealed class CurrentProjection
    {
        public bool Found { get; set; }
        public int Index { get; set; } = -1;
        public int Type { get; set; } = int.MinValue;
        public string? Name { get; set; }
    }

    /// <summary>The API7 document of the same window plus whether its change flag is readable at all.</summary>
    internal sealed class ChangedProbe
    {
        public bool Available { get; set; }
        public IKompasDocument? Document { get; set; }
    }
}

/// <summary>Raster format codes as the vendor enum defines them, by NAME — never a copied literal. The
/// tool publishes four formats; the probe needs only PNG.</summary>
/// <remarks>MEASURED: <c>ksRasterFormatEnum</c> members are <c>ksRasterFormatBMP=0</c>,
/// <c>ksRasterFormatJPG=2</c>, <c>ksRasterFormatPNG=3</c>, <c>ksRasterFormatTIF=4</c>. An earlier
/// revision of this file asked for member <c>rfPNG</c> with fallback <c>4</c>; neither the name nor
/// the number exists in the enum, so the request reached the kernel as TIF and the "PNG" snapshots
/// were TIFF — a probe defect that read as a product fact until the pixels were decoded.
/// History: docs/decisions/probes.md#raster-format-codes</remarks>
internal static class RasterFormatCodes
{
    public static short Bmp => VendorConstants.Value("ksRasterFormatEnum", "ksRasterFormatBMP", 0);

    public static short Png => VendorConstants.Value("ksRasterFormatEnum", "ksRasterFormatPNG", 3);
}
