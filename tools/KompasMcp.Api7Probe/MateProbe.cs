using System.Diagnostics;
using System.Globalization;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe M — assembly mates: are COMPONENT FACES addressable and is a mate created?</summary>
/// <remarks>DOC: the mate route, read 05.10.2026, is <c>IPart7.MateConstraints</c> →
/// <c>IMateConstraints3D.Add(MateConstraintType)</c> → <c>IMateConstraint3D</c> (<c>BaseObject1</c>,
/// <c>BaseObject2</c>, <c>Alignment</c>, <c>ParamValue</c>, <c>Fixed</c>, <c>Update()</c>).
/// INVARIANT: <c>BaseObject1/2</c> are <c>IModelObject</c> and must address the COMPONENT document,
/// not the assembly itself — if a component face cannot be reached from there, the block must be
/// built differently. DOC: the face route is the documented <c>ksPart.GetMainBody() →
/// ksBody.FaceCollection()</c>, NOT <c>EntityCollection(o3d_face)</c>; the API7 transfer is
/// <c>TransferInterface(…, ksAPI7Dual, …)</c>. MEASURED 04.10.2026: a second
/// <c>CreatePartInAssembly</c> of the same file returns null, so a repeat instance uses
/// <c>CopyPart(source, placement)</c>. TEST: the evidence is the mate count before/after, the new
/// mate's <c>Valid</c>, non-empty <c>BaseObject1/2</c>, and — decisively — whether the component
/// MOVED; a mate "created" without motion does not separate an accepted link from an empty record.
/// History: docs/decisions/probes.md#mate</remarks>
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

    /// <summary>Writes the report from inside the run — for the diagnostic <c>--keep</c> path.</summary>
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
            // DISCRIMINATING CONTROL runs FIRST and on its own document: it does not depend on the
            // programmatic assembly succeeding and must run even if everything after it stops —
            // otherwise the control is "not reached" and the finding has no support.
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

            // REOPEN is the documented check: if bodies appear only after loading the document from
            // disk, the cause "no bodies" is named, and that condition becomes part of the route.
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

    // ═══════════════════════════════════════════════════════════════ session ══

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

    // ═══════════════════════════════════════════════════════ source part ══

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

        // INSTRUMENT CONTROL: does the route ksPart.BodyCollection() itself work where a body surely
        // exists? Without it, "component has no bodies" is indistinguishable from "the instrument
        // calls the wrong thing".
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

        // The document is CLOSED: MEASURED 05.10.2026 — with the source open, CreatePartInAssembly
        // returns null. The component face is taken not from this document but by point search
        // (IPart7.FindObjectsByPoint) in the assembly.
        document.close();
        step.Observe("файл: " + path + "; документ-источник закрыт");
        step.Pass("источник сохранён");
        return (path, part);
    }

    // ═════════════════════════════════════════════════════════════ assembly ══

    private (string Path, ksPart Part, ksPart SourcePart)? BuildAssemblyWithTwoComponents(
        string sourcePath)
    {
        var step = _report.Begin("M.2", "Сборка с ДВУМЯ компонентами — документированный IParts7.AddFromFile",
            "Документированный маршрут вставки даёт компоненты С ГЕОМЕТРИЕЙ?");
        _current = step;

        var directory = Path.Combine(_options.WorkDir, "mate");
        var path = Path.Combine(directory, "mate-assembly.m3d");

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, false))
        {
            step.Fail("Create(невидимый, сборка) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "Mate-asm";
        part.Update();

        // DOC (iparts7_addfromfile.html): IPart7.Parts → IParts7.AddFromFile(FileName, ExternalFile,
        // Redraw) → Part7. «FileName — имя файла, из которого будет ВСТАВЛЕН компонент», «ExternalFile —
        // TRUE — вставка СО ССЫЛКОЙ на внешний файл», «Redraw — признак перестроения документа после
        // вставки». It returns the INSERTED component, so one call gives both the address and the
        // rebuild.
        var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        var parts7 = top7?.Parts;
        if (parts7 is null)
        {
            step.Fail("IPart7.Parts не дал IParts7 — вставлять нечем.");
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Part7? inserted = null;
            try
            {
                inserted = parts7.AddFromFile(sourcePath, ExternalFile: true, Redraw: true);
            }
            catch (Exception ex)
            {
                step.Fail("AddFromFile бросил на попытке " + attempt + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            step.Observe("AddFromFile(источник, ExternalFile=true, Redraw=true) попытка " + attempt
                + " → " + (inserted is null ? "null" : "Part7"));
        }

        var count = Api5.SafeInt(() => parts7.Count);
        step.Data["components"] = count;
        step.Observe("компонентов по IParts7.Count: " + count);

        // THE SECOND COMPONENT IS OFFSET. Without an offset both inserts sit at the origin, their
        // faces coincide and the mate degenerates: every documented parameter combination returned
        // False. DOC/MEASURED (block C1): placement is written via ksDocument3D.DefaultPlacement →
        // InitByMatrix3D → SetPlacement → UpdatePlacement; layout [X,0][Y,0][Z,0][translation,1].
        var offset = new double[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            150, 0, 0, 1,
        };
        if (document.PartCollection(true) is ksPartCollection insertedParts
            && insertedParts.GetCount() >= 2
            && insertedParts.GetByIndex(1) is ksPart secondComponent
            && document.DefaultPlacement() is ksPlacement secondPlacement)
        {
            secondPlacement.InitByMatrix3D(offset);
            secondComponent.SetPlacement(secondPlacement);
            secondComponent.UpdatePlacement();
            document.RebuildDocument();
            step.Observe("второй компонент сдвинут на 150 мм по X");
        }
        else
        {
            step.Observe("сдвинуть второй компонент НЕ удалось");
        }

        // Bodies of each component — immediately, via the documented ksPart.BodyCollection().
        if (document.PartCollection(true) is ksPartCollection collection)
        {
            for (var index = 0; index < collection.GetCount(); index++)
            {
                if (collection.GetByIndex(index) is not ksPart component)
                {
                    continue;
                }

                var bodies = Api5.SafeInt(() => (component.BodyCollection() as ksBodyCollection)!.GetCount());
                step.Observe("компонент " + index + " '" + (Api5.SafeObject(() => component.name) ?? "—")
                    + "': тел = " + bodies);
                step.Data["component_bodies_" + index] = bodies;
            }
        }

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

    /// <summary>Reopens the saved assembly — via the documented <c>ksDocument3D.Open</c>.</summary>
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

        // PROBE DEFECT CAUGHT HERE: the object Open is called on does NOT become the document — its
        // PartCollection is empty. The document is taken fresh from the application, as sample M.7
        // does. KompasObject does not declare ActiveDocument statically, so the property is taken by
        // late binding, as the probe already does for ksGetApplication7.
        // History: docs/decisions/probes.md#mate-reopen
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

    // ═════════════════════════════════════════════ DECISIVE EXPERIMENT: component faces ══

    /// <summary>Component faces as <c>ksEntity</c> — via the DOCUMENTED route
    /// <c>ksPart.BodyCollection() → ksBody.FaceCollection()</c>.</summary>
    /// <remarks>DOC (<c>kspart_getmainbody.html</c>): «Пример: Деталь имеет массив интерфейсов тел
    /// <c>IBody</c>» — a part may have SEVERAL bodies, and a single "main body" does not read on an
    /// assembly component (MEASURED: null). DOC (<c>kspart_bodycollection.html</c>):
    /// <c>ksPart.BodyCollection()</c> returns <c>ksBodyCollection</c>. <c>ksEntity</c> is exactly the
    /// type <c>ksDocument3D::AddMateConstraint</c> accepts («object1 — указатель на интерфейс первого
    /// объекта, на который накладывается сопряжение (<c>ksEntity</c> или <c>IEntity</c>)»).
    /// History: docs/decisions/probes.md#mate-faces</remarks>
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

                // DOC (ipart7_rebuildmodel.html): «Redraw — TRUE перестроить документ». Checks whether
                // a rebuild materialises the component geometry.
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

                // DOC (ipart7_opensourcedocument.html): open the component's SOURCE document. Checks
                // whether this materialises the geometry: if so, the cause "no bodies" is the unloaded
                // source, not a missing route.
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

                // DOC (ipart7_islocal.html): «IsLocal — получить И УСТАНОВИТЬ свойство» (put_IsLocal).
                // A local component keeps geometry IN THE ASSEMBLY rather than by reference — if
                // bodies appear here, the cause "no bodies" is named: the component is a reference.
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

    /// <summary>Component by ordinal as <c>IPart7</c> (address MEASURED in block C1).</summary>
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

    /// <summary>Coerces the <c>FindObjectsByPoint</c> result into a list of <c>IModelObject</c>.</summary>
    /// <remarks>The documentation does not fix the shape of the answer: it may be a single object, a
    /// SAFEARRAY or an <c>object[]</c>. All three are handled, and an unexpected shape is NAMED, not
    /// swallowed.</remarks>
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

    /// <summary>Fallback documented path: open the component's SOURCE document
    /// (<c>IPart7.OpenSourceDocument</c>) and read the body there.</summary>
    /// <remarks>Kept diagnostic: if <c>Load(true)</c> already gave a body, we do not come here. If it
    /// did not, this step NAMES what <c>OpenSourceDocument</c> returned instead of a silent "failed":
    /// the next edit must rest on observation, not a guess.</remarks>
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

    // ═══════════════════════════════════════════════════ DECISIVE EXPERIMENT: mate ══

    /// <summary>Permanent mate — via the DOCUMENTED <c>ksDocument3D.AddMateConstraint</c>.</summary>
    /// <remarks>DOC (<c>ksdocument3d_getmateconstraint.html</c>, <c>ksmateconstraint_create.html</c>):
    /// «Сопряжения бывают постоянными и временными… <b>Постоянные сопряжения создаются с помощью метода
    /// <c>ksDocument3D::AddMateConstraint</c></b>», while <c>Create()</c> serves only the TEMPORARY mate
    /// inside <c>UserGetPlacementAndEntity</c>. Signature: <c>BOOL AddMateConstraint(long constraintType,
    /// LPDISPATCH object1, LPDISPATCH object2, short direction, short fixed, double value)</c>;
    /// <c>direction</c>: 1 unidirectional, 0 ignored, −1 opposite.
    /// History: docs/decisions/probes.md#mate-create</remarks>
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

            // PARAMETERS ARE ENUMERATED BY THEIR DOCUMENTED VALUES, not at random.
            // ksdDocument3d_addmateconstraint.html: direction — «1 однонаправленные, 0 направление не
            // учитывается, −1 разнонаправленные»; fixed — «0 детали не фиксируются, 1 фиксируется первая
            // деталь, 2 фиксируется вторая деталь»; val — «параметр для ограничений (расстояние или
            // угол)», and «НАПРАВЛЕНИЕ ЗАДАЁТСЯ ЗНАКОМ параметра val». MEASURED: the first call (all
            // zeroes) returned False — the zero set is not a working one.
            var combos = new (string Label, MateConstraintType Type, short Direction, short Fix, double Value)[]
            {
                ("совпадение: direction=0, fixed=1", MateConstraintType.mc_Coincidence, 0, 1, 0d),
                ("совпадение: direction=-1, fixed=1", MateConstraintType.mc_Coincidence, -1, 1, 0d),
                ("совпадение: direction=1, fixed=1", MateConstraintType.mc_Coincidence, 1, 1, 0d),
                ("расстояние 50: direction=0, fixed=1", MateConstraintType.mc_Distance, 0, 1, 50d),
                ("параллельность: direction=0, fixed=1", MateConstraintType.mc_Parallel, 0, 1, 0d),
            };

            bool? created = null;
            foreach (var combo in combos)
            {
                bool? result = null;
                try
                {
                    result = document.AddMateConstraint(
                        (int)combo.Type, first, second, combo.Direction, combo.Fix, combo.Value);
                }
                catch (Exception ex)
                {
                    step.Observe(combo.Label + ": бросил " + ex.GetType().Name + ": " + ex.Message);
                    continue;
                }

                document.RebuildDocument();
                var matesNow = MateCount(document);
                step.Observe(combo.Label + " → " + result + ", сопряжений " + matesNow);
                step.Data["combo_" + combo.Label] = result?.ToString() ?? "null";

                if (result == true)
                {
                    created = true;
                    break;
                }
            }

            // SECOND DOCUMENTED PATH — API7: IPart7.MateConstraints → IMateConstraints3D.Add
            // (imateconstraints3d_add.html) → BaseObject1/BaseObject2 → Update(). It is tried ON THE SAME
            // real faces: if the API5 method refused on the objects, this will show it.
            if (created != true)
            {
                var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
                var mates7 = top7?.MateConstraints;
                if (mates7 is not null)
                {
                    var face1 = TransferTo7(first);
                    var face2 = TransferTo7(second);
                    step.Observe("API7: грани перенесены — первая " + (face1 is null ? "null" : "да")
                        + ", вторая " + (face2 is null ? "null" : "да"));

                    if (face1 is not null && face2 is not null)
                    {
                        var before7 = Api5.SafeInt(() => mates7.Count);
                        IMateConstraint3D? mate = null;
                        try
                        {
                            mate = mates7.Add(MateConstraintType.mc_Coincidence);
                            mate.BaseObject1 = face1;
                            mate.BaseObject2 = face2;
                        }
                        catch (Exception ex)
                        {
                            step.Observe("API7: присваивание бросило " + ex.GetType().Name + ": " + ex.Message);
                        }

                        if (mate is not null)
                        {
                            bool? updated = null;
                            try
                            {
                                updated = mate.Update();
                            }
                            catch (Exception ex)
                            {
                                step.Observe("API7: Update() бросил " + ex.GetType().Name + ": " + ex.Message);
                            }

                            document.RebuildDocument();
                            var after7 = Api5.SafeInt(() => mates7.Count);
                            var valid = Api5.SafeBool(() => mate.Valid);
                            step.Observe("API7 Add(mc_Coincidence) + BaseObject1/2 + Update() → " + updated
                                + ", Valid=" + valid + ", сопряжений " + before7 + " → " + after7);
                            step.Data["api7_update"] = updated;
                            step.Data["api7_valid"] = valid;
                            step.Data["api7_count_after"] = after7;
                            if (updated == true && valid == true)
                            {
                                created = true;
                            }
                        }
                    }
                }
            }

            var after = MateCount(document);
            step.Data["created"] = created;
            step.Data["count_after"] = after;

            // Read back by the same documented route: MateConstraintCollection → GetCount/GetByIndex →
            // GetBaseObj(1|2). Without the read, "created" is indistinguishable from "accepted silently".
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

    /// <summary>Negative control: the same face in both positions. The documented method is not obliged
    /// to reject it, and the probe NAMES the outcome rather than passing off the wish as the measured.</summary>
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

    /// <summary>DISCRIMINATING CONTROL: component bodies in an assembly NOT created by the probe.</summary>
    /// <remarks>A ready-made assembly from the KOMPAS-3D install is opened (MEASURED: 607 on this
    /// machine) and the same instrument reads its component bodies. Without this control, "component
    /// has no bodies" is indistinguishable from "the probe builds the assembly so geometry does not
    /// materialise". The expectation was stated IN ADVANCE so the outcome is not fitted: if the manual
    /// assembly HAS bodies, the cause is the creation method (programmatic assembly); if it has NONE
    /// either, the instrument itself is wrong and all earlier conclusions are revoked.
    /// History: docs/decisions/probes.md#mate-sample</remarks>
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

    // ═════════════════════════════════════════════════════════════════ bridge ══

    /// <summary>Transfers an API5 object into API7 as <c>Part7</c> (argument type <c>FindObject</c>).</summary>
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

    /// <summary>Transfers an API5 object into API7 as <c>IModelObject</c>, or null with a reason.</summary>
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
