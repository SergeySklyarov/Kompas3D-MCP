using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace AscProbe;

/// <summary>ASSEMBLY_CHECKPOINT attempt 3, remarks §1: does a reassigned copy of an assembly keep its
/// components, on route A (<c>ksPart.fileName</c>) and route B (<c>IPart7.FileName</c>)?</summary>
/// <remarks>WHY A PROBE: route B is not reachable through the product, so the two routes can only be
/// compared on raw interop. The probe builds the four settings of the remarks table, copies the graph the
/// way the checkpoint does (one copy per unique file, ordinal-prefixed names), reassigns on one route,
/// saves, closes, reopens from the FILE and counts what the file holds.
/// DOC: .cycle/remarks/ASSEMBLY_CHECKPOINT-2.md §1; ipart7_filename.txt; kspart_setfilename.html.
/// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
internal static class Program
{
    private const int PlaneXoy = 1;
    private const int SketchType = 5;
    private const int BossExtrusionType = 25;

    private static KompasObject App = null!;
    private static string Work = null!;
    private static readonly JsonArray Rows = new();

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ResolveInteropFromInstallation();
        Work = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("scratch", "asc-measure"));
        if (Directory.Exists(Work))
        {
            Directory.Delete(Work, recursive: true);
        }

        Directory.CreateDirectory(Work);
        Launch();
        try
        {
            var plate = MakePart("plate", "plate.m3d", 30, 20, 10);
            var left = MakePart("left", Path.Combine("left", "bracket.m3d"), 30, 20, 10);
            var right = MakePart("right", Path.Combine("right", "bracket.m3d"), 10, 10, 5);

            var settings = new (string Name, string[] Inserts, bool Spread)[]
            {
                ("M1", new[] { plate, plate }, false),
                ("M2", new[] { left, right }, false),
                ("M3", new[] { left, right, right }, false),
                ("M4", new[] { left, right, right }, true),
            };
            foreach (var setting in settings)
            {
                Measure(setting.Name, setting.Inserts, setting.Spread, stepwise: false);
            }

            // The one remaining difference from the product's path: the client's own assembly is OPEN
            // while the copy is reassigned. Measured separately so the cause is named, not guessed.
            Measure("M3-open", new[] { left, right, right }, spread: false, stepwise: false, only: "R",
                keepSourceOpen: true);

            // A row whose FILE holds fewer components than the document is re-measured one replacement
            // at a time, so the loss is pinned to a replacement instead of to "the route".
            foreach (var row in Rows.OfType<JsonObject>().ToArray())
            {
                var file = row["in_file"]?.GetValue<int>() ?? -1;
                var memory = row["in_memory"]?.GetValue<int>() ?? -1;
                if (file >= 0 && memory > file)
                {
                    var name = row["setting"]!.GetValue<string>();
                    var route = row["route"]!.GetValue<string>();
                    var setting = settings.First(s => s.Name == name);
                    Measure($"{name}-stepwise", setting.Inserts, setting.Spread, stepwise: true, only: route);
                }
            }
        }
        finally
        {
            Quit();
        }

        var outPath = Path.Combine(Work, "asc-measure.json");
        File.WriteAllText(outPath, new JsonObject { ["rows"] = Rows }.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        Console.WriteLine("JSON: " + outPath);
        return 0;
    }

    // ── settings ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Build one setting, then measure the copy on both routes.</summary>
    private static void Measure(
        string name, string[] inserts, bool spread, bool stepwise, string? only = null,
        bool keepSourceOpen = false)
    {
        var top = BuildAssembly(name, inserts, spread);
        if (top is null)
        {
            return;
        }

        var sources = inserts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var client = keepSourceOpen ? Open(top) : null;
        try
        {
            foreach (var route in new[] { "A", "B", "R" })
            {
                if (only is not null && route != only)
                {
                    continue;
                }

                MeasureRoute(name, route, top, sources, inserts.Length, stepwise);
            }
        }
        finally
        {
            Close(client, save: false);
        }
    }

    private static string? BuildAssembly(string name, string[] inserts, bool spread)
    {
        var folder = Path.Combine(Work, name);
        Directory.CreateDirectory(folder);
        var top = Path.Combine(folder, "top.m3d");
        ksDocument3D? document = null;
        try
        {
            document = New3D(isPart: false);
            var document7 = ToApi7(document);
            var parts7 = document7?.TopPart?.Parts
                ?? throw new InvalidOperationException("IPart7.Parts недостижим");
            var index = 0;
            foreach (var source in inserts)
            {
                parts7.AddFromFile(source, ExternalFile: true, Redraw: true);
                if (spread)
                {
                    SpreadLastComponent(document, index * 50.0);
                }

                index++;
            }

            if (document.SaveAs(top) != true)
            {
                throw new InvalidOperationException("SaveAs верхней сборки вернул не true");
            }

            Console.WriteLine($"{name}: вставок {inserts.Length}, в памяти {Count(document)}");
            return top;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            Console.WriteLine($"{name}: сборка не построена - {Describe(ex)}");
            return null;
        }
        finally
        {
            Close(document, save: false);
        }
    }

    /// <summary>Move the last inserted component, so the insertions do not share one placement.</summary>
    /// <remarks>DOC: ksPart.GetPlacement/SetPlacement/UpdatePlacement (kspart_setplacement.html) — the
    /// same documented route the product writes placement with.</remarks>
    private static void SpreadLastComponent(ksDocument3D document, double offset)
    {
        var parts = Components(document);
        if (parts.Count == 0)
        {
            return;
        }

        var part = parts[^1];
        if (part.GetPlacement() is not ksPlacement placement)
        {
            return;
        }

        var matrix = new double[16];
        matrix[0] = 1;
        matrix[5] = 1;
        matrix[10] = 1;
        matrix[15] = 1;
        matrix[12] = offset;
        placement.InitByMatrix3D(matrix);
        part.SetPlacement(placement);
        part.UpdatePlacement();
    }

    // ── one route on one setting ─────────────────────────────────────────────────────────────────

    private static void MeasureRoute(
        string name, string route, string top, string[] sources, int expected, bool stepwise)
    {
        var folder = Path.Combine(Work, $"{name}-{route}{(stepwise ? "-stepwise" : string.Empty)}");
        Directory.CreateDirectory(folder);
        var copyTop = Path.Combine(folder, "top.m3d");
        var files = Path.Combine(folder, "files");
        Directory.CreateDirectory(files);
        File.Copy(top, copyTop, overwrite: true);

        // The copy plan of the checkpoint: one file per unique source, ordinal-prefixed name.
        var copyBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ordinal = 0;
        foreach (var source in sources)
        {
            ordinal++;
            var copy = Path.Combine(files, $"{ordinal:D3}-{Path.GetFileName(source)}");
            File.Copy(source, copy, overwrite: true);
            copyBySource[source] = copy;
        }

        ksDocument3D? document = null;
        var steps = new JsonArray();
        try
        {
            document = Open(copyTop);
            if (document is null)
            {
                Record(name, route, stepwise, -1, -1, Array.Empty<string>(), "копия не открылась", steps);
                return;
            }

            while (true)
            {
                var replaced = ReplaceNext(document, route, copyBySource);
                if (replaced is null)
                {
                    break;
                }

                steps.Add(new JsonObject { ["replaced"] = replaced, ["in_memory"] = Count(document) });
                if (stepwise)
                {
                    document.Save();
                    Close(document, save: false);
                    var reopened = Open(copyTop, readOnly: true);
                    var after = reopened is null ? -1 : Count(reopened);
                    steps[^1]!["in_file_after_step"] = after;
                    Close(reopened, save: false);
                    document = Open(copyTop);
                    if (document is null)
                    {
                        Record(name, route, stepwise, -1, after, Array.Empty<string>(),
                            "копия не переоткрылась для продолжения", steps);
                        return;
                    }
                }
            }

            var inMemory = Count(document);
            document.Save();
            Close(document, save: false);
            document = null;

            var verify = Open(copyTop, readOnly: true);
            if (verify is null)
            {
                Record(name, route, stepwise, inMemory, -1, Array.Empty<string>(), "копия не открылась повторно",
                    steps);
                return;
            }

            var inFile = Count(verify);
            var paths = Components(verify).Select(part => part.fileName).ToArray();
            Close(verify, save: false);
            Record(name, route, stepwise, inMemory, inFile, paths, expected == inFile ? null : "потеря", steps);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            Record(name, route, stepwise, -1, -1, Array.Empty<string>(), Describe(ex), steps);
        }
        finally
        {
            Close(document, save: false);
        }
    }

    /// <summary>Point the FIRST component that still names a client file at its copy; <c>null</c> when none.</summary>
    private static string? ReplaceNext(
        ksDocument3D document, string route, Dictionary<string, string> copyBySource)
    {
        if (route is "A" or "R")
        {
            foreach (var part in Components(document))
            {
                var current = part.fileName;
                if (current is not null && copyBySource.TryGetValue(current, out var copy))
                {
                    part.fileName = copy;
                    part.Update();
                    if (route == "R")
                    {
                        // R = A plus the rebuild the product's replace route runs after every replacement.
                        document.RebuildDocument();
                    }

                    return current;
                }
            }

            return null;
        }

        var document7 = ToApi7(document);
        var top7 = document7?.TopPart;
        if (top7 is null)
        {
            return null;
        }

        foreach (var part in Parts7(top7))
        {
            var current = part.FileName;
            if (current is not null && copyBySource.TryGetValue(current, out var copy))
            {
                part.FileName = copy;
                part.Update();
                return current;
            }
        }

        return null;
    }

    /// <summary>Every component of the top part, via the documented <c>IPart7.PartsEx(ksAllParts)</c>.</summary>
    private static IEnumerable<IPart7> Parts7(IPart7 top)
    {
        var raw = top.get_PartsEx((object)(int)ksPart7CollectionTypeEnum.ksAllParts);
        if (raw is not Array array)
        {
            yield break;
        }

        foreach (var item in array)
        {
            if (item is IPart7 part)
            {
                yield return part;
            }
        }
    }

    // ── documents ────────────────────────────────────────────────────────────────────────────────

    private static ksDocument3D New3D(bool isPart)
    {
        var document = (ksDocument3D)App.Document3D();
        if (!document.Create(true, isPart))
        {
            throw new InvalidOperationException("ksDocument3D.Create вернул false");
        }

        return document;
    }

    private static ksDocument3D? Open(string path, bool readOnly = false)
    {
        var document = (ksDocument3D)App.Document3D();
        if (!document.Open(path, true))
        {
            ComApartmentRelease(document);
            return null;
        }

        return document;
    }

    private static void Close(ksDocument3D? document, bool save)
    {
        if (document is null)
        {
            return;
        }

        try
        {
            if (save)
            {
                document.Save();
            }

            if (ToApi7(document) is { } document7)
            {
                document7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            }
            else
            {
                document.close();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.WriteLine("закрытие: " + Describe(ex));
        }
        finally
        {
            ComApartmentRelease(document);
        }
    }

    private static IKompasDocument3D? ToApi7(ksDocument3D document) =>
        App.TransferInterface(document, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D;

    private static List<ksPart> Components(ksDocument3D document)
    {
        var parts = new List<ksPart>();
        if (document.PartCollection(true) is not ksPartCollection collection)
        {
            return parts;
        }

        var count = collection.GetCount();
        for (var index = 0; index < count; index++)
        {
            if (collection.GetByIndex(index) is ksPart part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    private static int Count(ksDocument3D document) => Components(document).Count;

    private static string MakePart(string folder, string relative, double width, double depth, double height)
    {
        var path = Path.Combine(Work, folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ksDocument3D? document = null;
        try
        {
            document = New3D(isPart: true);
            var part = (ksPart)document.GetPart(-1);
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
            editor.ksLineSeg(0, 0, width, 0, 1);
            editor.ksLineSeg(width, 0, width, depth, 1);
            editor.ksLineSeg(width, depth, 0, depth, 1);
            editor.ksLineSeg(0, depth, 0, 0, 1);
            definition.EndEdit();
            var feature = part.NewEntity(BossExtrusionType) as ksEntity
                ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
            var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
                ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
            extrusion.SetSketch(sketch);
            extrusion.SetSideParam(true, 0, height, 0, false);
            feature.Create();
            part.Update();
            document.RebuildDocument();
            if (document.SaveAs(path) != true)
            {
                throw new InvalidOperationException("SaveAs детали вернул не true");
            }

            return path;
        }
        finally
        {
            Close(document, save: false);
        }
    }

    private static void Record(
        string setting, string route, bool stepwise, int inMemory, int inFile, string[] paths, string? note,
        JsonArray steps)
    {
        var row = new JsonObject
        {
            ["setting"] = setting,
            ["route"] = route,
            ["stepwise"] = stepwise,
            ["in_memory"] = inMemory,
            ["in_file"] = inFile,
            ["paths"] = new JsonArray(paths.Select(p => (JsonNode)JsonValue.Create(p ?? "null")!).ToArray()),
            ["note"] = note,
            ["steps"] = steps.DeepClone(),
        };
        Rows.Add(row);
        Console.WriteLine(
            $"{setting,-12} {route} memory={inMemory} file={inFile} note={note ?? "-"} "
            + $"paths=[{string.Join(", ", paths.Select(Path.GetFileName))}]");
    }

    // ── environment ──────────────────────────────────────────────────────────────────────────────

    private static void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false)
            ?? throw new InvalidOperationException("KOMPAS.Application.5 не зарегистрирован");
        App = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        App.Visible = false;
    }

    private static void Quit()
    {
        try
        {
            App.Quit();
        }
        catch (COMException)
        {
            // A quit that fails leaves the instance for the next run's cleanup; nothing is measured after.
        }
    }

    private static void ComApartmentRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void ResolveInteropFromInstallation()
    {
        var directory = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
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
}
