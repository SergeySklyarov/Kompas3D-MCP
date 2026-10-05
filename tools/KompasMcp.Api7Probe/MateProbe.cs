using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>
/// Проба M — сопряжения сборки: адресуются ли ГРАНИ КОМПОНЕНТОВ и создаётся ли сопряжение.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем эта проба.</b> Блок C1 (минимальные сборки) закрыт, и следующий блок — сопряжения.
/// Маршрут сопряжения ДОКУМЕНТИРОВАН и прочитан по проводу 05.10.2026:
/// <c>IPart7.MateConstraints</c> → <c>IMateConstraints3D.Add(MateConstraintType)</c> →
/// <c>IMateConstraint3D</c> (<c>BaseObject1</c>, <c>BaseObject2</c>, <c>Alignment</c>,
/// <c>ParamValue</c>, <c>Fixed</c>, <c>Update()</c>). Не измерено другое: <c>BaseObject1/2</c>
/// имеют тип <c>IModelObject</c> и обязаны указывать на грани КОМПОНЕНТОВ, то есть адрес должен
/// вести в документ компонента, а не в саму сборку. Если грань компонента оттуда не достаётся,
/// блок придётся строить иначе — и узнать это надо ДО проектирования режимов.
/// </para>
/// <para>
/// <b>Только документированные вызовы.</b> Маршрут чтения грани — тот, который проект уже
/// применяет и который описан в справке: <c>ksPart.GetMainBody() → ksBody.FaceCollection()</c>,
/// а НЕ <c>EntityCollection(o3d_face)</c>. Передача в API7 — документированный
/// <c>TransferInterface(…, ksAPI7Dual, …)</c>. Составные части вставляются маршрутом, ИЗМЕРЕННЫМ
/// в блоке C1: <c>CreatePartInAssembly(file, plane)</c> для первого экземпляра и
/// <c>CopyPart(source, placement)</c> для повторного (повторная <c>CreatePartInAssembly</c> того же
/// файла возвращает null — измерено 04.10.2026).
/// </para>
/// <para>
/// <b>Что здесь доказательство.</b> Числа: число сопряжений до и после, <c>Valid</c> созданного
/// сопряжения, непустые <c>BaseObject1/2</c>, и — решающее — сместился ли компонент после
/// сопряжения. Сопряжение «создано» без смещения не отличает принятую связь от пустой записи.
/// </para>
/// </remarks>
internal sealed class MateProbe
{
    private const double PlateWidth = 100d;
    private const double PlateHeight = 80d;
    private const double PlateThickness = 10d;
    private const double PlateVolume = PlateWidth * PlateHeight * PlateThickness;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly List<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private IApplication? _app7;
    private ksPart? _sourcePart;
    private int _ownPid;

    public MateProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    /// <summary>Пишет отчёт изнутри прогона — для диагностического пути <c>--keep</c>.</summary>
    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "mate.json"),
            Path.Combine(options.ReportDir, "mate.md"));
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
            // РАЗЛИЧАЮЩИЙ КОНТРОЛЬ идёт ПЕРВЫМ и на своём документе: он не зависит от того,
            // получится ли программная сборка, и обязан выполниться даже если всё дальнейшее
            // остановится. Иначе контроль «не достигнут» — и находка остаётся без опоры.
            SampleAssemblyControl();

            var source = BuildSourcePart();
            if (source is null)
            {
                return;
            }

            var assembly = BuildAssemblyWithTwoComponents(source.Value.Path);
            if (assembly is null)
            {
                return;
            }

            // ПЕРЕОТКРЫТИЕ — документированная проверка: если тела появляются только после загрузки
            // документа с диска, то причина «тел нет» названа, и это условие входит в маршрут.
            var reopened = ReopenAssembly(assembly.Value.Path);
            if (reopened is null)
            {
                return;
            }

            ReadComponents(reopened.Value.Document, reopened.Value.Part);

            var objects = ReadComponentFaces(reopened.Value.Document);
            if (objects is null)
            {
                return;
            }

            CreateMate(reopened.Value.Document, objects[0], objects[1]);
            Negative_SameObjectTwice(reopened.Value.Document, objects[0]);
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("M.X", "Необработанное исключение пробы M");
            crash.Errors.Add(ex.ToString());
            crash.Fail(ex.Message);
        }
        finally
        {
            Shutdown();
        }
    }

    // ═══════════════════════════════════════════════════════════════ сеанс ══

    private void Launch()
    {
        var step = _report.Begin("M.0", "Свой невидимый экземпляр КОМПАС-3D v24",
            "Сеанс поднимается и завершается сам, без чужих процессов?");
        _current = step;
        _clock.Restart();

        foreach (var process in Process.GetProcessesByName("KOMPAS"))
        {
            _pidsBefore.Add(process.Id);
            process.Dispose();
        }

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

        step.Observe("процессов KOMPAS.exe было до запуска: " + _pidsBefore.Count
            + (_ownPid == 0 ? "; новый процесс сразу не виден" : "; свой процесс " + _ownPid));

        try
        {
            _app7 = _app.GetType().InvokeMember(
                "ksGetApplication7", System.Reflection.BindingFlags.InvokeMethod, null, _app, null) as IApplication;
            step.Observe("ksGetApplication7 → " + (_app7 is null ? "null" : "IApplication"));
        }
        catch (Exception ex)
        {
            step.Observe("ksGetApplication7 бросил: " + ex.Message);
        }

        step.Pass("сеанс поднят за " + _clock.ElapsedMilliseconds + " мс");
    }

    private void Shutdown()
    {
        var step = _report.Begin("M.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
        try
        {
            _app.Quit();
        }
        catch (Exception ex)
        {
            step.Observe("Quit() бросил: " + ex.GetType().Name);
        }

        System.Threading.Thread.Sleep(1500);
        var left = Process.GetProcessesByName("KOMPAS")
            .Select(p =>
            {
                var pid = p.Id;
                p.Dispose();
                return pid;
            })
            .Where(pid => !_pidsBefore.Contains(pid))
            .ToList();
        step.Data["new_processes_left"] = left;
        if (left.Count == 0)
        {
            step.Pass("новых процессов KOMPAS.exe не осталось");
        }
        else
        {
            step.Fail("остались процессы: " + string.Join(", ", left));
        }
    }

    // ═══════════════════════════════════════════════════════ деталь-источник ══

    private (string Path, ksPart Part)? BuildSourcePart()
    {
        var step = _report.Begin("M.1", "Деталь-источник: плита 100×80×10 на диске",
            "Есть ли файл, из которого вставлять компонент?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mate");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mate-source.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, true))
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        _sourcePart = part;
        part.name = "Mate-src";
        part.Update();

        if (Api5.BasePlate(part, PlateWidth, PlateHeight, PlateThickness, step, "mate-src") is null)
        {
            step.Fail("плита не построена.");
            return null;
        }

        var volume = Api5.Volume(part);
        step.Data["volume_mm3"] = volume;
        step.Data["analytic_mm3"] = PlateVolume;
        step.Observe("объём источника: " + Api5.Num(volume) + " (аналитика " + Api5.Num(PlateVolume) + ")");

        // КОНТРОЛЬ ПРИБОРА: работает ли сам маршрут ksPart.BodyCollection() там, где тело заведомо
        // есть. Без этого контроля «у компонента тел нет» неотличимо от «прибор зовёт не то».
        var sourceBodies = Api5.SafeInt(() => (part.BodyCollection() as ksBodyCollection)!.GetCount());
        step.Data["source_bodies"] = sourceBodies;
        step.Observe("контроль: BodyCollection().GetCount() у ДЕТАЛИ-источника = " + sourceBodies);
        if (sourceBodies is > 0 && part.BodyCollection() is ksBodyCollection bodiesOfSource
            && bodiesOfSource.GetByIndex(0) is ksBody sourceBody)
        {
            var sourceFaces = Api5.SafeInt(() => (sourceBody.FaceCollection() as ksFaceCollection)!.GetCount());
            step.Data["source_faces"] = sourceFaces;
            step.Observe("контроль: FaceCollection().GetCount() у детали-источника = " + sourceFaces);
        }

        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true.");
            return null;
        }

        // Документ ЗАКРЫВАЕТСЯ: измерено 05.10.2026 — при открытом источнике CreatePartInAssembly
        // возвращает null. Грань компонента берётся не из этого документа, а поиском по точке
        // (IPart7.FindObjectsByPoint) уже в сборке.
        document.close();
        step.Observe("файл: " + path + "; документ-источник закрыт");
        step.Pass("источник сохранён");
        return (path, part);
    }

    // ═════════════════════════════════════════════════════════════ сборка ══

    private (string Path, ksPart Part, ksPart SourcePart)? BuildAssemblyWithTwoComponents(
        string sourcePath)
    {
        var step = _report.Begin("M.2", "Сборка с ДВУМЯ компонентами одной детали",
            "Сборка, в которой есть что сопрягать?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mate");
        var path = Path.Combine(directory, "mate-assembly.m3d");

        var document = (ksDocument3D)_app.Document3D();
        // ВИДИМЫЙ документ сборки: Create(invisible=false, isDetail=false). Первая редакция создавала
        // сборку невидимой, и у компонентов не было тел ни одним документированным путём; проверяется,
        // материализует ли геометрию именно видимость документа.
        if (!document.Create(false, false))
        {
            step.Fail("Create(видимый, сборка) вернул false.");
            return null;
        }

        step.Observe("документ сборки создан ВИДИМЫМ (Create(false, false))");

        var part = (ksPart)document.GetPart(-1);
        part.name = "Mate-asm";
        part.Update();

        // ВСТАВКА КОМПОНЕНТА — документированный SetPartFromFile, а НЕ CreatePartInAssembly.
        //
        // ИЗМЕРЕНО 05.10.2026 и подтверждено справкой ДОСЛОВНО:
        //  * ksdDocument3d_createpartinassembly.html: «fileName — имя файла детали СОЗДАВАЕМОЙ в
        //    сборке», «plane — плоскость, к которой ПРИКЛЕИВАЕТСЯ деталь» — это СОЗДАНИЕ новой
        //    (пустой) детали в сборке, а не вставка существующей. Отсюда 0 тел у компонента.
        //  * ksdDocument3d_setpartfromfile.html: «fileName — имя файла, из которого будет ВСТАВЛЕН
        //    компонент», «externalFile — TRUE — вставка СО ССЫЛКОЙ на внешний файл» — это вставка.
        //
        // Оба экземпляра вставляются ЭТИМ методом: он документирован, и повторная вставка того же
        // файла даёт второй экземпляр (в C1 это ошибочно считалось невозможным).
        // С null метод вернул FALSE (измерено) — значит `part` не выходной параметр, а входной.
        // Поэтому сначала создаётся «деталь в сборке» (единственный вызов, который у нас работает),
        // и уже НА НЕЙ проверяется документированный SetPartFromFile с реальным компонентом.
        if (part.GetDefaultEntity(Api5.PlaneXoy) is not { } gluePlane)
        {
            step.Fail("GetDefaultEntity(o3d_planeXOY=1) не вернул плоскость.");
            return null;
        }

        object? seeded;
        try
        {
            seeded = document.CreatePartInAssembly(sourcePath, gluePlane);
        }
        catch (Exception ex)
        {
            step.Fail("CreatePartInAssembly бросил: " + ex.Message);
            return null;
        }

        if (seeded is not ksPart seededPart)
        {
            step.Fail("CreatePartInAssembly не вернул ksPart — вставлять SetPartFromFile не на чем.");
            return null;
        }

        step.Observe("затравка: CreatePartInAssembly создал компонент; "
            + "тел у него = " + Api5.SafeInt(() => (seededPart.BodyCollection() as ksBodyCollection)!.GetCount()));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            bool? inserted = null;
            try
            {
                inserted = document.SetPartFromFile(sourcePath, seededPart, externalFile: true);
            }
            catch (Exception ex)
            {
                step.Observe("SetPartFromFile бросил на попытке " + attempt + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            var bodies = Api5.SafeInt(() => (seededPart.BodyCollection() as ksBodyCollection)!.GetCount());
            var components = Api5.SafeInt(() => (document.PartCollection(true) as ksPartCollection)!.GetCount());
            step.Observe("SetPartFromFile(источник, существующий компонент, externalFile=true) попытка "
                + attempt + " → " + inserted + ", компонентов " + components
                + ", тел у переданного компонента " + bodies);
        }

        document.RebuildDocument();

        // ЧТО ДАЛ ДОКУМЕНТИРОВАННЫЙ SetPartFromFile: тела у КАЖДОГО компонента, поимённо.
        if (document.PartCollection(true) is ksPartCollection afterInsert)
        {
            for (var index = 0; index < afterInsert.GetCount(); index++)
            {
                if (afterInsert.GetByIndex(index) is not ksPart component)
                {
                    continue;
                }

                var bodies = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                step.Observe("компонент " + index + " '" + (Api5.SafeObject(() => component.name) ?? "—")
                    + "': тел = " + bodies);
                step.Data["component_bodies_" + index] = bodies;
            }
        }

        var count = CountComponents(document);
        step.Data["components"] = count;
        if (count != 2)
        {
            step.Fail("компонентов " + count + ", ожидалось 2.");
            return null;
        }

        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true — переоткрытие мерить не на чем.");
            return null;
        }

        document.close();
        step.Observe("сборка сохранена и ЗАКРЫТА: " + path);
        step.Pass("сборка с двумя компонентами сохранена");
        return (path, part, _sourcePart!);
    }

    /// <summary>Открывает сохранённую сборку заново — документированным <c>ksDocument3D.Open</c>.</summary>
    private (ksDocument3D Document, ksPart Part)? ReopenAssembly(string path)
    {
        var step = _report.Begin("M.2b", "Переоткрытие сборки с диска",
            "Появляются ли тела компонентов после загрузки документа?");
        _current = step;

        var document = (ksDocument3D)_app.Document3D();
        var opened = Api5.SafeBool(() => document.Open(path));
        step.Observe("Open(" + path + ") = " + opened);
        if (opened != true)
        {
            step.Fail("сборка не открылась");
            return null;
        }

        // ДЕФЕКТ ПРОБЫ, ПОЙМАННЫЙ ЗДЕСЬ: объект, на котором вызван Open, документом НЕ становится —
        // его PartCollection пуст. Документ берётся заново у приложения, как это делает и образец
        // M.7 (там документ получен от приложения, и компоненты видны).
        // KompasObject не объявляет ActiveDocument статически — свойство берётся поздним связыванием,
        // как это уже делает проба для ksGetApplication7.
        var active = Api5.SafeObject(() => _app.GetType().InvokeMember(
            "ActiveDocument", System.Reflection.BindingFlags.GetProperty, null, _app, null)) as ksDocument3D;
        step.Observe("_app.ActiveDocument после Open: " + (active is null ? "null" : "получен"));
        if (active is not null)
        {
            document = active;
        }

        var part = (ksPart)document.GetPart(-1);
        var components = Api5.SafeInt(() => (document.PartCollection(true) as ksPartCollection)!.GetCount());
        step.Data["components_after_reopen"] = components;
        step.Observe("компонентов после переоткрытия: " + components);
        step.Pass("сборка переоткрыта");
        return (document, part);
    }

    private static int CountComponents(ksDocument3D document)
    {
        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                return -1;
            }

            return collection.GetCount();
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void ReadComponents(ksDocument3D document, ksPart assemblyPart)
    {
        var step = _report.Begin("M.3", "Компоненты сборки читаются по порядку",
            "Достаётся ли ksPart каждого компонента?");
        _current = step;

        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                step.Fail("PartCollection(true) не вернул ksPartCollection.");
                return;
            }

            var names = new List<string>();
            for (var index = 0; index < collection.GetCount(); index++)
            {
                if (collection.GetByIndex(index) is ksPart component)
                {
                    names.Add(index + ":" + (Api5.SafeObject(() => component.name) ?? "—"));
                }
                else
                {
                    names.Add(index + ":не ksPart");
                }
            }

            step.Data["components"] = names;
            step.Observe("компоненты: " + string.Join(", ", names));
            step.Pass("перечислены " + names.Count + " компонент(ов)");
        }
        catch (Exception ex)
        {
            step.Fail("перечисление бросило: " + ex.Message);
        }
    }

    // ═════════════════════════════════════════════ РЕШАЮЩИЙ ОПЫТ: грани компонентов ══

    /// <summary>
    /// Грани компонентов как <c>ksEntity</c> — ДОКУМЕНТИРОВАННЫМ маршрутом
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Почему не <c>GetMainBody()</c>.</b> Справка <c>kspart_getmainbody.html</c> говорит:
    /// «Пример: Деталь имеет массив интерфейсов тел <c>IBody</c>» — то есть у детали тел МОЖЕТ быть
    /// несколько, и одиночное «главное тело» у компонента сборки не читается (измерено: null).
    /// Документированный маршрут к телам — <c>kspart_bodycollection.html</c>:
    /// <c>ksPart.BodyCollection()</c> возвращает <c>ksBodyCollection</c>.
    /// </para>
    /// <para>
    /// <b>Только документированные вызовы.</b> <c>ksEntity</c> — ровно тот тип, который принимает
    /// <c>ksDocument3D::AddMateConstraint</c> («object1 — указатель на интерфейс первого объекта,
    /// на который накладывается сопряжение (<c>ksEntity</c> или <c>IEntity</c>)»).
    /// </para>
    /// </remarks>
    private List<object>? ReadComponentFaces(ksDocument3D document)
    {
        var step = _report.Begin("M.4", "Грань компонента как ksEntity — BodyCollection → FaceCollection",
            "Достаётся ли грань компонента документированным ksPart.BodyCollection()?");
        _current = step;

        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                step.Fail("PartCollection(true) не вернул ksPartCollection.");
                return null;
            }

            var faces = new List<object>();
            for (var index = 0; index < collection.GetCount() && faces.Count < 2; index++)
            {
                if (collection.GetByIndex(index) is not ksPart component)
                {
                    step.Observe("компонент " + index + ": не ksPart");
                    continue;
                }

                var bodyCount = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                step.Observe("компонент " + index + ": BodyCollection().GetCount() до перестроения = " + bodyCount);

                // Документированный ipart7_rebuildmodel.html: «Redraw — TRUE перестроить документ».
                // Проверяется, материализует ли перестроение геометрию компонента.
                if (bodyCount == 0 && TransferTo7(component) is IPart7 componentRebuild)
                {
                    bool? rebuilt = null;
                    try
                    {
                        rebuilt = componentRebuild.RebuildModel(true);
                    }
                    catch (Exception ex)
                    {
                        step.Observe("компонент " + index + ": RebuildModel бросил " + ex.GetType().Name);
                    }

                    document.RebuildDocument();
                    bodyCount = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                    step.Observe("компонент " + index + ": RebuildModel(true)=" + rebuilt
                        + ", BodyCollection().GetCount() после = " + bodyCount);
                }

                // Документированный ipart7_opensourcedocument.html: открыть документ-ИСТОЧНИК
                // компонента. Проверяется, материализует ли это геометрию: если да, то причина
                // «тел нет» — не загруженный источник, а не отсутствие маршрута.
                if (bodyCount == 0 && TransferTo7(component) is IPart7 componentSource)
                {
                    try
                    {
                        var parameters = componentSource.GetOpenDocumentParam();
                        var sourceDoc = parameters is null ? null : componentSource.OpenSourceDocument(parameters);
                        document.RebuildDocument();
                        bodyCount = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                        step.Observe("компонент " + index + ": OpenSourceDocument → "
                            + (sourceDoc is null ? "null" : "документ открыт")
                            + ", BodyCollection().GetCount() после = " + bodyCount);
                    }
                    catch (Exception ex)
                    {
                        step.Observe("компонент " + index + ": OpenSourceDocument бросил "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                // ДОКУМЕНТИРОВАННЫЙ ipart7_islocal.html: «IsLocal — получить И УСТАНОВИТЬ свойство»
                // (put_IsLocal). Локальный компонент хранит геометрию В СБОРКЕ, а не по ссылке —
                // если тела появляются отсюда, то причина «тел нет» названа: компонент ссылочный.
                if (bodyCount == 0 && TransferTo7(component) is IPart7 componentLocal)
                {
                    var wasLocal = Api5.SafeBool(() => componentLocal.IsLocal);
                    var hasLocalResult = Api5.SafeBool(() => componentLocal.IsLocalResultExist(false));
                    step.Observe("компонент " + index + ": IsLocal до = " + wasLocal
                        + ", IsLocalResultExist(false) = " + hasLocalResult);

                    bool? madeLocal = null;
                    try
                    {
                        componentLocal.IsLocal = true;
                        madeLocal = componentLocal.IsLocal;
                    }
                    catch (Exception ex)
                    {
                        step.Observe("компонент " + index + ": IsLocal=true бросил "
                            + ex.GetType().Name + ": " + ex.Message);
                    }

                    document.RebuildDocument();
                    bodyCount = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                    step.Observe("компонент " + index + ": IsLocal после = " + madeLocal
                        + ", BodyCollection().GetCount() = " + bodyCount);
                }

                if (component.BodyCollection() is not ksBodyCollection bodies || bodies.GetCount() == 0)
                {
                    step.Observe("компонент " + index + ": тел нет");
                    continue;
                }

                var body = bodies.GetByIndex(0) as ksBody;
                if (body?.FaceCollection() is not ksFaceCollection bodyFaces || bodyFaces.GetCount() == 0)
                {
                    step.Observe("компонент " + index + ": FaceCollection() пуста");
                    continue;
                }

                var face = bodyFaces.GetByIndex(0);
                step.Observe("компонент " + index + ": граней " + bodyFaces.GetCount()
                    + ", грань[0]=" + Api5.RuntimeName(face));
                if (face is not null)
                {
                    faces.Add(face);
                }
            }

            step.Data["faces"] = faces.Count;
            if (faces.Count < 2)
            {
                step.Fail("граней собрано: " + faces.Count + ", нужно 2 (по одной с каждого компонента).");
                return null;
            }

            step.Pass("собрано граней: " + faces.Count + " — по одной с каждого компонента");
            return faces;
        }
        catch (Exception ex)
        {
            step.Fail("чтение граней бросило: " + ex.Message);
            return null;
        }
    }

    /// <summary>Компонент по порядковому номеру как <c>IPart7</c> (адрес измерен в блоке C1).</summary>
    private IPart7? ComponentByIndex(ksDocument3D document, int index)
    {
        try
        {
            return document.PartCollection(true) is ksPartCollection collection
                && collection.GetByIndex(index) is ksPart part
                ? TransferTo7(part) as IPart7
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Приводит результат <c>FindObjectsByPoint</c> к списку <c>IModelObject</c>.</summary>
    /// <remarks>
    /// Документация не фиксирует форму ответа: это может быть один объект, SAFEARRAY или массив
    /// <c>object[]</c>. Разбираются все три случая, а неожиданная форма НАЗЫВАЕТСЯ, а не
    /// проглатывается.
    /// </remarks>
    private List<IModelObject> AsModelObjects(object? raw)
    {
        var result = new List<IModelObject>();
        switch (raw)
        {
            case null:
                return result;
            case IModelObject single:
                result.Add(single);
                return result;
            case System.Array array:
                foreach (var item in array)
                {
                    if (item is IModelObject model)
                    {
                        result.Add(model);
                    }
                }

                return result;
            default:
                _report.Note(_current?.Id ?? "M", "FindObjectsByPoint вернул неожиданную форму: "
                    + raw.GetType().FullName);
                return result;
        }
    }

    /// <summary>
    /// Запасной документированный путь: открыть документ-ИСТОЧНИК компонента
    /// (<c>IPart7.OpenSourceDocument</c>) и прочитать тело там.
    /// </summary>
    /// <remarks>
    /// Оставлен диагностическим: если <c>Load(true)</c> уже дал тело, сюда не заходим. Если не дал —
    /// этот шаг НАЗЫВАЕТ, что именно вернул <c>OpenSourceDocument</c>, вместо молчаливого «не
    /// получилось»: следующая правка должна опираться на наблюдение, а не на догадку.
    /// </remarks>
    private ksBody? BodyFromSourceDocument(ksPart component, int index, ProbeStep step)
    {
        try
        {
            if (TransferTo7(component) is not IPart7 component7)
            {
                step.Observe("компонент " + index + ": документ-источник не открывается — нет IPart7");
                return null;
            }

            var parameters = component7.GetOpenDocumentParam();
            if (parameters is null)
            {
                step.Observe("компонент " + index + ": GetOpenDocumentParam() вернул null");
                return null;
            }

            var source = component7.OpenSourceDocument(parameters);
            step.Observe("компонент " + index + ": OpenSourceDocument → "
                + (source is null ? "null" : source.GetType().FullName));
            return null;
        }
        catch (Exception ex)
        {
            step.Observe("компонент " + index + ": OpenSourceDocument бросил "
                + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════ РЕШАЮЩИЙ ОПЫТ: сопряжение ══

    /// <summary>
    /// Постоянное сопряжение — ДОКУМЕНТИРОВАННЫМ методом <c>ksDocument3D.AddMateConstraint</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Справка <c>ksdocument3d_getmateconstraint.html</c> и <c>ksmateconstraint_create.html</c> говорят
    /// прямо: «Сопряжения бывают постоянными и временными… <b>Постоянные сопряжения создаются с
    /// помощью метода <c>ksDocument3D::AddMateConstraint</c></b>», а <c>Create()</c> служит только
    /// для ВРЕМЕННОГО сопряжения внутри процесса <c>UserGetPlacementAndEntity</c>. Поэтому блок
    /// строится на <c>AddMateConstraint</c>, а не на API7-коллекции.
    /// </para>
    /// <para>
    /// Подпись: <c>BOOL AddMateConstraint(long constraintType, LPDISPATCH object1, LPDISPATCH object2,
    /// short direction, short fixed, double value)</c>; <c>direction</c>: 1 — однонаправленные,
    /// 0 — направление не учитывается, −1 — разнонаправленные.
    /// </para>
    /// </remarks>
    private void CreateMate(ksDocument3D document, object first, object second)
    {
        var step = _report.Begin("M.5", "Сопряжение создаётся ksDocument3D.AddMateConstraint",
            "Документированный метод ПОСТОЯННОГО сопряжения даёт сопряжение в сборке?");
        _current = step;

        try
        {
            var before = MateCount(document);
            step.Data["count_before"] = before;
            step.Observe("сопряжений до: " + before);

            bool? created = null;
            try
            {
                created = document.AddMateConstraint(
                    // Аргументы ПОЗИЦИОННЫЕ: direction, fixed, value. Имя `fixed` в C# — ключевое
                    // слово, поэтому именованный аргумент здесь не компилируется.
                    (int)MateConstraintType.mc_Coincidence, first, second, 0, 0, 0d);
            }
            catch (Exception ex)
            {
                step.Fail("AddMateConstraint бросил " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            document.RebuildDocument();

            var after = MateCount(document);
            step.Data["created"] = created;
            step.Data["count_after"] = after;
            step.Observe("AddMateConstraint(mc_Coincidence, direction=0, fixed=0, value=0) → " + created
                + ", сопряжений после: " + after);

            // Чтение обратно — тем же документированным маршрутом: MateConstraintCollection →
            // GetCount/GetByIndex → GetBaseObj(1|2). Без чтения «создано» неотличимо от «принято молча».
            if (document.MateConstraintCollection() is ksMateConstraintCollection mates && mates.GetCount() > 0)
            {
                if (mates.GetByIndex(mates.GetCount() - 1) is ksMateConstraint mate)
                {
                    var base1 = Api5.SafeObject(() => mate.GetBaseObj(1));
                    var base2 = Api5.SafeObject(() => mate.GetBaseObj(2));
                    step.Data["base1"] = base1 is not null;
                    step.Data["base2"] = base2 is not null;
                    step.Observe("последнее сопряжение: GetBaseObj(1)=" + Api5.RuntimeName(base1)
                        + ", GetBaseObj(2)=" + Api5.RuntimeName(base2)
                        + ", constraintType=" + Api5.SafeInt(() => mate.constraintType));
                }
            }

            if (created == true && after is not null && before is not null && after > before)
            {
                step.Pass("постоянное сопряжение создано: сопряжений " + before + " → " + after);
            }
            else
            {
                step.Fail("сопряжение не подтверждено: AddMateConstraint=" + created
                    + ", сопряжений " + before + " → " + after);
            }
        }
        catch (Exception ex)
        {
            step.Fail("сопряжение бросило: " + ex.Message);
        }
    }

    private static int? MateCount(ksDocument3D document)
    {
        try
        {
            return document.MateConstraintCollection() is ksMateConstraintCollection mates
                ? mates.GetCount()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Отрицательный контроль: одна и та же грань в обе позиции. Документированный метод не обязан
    /// это отвергать, и проба НАЗЫВАЕТ исход, а не выдаёт желаемое за измеренное.
    /// </summary>
    private void Negative_SameObjectTwice(ksDocument3D document, object only)
    {
        var step = _report.Begin("M.6", "Отрицательный контроль: один объект в обе позиции",
            "Сопряжение грани с самой собой отвергается или принимается?");
        _current = step;

        try
        {
            var before = MateCount(document);
            bool? created;
            try
            {
                created = document.AddMateConstraint(
                    (int)MateConstraintType.mc_Coincidence, only, only, 0, 0, 0d);
            }
            catch (Exception ex)
            {
                step.Observe("AddMateConstraint бросил: " + ex.GetType().Name + ": " + ex.Message);
                step.Pass("отказ назван исключением");
                return;
            }

            document.RebuildDocument();
            var after = MateCount(document);
            step.Data["created"] = created;
            step.Data["count_before"] = before;
            step.Data["count_after"] = after;
            step.Observe("AddMateConstraint(грань, та же грань) → " + created
                + ", сопряжений " + before + " → " + after);

            if (created == true)
            {
                step.Fail("вырожденное сопряжение принято — это факт о продукте, а не о пробе");
            }
            else
            {
                step.Pass("вырожденное сопряжение отвергнуто (AddMateConstraint=" + created + ")");
            }
        }
        catch (Exception ex)
        {
            step.Fail("отрицательный контроль бросил: " + ex.Message);
        }
    }

    /// <summary>
    /// РАЗЛИЧАЮЩИЙ КОНТРОЛЬ: тела компонентов у сборки, созданной НЕ пробой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого контроля «у компонента нет тел» неотличимо от «проба строит сборку так, что
    /// геометрия не материализуется». Открывается готовая сборка из поставки КОМПАС-3D (их на
    /// машине 607), и тем же прибором читаются тела её компонентов.
    /// </para>
    /// <para>
    /// Ожидание сформулировано ЗАРАНЕЕ, чтобы исход не был подогнан: если у ручной сборки тела
    /// ЕСТЬ, причина «тел нет» — в способе создания (программная сборка), а не в КОМПАСе; если тел
    /// НЕТ и там, значит неверен сам прибор, и все прежние выводы отзываются.
    /// </para>
    /// </remarks>
    private void SampleAssemblyControl()
    {
        var step = _report.Begin("M.7", "Различающий контроль: сборка, созданная НЕ пробой",
            "Есть ли тела у компонентов готовой сборки КОМПАС-3D?");
        _current = step;

        var samples = new[]
        {
            Path.Combine(_options.KompasRoot, "Libs", "Pipeline", "TemplateHPH",
                "концевики", "Ниппель прямой (П.1)", "Законцовка прямая-10.a3d"),
            Path.Combine(_options.KompasRoot, "Libs", "Cable3D", "template.a3d"),
            Path.Combine(_options.KompasRoot, "Libs", "Pipeline", "StylesTemplate.a3d"),
        };

        foreach (var sample in samples)
        {
            if (!File.Exists(sample))
            {
                step.Observe("нет файла: " + sample);
                continue;
            }

            try
            {
                var document = (ksDocument3D)_app.Document3D();
                var opened = Api5.SafeBool(() => document.Open(sample));
                var components = Api5.SafeInt(() => (document.PartCollection(true) as ksPartCollection)!.GetCount());
                step.Observe(Path.GetFileName(sample) + ": Open=" + opened + ", компонентов=" + components);

                if (components is > 0 && document.PartCollection(true) is ksPartCollection collection)
                {
                    for (var index = 0; index < Math.Min(2, collection.GetCount()); index++)
                    {
                        if (collection.GetByIndex(index) is not ksPart component)
                        {
                            continue;
                        }

                        var bodies = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                        var name = Api5.SafeObject(() => component.name);
                        step.Observe("   компонент " + index + " '" + (name ?? "—") + "': тел = " + bodies);
                        step.Data["sample_component_bodies_" + index] = bodies;
                    }

                    step.Data["sample"] = Path.GetFileName(sample);
                    step.Data["sample_components"] = components;
                }

                document.close();
            }
            catch (Exception ex)
            {
                step.Observe("файл " + Path.GetFileName(sample) + " бросил "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        var anyBodies = step.Data.Where(pair => pair.Key.StartsWith("sample_component_bodies_"))
            .Any(pair => pair.Value is int value && value > 0);
        step.Data["sample_has_bodies"] = anyBodies;
        if (anyBodies)
        {
            step.Pass("у компонентов ГОТОВОЙ сборки тела ЕСТЬ — причина «тел нет» в способе создания");
        }
        else
        {
            step.Fail("тел нет и у готовой сборки — прибор или чтение тел под вопросом, прежние выводы отзываются");
        }
    }

    // ═════════════════════════════════════════════════════════════════ мост ══

    /// <summary>Переносит объект API5 в API7 как <c>Part7</c> (тип аргумента <c>FindObject</c>).</summary>
    private Part7? TransferPart7(object? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as Part7;
        }
        catch (Exception ex)
        {
            _report.Note(_current?.Id ?? "M", "TransferInterface(→ Part7) бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>Переносит объект API5 в API7 как <c>IModelObject</c>, либо null с причиной.</summary>
    private IModelObject? TransferTo7(object? source)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return _app.TransferInterface(source, 2 /* ksAPI7Dual */, 0) as IModelObject;
        }
        catch (Exception ex)
        {
            _report.Note(_current?.Id ?? "M", "TransferInterface(→ API7) бросил " + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    private ProbeStep? _current;
}
