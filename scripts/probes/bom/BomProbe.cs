using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace BomProbe;

/// <summary>BOM probe: builds an assembly with the cases §1.11 names, then reads it back through the
/// documented routes and compares every number with what the fixture was built from.</summary>
/// <remarks>WHY A PROBE AND NOT A TEST: the order is research — the product has no BOM tool yet, so the
/// only thing that can be measured is what KOMPAS itself answers on the documented routes.
/// DOC: BOM_RESEARCH_DEVELOPER_PROMPT.md §1.5. History: docs/decisions/bom.md#probe</remarks>
internal sealed class BomProbe : IDisposable
{
    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _reads = new();
    private KompasObject? _app5;
    private IApplication? _app7;
    private string _fixtureDir = string.Empty;

    public BomProbe(string outPath)
    {
        _outPath = Path.GetFullPath(outPath);
    }

    public int Run()
    {
        try
        {
            Launch();
            BuildFixture();
            var built = ReadAssembly("built");
            ProbeSpecificationRoute();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            Record("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }
        finally
        {
            Write();
        }

        return _rows.Any(r => r?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    /// <summary>Second half of the save→reopen check, run as its OWN process: KOMPAS refuses to open a
    /// path that is still loaded, and a closed document stays loaded while any reference to it lives.
    /// A separate process has no such reference, so the numbers it reads are the numbers on disk.</summary>
    public int RunVerify(string assemblyPath)
    {
        try
        {
            Launch();
            _fixtureDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath)) ?? string.Empty;
            var document = (ksDocument3D)_app5!.Document3D();
            var opened = document.Open(Path.GetFullPath(assemblyPath), true);
            Record("reopened_open", "Open() переоткрывает сохранённую сборку", opened ? "true" : "false",
                opened ? "PASS" : "FAIL");
            if (opened)
            {
                ReadAssembly("reopened");
                ProbeSpecificationRoute();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            Record("probe", "проба дошла до конца", Describe(ex), "FAIL");
        }
        finally
        {
            Write();
        }

        return _rows.Any(r => r?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    // ── environment ─────────────────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            Record("app5_progid", "ProgID KOMPAS.Application.5 зарегистрирован", "не зарегистрирован", "FAIL");
            throw new InvalidOperationException("KOMPAS is not registered");
        }

        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject is not reachable");
        _app5.Visible = false;
        Record("app5", "экземпляр КОМПАС создан", "KompasObject", "PASS");

        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app7", "ksGetApplication7() даёт IApplication", _app7 is null ? "null" : "IApplication",
            _app7 is null ? "FAIL" : "PASS");
        if (_app7 is null)
        {
            throw new InvalidOperationException("API7 is not reachable");
        }
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the fixture §1.5 asks for: repeated instances, a subassembly, two different files
    /// with the SAME file name, and a sheet-metal part from the previous block.</summary>
    private void BuildFixture()
    {
        var root = RepoRoot();
        _fixtureDir = Path.Combine(root, "scratch", "bom-probe", "fixture");
        if (Directory.Exists(_fixtureDir))
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }

        var dirA = Path.Combine(_fixtureDir, "a");
        var dirB = Path.Combine(_fixtureDir, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // Two DIFFERENT sources saved under the SAME file name — the case §1.11 forbids merging.
        File.Copy(Path.Combine(root, "docs", "acceptance", "evidence", "20261024-sheetmetal-dxf-probe-a397fba",
            "sheet-metal-dxf.m3d"), Path.Combine(dirA, "part.m3d"), overwrite: true);
        File.Copy(Path.Combine(root, "docs", "acceptance", "evidence", "20261015-hole-false-success-ca9d444",
            "defect", "Stress_Plate_Holes_SideFailure.m3d"), Path.Combine(dirB, "part.m3d"), overwrite: true);

        var partA = Path.Combine(dirA, "part.m3d");
        var partB = Path.Combine(dirB, "part.m3d");
        var subPath = Path.Combine(_fixtureDir, "sub.m3d");
        var asmPath = Path.Combine(_fixtureDir, "asm.m3d");

        // Subassembly: two instances of the same part file.
        var sub = NewAssembly("Сборочная единица", "SUB-1");
        Insert(sub, partA, 2);
        Save(sub, subPath);
        Close(sub);

        // Main assembly: 3 x A, 1 x B (same file name, other file), 2 x subassembly.
        var asm = NewAssembly("Сборка BOM", "ASM-1");
        Insert(asm, partA, 3);
        Insert(asm, partB, 1);
        Insert(asm, subPath, 2);
        Save(asm, asmPath);

        Record("fixture", "сборка собрана: 3 x a/part.m3d + 1 x b/part.m3d + 2 x sub.m3d",
            $"{asmPath}; sub={subPath}", "PASS");
    }

    private (ksDocument3D Doc5, IKompasDocument3D Doc7) NewAssembly(string name, string marking)
    {
        var doc5 = (ksDocument3D)_app5!.Document3D();
        if (!doc5.Create(true, false))
        {
            throw new InvalidOperationException("ksDocument3D.Create(true, isDetail=false) вернул false");
        }

        if (doc5.GetPart(-1) is ksPart top)
        {
            top.name = name;
            top.marking = marking;
            top.Update();
        }

        doc5.RebuildDocument();
        var doc7 = _app5.TransferInterface(doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");
        return (doc5, doc7);
    }

    /// <summary>Inserts one component from a file per call — the documented <c>IParts7.AddFromFile</c>
    /// route the product itself uses, so the fixture is built the same way a client would build it.</summary>
    private void Insert((ksDocument3D Doc5, IKompasDocument3D Doc7) document, string path, int times)
    {
        var parts7 = document.Doc7.TopPart?.Parts
            ?? throw new InvalidOperationException("IPart7.Parts не вернул IParts7");
        for (var index = 0; index < times; index++)
        {
            var inserted = parts7.AddFromFile(path, ExternalFile: true, Redraw: true);
            Record("insert", $"AddFromFile({Path.GetFileName(path)}) #{index + 1}",
                inserted is null ? "null" : "Part7", inserted is null ? "FAIL" : "PASS");
        }
    }

    private void Save((ksDocument3D Doc5, IKompasDocument3D Doc7) document, string path)
    {
        var ok = document.Doc5.SaveAs(path);
        Record("save", $"SaveAs({Path.GetFileName(path)})", ok ? "true" : "false", ok ? "PASS" : "FAIL");
    }

    private void Close((ksDocument3D Doc5, IKompasDocument3D Doc7) document)
    {
        var ok = document.Doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        Record("close", "IKompasDocument.Close(kdDoNotSaveChanges)", ok ? "true" : "false",
            ok ? "PASS" : "FAIL");
    }

    // ── reading ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the assembly through the documented API5 component collection and the documented
    /// <c>ksPart</c> properties, then compares the numbers with the fixture it was built from.</summary>
    private JsonObject ReadAssembly(string label)
    {
        var doc5 = (ksDocument3D)_app5!.ActiveDocument3D();
        var components = new JsonArray();
        var parts = Components(doc5);
        foreach (var part in parts)
        {
            components.Add(Describe(part));
        }

        var byFile = parts
            .GroupBy(p => Safe(() => p.fileName) ?? "<нет>", StringComparer.OrdinalIgnoreCase)
            .Select(g => new { file = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .ToList();
        var byName = parts
            .GroupBy(p => Path.GetFileName(Safe(() => p.fileName) ?? "<нет>"), StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Key, count = g.Count() })
            .OrderByDescending(g => g.count)
            .ToList();

        var nested = SubassemblyChildren();

        Record($"{label}_top_count", "число экземпляров верхнего уровня = 6", parts.Count.ToString(CultureInfo.InvariantCulture),
            parts.Count == 6 ? "PASS" : "FAIL");
        Record($"{label}_unique_files", "уникальных файлов = 3 (a/part.m3d, b/part.m3d, sub.m3d)",
            byFile.Count.ToString(CultureInfo.InvariantCulture), byFile.Count == 3 ? "PASS" : "FAIL");
        Record($"{label}_unique_names", "уникальных имён файлов = 2 (part.m3d, sub.m3d)",
            byName.Count.ToString(CultureInfo.InvariantCulture), byName.Count == 2 ? "PASS" : "FAIL");
        Record($"{label}_nested", "подсборка как отдельный документ даёт 2 экземпляра",
            nested.ToString(CultureInfo.InvariantCulture), nested == 2 ? "PASS" : "FAIL");

        var result = new JsonObject
        {
            ["label"] = label,
            ["top_count"] = parts.Count,
            ["components"] = components,
            ["by_file"] = new JsonArray(byFile.Select(g => (JsonNode)new JsonObject
            {
                ["file"] = g.file,
                ["count"] = g.count,
            }).ToArray()),
            ["by_name"] = new JsonArray(byName.Select(g => (JsonNode)new JsonObject
            {
                ["name"] = g.name,
                ["count"] = g.count,
            }).ToArray()),
            ["nested_children"] = nested,
        };
        _reads.Add(result);
        return result;
    }

    /// <summary>Recursion in API5 has no child collection on <c>ksPart</c> (its documented members are
    /// properties plus bodies, not components), so a nested level is only reachable by reading the
    /// subassembly as its own document. The number is what the fixture put there: 2.</summary>
    private int SubassemblyChildren()
    {
        var subPath = Path.Combine(_fixtureDir, "sub.m3d");
        var document = (ksDocument3D)_app5!.Document3D();
        if (!document.Open(subPath, true))
        {
            Record("nested_open", "подсборка открывается как документ", "false", "FAIL");
            return -1;
        }

        var count = Components(document).Count;
        Record("nested_open", "подсборка открывается как документ", "true", "PASS");
        return count;
    }

    /// <summary>Does the documented API5 specification route answer on a model at all? The probe records
    /// what came back instead of assuming it: an empty specification and a refusal look the same in a
    /// summary and are not the same fact.</summary>
    private void ProbeSpecificationRoute()
    {
        var doc5 = (ksDocument3D)_app5!.ActiveDocument3D();
        try
        {
            var specification = doc5.GetSpecification();
            var typed = specification as ksSpecification;
            Record("specification_route", "ksDocument3D.GetSpecification() на модели приводится к ksSpecification",
                typed is null ? "не приводится" : "ksSpecification",
                typed is null ? "INFO" : "PASS");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record("specification_route", "ksDocument3D.GetSpecification() на модели", Describe(ex), "INFO");
        }
    }

    private static List<ksPart> Components(ksDocument3D document) => Components(document.PartCollection(true));

    private static List<ksPart> Components(object? collection)
    {
        var parts = new List<ksPart>();
        if (collection is not ksPartCollection typed)
        {
            return parts;
        }

        var count = typed.GetCount();
        for (var index = 0; index < count; index++)
        {
            if (typed.GetByIndex(index) is ksPart part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    private static JsonObject Describe(ksPart part) => new()
    {
        ["file"] = Safe(() => part.fileName),
        ["name"] = Safe(() => part.name),
        ["marking"] = Safe(() => part.marking),
        ["material"] = Safe(() => part.material),
        ["hidden"] = Safe(() => part.hidden),
        ["excluded"] = Safe(() => part.excluded),
        ["standard"] = Safe(() => part.standardComponent),
        ["multi_body"] = Safe(() => part.MultiBodyParts),
        ["property_editable"] = Safe(() => part.PropertyObjectEditable),
    };

    private static string? Safe(Func<object?> read)
    {
        try
        {
            var value = read();
            return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return "<" + Describe(ex) + ">";
        }
    }

    // ── output ──────────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string expected, string actual, string verdict)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["expected"] = expected,
            ["actual"] = actual,
            ["verdict"] = verdict,
        });
        Console.WriteLine($"{verdict,-4} {id}: {actual}");
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_outPath)!);
        var document = new JsonObject
        {
            ["probe"] = "bom",
            ["measured_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["fixture_dir"] = _fixtureDir,
            ["rows"] = _rows,
            ["reads"] = _reads,
            ["counts"] = new JsonObject
            {
                ["pass"] = _rows.Count(r => r?["verdict"]?.GetValue<string>() == "PASS"),
                ["fail"] = _rows.Count(r => r?["verdict"]?.GetValue<string>() == "FAIL"),
                ["info"] = _rows.Count(r => r?["verdict"]?.GetValue<string>() == "INFO"),
            },
        };
        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report: {_outPath}");
    }

    private static string RuntimeName(object value)
    {
        try
        {
            return Type.GetTypeFromHandle(Type.GetTypeHandle(value))?.Name ?? value.GetType().Name;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return "interface";
        }
    }

    private static string Describe(Exception ex) => ex is COMException com
        ? $"COM 0x{com.HResult:X8}: {com.Message}"
        : ex.Message;

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KompasMcp.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        try
        {
            _app5?.Quit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // A probe that cannot quit KOMPAS still has its measurements: the caller kills the process.
        }

        Marshal.FinalReleaseComObject(_app5!);
    }
}
