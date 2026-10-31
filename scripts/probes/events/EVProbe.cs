using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;

namespace EVProbe;

/// <summary>Probe for the EVENTS_FINGERPRINT order: does an out-of-process automation client receive the
/// model document's events, and which event answers which action.</summary>
/// <remarks>WHY A PROBE AND NOT A TEST: the product subscribes to nothing yet, so the only thing that can be
/// measured is what KOMPAS itself delivers to a ConnectionPoint subscriber outside the application.
/// DOC: the events pages of the v24 help mirror. The probe and its modes are the order's §2.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
internal sealed class EVProbe : IDisposable
{
    private const double Width = 40;
    private const double Depth = 30;
    private const double Height = 20;
    private const int PlaneXoy = 1;
    private const int SketchType = 5;
    private const int BossExtrusionType = 25;

    // The connection point of ksDocument3DNotify carries the event interface IID (tlbimp source IID).
    private static readonly Guid NotifyIid = new("b6c1bcfd-68da-4a0a-a95c-296084c6a01a");

    private readonly string _outPath;
    private readonly string _docPath;
    private readonly List<string> _events = new();
    private readonly JsonArray _rows = new();
    private KompasObject? _app5;
    private ksDocument3D? _doc5;
    private IConnectionPoint? _point;
    private int _cookie = -1;

    public EVProbe(string outPath, string docPath)
    {
        _outPath = Path.GetFullPath(outPath);
        _docPath = Path.GetFullPath(docPath);
    }

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ResolveInteropFromInstallation();
        if (args.Any(a => a is "--mutate" or "--mutate-norebuild" or "--rename" or "--rename-rebuild"))
        {
            return MutateFromAnotherProcess();
        }

        var outPath = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : Path.Combine("scratch", "probe-events.json");
        var docPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "ev-probe.m3d");
        using var probe = new EVProbe(outPath, docPath);
        return probe.Run();
    }

    private int Run()
    {
        try
        {
            Launch();
            BuildBox();
            Subscribe();
            OwnMutation();
            OwnMutationDrained();
            ExternalMutation();
            ExternalMutationNoRebuild();
            ExternalBlindChange();
            SaveDocument();
            CloseDocument();
        }
        catch (Exception ex)
        {
            Record("probe", "проба дошла до конца", "исключение", "FAIL", ex.ToString());
        }
        finally
        {
            Unsubscribe();
            Write();
            Dispose();
        }

        var fails = _rows.Count(r => (string?)r!["verdict"] == "FAIL");
        Console.WriteLine($"строк {_rows.Count}, FAIL {fails}, события: {_events.Count}");
        return fails == 0 ? 0 : 1;
    }

    // ── environment ─────────────────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
            ?? throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован");
        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        _app5.Visible = false;
        Record("app5", "экземпляр КОМПАС создан", "KompasObject", "PASS");
    }

    private void BuildBox()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_docPath)!);
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, false) вернул false");
        }

        var part = _doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(PlaneXoy) as ksEntity
            ?? throw new InvalidOperationException("плоскость XOY не получена");
        var sketch = part.NewEntity(SketchType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_sketch) → null");
        var definition = sketch.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition не получен");
        definition.SetPlane(plane);
        sketch.Create();
        var editor = definition.BeginEdit() as ksDocument2D
            ?? throw new InvalidOperationException("BeginEdit() → не ksDocument2D");
        editor.ksLineSeg(0, 0, Width, 0, 1);
        editor.ksLineSeg(Width, 0, Width, Depth, 1);
        editor.ksLineSeg(Width, Depth, 0, Depth, 1);
        editor.ksLineSeg(0, Depth, 0, 0, 1);
        definition.EndEdit();

        var feature = part.NewEntity(BossExtrusionType) as ksEntity
            ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(sketch);
        extrusion.SetSideParam(true, 0, Height, 0, false);
        feature.Create();
        part.Update();
        _doc5.RebuildDocument();

        // A second client sees the document as "active" only if KOMPAS marked it so: without this the child
        // process gets an empty ActiveDocument3D and the whole external half of the table measures nothing.
        try
        {
            Record("active", "документ назначен активным (ksDocument3D.SetActive)", _doc5.SetActive().ToString(),
                "PASS");
        }
        catch (Exception ex)
        {
            Record("active", "документ назначен активным (ksDocument3D.SetActive)", ex.Message, "FAIL");
        }

        var saved = _doc5.SaveAs(_docPath);
        Record("fixture", $"брусок {Width}x{Depth}x{Height} построен и сохранён",
            $"{_docPath}, SaveAs={saved}", saved ? "PASS" : "FAIL");
    }

    private void Subscribe()
    {
        var notify = _doc5!.GetDocument3DNotify();
        Record("source", "ksDocument3D.GetDocument3DNotify()", notify is null ? "null" : "объект",
            notify is null ? "FAIL" : "PASS");
        if (notify is null)
        {
            return;
        }

        var container = notify as IConnectionPointContainer;
        if (container is null)
        {
            Record("container", "источник событий приводит к IConnectionPointContainer", "приведение не удалось",
                "FAIL");
            return;
        }

        var iid = NotifyIid;
        try
        {
            container.FindConnectionPoint(ref iid, out _point);
            Record("find", "FindConnectionPoint(IID ksDocument3DNotify)", "точка найдена", "PASS");
        }
        catch (Exception ex)
        {
            Record("find", "FindConnectionPoint(IID ksDocument3DNotify)", ex.Message, "FAIL");
            return;
        }

        try
        {
            _point.Advise(new EvSink(_events), out _cookie);
            Record("advise", "IConnectionPoint.Advise(приёмник)", $"cookie={_cookie}", "PASS");
        }
        catch (Exception ex)
        {
            Record("advise", "IConnectionPoint.Advise(приёмник)", ex.Message, "FAIL");
        }
    }

    private void Unsubscribe()
    {
        if (_point is null || _cookie < 0)
        {
            return;
        }

        try
        {
            _point.Unadvise(_cookie);
            Record("unadvise", "IConnectionPoint.Unadvise", $"cookie={_cookie}", "PASS");
        }
        catch (Exception ex)
        {
            Record("unadvise", "IConnectionPoint.Unadvise", ex.Message, "FAIL");
        }

        _cookie = -1;
    }

    // ── measured actions ────────────────────────────────────────────────────────────────────────

    private void OwnMutation()
    {
        var before = _events.Count;
        ChangeDepth(Height + 5);
        Pump(3000);
        Record("own.mutation", "мутация из ЭТОГО процесса (глубина +5, Update + Rebuild)",
            string.Join(", ", _events.Skip(before)) is { Length: > 0 } text ? text : "событий нет", "MEASURED");
    }

    /// <summary>§1 п.4: is our own event delivered inside a short drain right after the mutation, or later?</summary>
    /// <remarks>MEASURED: this decides whether "own" can be suppressed by draining the queue with a flag on, or
    /// whether the mark has to carry a fingerprint comparison.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private void OwnMutationDrained()
    {
        var before = _events.Count;
        ChangeDepth(Height + 2);
        Pump(400);
        var inDrain = _events.Count - before;
        Pump(2500);
        var later = _events.Count - before - inDrain;
        Record("own.drain", "своя мутация, затем слив очереди 400 мс и ожидание ещё 2500 мс",
            $"за сливом: {inDrain}, позже: {later}; всего: {string.Join(", ", _events.Skip(before))}",
            "MEASURED");
    }

    private void ExternalMutation() => RunChild("--mutate", "external.mutation",
        "мутация из ДРУГОГО процесса (тот же экземпляр, глубина +10, Update + Rebuild)");

    /// <summary>§2: does the event come from the rebuild itself or from the parameter change?</summary>
    /// <remarks>MEASURED: this decides how "our own mutation" can be told apart from a foreign one - if only an
    /// explicit rebuild raises the event, the mark must not be tied to the model change alone.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private void ExternalMutationNoRebuild() => RunChild("--mutate-norebuild", "external.norebuild",
        "мутация из ДРУГОГО процесса без явного RebuildDocument (глубина +1, только Update)");

    /// <summary>§2: a change the fingerprint cannot see - the document's part name, not its geometry.</summary>
    /// <remarks>MEASURED: the order needs this case for the positive control of §4: if it raises no event either,
    /// then events alone cannot catch it and the row has to be re-thought.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private void ExternalBlindChange() => RunChild("--rename", "external.blind",
        "изменение, невидимое отпечатку (имя детали через ksPart.name, геометрия не тронута)");

    private void RunChild(string mode, string id, string title)
    {
        var before = _events.Count;
        var start = DateTime.Now;
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(_docPath);
        using var child = Process.Start(info);
        Pump(15000, child);
        var output = child?.StandardOutput.ReadToEnd().Trim() ?? "";
        var error = child?.StandardError.ReadToEnd().Trim() ?? "";
        Record(id, title,
            $"{(string.Join(", ", _events.Skip(before)) is { Length: > 0 } text ? text : "событий нет")}"
            + $" | {(DateTime.Now - start).TotalSeconds:0.0} с | выход: {output} {error}".TrimEnd(), "MEASURED");
    }

    private void SaveDocument()
    {
        var before = _events.Count;
        var saved = _doc5!.SaveAs(_docPath);
        Pump(2000);
        Record("save", "сохранение документа из этого процесса", $"SaveAs={saved}; "
            + $"{(string.Join(", ", _events.Skip(before)) is { Length: > 0 } text ? text : "событий нет")}",
            "MEASURED");
    }

    private void CloseDocument()
    {
        var before = _events.Count;
        var closed = _doc5!.close();
        Pump(2000);
        Record("close", "закрытие документа из этого процесса", $"Close={closed}; "
            + $"{(string.Join(", ", _events.Skip(before)) is { Length: > 0 } text ? text : "событий нет")}",
            "MEASURED");
    }

    private void ChangeDepth(double height)
    {
        var part = _doc5!.GetPart(-1) as ksPart
            ?? throw new InvalidOperationException("GetPart(-1) → null");
        if (FirstExtrusion(part) is not { } item)
        {
            Record("own.mutation", "первое выдавливание найдено", "не найдено", "FAIL");
            return;
        }

        var extrusion = item.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSideParam(true, 0, height, 0, false);
        item.Update();
        part.Update();
        _doc5.RebuildDocument();
    }

    /// <summary>First boss extrusion of the part, through the entity collection KOMPAS documents.</summary>
    /// <remarks>Late binding on purpose: the probe only needs "the one extrusion the fixture built", and a
    /// typed route would add a compile-time dependency for no measurement.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private static ksEntity? FirstExtrusion(ksPart part)
    {
        object raw = part.EntityCollection((short)BossExtrusionType);
        if (raw is null)
        {
            return null;
        }

        dynamic collection = raw;
        return collection.GetCount() == 0 ? null : collection.GetByIndex(0) as ksEntity;
    }

    /// <summary>Child process: attaches to the running instance and changes the same document.</summary>
    /// <remarks>MEASURED: a second client is what "external change" means for the worker - it is itself an
    /// automation client outside KOMPAS. History: docs/decisions/adapter-core.md#document-events</remarks>
    private static int MutateFromAnotherProcess()
    {
        var args = Environment.GetCommandLineArgs();
        var path = args.Last();
        var mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal)) ?? "--mutate";
        try
        {
            var app = Attach();
            var doc = app.ActiveDocument3D() as ksDocument3D;
            if (doc is null)
            {
                Console.Write("нет активного документа-модели");
                return 2;
            }

            var part = doc.GetPart(-1) as ksPart;
            if (part is null)
            {
                Console.Write("деталь не получена");
                return 3;
            }

            if (mode is "--rename" or "--rename-rebuild")
            {
                part.name = "деталь переименована извне";
                if (mode == "--rename-rebuild")
                {
                    // The rename alone changes no geometry; the rebuild is what makes the model react.
                    doc.RebuildDocument();
                }

                Console.Write($"имя детали изменено, перестроение={mode == "--rename-rebuild"} "
                              + $"({Path.GetFileName(path)})");
                return 0;
            }

            var item = FirstExtrusion(part);
            if (item?.GetDefinition() is not ksBossExtrusionDefinition extrusion)
            {
                Console.Write("выдавливание не найдено");
                return 4;
            }

            var height = mode == "--mutate-norebuild" ? Height + 1 : Height + 10;
            extrusion.SetSideParam(true, 0, height, 0, false);
            item.Update();
            part.Update();
            if (mode != "--mutate-norebuild")
            {
                doc.RebuildDocument();
            }

            Console.Write($"глубина изменена на {height} ({Path.GetFileName(path)})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Write(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    /// <summary>Binds the already running instance through the running object table.</summary>
    /// <remarks>INVARIANT: only an existing instance is used - <c>new KompasObject()</c> would start a second
    /// one and the measurement would be about two documents instead of one.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private static KompasObject Attach()
    {
        CreateBindCtx(0, out var bindCtx);
        GetRunningObjectTable(0, out var rot);
        KompasObject? fallback = null;
        rot.EnumRunning(out var enumerator);
        var monikers = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
        while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
        {
            if (monikers[0] is null)
            {
                continue;
            }

            string name;
            try
            {
                monikers[0].GetDisplayName(bindCtx, null, out name);
            }
            catch
            {
                continue;
            }

            if (name.Contains("6B0B5194", StringComparison.OrdinalIgnoreCase)
                || name.Contains("kompas", StringComparison.OrdinalIgnoreCase))
            {
                rot.GetObject(monikers[0], out var bound);
                if (bound is not KompasObject app)
                {
                    continue;
                }

                // Several instances can sit in the table (a previous probe leaves one running). Prefer the one
                // holding an open model document; an invisible document may not report as active, so the
                // first bound instance stays the fallback.
                if (app.ActiveDocument3D() is not null)
                {
                    return app;
                }

                fallback ??= app;
            }
        }

        return fallback ?? throw new InvalidOperationException("экземпляр КОМПАС в ROT не найден");
    }

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ppbc);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved,
        out System.Runtime.InteropServices.ComTypes.IRunningObjectTable prot);

    // ── message pump ────────────────────────────────────────────────────────────────────────────

    private const int PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    /// <summary>Pumps the STA queue for the given time - the pump is what delivers the callbacks.</summary>
    /// <remarks>DOC: eventscommon.html - the Automation route is ConnectionPoint, and the subscriber must run
    /// a message loop for the server to call it. History: docs/decisions/adapter-core.md#document-events</remarks>
    private static void Pump(int milliseconds, Process? child = null)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (child is { HasExited: true })
            {
                break;
            }

            Thread.Sleep(15);
        }

        // Drain whatever arrived while the child was finishing.
        for (var i = 0; i < 40; i++)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            Thread.Sleep(10);
        }
    }

    // ── evidence ────────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string check, string observed, string verdict, string? note = null)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["check"] = check,
            ["observed"] = observed,
            ["verdict"] = verdict,
            ["note"] = note,
        });
    }

    private void Write()
    {
        var document = new JsonObject
        {
            ["probe"] = "events",
            ["at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["doc"] = _docPath,
            ["iid"] = NotifyIid.ToString("D"),
            ["events"] = new JsonArray(_events.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            ["rows"] = _rows.DeepClone(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_outPath)!);
        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Console.WriteLine("записано: " + _outPath);
    }

    /// <summary>Loads the vendor interop from the installation: the project references it with
    /// <c>Private=false</c>, so nothing is copied next to the probe.</summary>
    private static void ResolveInteropFromInstallation()
    {
        var directory = typeof(EVProbe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "KompasInteropDir")?.Value;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name;
            var candidate = Path.Combine(directory, name + ".dll");
            return name is not null && File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };
    }

    public void Dispose()
    {
        if (_doc5 is not null)
        {
            Marshal.ReleaseComObject(_doc5);
        }

        if (_app5 is not null)
        {
            // Leaving the application behind would put a second instance into the ROT and make the next
            // run attach to a stale one.
            try
            {
                ((dynamic)_app5).Exit();
            }
            catch (Exception)
            {
                // The measurement is already written; a refused exit changes nothing it reported.
            }

            Marshal.ReleaseComObject(_app5);
        }
    }
}
