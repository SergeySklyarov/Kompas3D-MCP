using System.Diagnostics;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants3D;
using KompasAPI7;

namespace KompasMcp.Api7Probe;

/// <summary>Probe DC — deleting an assembly component and the fate of its dependent mates.</summary>
/// <remarks>QUESTIONS. (1) Does the DOCUMENTED <c>ksDocument3D.DeleteObject(obj)</c>
/// (<c>ksdocument3d_deleteobject.html</c>: «Удалить объект: деталь, операцию, сопряжение»; «Удалить
/// можно любой трехмерный объект») accept a component — a <c>ksPart</c> from
/// <c>ksDocument3D.PartCollection(true)</c>? (2) How is a mate tied to the component it constrains,
/// by a DOCUMENTED route: <c>IMateConstraint3D.BaseObject1/2</c> → <c>IModelObject.Part</c>
/// (<c>imodelobject_part.html</c>: «Компонент, владеющий элементом») — or
/// <c>IMateConstraints3D.ObjectConstraints(Object)</c> («Возвращает массив сопряжений, связанных с
/// объектом»)? (3) Does deleting a component shift the OTHER components' placements? (4) What happens
/// to a mate whose component is deleted — does the kernel drop it, leave it invalid, or leave it
/// readable?
/// DISCRIMINATING CONTROL. Three components: two insertions of ONE file (A0, A1) and one of another
/// (B2); a single mate links A0 and B2. A route that identifies the owning component must return A0
/// and B2 and MUST NOT return A1 — the same file makes a name/file comparison unable to tell A0 from
/// A1, so a route that "finds a mate" by name would falsely match A1. Without this control,
/// "the route works" is indistinguishable from "the route matches anything".
/// History: docs/decisions/assembly.md#delete-component</remarks>
internal sealed class DeleteComponentProbe
{
    private const double PlateAWidth = 100d;
    private const double PlateAHeight = 80d;
    private const double PlateAThickness = 10d;
    private const double PlateBWidth = 60d;
    private const double PlateBHeight = 40d;
    private const double PlateBThickness = 20d;

    private readonly ProbeReport _report;
    private readonly Options _options;
    private readonly Stopwatch _clock = new();
    private readonly List<int> _pidsBefore = new();

    private KompasObject _app = null!;
    private IApplication? _app7;
    private int _ownPid;
    private ProbeStep? _current;

    public DeleteComponentProbe(ProbeReport report, Options options)
    {
        _report = report;
        _options = options;
    }

    public static void Flush(ProbeReport report, Options options)
    {
        report.Flush(
            Path.Combine(options.ReportDir, "delete-component.json"),
            Path.Combine(options.ReportDir, "delete-component.md"));
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
            var dir = Path.Combine(_options.WorkDir, "delete-component");
            Directory.CreateDirectory(dir);
            var pathA = Path.Combine(dir, "dc-source-a.m3d");
            var pathB = Path.Combine(dir, "dc-source-b.m3d");

            if (!BuildSourcePart("D.1", "dc-src-a", pathA, PlateAWidth, PlateAHeight, PlateAThickness))
            {
                return;
            }

            if (!BuildSourcePart("D.1b", "dc-src-b", pathB, PlateBWidth, PlateBHeight, PlateBThickness))
            {
                return;
            }

            var asmPath = Path.Combine(dir, "dc-assembly.m3d");
            var document = BuildAssembly(pathA, pathA, pathB, asmPath);
            if (document is null)
            {
                return;
            }

            var mateOrdinal = CreateMateBetween(document, 0, 2);
            IdentifyMateBaseObjects(document, mateOrdinal);
            ReadObjectConstraints(document);
            // OBSERVE ONLY here: removing the leftover mate in this step would destroy the very
            // question the NEXT step asks ("does deleting a MATED component drop the mate?").
            DeleteComponent(document, ordinal: 1, "средний из трёх, БЕЗ сопряжений", tryRemoveLeftoverMate: false);
            DeleteComponent(document, ordinal: 0, "сопряжённый (в сопряжении с компонентом 2)", tryRemoveLeftoverMate: true);
            ReopenAndCount(document, asmPath);
        }
        catch (Exception ex)
        {
            var crash = _report.Begin("D.X", "Необработанное исключение пробы DC");
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
        var step = _report.Begin("D.0", "Свой невидимый экземпляр КОМПАС-3D v24",
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
        var step = _report.Begin("D.Z", "Завершение сеанса", "Осиротевший процесс остаётся?");
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

    // ═══════════════════════════════════════════════════════ source parts ══

    private bool BuildSourcePart(
        string id, string name, string path, double width, double height, double thickness)
    {
        var step = _report.Begin(id, "Деталь-источник " + name + " на диске",
            "Есть ли файл, из которого вставлять компонент?");
        _current = step;

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, true))
        {
            step.Fail("Create(невидимый, деталь) вернул false.");
            return false;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = name;
        part.Update();

        if (Api5.BasePlate(part, width, height, thickness, step, name) is null)
        {
            step.Fail("плита не построена.");
            return false;
        }

        var volume = Api5.Volume(part);
        step.Data["volume_mm3"] = volume;
        step.Observe("объём источника: " + Api5.Num(volume));

        if (document.SaveAs(path) != true)
        {
            step.Fail("SaveAs(" + path + ") не дал true.");
            return false;
        }

        document.close();
        step.Observe("файл: " + path);
        step.Pass("источник сохранён");
        return true;
    }

    // ═══════════════════════════════════════════════════════════ assembly ══

    /// <summary>Assembly with THREE components: two insertions of <paramref name="pathA"/> and one of
    /// <paramref name="pathB"/>, offset along X so faces do not coincide.</summary>
    private ksDocument3D? BuildAssembly(string pathA, string pathA2, string pathB, string savePath)
    {
        var step = _report.Begin("D.2", "Сборка с ТРЕМЯ компонентами: A, A, B со смещениями",
            "Вставляются ли три компонента документированным IParts7.AddFromFile?");
        _current = step;

        var document = (ksDocument3D)_app.Document3D();
        if (!document.Create(true, false))
        {
            step.Fail("Create(невидимый, сборка) вернул false.");
            return null;
        }

        var part = (ksPart)document.GetPart(-1);
        part.name = "DC-asm";
        part.Update();

        var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        var parts7 = top7?.Parts;
        if (parts7 is null)
        {
            step.Fail("IPart7.Parts не дал IParts7 — вставлять нечем.");
            return null;
        }

        var sources = new[] { pathA, pathA2, pathB };
        for (var attempt = 0; attempt < sources.Length; attempt++)
        {
            Part7? inserted = null;
            try
            {
                inserted = parts7.AddFromFile(sources[attempt], ExternalFile: true, Redraw: true);
            }
            catch (Exception ex)
            {
                step.Fail("AddFromFile бросил на попытке " + attempt + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            step.Observe("AddFromFile(" + Path.GetFileName(sources[attempt]) + ") попытка " + attempt
                + " → " + (inserted is null ? "null" : "Part7"));
        }

        var count = Api5.SafeInt(() => parts7.Count);
        step.Data["components"] = count;
        step.Observe("компонентов по IParts7.Count: " + count);
        if (count != 3)
        {
            step.Fail("компонентов " + count + ", ожидалось 3.");
            return null;
        }

        // OFFSETS: components 0, 1, 2 at x = 0, 200, 400. Without them the faces coincide and a mate
        // between them degenerates.
        var offsets = new[] { 0d, 200d, 400d };
        for (var index = 0; index < offsets.Length; index++)
        {
            SetComponentOffset(document, index, offsets[index], step);
        }

        document.RebuildDocument();
        step.Observe("смещения по X: " + string.Join(", ", offsets));

        if (document.SaveAs(savePath) != true)
        {
            step.Fail("SaveAs(" + savePath + ") не дал true.");
            return null;
        }

        step.Pass("сборка с тремя компонентами сохранена");
        return document;
    }

    private void SetComponentOffset(ksDocument3D document, int ordinal, double x, ProbeStep step)
    {
        try
        {
            if (document.PartCollection(true) is ksPartCollection parts
                && ordinal < parts.GetCount()
                && parts.GetByIndex(ordinal) is ksPart component
                && document.DefaultPlacement() is ksPlacement placement)
            {
                var matrix = new double[]
                {
                    1, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0,
                    x, 0, 0, 1,
                };
                placement.InitByMatrix3D(matrix);
                component.SetPlacement(placement);
                component.UpdatePlacement();
            }
            else
            {
                step.Observe("смещение компонента " + ordinal + " НЕ применено (нет ksPart/ksPlacement)");
            }
        }
        catch (Exception ex)
        {
            step.Observe("смещение компонента " + ordinal + " бросило " + ex.GetType().Name);
        }
    }

    // ═══════════════════════════════════════════════════════════════ mate ══

    /// <summary>Creates a mate between component <paramref name="first"/> and
    /// <paramref name="second"/> through the PRODUCT route (API7 <c>MateConstraints.Add</c> →
    /// <c>BaseObject1/2</c> → <c>Update()</c>). Returns the mate's ordinal in
    /// <c>MateConstraintCollection</c>, or <c>-1</c>.</summary>
    private int CreateMateBetween(ksDocument3D document, int first, int second)
    {
        var step = _report.Begin("D.3", "Сопряжение между компонентами " + first + " и " + second,
            "Создаётся ли сопряжение на гранях двух РАЗНЫХ компонентов?");
        _current = step;

        var faceFirst = ComponentFace7(document, first, step);
        var faceSecond = ComponentFace7(document, second, step);
        if (faceFirst is null || faceSecond is null)
        {
            step.Fail("грани компонентов не перенесены в API7.");
            return -1;
        }

        var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        var mates7 = top7?.MateConstraints;
        if (mates7 is null)
        {
            step.Fail("IPart7.MateConstraints не дал IMateConstraints3D.");
            return -1;
        }

        var before = Api5.SafeInt(() => mates7.Count);
        IMateConstraint3D? mate = null;
        try
        {
            mate = mates7.Add(MateConstraintType.mc_Coincidence);
            mate.BaseObject1 = faceFirst;
            mate.BaseObject2 = faceSecond;
            mate.Update();
        }
        catch (Exception ex)
        {
            step.Fail("создание сопряжения бросило " + ex.GetType().Name + ": " + ex.Message);
            return -1;
        }

        document.RebuildDocument();
        var after = Api5.SafeInt(() => mates7.Count);
        var valid = Api5.SafeBool(() => mate.Valid);
        step.Data["mates_before"] = before;
        step.Data["mates_after"] = after;
        step.Data["mate_valid"] = valid;
        step.Observe("сопряжений " + before + " → " + after + ", Valid=" + valid);

        if (after is not null && before is not null && after > before)
        {
            step.Pass("сопряжение создано: номер " + (after - 1));
            return after.Value - 1;
        }

        step.Fail("сопряжение не подтверждено.");
        return -1;
    }

    /// <summary>The first face of component <paramref name="ordinal"/> as <c>IModelObject</c>.</summary>
    private IModelObject? ComponentFace7(ksDocument3D document, int ordinal, ProbeStep step)
    {
        try
        {
            if (document.PartCollection(true) is not ksPartCollection parts
                || ordinal >= parts.GetCount()
                || parts.GetByIndex(ordinal) is not ksPart component
                || component.BodyCollection() is not ksBodyCollection bodies
                || bodies.GetCount() == 0
                || bodies.GetByIndex(0) is not ksBody body
                || body.FaceCollection() is not ksFaceCollection faces
                || faces.GetCount() == 0)
            {
                step.Observe("компонент " + ordinal + ": грань не получена (нет тела/граней)");
                return null;
            }

            return TransferTo7(faces.GetByIndex(0));
        }
        catch (Exception ex)
        {
            step.Observe("компонент " + ordinal + ": чтение грани бросило " + ex.GetType().Name);
            return null;
        }
    }

    // ═══════════════════════════ DECISIVE: which component owns a mate's base object ══

    /// <summary>For the created mate, reads <c>BaseObject1/2</c>, their <c>IModelObject.Part</c>, and
    /// matches the owner against EVERY component. The DISCRIMINATING CONTROL is component 1 — the
    /// second insertion of the SAME file as component 0, which is NOT mated.</summary>
    private void IdentifyMateBaseObjects(ksDocument3D document, int mateOrdinal)
    {
        var step = _report.Begin("D.4", "Владелец базового объекта сопряжения — IModelObject.Part",
            "Называет ли документированное Part компонент-владельца и ОТЛИЧАЕТ ли одноимённый?");
        _current = step;

        if (mateOrdinal < 0)
        {
            step.Fail("сопряжения нет — идентифицировать нечего.");
            return;
        }

        if (document.MateConstraintCollection() is not ksMateConstraintCollection mates
            || mateOrdinal >= mates.GetCount()
            || mates.GetByIndex(mateOrdinal) is not ksMateConstraint mate5)
        {
            step.Fail("сопряжение по номеру " + mateOrdinal + " не прочитано.");
            return;
        }

        // The three components as API7 IPart7, taken from the SAME PartCollection order the address uses.
        var components = new List<(int Ordinal, IPart7 Part7, string Name, string File)>();
        if (document.PartCollection(true) is ksPartCollection parts)
        {
            for (var index = 0; index < parts.GetCount(); index++)
            {
                if (parts.GetByIndex(index) is ksPart component
                    && TransferTo7(component) is IPart7 part7)
                {
                    components.Add((index, part7, NameOf(component), FileOf(component)));
                }
            }
        }

        step.Observe("компонентов для сверки: " + components.Count);
        foreach (var (ordinal, _, name, file) in components)
        {
            step.Observe("   [" + ordinal + "] '" + name + "' файл '" + file + "'");
        }

        foreach (short which in new short[] { 1, 2 })
        {
            var baseObject = Api5.SafeObject(() => mate5.GetBaseObj(which));
            var model = TransferTo7(baseObject);
            step.Observe("GetBaseObj(" + which + ") → " + Api5.RuntimeName(baseObject)
                + "; перенос в API7 IModelObject: " + (model is null ? "null" : "да"));

            if (model is null)
            {
                step.Data["base" + which + "_owner_ordinal"] = null;
                continue;
            }

            Part7? owner = null;
            try
            {
                owner = model.Part;
            }
            catch (Exception ex)
            {
                step.Observe("   IModelObject.Part бросил " + ex.GetType().Name + ": " + ex.Message);
            }

            if (owner is null)
            {
                step.Data["base" + which + "_owner_ordinal"] = null;
                step.Observe("   IModelObject.Part = null — владелец не назван");
                continue;
            }

            // TWO SIGNALS ARE COMPARED, AND THEY DO NOT AGREE: RCW reference equality of the returned
            // owner against each component's IPart7, and the (weaker) name+file pair. The name+file
            // pair CANNOT tell two insertions of one file apart, so it is reported but does not decide.
            var ownerName = Api5.SafeObject(() => owner.Name) as string ?? "—";
            var ownerFile = Path.GetFileName(Api5.SafeObject(() => owner.FileName) as string ?? "—");
            var byReference = new List<int>();
            var byNameFile = new List<int>();
            foreach (var (ordinal, part7, name, file) in components)
            {
                if (ReferenceEquals(owner, part7))
                {
                    byReference.Add(ordinal);
                }

                if (name == ownerName && file == ownerFile)
                {
                    byNameFile.Add(ordinal);
                }

                step.Observe("   против [" + ordinal + "] '" + name + "'/" + file
                    + "': ссылка=" + ReferenceEquals(owner, part7)
                    + ", имя+файл=" + (name == ownerName && file == ownerFile));
            }

            step.Data["base" + which + "_owner_by_reference"] = byReference;
            step.Data["base" + which + "_owner_by_name_file"] = byNameFile;
            step.Data["base" + which + "_owner_name"] = ownerName;
            step.Data["base" + which + "_owner_file"] = ownerFile;
            step.Observe("   владелец '" + ownerName + "'/" + ownerFile
                + " → по ССЫЛКЕ [" + string.Join(", ", byReference) + "]"
                + ", по имени+файлу [" + string.Join(", ", byNameFile) + "]");
        }

        var ref1 = step.Data.TryGetValue("base1_owner_by_reference", out var r1) ? r1 as List<int> : null;
        var ref2 = step.Data.TryGetValue("base2_owner_by_reference", out var r2) ? r2 as List<int> : null;
        var nameFile1 = step.Data.TryGetValue("base1_owner_by_name_file", out var n1) ? n1 as List<int> : null;
        var ok = ref1 is { Count: 1 } && ref1[0] == 0
                 && ref2 is { Count: 1 } && ref2[0] == 2;
        if (ok)
        {
            step.Pass("владельцы названы ТОЧНО по ссылке: компонент 0 и компонент 2; одноимённый "
                + "компонент 1 НЕ совпал (имя+файл даёт " + (nameFile1?.Count ?? 0)
                + " совпадений — этот признак НЕ различает)");
        }
        else
        {
            step.Fail("владельцы не названы различимо по ссылке: base1=[" + string.Join(", ", ref1 ?? new List<int>())
                + "], base2=[" + string.Join(", ", ref2 ?? new List<int>()) + "] (ожидалось [0] и [2])");
        }
    }

    /// <summary>Does <c>IMateConstraints3D.ObjectConstraints</c> return the mates of a component?
    /// Asked per component: a route that returns the same answer for every component does not
    /// identify anything.</summary>
    private void ReadObjectConstraints(ksDocument3D document)
    {
        var step = _report.Begin("D.5", "ObjectConstraints по каждому компоненту",
            "Возвращает ли документированное свойство сопряжения ИМЕННО этого компонента?");
        _current = step;

        var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
        var mates7 = top7?.MateConstraints;
        if (mates7 is null)
        {
            step.Fail("IMateConstraints3D недостижим.");
            return;
        }

        if (document.PartCollection(true) is not ksPartCollection parts)
        {
            step.Fail("PartCollection не получена.");
            return;
        }

        for (var index = 0; index < parts.GetCount(); index++)
        {
            if (parts.GetByIndex(index) is not ksPart component
                || TransferTo7(component) is not IModelObject model)
            {
                step.Observe("компонент " + index + ": не перенесён в API7");
                continue;
            }

            object? raw = null;
            try
            {
                raw = mates7.ObjectConstraints[model];
            }
            catch (Exception ex)
            {
                step.Observe("компонент " + index + ": ObjectConstraints бросил "
                    + ex.GetType().Name + ": " + ex.Message);
                step.Data["object_constraints_" + index] = "исключение " + ex.GetType().Name;
                continue;
            }

            var count = CountVariant(raw);
            step.Data["object_constraints_" + index] = count;
            step.Observe("компонент " + index + ": ObjectConstraints → " + Api5.RuntimeName(raw)
                + ", число = " + (count?.ToString() ?? "не сосчитано"));

            // WHAT THE ELEMENTS ARE: "count = 1" is not enough to map a returned element back to a
            // row of MateConstraintCollection — the element type decides whether that is possible.
            if (raw is System.Array array)
            {
                for (var element = 0; element < array.Length; element++)
                {
                    var item = array.GetValue(element);
                    var asMate = item as IMateConstraint3D;
                    var asModel = item as IModelObject;
                    step.Observe("      элемент " + element + ": " + Api5.RuntimeName(item)
                        + "; IMateConstraint3D=" + (asMate is null ? "нет" : "да")
                        + ", тип=" + (asMate is null ? "—" : Api5.SafeInt(() => (int)asMate.ConstraintType)?.ToString() ?? "?")
                        + ", имя=" + (asMate is null ? "—" : (Api5.SafeObject(() => asMate.Name) as string ?? "—"))
                        + "; IModelObject=" + (asModel is null ? "нет" : "да"));
                }
            }
        }

        step.Pass("ObjectConstraints опрошен по каждому компоненту");
    }

    private int? CountVariant(object? raw) => raw switch
    {
        null => 0,
        System.Array array => array.Length,
        _ => 1,
    };

    // ═══════════════════════════════════ DECISIVE: delete a component ══

    /// <summary>Deletes the component at <paramref name="ordinal"/> and measures: the return value,
    /// the count, the OTHER components' sources and placements (did they shift?), and the mate count.
    /// When a mate survives, its <c>Valid</c> and whether it can still be removed is read.</summary>
    private void DeleteComponent(ksDocument3D document, int ordinal, string label, bool tryRemoveLeftoverMate)
    {
        var step = _report.Begin("D.6." + ordinal, "Удаление компонента " + ordinal + " (" + label + ")",
            "Удаляет ли DeleteObject компонент и сдвигает ли остальные?");
        _current = step;

        var before = Snapshot(document, step);
        step.Observe("до: " + before.Describe());

        if (document.PartCollection(true) is not ksPartCollection parts || ordinal >= parts.GetCount())
        {
            step.Fail("компонент по номеру " + ordinal + " не получен.");
            return;
        }

        var target = parts.GetByIndex(ordinal) as ksPart;
        if (target is null)
        {
            step.Fail("GetByIndex(" + ordinal + ") не ksPart.");
            return;
        }

        var targetFile = FileOf(target);
        var targetMatrix = PlacementOf(target);
        step.Data["target_file"] = targetFile;
        step.Data["target_placement"] = targetMatrix;

        bool? deleted = null;
        try
        {
            deleted = document.DeleteObject(target);
        }
        catch (Exception ex)
        {
            step.Observe("DeleteObject бросил " + ex.GetType().Name + ": " + ex.Message);
        }

        step.Observe("DeleteObject(ksPart[" + ordinal + "]) → " + deleted);
        step.Data["delete_returned"] = deleted;

        document.RebuildDocument();
        var after = Snapshot(document, step);
        step.Observe("после: " + after.Describe());
        step.Data["count_after"] = after.Count;

        // THE SURVIVORS: for every component still present, its source and placement. "The count fell
        // by one" is not enough — the claim is that the ADDRESSED component left and the OTHERS kept
        // their placements.
        var survivors = new List<object?>();
        if (document.PartCollection(true) is ksPartCollection remaining)
        {
            for (var index = 0; index < remaining.GetCount(); index++)
            {
                if (remaining.GetByIndex(index) is ksPart component)
                {
                    survivors.Add(new
                    {
                        ordinal = index,
                        name = Api5.SafeObject(() => component.name),
                        file = FileOf(component),
                        placement = PlacementOf(component),
                    });
                }
            }
        }

        step.Data["survivors"] = survivors;
        step.Observe("осталось компонентов: " + after.Count);

        // MATE FATE: read the mate count and, if a mate remains, whether it is Valid and removable.
        var mateCount = MateCount(document);
        step.Data["mate_count_after"] = mateCount;
        step.Observe("сопряжений после: " + (mateCount?.ToString() ?? "не прочитано"));

        ReadRemainingMates(document, step, tryRemoveLeftoverMate);

        if (deleted == true && after.Count == before.Count - 1)
        {
            step.Pass("компонент удалён: " + before.Count + " → " + after.Count);
        }
        else
        {
            step.Fail("удаление не подтверждено: returned=" + deleted
                + ", " + before.Count + " → " + after.Count);
        }
    }

    private void ReadRemainingMates(ksDocument3D document, ProbeStep step, bool tryRemove)
    {
        if (document.MateConstraintCollection() is not ksMateConstraintCollection mates)
        {
            step.Observe("MateConstraintCollection недоступна");
            return;
        }

        var count = Api5.SafeInt(() => mates.GetCount());
        if (count is null or 0)
        {
            step.Observe("сопряжений не осталось (или счётчик не прочитан): " + count);
            return;
        }

        for (var index = 0; index < count.Value; index++)
        {
            var mate = Api5.SafeObject(() => mates.GetByIndex(index)) as ksMateConstraint;
            if (mate is null)
            {
                continue;
            }

            var type = Api5.SafeInt(() => mate.constraintType);
            var base1 = Api5.SafeObject(() => mate.GetBaseObj(1));
            var base2 = Api5.SafeObject(() => mate.GetBaseObj(2));
            step.Observe("сопряжение " + index + ": тип=" + type
                + ", base1=" + (base1 is null ? "null" : Api5.RuntimeName(base1))
                + ", base2=" + (base2 is null ? "null" : Api5.RuntimeName(base2)));

            // The API7 view of the SAME ordinal: is the leftover mate still VALID after its component
            // is gone? An invalid leftover must not be passed off as a whole one.
            var mate7 = Mate7(document, index);
            var valid = mate7 is null ? (bool?)null : Api5.SafeBool(() => mate7.Valid);
            step.Observe("   Valid (API7) = " + (valid?.ToString() ?? "не прочитано"));
            step.Data["remaining_mate_" + index + "_valid"] = valid;

            if (!tryRemove)
            {
                step.Observe("   RemoveMateConstraint НЕ вызывается: удаление остатка испортило бы "
                    + "следующий опыт (удаление СОПРЯЖЁННОГО компонента)");
                continue;
            }

            var removed = false;
            if (type is not null && base1 is not null && base2 is not null)
            {
                try
                {
                    removed = document.RemoveMateConstraint(type.Value, base1, base2);
                }
                catch (Exception ex)
                {
                    step.Observe("   RemoveMateConstraint бросил " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            step.Observe("   RemoveMateConstraint → " + removed);
            step.Data["remaining_mate_" + index + "_removable"] = removed;
        }
    }

    /// <summary>save → close → reopen the SAME document object the whole run used.</summary>
    /// <remarks>THE DOCUMENT IS CARRIED IN, NOT RE-FETCHED: the first edition took it from the
    /// late-bound <c>_app.ActiveDocument</c>, which answered <c>DISP_E_UNKNOWNNAME</c> — the first run
    /// reported only that name, so which call failed was not knowable. The reference already in hand
    /// needs no late binding at all.</remarks>
    private void ReopenAndCount(ksDocument3D document, string asmPath)
    {
        var step = _report.Begin("D.7", "save → close → reopen после удалений",
            "Переживают ли удаления цикл сохранения?");
        _current = step;

        try
        {
            var saved = Api5.SafeBool(() => document.SaveAs(asmPath) == true);
            step.Observe("SaveAs(" + asmPath + ") = " + saved);

            var closed = Api5.SafeBool(() => { document.close(); return true; });
            step.Observe("close() = " + closed);

            var reopened = (ksDocument3D)_app.Document3D();
            var opened = Api5.SafeBool(() => reopened.Open(asmPath));
            step.Observe("Open(" + asmPath + ") = " + opened);

            var count = Api5.SafeInt(() => (reopened.PartCollection(true) as ksPartCollection)!.GetCount());
            step.Data["reopen_open"] = opened;
            step.Data["reopen_components"] = count;
            step.Observe("Open=" + opened + ", компонентов после переоткрытия: " + count);

            if (count == 1)
            {
                step.Pass("после переоткрытия остался ровно один компонент (как и до закрытия)");
            }
            else
            {
                step.Fail("после переоткрытия компонентов " + count + ", ожидался 1");
            }

            Api5.SafeBool(() => { reopened.close(); return true; });
        }
        catch (Exception ex)
        {
            step.Fail("цикл переоткрытия бросил " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════ helpers ══

    private sealed record AssemblySnapshot(int Count, List<string> Files, List<double?[]> Placements)
    {
        public string Describe() =>
            "компонентов " + Count + ": [" + string.Join(", ", Files) + "]";
    }

    private AssemblySnapshot Snapshot(ksDocument3D document, ProbeStep step)
    {
        var files = new List<string>();
        var placements = new List<double?[]>();
        try
        {
            if (document.PartCollection(true) is ksPartCollection parts)
            {
                for (var index = 0; index < parts.GetCount(); index++)
                {
                    if (parts.GetByIndex(index) is ksPart component)
                    {
                        files.Add(FileOf(component));
                        placements.Add(PlacementOf(component));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            step.Observe("снимок сборки бросил " + ex.GetType().Name);
        }

        return new AssemblySnapshot(files.Count, files, placements);
    }

    private static double?[] PlacementOf(ksPart component)
    {
        try
        {
            if (component.GetPlacement() is ksPlacement placement
                && placement.GetMatrix3D(out var raw)
                && raw is Array array
                && array.Length >= 16)
            {
                var result = new double?[16];
                for (var i = 0; i < 16; i++)
                {
                    result[i] = Convert.ToDouble(array.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                }

                return result;
            }
        }
        catch (Exception)
        {
            // "not read" stays null
        }

        return new double?[16];
    }

    /// <summary>A mate by ordinal as <c>IMateConstraint3D</c> (API7), or <c>null</c>.</summary>
    private IMateConstraint3D? Mate7(ksDocument3D document, int ordinal)
    {
        try
        {
            var top7 = (_app7?.ActiveDocument as IKompasDocument3D)?.TopPart;
            return top7?.MateConstraints?.MateConstraint3D[ordinal];
        }
        catch (Exception)
        {
            return null;
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

    /// <summary>Component display name, or <c>"—"</c> when it was not read (an unread name is named,
    /// not substituted by an empty string).</summary>
    private static string NameOf(ksPart component) =>
        Api5.SafeObject(() => component.name) as string is { Length: > 0 } name ? name : "—";

    /// <summary>Component source FILE NAME (not the path — the address compares file names), or
    /// <c>"—"</c> when the source was not read.</summary>
    private static string FileOf(ksPart component) =>
        Api5.SafeObject(() => component.fileName) as string is { Length: > 0 } path
            ? Path.GetFileName(path)
            : "—";

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
            _report.Note(_current?.Id ?? "D", "TransferInterface(→ API7) бросил "
                + ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }
}
