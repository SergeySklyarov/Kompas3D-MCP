using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace SheetMetalProbe;

/// <summary>Measures the documented sheet-metal routes on this installation, end to end.</summary>
/// <remarks>
/// INVARIANT: every check records EXPECTED and READ, so a route that answers without building
/// anything cannot be reported as working. INVARIANT: the entry into KOMPAS is the product's own
/// route (ProgID KOMPAS.Application.5, then API7 through ksGetApplication7), never a CoCreateInstance
/// of the API7 server.
/// DOC: isheetmetalcontainer.html in the reference/kompas-sdk-v24 mirror. History: docs/decisions/sheet-metal.md#probe
/// </remarks>
internal sealed class SheetMetalProbe : IDisposable
{
    private const short PlaneXoy = 1;
    private const short ObjSketch = 5;

    /// <summary>ksAPITypeEnum.ksAPI7Dual — the transfer mode the product's bridge uses.</summary>
    private static readonly int Api7Dual = (int)ksAPITypeEnum.ksAPI7Dual;

    private readonly string _outPath;
    private readonly List<Check> _checks = new();

    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;
    private ISheetMetalContainer? _container;
    private ISheetMetalBody? _body;
    private ISheetMetalBendUnfoldParameters? _unfold;
    private string _version = "unknown";
    private int? _pid;

    public SheetMetalProbe(string outPath) => _outPath = outPath;

    public int Run()
    {
        try
        {
            Launch();
            Document();
            Container();
            Body();
            Bend();
            UnfoldOperation();
            Unfold();
            Reopen();
        }
        catch (ProbeStop)
        {
            // The reason is already recorded by the step that stopped the probe.
        }
        catch (Exception ex)
        {
            Record("probe", "проба доходит до конца", Describe(ex), Verdict.Fail);
        }

        Write();
        return Failed ? 1 : 0;
    }

    private bool Failed => _checks.Any(c => c.Verdict == Verdict.Fail);

    // ---------------------------------------------------------------------------------------------
    // Steps
    // ---------------------------------------------------------------------------------------------

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "не зарегистрирован", Verdict.Fail);
            throw new ProbeStop();
        }

        var before = KompasProcessIds();
        try
        {
            _app5 = Activator.CreateInstance(type) as KompasObject;
        }
        catch (COMException ex)
        {
            Record("app5_launch", "экземпляр КОМПАС создан", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (_app5 is null)
        {
            Record("app5_launch", "объект приводится к KompasObject", "не приводится", Verdict.Fail);
            throw new ProbeStop();
        }

        var appeared = KompasProcessIds().Except(before).ToArray();
        _pid = appeared.Length == 1 ? appeared[0] : null;
        Record("app5_launch", "объект приводится к KompasObject", "KompasObject", Verdict.Pass);

        try
        {
            _app5.Visible = false;
        }
        catch (COMException ex)
        {
            Record("app5_visible", "Visible = false", Describe(ex), Verdict.Info);
        }

        _version = ReadVersion(_app5);
        Record("kompas_version", "версия прочитана", _version, Verdict.Info);

        try
        {
            _app7 = _app5.ksGetApplication7() as IApplication;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("api7_bridge", "ksGetApplication7() даёт IApplication", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (_app7 is null)
        {
            Record("api7_bridge", "ksGetApplication7() даёт IApplication", "null", Verdict.Fail);
            throw new ProbeStop();
        }

        Record("api7_bridge", "ksGetApplication7() даёт IApplication", "IApplication", Verdict.Pass);
    }

    private void Document()
    {
        IKompasDocument added;
        try
        {
            added = _app7!.Documents.Add(DocumentTypeEnum.ksDocumentPart, false);
        }
        catch (COMException ex)
        {
            Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        _doc7 = added as IKompasDocument3D;
        if (_doc7 is null)
        {
            Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", RuntimeName(added), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("document", "Documents.Add(ksDocumentPart) → IKompasDocument3D", "IKompasDocument3D", Verdict.Pass);

        _doc5 = _app5!.ActiveDocument3D() as ksDocument3D
            ?? _app5.TransferInterface(_doc7, (int)ksAPITypeEnum.ksAPI5Auto, 0) as ksDocument3D;
        if (_doc5 is null)
        {
            Record("document_api5", "API5-вид того же документа получен", "null", Verdict.Fail);
            throw new ProbeStop();
        }

        Record("document_api5", "API5-вид того же документа получен", "ksDocument3D", Verdict.Pass);
    }

    private void Container()
    {
        var part = _doc7!.TopPart;
        _container = part as ISheetMetalContainer;
        if (_container is null)
        {
            Record("container_qi", "IPart7 приводится к ISheetMetalContainer", RuntimeName(part), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("container_qi", "IPart7 приводится к ISheetMetalContainer", "ISheetMetalContainer", Verdict.Pass);
        CheckInt("bodies_count_initial", 0, _container.SheetMetalBodies.Count);
        CheckInt("bends_count_initial", 0, _container.SheetMetalBends.Count);
    }

    private void Body()
    {
        var sketch5 = MakeSketch();

        object? transferred;
        ISketch? sketch7;
        try
        {
            transferred = _app5!.TransferInterface(sketch5, Api7Dual, 0);
            sketch7 = transferred as ISketch;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("sketch_to7", "эскиз перенесён в API7 (ISketch)", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (sketch7 is null)
        {
            Record("sketch_to7", "эскиз перенесён в API7 (ISketch)", RuntimeName(transferred), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("sketch_to7", "эскиз перенесён в API7 (ISketch)", "ISketch", Verdict.Pass);

        SheetMetalBody created;
        try
        {
            created = _container!.SheetMetalBodies.Add();
        }
        catch (COMException ex)
        {
            Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        _body = created as ISheetMetalBody;
        if (_body is null)
        {
            Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", RuntimeName(created), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("body_add", "ISheetMetalBodies.Add() отдаёт ISheetMetalBody", "ISheetMetalBody", Verdict.Pass);

        _body.Sketch = (Sketch)sketch7;
        _body.Thickness = 1.5d;
        _body.Radius = 2d;
        _body.Direction = ksDirectionTypeEnum.dtNormal;

        CheckBool("body_update", true, SafeBool(() => _body.Update()));
        CheckInt("bodies_count_after_add", 1, _container.SheetMetalBodies.Count);
        CheckDouble("body_thickness_read_back", 1.5d, SafeDouble(() => _body.Thickness));
        CheckDouble("body_radius_read_back", 2d, SafeDouble(() => _body.Radius));
        Record("body_profile_read", "Profile после задания Sketch (справка связи не описывает)",
            RuntimeName(SafeObject(() => _body.Profile)), Verdict.Info);
    }

    private void Bend()
    {
        var edge = FirstEdge();

        object? transferred;
        IModelObject? edge7;
        try
        {
            transferred = _app5!.TransferInterface(edge, Api7Dual, 0);
            edge7 = transferred as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("edge_to7", "ребро тела перенесено в API7 (IModelObject)", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (edge7 is null)
        {
            Record("edge_to7", "ребро тела перенесено в API7 (IModelObject)", RuntimeName(transferred), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("edge_to7", "ребро тела перенесено в API7 (IModelObject)", "IModelObject", Verdict.Pass);

        SheetMetalBend created;
        try
        {
            created = _container!.SheetMetalBends.Add();
        }
        catch (COMException ex)
        {
            Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        var bend = created as ISheetMetalBend;
        if (bend is null)
        {
            Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", RuntimeName(created), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("bend_add", "ISheetMetalBends.Add() отдаёт ISheetMetalBend", "ISheetMetalBend", Verdict.Pass);

        bend.BendObject = edge7;
        bend.Angle = 90d;
        CheckBool("bend_update", true, SafeBool(() => bend.Update()));
        CheckInt("bends_count_after_add", 1, _container.SheetMetalBends.Count);
        CheckDouble("bend_angle_read_back", 90d, SafeDouble(() => bend.Angle));
    }

    private void Unfold()
    {
        var unfold = _unfold = _container!.SheetMetalBendUnfoldParameters;
        Record("unfold_params_created", "IsCreated у параметров развёртки после сгиба",
            Flag(SafeBool(() => unfold.IsCreated)), Verdict.Info);
        Record("unfold_flag", "Unfold до разгибания", Flag(SafeBool(() => unfold.Unfold)), Verdict.Info);

        var bent = Gabarit();
        _body!.Straighten = true;
        CheckBool("straighten_read_back", true, SafeBool(() => _body.Straighten));
        CheckBool("body_update_after_straighten", true, SafeBool(() => _body.Update()));
        var straightened = Gabarit();

        Record("gabarit_bent", "габарит согнутого тела", bent, Verdict.Info);
        Record("gabarit_after_straighten", "габарит после Straighten", straightened, Verdict.Info);
        CheckBool("gabarit_changed_by_straighten", true, bent != straightened);

        // The second documented route to an unfolded state: the unfold parameters themselves, with
        // the inputs the same pages name (plane, fixed faces) before UpdateParam().
        // DOC: isheetmetalbendunfoldparameters_unfold.html, ..._unfoldplane.html, ..._updateparam.html
        if (Plane7() is { } plane)
        {
            TrySet("unfold_plane_set", () => unfold.UnfoldPlane = plane);
        }

        if (FirstFace() is { } fixedFace)
        {
            TrySet("unfold_fixed_faces_set", () => unfold.FixedFaces = new object[] { fixedFace });
        }

        TrySet("unfold_set", () => unfold.Unfold = true);
        CheckBool("unfold_update_param", true, SafeBool(() => unfold.UpdateParam()));
        CheckBool("unfold_is_created", true, SafeBool(() => unfold.IsCreated));
        CheckBool("unfold_flag_read_back", true, SafeBool(() => unfold.Unfold));
        var unfolded = Gabarit();
        Record("gabarit_after_unfold", "габарит после Unfold + UpdateParam", unfolded, Verdict.Info);
        CheckBool("gabarit_changed_by_unfold", true, bent != unfolded);
    }

    /// <summary>The documented "Согнуть/Разогнуть" operation: its type comes as the first argument.</summary>
    /// <remarks>DOC: isheetmetalbendedstraightens_add.html, isheetmetalbendedstraighten_props.html.
    /// MEASURED: the body's <c>Straighten</c> flag alone changes no geometry, so the operation is the
    /// route that has to be tried before "the unfold is unreachable" can be said.
    /// History: docs/decisions/sheet-metal.md#probe</remarks>
    private void UnfoldOperation()
    {
        var operations = _container!.SheetMetalBendedStraightens;
        SheetMetalBendedStraighten created;
        try
        {
            created = operations.Add(ksObj3dTypeEnum.o3d_sheetMetalBendUnfold);
        }
        catch (COMException ex)
        {
            Record("unfold_op_add", "ISheetMetalBendedStraightens.Add(o3d_sheetMetalBendUnfold)", Describe(ex), Verdict.Fail);
            return;
        }

        if (created as ISheetMetalBendedStraighten is not { } operation)
        {
            Record("unfold_op_add", "Add(o3d_sheetMetalBendUnfold) отдаёт ISheetMetalBendedStraighten",
                RuntimeName(created), Verdict.Fail);
            return;
        }

        Record("unfold_op_add", "Add(o3d_sheetMetalBendUnfold) отдаёт ISheetMetalBendedStraighten",
            "ISheetMetalBendedStraighten", Verdict.Pass);

        if (FirstFace() is { } face)
        {
            TrySet("unfold_op_fixed_face", () => operation.FixedFace = face);
        }

        CheckBool("unfold_op_update", true, SafeBool(() => operation.Update()));
        CheckInt("unfold_op_count", 1, operations.Count);
        Record("unfold_op_is_created", "IsCreated после операции развёртки",
            Flag(_unfold is null ? null : SafeBool(() => _unfold.IsCreated)), Verdict.Info);
        Record("gabarit_after_unfold_op", "габарит после операции развёртки", Gabarit(), Verdict.Info);
    }

    /// <summary>Applies a write whose refusal is a measurement, not a crash.</summary>
    private void TrySet(string id, Action write)
    {
        try
        {
            write();
            Record(id, "запись принята", "принята", Verdict.Pass);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record(id, "запись принята", Describe(ex), Verdict.Fail);
        }
    }

    private void Reopen()
    {
        var file = Path.Combine(Path.GetTempPath(),
            "sheet-metal-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".m3d");

        try
        {
            _doc7!.SaveAs(file);
        }
        catch (COMException ex)
        {
            Record("save", "деталь сохранена", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (!File.Exists(file))
        {
            Record("save", "деталь сохранена", "файла нет: " + file, Verdict.Fail);
            throw new ProbeStop();
        }

        Record("save", "деталь сохранена", file, Verdict.Pass);

        IKompasDocument reopened;
        try
        {
            reopened = _app7!.Documents.Open(file, false, true);
        }
        catch (COMException ex)
        {
            Record("reopen", "сохранённый файл открывается", Describe(ex), Verdict.Fail);
            throw new ProbeStop();
        }

        if (reopened as IKompasDocument3D is not { TopPart: { } part } || part as ISheetMetalContainer is not { } container)
        {
            Record("reopen", "сохранённый файл открывается как листовая деталь", RuntimeName(reopened), Verdict.Fail);
            throw new ProbeStop();
        }

        Record("reopen", "сохранённый файл открывается как листовая деталь", "ISheetMetalContainer", Verdict.Pass);
        CheckInt("reopen_bodies_count", 1, container.SheetMetalBodies.Count);
        CheckInt("reopen_bends_count", 1, container.SheetMetalBends.Count);

        var body = BodyAt(container);
        if (body is null)
        {
            Record("reopen_body_read", "тело читается после открытия", "элемент не приводится к ISheetMetalBody", Verdict.Fail);
            return;
        }

        CheckDouble("reopen_thickness", 1.5d, SafeDouble(() => body.Thickness));
        CheckDouble("reopen_radius", 2d, SafeDouble(() => body.Radius));
        CheckBool("reopen_straighten", true, SafeBool(() => body.Straighten));
    }

    // ---------------------------------------------------------------------------------------------
    // API5 side: the sketch and the body edge the documented route needs as inputs
    // ---------------------------------------------------------------------------------------------

    private ksEntity MakeSketch()
    {
        var part = Part5();
        if (part.GetDefaultEntity(PlaneXoy) is not ksEntity plane)
        {
            Record("sketch", "эскиз 40x40 на плоскости XOY создан", "GetDefaultEntity(o3d_planeXOY) → null", Verdict.Fail);
            throw new ProbeStop();
        }

        if (part.NewEntity(ObjSketch) is not ksEntity sketch || sketch.GetDefinition() is not ksSketchDefinition definition)
        {
            Record("sketch", "эскиз 40x40 на плоскости XOY создан", "NewEntity(o3d_sketch) или его определение → null", Verdict.Fail);
            throw new ProbeStop();
        }

        definition.SetPlane(plane);
        sketch.Create();
        if (definition.BeginEdit() is not ksDocument2D editor)
        {
            Record("sketch", "эскиз 40x40 на плоскости XOY создан", "BeginEdit() → не ksDocument2D", Verdict.Fail);
            throw new ProbeStop();
        }

        const double half = 20d;
        editor.ksLineSeg(-half, -half, half, -half, 1);
        editor.ksLineSeg(half, -half, half, half, 1);
        editor.ksLineSeg(half, half, -half, half, 1);
        editor.ksLineSeg(-half, half, -half, -half, 1);
        definition.EndEdit();

        Record("sketch", "эскиз 40x40 на плоскости XOY создан", "4 отрезка", Verdict.Pass);
        return sketch;
    }

    private object FirstEdge()
    {
        var part = Part5();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces)
        {
            Record("edge_source", "ребро тела найдено", "GetMainBody()/FaceCollection() → null", Verdict.Fail);
            throw new ProbeStop();
        }

        for (var f = 0; f < faces.GetCount(); f++)
        {
            var element = faces.GetByIndex(f);
            var face = element as ksFaceDefinition ?? (element as ksEntity)?.GetDefinition() as ksFaceDefinition;
            if (face?.EdgeCollection() is not ksEdgeCollection edges || edges.GetCount() < 1)
            {
                continue;
            }

            Record("edge_source", "ребро тела найдено", "граней " + faces.GetCount() + ", взято ребро 0", Verdict.Pass);
            return edges.GetByIndex(0);
        }

        Record("edge_source", "ребро тела найдено", "ни у одной грани нет рёбер", Verdict.Fail);
        throw new ProbeStop();
    }

    /// <summary>The XOY plane of the part, transferred to API7 — the unfold plane input.</summary>
    private IModelObject? Plane7()
    {
        try
        {
            return Part5().GetDefaultEntity(PlaneXoy) is { } plane
                ? _app5!.TransferInterface(plane, Api7Dual, 0) as IModelObject
                : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>The first face of the body, transferred to API7 — the fixed face the unfold needs.</summary>
    private IModelObject? FirstFace()
    {
        var part = Part5();
        if (part.GetMainBody() is not ksBody body || body.FaceCollection() is not ksFaceCollection faces
            || faces.GetCount() < 1)
        {
            return null;
        }

        try
        {
            return _app5!.TransferInterface(faces.GetByIndex(0), Api7Dual, 0) as IModelObject;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static ISheetMetalBody? BodyAt(ISheetMetalContainer container)
    {
        var collection = container.SheetMetalBodies;
        foreach (var index in new object[] { 1, 0 })
        {
            try
            {
                if (collection.get_SheetMetalBody(index) is { } item)
                {
                    return item as ISheetMetalBody;
                }
            }
            catch (COMException)
            {
                // An out-of-range index is the documented collection answer; the other base is tried.
            }
        }

        return null;
    }

    /// <summary>The API5 part of the document this probe created: <c>GetPart(-1)</c> is the route the
    /// product itself uses, so the probe measures the same object the product would touch.</summary>
    private ksPart Part5() => (ksPart)_doc5!.GetPart(-1);

    private string Gabarit()
    {
        try
        {
            if (_doc5 is null || Part5() is not { } part)
            {
                return "нет API5-детали";
            }

            if (!part.GetGabarit(true, true, out var x1, out var y1, out var z1, out var x2, out var y2, out var z2))
            {
                return "GetGabarit → false";
            }

            return "x " + Num(x1) + ".." + Num(x2) + ", y " + Num(y1) + ".." + Num(y2)
                + ", z " + Num(z1) + ".." + Num(z2);
        }
        catch (COMException ex)
        {
            return Describe(ex);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Shutdown, checks and evidence
    // ---------------------------------------------------------------------------------------------

    public void Dispose()
    {
        if (_app5 is null)
        {
            return;
        }

        try
        {
            _app5.Quit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Quit() refusing is not a measurement: the instance is taken down by its own PID below.
        }

        if (_pid is not int pid)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // Already gone: the normal case.
        }
    }

    private void CheckBool(string id, bool expected, bool? read)
    {
        Record(id, expected ? "true" : "false", read?.ToString() ?? "не прочитано",
            read == expected ? Verdict.Pass : Verdict.Fail);
    }

    private void CheckInt(string id, int expected, int? read)
    {
        Record(id, expected.ToString(CultureInfo.InvariantCulture), read?.ToString(CultureInfo.InvariantCulture) ?? "не прочитано",
            read == expected ? Verdict.Pass : Verdict.Fail);
    }

    private void CheckDouble(string id, double expected, double? read)
    {
        var same = read is { } value && Math.Abs(value - expected) <= 1e-6;
        Record(id, Num(expected), read is null ? "не прочитано" : Num(read.Value), same ? Verdict.Pass : Verdict.Fail);
    }

    private void Record(string id, string expected, string read, Verdict verdict)
    {
        _checks.Add(new Check(id, expected, read, verdict));
        Console.WriteLine($"{verdict,-5} {id,-32} ожидалось: {expected} | прочитано: {read}");
    }

    private void Write()
    {
        var full = Path.GetFullPath(_outPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = new StringBuilder();
        json.Append("{\n");
        json.Append("  \"probe\": \"sheet-metal\",\n");
        json.Append("  \"at\": \"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append("\",\n");
        json.Append("  \"kompas_version\": \"").Append(Escape(_version)).Append("\",\n");
        json.Append("  \"command\": \"dotnet run --project scripts/probes/sheet-metal/SheetMetalProbe.csproj -c Release -- <out.json>\",\n");
        json.Append("  \"summary\": { \"pass\": ").Append(Count(Verdict.Pass))
            .Append(", \"fail\": ").Append(Count(Verdict.Fail))
            .Append(", \"info\": ").Append(Count(Verdict.Info)).Append(" },\n");
        json.Append("  \"checks\": [\n");
        for (var i = 0; i < _checks.Count; i++)
        {
            var check = _checks[i];
            json.Append("    { \"id\": \"").Append(Escape(check.Id))
                .Append("\", \"verdict\": \"").Append(check.Verdict.ToString().ToUpperInvariant())
                .Append("\", \"expected\": \"").Append(Escape(check.Expected))
                .Append("\", \"read\": \"").Append(Escape(check.Read)).Append("\" }")
                .Append(i + 1 < _checks.Count ? ",\n" : "\n");
        }

        json.Append("  ]\n}\n");
        File.WriteAllText(full, json.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine("JSON: " + full);
        Console.WriteLine("PASS " + Count(Verdict.Pass) + " / FAIL " + Count(Verdict.Fail) + " / INFO " + Count(Verdict.Info));
    }

    private int Count(Verdict verdict) => _checks.Count(c => c.Verdict == verdict);

    private static string ReadVersion(KompasObject app)
    {
        try
        {
            int major = 0, minor = 0, build = 0, revision = 0;
            app.ksGetSystemVersion(out major, out minor, out build, out revision);
            return major + "." + minor + "." + build + "." + revision;
        }
        catch (Exception ex) when (ex is COMException or MissingMethodException)
        {
            return "unknown";
        }
    }

    private static int[] KompasProcessIds() =>
        Process.GetProcessesByName("KOMPAS").Select(p => p.Id).ToArray();

    private static string Flag(bool? value) => value is null ? "не прочитано" : value.Value ? "true" : "false";

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string RuntimeName(object? value) =>
        value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;

    private static string Describe(Exception ex) =>
        ex is COMException com ? $"COMException 0x{com.HResult:X8}: {com.Message}" : ex.GetType().Name + ": " + ex.Message;

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    private static bool? SafeBool(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static double? SafeDouble(Func<double> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static object? SafeObject(Func<object?> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private enum Verdict
    {
        Pass,
        Fail,
        Info,
    }

    private sealed record Check(string Id, string Expected, string Read, Verdict Verdict);

    private sealed class ProbeStop : Exception;
}
