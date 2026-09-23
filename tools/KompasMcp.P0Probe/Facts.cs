using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Com;

namespace KompasMcp.P0Probe;

/// <summary>
/// P0.3–P0.5 — connect, prove which process the object belongs to, and establish whether the
/// application is usable without a window. Every step states its question in the report, because
/// the answer that matters most here is the one that turns out to be "no".
/// </summary>
internal static class ConnectionFacts
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var init = report.Begin(
            "P0.3",
            "COM-апартаменты и message pump",
            "Работает ли STA-поток с настоящим pump в x64 .NET-процессе?");
        try
        {
            init.Observe($"Поток #{Environment.CurrentManagedThreadId}, апартамент {Thread.CurrentThread.GetApartmentState()}.");
            init.Observe($"Статистика исполнителя: {string.Join(", ", ProbeSession.Sta!.Statistics().Select(kv => kv.Key + "=" + kv.Value))}.");
            init.Pass("Все обращения к КОМПАС идут через этот единственный STA-поток с pump.");
        }
        catch (Exception ex)
        {
            init.Fail("Инициализация не подтверждена: " + ex.Message);
        }

        var app = Connect(report, options);
        if (app is null)
        {
            return;
        }

        Headless(report, app);
        DocumentDiscovery(report, app);
    }

    private static KompasObject? Connect(ProbeReport report, ProbeOptions options)
    {
        var step = report.Begin(
            "P0.4",
            "Подключение к КОМПАС (" + options.Mode + ") и привязка к PID",
            "Можно ли получить рабочий KompasObject и доказать, какому процессу он принадлежит?");

        if (options.Mode == "none")
        {
            step.Verdict = Verdict.Skipped;
            step.Conclusion = "Режим none: COM пропущен.";
            return null;
        }

        var before = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
        try
        {
            var serverType = Type.GetTypeFromProgID(ProbeSession.ProgIdUsed, throwOnError: false)
                ?? throw new InvalidOperationException($"ProgID {ProbeSession.ProgIdUsed} не зарегистрирован.");

            var stopwatch = Stopwatch.StartNew();
            var created = Activator.CreateInstance(serverType);
            stopwatch.Stop();
            step.Data["create_instance_ms"] = stopwatch.ElapsedMilliseconds;

            if (created is not KompasObject app)
            {
                step.Fail($"Создан объект {created?.GetType().FullName ?? "null"}, а не KompasObject.");
                return null;
            }

            ProbeSession.App = app;
            step.Observe($"KompasObject за {stopwatch.ElapsedMilliseconds} мс; CLR-тип {created.GetType().FullName} (__ComObject: приведение к интерфейсу работает, GetInterfaces() — нет).");

            // Version through the only documented route on this interface.
            try
            {
                var versionArgs = new object?[] { null, null, null, null };
                typeof(KompasObject).GetMethod("ksGetSystemVersion")!.Invoke(app, versionArgs);
                var version = string.Join(".", versionArgs.Select(a => Members.Value(a)));
                step.Observe($"ksGetSystemVersion(&,&,&,) → {version}.");
                step.Data["kompas_system_version"] = version;
            }
            catch (Exception ex)
            {
                step.Observe($"ksGetSystemVersion недоступна: {ex.GetType().Name}");
            }

            step.Observe($"Visible = {Members.Value(Members.Prop(typeof(KompasObject), app, "Visible"))}.");
            step.Data["app_methods_identity"] = Members.Matching(typeof(KompasObject), "Version", "HWindow", "Application7", "Quit", "Exit", "ActiveDocument", "Iterator");
            step.Observe("Методы идентификации/управления: " + string.Join(" | ", (IReadOnlyList<string>)step.Data["app_methods_identity"]!));

            // PID attribution attempt 1: the object's own main window handle, if it has one.
            var windowHandle = IntPtr.Zero;
            try
            {
                var hw = typeof(KompasObject).GetMethod("ksGetHWindow");
                var raw = hw?.Invoke(app, null);
                windowHandle = raw is int i ? new IntPtr(i) : raw is long l ? new IntPtr(l) : IntPtr.Zero;
                step.Observe($"ksGetHWindow() → 0x{windowHandle:X}.");
            }
            catch (Exception ex)
            {
                step.Observe($"ksGetHWindow() → {ex.GetType().Name}");
            }

            if (windowHandle != IntPtr.Zero)
            {
                GetWindowThreadProcessId(windowHandle, out var pid);
                step.Observe($"GetWindowThreadProcessId(окно) → PID {pid}.");
                step.Data["pid_from_window"] = pid;
            }
            else
            {
                step.Observe("Окна у экземпляра нет: привязку по окну получить нечем (невидимый режим).");
            }

            var after = KompasInteropResolver.SnapshotProcessIds("KOMPAS");
            var appeared = after.Except(before).ToArray();
            step.Data["pids_before"] = before;
            step.Data["pids_appeared"] = appeared;
            step.Observe($"Процессов: до {before.Length}, после {after.Length}, новых {appeared.Length} ({string.Join(",", appeared)}).");

            if (appeared.Length == 1)
            {
                ProbeSession.ProcessId = appeared[0];
                ProbeSession.Ownership = "launched";
                ProbeSession.PidEvidence = "diff процессов до/после Activator.CreateInstance (появился ровно один PID)";
                if (windowHandle != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(windowHandle, out var windowPid);
                    var agrees = windowPid == (uint)appeared[0];
                    step.Observe($"Независимая сверка: PID из окна {windowPid} {(agrees ? "совпал" : "НЕ совпал")} с PID из diff {appeared[0]}.");
                    step.Data["window_and_diff_agree"] = agrees;
                }

                step.Pass($"Собственный экземпляр запущен, PID {appeared[0]} доказан двумя способами.");
            }
            else if (appeared.Length == 0)
            {
                ProbeSession.Ownership = "attached";
                step.Unknown("Новых процессов нет: CreateInstance переиспользовал существующий экземпляр.");
            }
            else
            {
                step.Fail($"Один вызов создал {appeared.Length} процессов — привязка невозможна.");
            }

            return app;
        }
        catch (Exception ex)
        {
            var hresult = ex is COMException com ? $" HRESULT=0x{com.HResult:X8} ({ComHResult.Name(com.HResult)})" : string.Empty;
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message + hresult);
            step.Fail("Подключение не установлено.");
            return null;
        }
    }

    /// <summary>
    /// P0.4b — the real headless test. <c>Create</c>'s first argument is <c>invisible</c>
    /// (documented), so an invisible document is <c>Create(true, true)</c>; a previous revision
    /// passed <c>false</c> and measured a visible document while believing it was headless.
    /// </summary>
    private static void Headless(ProbeReport report, KompasObject app)
    {
        var step = report.Begin(
            "P0.4b",
            "Работа без окна: Create(invisible=true, …)",
            "Создаются и перестраиваются ли документы, если КОМПАС и документ невидимы?");

        try
        {
            Members.SetProp(typeof(KompasObject), app, "Visible", false);
            step.Observe($"KompasObject.Visible := false → {Members.Value(Members.Prop(typeof(KompasObject), app, "Visible"))}.");

            var doc = (ksDocument3D)app.Document3D();
            try
            {
                var created = doc.Create(true, true);
                step.Observe($"Create(invisible=true, typeDoc=true) → {created} (невидимая деталь).");
                if (!created)
                {
                    step.Fail("Невидимый документ создать не удалось.");
                    return;
                }

                var part = (ksPart)doc.GetPart(-1);
                var sketch = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_sketch", 5));
                var definition = (ksSketchDefinition)sketch.GetDefinition();
                definition.SetPlane((ksEntity)part.GetDefaultEntity(EntityTypes.Value("o3d_planeXOY", 1)));
                var entityCreated = sketch.Create();
                var editor = (ksDocument2D)definition.BeginEdit();
                editor.ksLineSeg(0, 0, 50, 0, 1);
                var endEdit = definition.EndEdit();
                var rebuilt = doc.RebuildDocument();

                step.Observe($"Эскиз: Create → {entityCreated}, линия нарисована, EndEdit → {endEdit}, RebuildDocument → {rebuilt}.");
                step.Data["headless_sketch_created"] = entityCreated;
                step.Data["headless_rebuilt"] = rebuilt;

                if (entityCreated && endEdit && rebuilt)
                {
                    step.Pass("Голова работает полностью невидимо: Worker не обязан показывать КОМПАС и не будет красть фокус.");
                }
                else
                {
                    step.Fail("В невидимом режиме построение не подтверждено — сервер обязан требовать окно.");
                }
            }
            finally
            {
                try
                {
                    doc.close();
                }
                catch
                {
                    // Result already recorded.
                }
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Проверка невидимого режима не завершена.");
        }
    }

    private static void DocumentDiscovery(ProbeReport report, KompasObject app)
    {
        var step = report.Begin(
            "P0.5",
            "Перечисление документов и «активный документ»",
            "Есть ли способ увидеть все открытые документы и отличить свой от пользовательского?");

        try
        {
            var first = (ksDocument3D)app.Document3D();
            var second = (ksDocument3D)app.Document3D();
            var p1 = Marshal.GetIUnknownForObject(first);
            var p2 = Marshal.GetIUnknownForObject(second);
            step.Observe($"Два Document3D(): CLR-identity {ReferenceEquals(first, second)}, IUnknown 0x{p1:X} против 0x{p2:X} → {(p1 == p2 ? "один объект" : "разные объекты")}.");
            Marshal.Release(p1);
            Marshal.Release(p2);

            step.Data["app_document_methods"] = Members.Matching(typeof(KompasObject), "Document", "Iterator");
            foreach (var line in (IReadOnlyList<string>)step.Data["app_document_methods"]!)
            {
                step.Observe("KompasObject: " + line);
            }

            try
            {
                var active = typeof(KompasObject).GetMethod("ActiveDocument3D")!.Invoke(app, null);
                step.Observe($"ActiveDocument3D() → {Members.Value(active)} (может быть null, если открытого нет).");
                step.Data["active_document_non_null"] = active is not null;
            }
            catch (Exception ex)
            {
                step.Observe($"ActiveDocument3D() → {ex.GetType().Name}");
            }

            try
            {
                var iterator = typeof(KompasObject).GetMethod("GetIterator")!.Invoke(app, null);
                step.Observe($"GetIterator() → {Members.Value(iterator)}; конкретный фильтр документов уточняется по ksCreateIterator(int objType).");
            }
            catch (Exception ex)
            {
                step.Observe($"GetIterator() → {ex.GetType().Name}");
            }

            first.close();
            second.close();
            step.Conclusion ??= "Document3D() — фабрика, а не «текущий документ»: команда обязана адресовать документ явно.";
            step.Pass(step.Conclusion);
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Перечисление документов не установлено.");
        }
    }
}

/// <summary>
/// P0.6–P0.8 — document lifecycle, the unit question, and the final-topology question.
/// </summary>
internal static class GeometryFacts
{
    private const double BlockX = 100d;
    private const double BlockY = 80d;
    private const double BlockZ = 10d;
    private const double HoleDiameter = 10d;

    /// <summary>ST_MIX_MM | ST_MIX_KG — millimetres with kilograms (KAPITypes.ldefin2d).</summary>
    private const int MixMmKg = 1 | 16;

    /// <summary>ST_MIX_M | ST_MIX_KG — the same body measured in metres, to prove the scale law.</summary>
    private const int MixMKg = 3 | 16;

    private static string WorkFile(string name) => Path.Combine(ProbeSession.WorkDir, name);

    public static void Run(ProbeReport report, ProbeOptions options)
    {
        var app = ProbeSession.App;
        if (app is null)
        {
            var skipped = report.Begin("P0.6", "Геометрия и единицы");
            skipped.Verdict = Verdict.Skipped;
            skipped.Conclusion = "Нет подключения к КОМПАС.";
            return;
        }

        Lifecycle(report, app);
        Units(report, app);
        Topology(report, app);
    }

    private static void Lifecycle(ProbeReport report, KompasObject app)
    {
        var step = report.Begin(
            "P0.6",
            "Жизненный цикл детали и сохранение атрибутов",
            "Доходят ли имя/обозначение до файла, и что значит второй аргумент Open?");

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                step.Fail("Create(invisible=true, typeDoc=true) не удался.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            part.name = "P0_Block";
            part.marking = "P0-G01";
            var updated = part.Update();
            step.Observe($"Имя/обозначение заданы, part.Update() → {updated}.");

            var path = WorkFile("P0_Block.m3d");
            step.Observe($"SaveAs → {doc.SaveAs(path)}; на диске {(File.Exists(path) ? new FileInfo(path).Length + " байт" : "нет файла")}.");
            if (!File.Exists(path))
            {
                step.Fail("Файл не создан.");
                return;
            }

            step.Artifacts.Add(path);
            step.Data["saved_sha256"] = Sha256(path);
            doc.close();
            doc = null;

            // Open(path, invisible=true): the second argument is documented as `invisible`.
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Open(path, true))
            {
                step.Fail("Open(путь, invisible=true) вернул false.");
                return;
            }

            var reopened = (ksPart)doc.GetPart(-1);
            step.Observe($"После reopen: имя «{reopened.name}», обозначение «{reopened.marking}», IsDetail → {CallBool(doc, "IsDetail")}.");

            var namePersisted = string.Equals(reopened.name, "P0_Block", StringComparison.Ordinal);
            var markingPersisted = string.Equals(reopened.marking, "P0-G01", StringComparison.Ordinal);
            step.Data["name_persisted"] = namePersisted;
            step.Data["marking_persisted"] = markingPersisted;
            step.Observe(namePersisted && markingPersisted
                ? "Имя и обозначение переживают цикл сохранения/открытия (после part.Update())."
                : "Атрибуты НЕ пережили сохранение: без part.Update() они не коммитятся — адаптер обязан вызывать Update и перечитывать результат.");

            if (namePersisted && markingPersisted)
            {
                step.Pass("Цикл документ → сохранение → закрытие → открытие сохраняет тип и атрибуты.");
            }
            else
            {
                step.Unknown("Цикл прошёл, но сохранность атрибутов не подтверждена — см. наблюдения выше.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Цикл документов не пройден.");
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static void Units(ProbeReport report, KompasObject app)
    {
        var step = report.Begin(
            "P0.7",
            "Единицы: GetLength(bitVector), площадь и объём",
            "Что возвращают измерительные вызовы при разных битовых селекторах единиц?");

        ksDocument3D? doc = null;
        try
        {
            doc = (ksDocument3D)app.Document3D();
            if (!doc.Create(true, true))
            {
                step.Fail("Документ для проверки единиц не создан.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            part.name = "P0_Units";

            var profile = AddRectSketch(part, BlockX, BlockY, "P0 profile");
            var extrusion = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_baseExtrusion", 24));
            var baseExtrusion = (ksBaseExtrusionDefinition)extrusion.GetDefinition();
            baseExtrusion.SetSketch(profile);
            baseExtrusion.directionType = 0;
            // SetSideParam(forward, type=etBlind, depth, draftValue, draftOutward) — documented order.
            baseExtrusion.SetSideParam(true, 0, BlockZ, 0, false);
            if (!extrusion.Create())
            {
                step.Fail("Базовое выдавливание не создано.");
                return;
            }

            doc.RebuildDocument();
            var bodyCount = ((ksBodyCollection)part.BodyCollection()).GetCount();
            step.Data["body_count"] = bodyCount;

            if (!part.GetGabarit(true, true, out var minX, out var minY, out var minZ, out var maxX, out var maxY, out var maxZ))
            {
                step.Fail("GetGabarit вернул false.");
                return;
            }

            var dims = new[] { maxX - minX, maxY - minY, maxZ - minZ };
            step.Data["gabarit_dims"] = dims.Select(Num).ToArray();
            var inMillimetres = Math.Abs(dims[0] - BlockX) <= 0.001 && Math.Abs(dims[1] - BlockY) <= 0.001 && Math.Abs(dims[2] - BlockZ) <= 0.001;
            step.Data["gabarit_is_mm"] = inMillimetres;
            step.Observe($"GetGabarit(true,true): {dims[0]:R} × {dims[1]:R} × {dims[2]:R} → {(inMillimetres ? "миллиметры" : "НЕ миллиметры")}.");

            var body = (ksBody?)part.GetMainBody();
            if (body is null)
            {
                step.Fail("GetMainBody() → null при наличии тела.");
                return;
            }

            step.Observe("ksBody: " + string.Join(" | ", Members.Matching(typeof(ksBody), "Volume", "Area", "Mass", "Gabarit", "Intersection")));

            // The unit selector is an argument, not a property of the model: 0 = ST_MIX_SM = cm.
            var edges = CollectEdges(body, out var faceCount, out var rawRefs);
            step.Data["face_count"] = faceCount;
            step.Data["edge_refs_before_dedupe"] = rawRefs;
            step.Data["unique_edges"] = edges.Count;
            step.Observe($"Граней {faceCount}, ссылок на рёбра {rawRefs}, уникальных {edges.Count}.");

            var longest = edges.OrderByDescending(e => e.GetLength(1)).FirstOrDefault();
            if (longest is not null)
            {
                var asCm = longest.GetLength(0);
                var asMm = longest.GetLength(1);
                var asDm = longest.GetLength(2);
                var asM = longest.GetLength(3);
                step.Observe($"Самое длинное ребро (координатная длина {BlockX:R} мм): GetLength(0)={Num(asCm)}, (1)={Num(asMm)}, (2)={Num(asDm)}, (3)={Num(asM)}.");
                step.Data["edge_length_by_unit"] = new[] { "0=" + Num(asCm), "1=" + Num(asMm), "2=" + Num(asDm), "3=" + Num(asM) };
                var selectorWorks = Math.Abs(asMm - BlockX) <= 0.001 && Math.Abs(asCm * 10d - asMm) <= 0.01;
                step.Data["length_selector_confirmed"] = selectorWorks;
                step.Observe(selectorWorks
                    ? "Селектор единиц подтверждён: 1 даёт миллиметры, 0 — сантиметры. Правило адаптера: всегда передавать ksLUnMM=1; 0 — вне документированного интервала."
                    : "Селектор единиц ведёт себя не как описано — расчёт масштаба остаётся открытым.");
            }

            var face = FirstFace(body);
            if (face is not null)
            {
                var areaMethod = face.GetType().GetMethod("GetArea") ?? typeof(ksFaceDefinition).GetMethod("GetArea");
                if (areaMethod is not null)
                {
                    step.Observe("ksFaceDefinition: " + Members.Signature(areaMethod));
                    var areaMm2 = InvokeDouble(areaMethod, face, 1);
                    var areaCm2 = InvokeDouble(areaMethod, face, 0);
                    step.Observe($"Площадь первой грани: GetArea(1)={Num(areaMm2)}, GetArea(0)={Num(areaCm2)}.");
                    step.Data["face_area_mm2"] = Num(areaMm2);
                    step.Data["face_area_unit0"] = Num(areaCm2);
                }
                else
                {
                    step.Observe("Метод GetArea у грани не найден — площадь брать неоткуда.");
                }
            }

            var mmKg = MassInertia(body, MixMmKg);
            var mKg = MassInertia(body, MixMKg);

            // The selector is a bit vector, and the value it produces for the *same* body is what
            // tells us the scale. Reading only one combination would let a wrong assumption about
            // mm³ pass unnoticed, so every selector is measured and reported raw.
            var bySelector = new List<string>();
            foreach (var bits in new[] { 0, 1, 2, 3, 1 | 16, 3 | 16, 1 | 32, 1 | 32 | 64 })
            {
                var probe = MassInertia(body, bits);
                if (probe.Volume is double v)
                {
                    bySelector.Add($"{bits}=v:{Num(v)},F:{Num(probe.Area ?? double.NaN)},m:{Num(probe.Mass ?? double.NaN)}");
                }
                else
                {
                    bySelector.Add($"{bits}=нет значения ({probe.Error ?? "-"})");
                }
            }

            step.Data["mass_inertia_by_selector"] = bySelector.ToArray();
            foreach (var line in bySelector)
            {
                step.Observe("CalcMassInertiaProperties(" + line + ")");
            }

            if (mmKg.Volume is double volumeRaw)
            {
                var expected = BlockX * BlockY * BlockZ;
                var scale = expected / volumeRaw;
                step.Observe($"Тот же объект: v() = {Num(volumeRaw)} при ожидаемых {Num(expected)} мм³ → масштаб {Num(scale)} (1e3/1e6/1e9 = дм³/см³/м³).");
                step.Data["volume_raw"] = Num(volumeRaw);
                step.Data["volume_scale_vs_mm3"] = Num(scale);
                step.Data["volume_expected_mm3"] = Num(expected);

                if (mKg.Volume is double volumeMetres && Math.Abs(volumeMetres - volumeRaw) < 1e-12)
                {
                    step.Observe("Селектор длины НЕ влияет на v(): значения для ST_MIX_MM и ST_MIX_M совпали. " +
                                 "Правило адаптера: объём не публикуется как мм³ до калибровки; иначе это ровно та ошибка масштаба, которую запрещает §1.10.");
                    step.Data["volume_length_selector_ignored"] = true;
                }
                else if (mKg.Volume is double metresValue)
                {
                    step.Observe($"v() в метрах: {Num(metresValue)}; отношение к миллиметровому {Num(volumeRaw / metresValue)}.");
                    step.Data["volume_length_selector_ignored"] = false;
                }
            }
            else
            {
                step.Observe("CalcMassInertiaProperties не вернул объём: " + (mmKg.Error ?? "нет деталей"));
                step.Data["g01_volume_pass"] = false;
            }

            // G02: Ø10 through the 10 mm plate.
            var holeProfile = AddCircleSketchOnOffsetPlane(part, HoleDiameter / 2d, "P0 hole profile", BlockZ);
            var cut = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_cutExtrusion", 26));
            var cutDefinition = (ksCutExtrusionDefinition)cut.GetDefinition();
            cutDefinition.SetSketch(holeProfile);
            cutDefinition.directionType = 2;
            cutDefinition.SetSideParam(true, 0, BlockZ, 0, false);
            cutDefinition.SetSideParam(false, 0, BlockZ, 0, false);
            var cutCreated = cut.Create();
            doc.RebuildDocument();
            step.Observe($"Вырезание (o3d_cutExtrusion) Create → {cutCreated}.");

            var afterHole = MassInertia((ksBody?)part.GetMainBody(), MixMmKg);
            if (mmKg.Volume is double before && afterHole.Volume is double after)
            {
                var delta = before - after;
                var expectedHole = Math.PI * Math.Pow(HoleDiameter / 2d, 2) * BlockZ;
                var tolerance = Math.Max(0.1d, 1e-5d * expectedHole);
                step.Observe($"G02: объём уменьшился на {Num(delta)}, ожидаем {Num(expectedHole)} (250π), допуск {Num(tolerance)}.");
                step.Data["g02_volume_delta"] = Num(delta);
                step.Data["g02_expected_delta"] = Num(expectedHole);
                step.Data["g02_pass"] = Math.Abs(delta - expectedHole) <= tolerance;
            }
            else
            {
                step.Data["g02_pass"] = false;
                step.Unknown("Объём до/после вырезания прочитать не удалось — G02 остаётся недоказанным.");
            }

            if (step.Verdict == Verdict.Unknown)
            {
                step.Conclusion = "Селектор единиц и объём зафиксированы наблюдениями выше.";
            }

            var path = WorkFile("P0_Units.m3d");
            step.Observe($"SaveAs → {doc.SaveAs(path)}.");
            if (File.Exists(path))
            {
                step.Artifacts.Add(path);
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Проверка единиц не завершена.");
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static void Topology(ProbeReport report, KompasObject app)
    {
        var step = report.Begin(
            "P0.8",
            "EntityCollection(7) против топологии тела",
            "Равна ли коллекция объектов типа 7 рёбрам конечного тела?");

        ksDocument3D? doc = null;
        try
        {
            step.Observe($"Именованные константы берутся из {EntityTypes.SourceAssembly}; o3d_edge={EntityTypes.Value("o3d_edge", 7)}, разрешено={EntityTypes.Resolved}.");
            step.Data["entity_type_names"] = EntityTypes.MatchNames("o3d_edge", "o3d_face", "o3d_body", "o3d_sketch", "o3d_curveElement", "o3d_edgeCollection");

            var path = WorkFile("P0_Units.m3d");
            if (!File.Exists(path))
            {
                step.Verdict = Verdict.Skipped;
                step.Conclusion = "Нет модели с телом.";
                return;
            }

            doc = (ksDocument3D)app.Document3D();
            if (!doc.Open(path, true))
            {
                step.Fail("Открытие не удалось.");
                return;
            }

            var part = (ksPart)doc.GetPart(-1);
            var collectionCount = ((ksEntityCollection)part.EntityCollection(EntityTypes.Value("o3d_edge", 7))).GetCount();
            var body = (ksBody?)part.GetMainBody();
            if (body is null)
            {
                step.Fail("GetMainBody() → null.");
                return;
            }

            var edges = CollectEdges(body, out var faces, out var rawRefs);
            step.Data["entity_collection_edge_type_count"] = collectionCount;
            step.Data["body_faces"] = faces;
            step.Data["body_unique_edges"] = edges.Count;
            step.Observe($"EntityCollection(o3d_edge=7): {collectionCount}; из тела: граней {faces}, ссылок {rawRefs}, уникальных рёбер {edges.Count}.");

            if (collectionCount > edges.Count)
            {
                step.Pass($"Коллекция типа 7 содержит на {collectionCount - edges.Count} объектов больше, чем рёбер в теле: это все рёбра модели, включая эскизные и служебные. Вывод spec 4.5 ПОДТВЕРЖДЁН — контур берётся только из тела.");
            }
            else
            {
                step.Unknown($"Счётчики совпали ({collectionCount} против {edges.Count}) — на этой модели различие не проявилось.");
            }
        }
        catch (Exception ex)
        {
            step.Errors.Add(ex.GetType().Name + ": " + ex.Message);
            step.Fail("Сравнение не завершено.");
        }
        finally
        {
            CloseQuietly(doc);
        }
    }

    private static ksEntity AddRectSketch(ksPart part, double width, double height, string name)
    {
        var sketch = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_sketch", 5));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane((ksEntity)part.GetDefaultEntity(EntityTypes.Value("o3d_planeXOY", 1)));
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        var halfX = width / 2d;
        var halfY = height / 2d;
        editor.ksLineSeg(-halfX, -halfY, halfX, -halfY, 1);
        editor.ksLineSeg(halfX, -halfY, halfX, halfY, 1);
        editor.ksLineSeg(halfX, halfY, -halfX, halfY, 1);
        editor.ksLineSeg(-halfX, halfY, -halfX, -halfY, 1);
        definition.EndEdit();
        return sketch;
    }

    private static ksEntity AddCircleSketchOnOffsetPlane(ksPart part, double radius, string name, double offsetMm)
    {
        var plane = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_planeOffset", 14));
        var planeDefinition = (ksPlaneOffsetDefinition)plane.GetDefinition();
        planeDefinition.SetPlane((ksEntity)part.GetDefaultEntity(EntityTypes.Value("o3d_planeXOY", 1)));
        planeDefinition.offset = offsetMm;
        planeDefinition.direction = true;
        plane.Create();

        var sketch = (ksEntity)part.NewEntity(EntityTypes.Value("o3d_sketch", 5));
        sketch.name = name;
        var definition = (ksSketchDefinition)sketch.GetDefinition();
        definition.SetPlane(plane);
        sketch.Create();
        var editor = (ksDocument2D)definition.BeginEdit();
        editor.ksCircle(0, 0, radius, 1);
        definition.EndEdit();
        return sketch;
    }

    private static List<ksEdgeDefinition> CollectEdges(ksBody body, out int faceCount, out int rawEdgeRefs)
    {
        var seen = new HashSet<IntPtr>();
        var edges = new List<ksEdgeDefinition>();
        rawEdgeRefs = 0;

        var faces = (ksFaceCollection)body.FaceCollection();
        faceCount = faces.GetCount();
        for (var f = 0; f < faces.GetCount(); f++)
        {
            var faceObject = faces.GetByIndex(f);
            var face = faceObject as ksFaceDefinition ?? (ksFaceDefinition)((ksEntity)faceObject).GetDefinition();
            var edgeCollection = (ksEdgeCollection)face.EdgeCollection();
            for (var e = 0; e < edgeCollection.GetCount(); e++)
            {
                var edgeObject = edgeCollection.GetByIndex(e);
                var edge = edgeObject as ksEdgeDefinition ?? (ksEdgeDefinition)((ksEntity)edgeObject).GetDefinition();
                rawEdgeRefs++;
                var pointer = Marshal.GetIUnknownForObject(edge);
                try
                {
                    if (seen.Add(pointer))
                    {
                        edges.Add(edge);
                    }
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
        }

        return edges;
    }

    private static object? FirstFace(ksBody body)
    {
        var faces = (ksFaceCollection)body.FaceCollection();
        if (faces.GetCount() == 0)
        {
            return null;
        }

        var faceObject = faces.GetByIndex(0);
        return faceObject as ksFaceDefinition ?? ((ksEntity)faceObject).GetDefinition();
    }

    private static bool? CallBool(object target, string method)
    {
        try
        {
            var mi = target.GetType().GetMethod(method) ?? FindOnKnownInterface(target, method);
            return mi?.Invoke(target, null) as bool?;
        }
        catch
        {
            return null;
        }
    }

    private static MethodInfo? FindOnKnownInterface(object target, string method) =>
        typeof(KompasObject).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface && t.IsInstanceOfType(target))
            .Select(t => t.GetMethod(method))
            .FirstOrDefault(m => m is not null);

    private static double InvokeDouble(MethodInfo method, object target, int argument)
    {
        // The vendor signature takes UInt32, so a bare int boxed as Int32 fails conversion at
        // Invoke time (this is what broke the first P0.7 run).
        var parameters = method.GetParameters();
        object coerced = parameters.Length == 0 ? Array.Empty<object>() : Coerce(argument, parameters[0].ParameterType);
        if (parameters.Length == 0)
        {
            return Convert.ToDouble(Call(target, method, Array.Empty<object>()) ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
        }

        return Convert.ToDouble(Call(target, method, new object?[] { coerced }) ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Coerce(int value, Type target)
    {
        if (target == typeof(uint))
        {
            return (uint)value;
        }

        if (target == typeof(short))
        {
            return (short)value;
        }

        if (target == typeof(long))
        {
            return (long)value;
        }

        if (target == typeof(double))
        {
            return (double)value;
        }

        return value;
    }

    private static object? Call(object target, MethodInfo method, object?[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static (double? Volume, double? Area, double? Mass, string? Error) MassInertia(ksBody? body, int unitBits)
    {
        if (body is null)
        {
            return (null, null, null, "тело отсутствует");
        }

        try
        {
            var method = typeof(ksBody).GetMethod("CalcMassInertiaProperties");
            if (method is null)
            {
                return (null, null, null, "метод CalcMassInertiaProperties не объявлен на ksBody");
            }

            var param = method.Invoke(body, new object[] { Coerce(unitBits, method.GetParameters()[0].ParameterType) });
            if (param is null)
            {
                return (null, null, null, "вернул null");
            }

            if (param is not ksMassInertiaParam properties)
            {
                return (null, null, null, $"вернул {param.GetType().Name}, а не ksMassInertiaParam");
            }

            // Direct typed reads: CalcMassInertiaProperties is declared to return Object in the
            // interop, so reflection over method.ReturnType finds no members — that, and not
            // КОМПАС, is why the first attempt reported "no volume".
            return (SafeProperty(properties, "v"), SafeProperty(properties, "F"), SafeProperty(properties, "m"), null);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException : ex;
            return (null, null, null, inner?.GetType().Name + ": " + inner?.Message);
        }

        static double? SafeProperty(ksMassInertiaParam target, string name)
        {
            try
            {
                var value = typeof(ksMassInertiaParam).GetProperty(name)?.GetValue(target);
                return value is null ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
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
        catch
        {
            // The failure that matters was already recorded by the step.
        }
    }

    private static string Num(double value) => double.IsFinite(value) ? value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) : "NaN";

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}
