using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using Kompas6Constants3D;
using KompasAPI7;

namespace AssemblyExtProbe;

/// <summary>Assembly-extension probe: component arrays, mate types beyond the minimal profile, and
/// nested components. Only KOMPAS's own answers on DOCUMENTED routes are measured.</summary>
/// <remarks>WHY A PROBE: the order asks which documented routes actually build, and what they read back
/// as. DOC: ASSEMBLY_EXT_RESEARCH_DEVELOPER_PROMPT.md §4; help snapshot under
/// reference/kompas-sdk-v24/docs-wire-...-assembly/. History: docs/decisions/assembly-ext.md</remarks>
internal sealed partial class AssemblyExtProbe : IDisposable
{
    private const double PartW = 20;
    private const double PartD = 10;
    private const double PartH = 5;

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonObject _facts = new();
    private KompasObject? _app5;
    private IApplication? _app7;
    private ksDocument3D? _doc5;
    private IKompasDocument3D? _doc7;
    private string _work = "";

    public AssemblyExtProbe(string outPath)
    {
        _outPath = Path.GetFullPath(outPath);
        _work = Path.Combine(Path.GetDirectoryName(_outPath)!, "assembly-ext-fixture");
    }

    public int Run(string? only, bool keepOpen)
    {
        try
        {
            Launch();
            Directory.CreateDirectory(_work);
            if (only is null or "array")
            {
                ArrayScenario();
            }

            if (only is null or "mates")
            {
                MateScenario();
            }

            if (only is null or "nested")
            {
                NestedScenario();
            }

            if (only is null or "nested-address")
            {
                NestedAddressScenario();
            }

            if (only is null or "path-pattern")
            {
                PathPatternScenario();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
                                       or NotSupportedException)
        {
            Record("probe", "проба дошла до конца", $"{ex.GetType().Name}: {ex.Message}", "FAIL");
        }

        Write();
        if (!keepOpen)
        {
            CloseCurrent();
        }

        return _rows.Any(row => row?["verdict"]?.GetValue<string>() == "FAIL") ? 1 : 0;
    }

    // ── environment ───────────────────────────────────────────────────────────────────────────

    private void Launch()
    {
        var type = Type.GetTypeFromProgID("KOMPAS.Application.5", throwOnError: false);
        if (type is null)
        {
            throw new InvalidOperationException("ProgID KOMPAS.Application.5 не зарегистрирован");
        }

        _app5 = Activator.CreateInstance(type) as KompasObject
            ?? throw new InvalidOperationException("KompasObject недостижим");
        _app5.Visible = false;
        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app", "экземпляр КОМПАС и IApplication получены",
            _app7 is null ? "IApplication = null" : "KompasObject + IApplication",
            _app7 is null ? "FAIL" : "PASS");
    }

    /// <summary>Creates a fresh document of the requested kind (part or assembly) and opens its API7 view.
    /// The document is VISIBLE on purpose: the nested scenario measures a route whose help page limits it
    /// to a visual document, and a hidden fixture could not tell the limit from the route.</summary>
    private (ksDocument3D Doc5, IKompasDocument3D Doc7, IPart7 Top7) NewDocument(bool isPart)
    {
        CloseCurrent();
        _doc5 = (ksDocument3D)_app5!.Document3D();
        if (!_doc5.Create(false, isPart))
        {
            throw new InvalidOperationException($"ksDocument3D.Create(false, {isPart}) вернул false");
        }

        _doc7 = _app5.TransferInterface(_doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument3D
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument3D");
        var top = _doc7.TopPart as IPart7
            ?? throw new InvalidOperationException("IKompasDocument3D.TopPart не даёт IPart7");
        return (_doc5, _doc7, top);
    }

    /// <summary>Part file with one box W×D×H on XOY, saved to <paramref name="path"/>. The length along X is
    /// given by the caller where a longer edge is needed as a trajectory.</summary>
    private string PartFile(string path, double h, double length = PartW)
    {
        var (doc5, _doc7Local, _) = NewDocument(isPart: true);
        var part = doc5.GetPart(-1) as ksPart ?? throw new InvalidOperationException("GetPart(-1) → null");
        var plane = part.GetDefaultEntity(1) as ksEntity ?? throw new InvalidOperationException("плоскость XOY не получена");

        var sketch = part.NewEntity(5) as ksEntity ?? throw new InvalidOperationException("NewEntity(o3d_sketch) → null");
        var definition = sketch.GetDefinition() as ksSketchDefinition
            ?? throw new InvalidOperationException("ksSketchDefinition не получен");
        definition.SetPlane(plane);
        sketch.Create();
        var editor = definition.BeginEdit() as ksDocument2D ?? throw new InvalidOperationException("BeginEdit → не ksDocument2D");
        editor.ksLineSeg(0, 0, length, 0, 1);
        editor.ksLineSeg(length, 0, length, PartD, 1);
        editor.ksLineSeg(length, PartD, 0, PartD, 1);
        editor.ksLineSeg(0, PartD, 0, 0, 1);
        definition.EndEdit();

        var feature = part.NewEntity(25) as ksEntity ?? throw new InvalidOperationException("NewEntity(o3d_bossExtrusion) → null");
        var extrusion = feature.GetDefinition() as ksBossExtrusionDefinition
            ?? throw new InvalidOperationException("ksBossExtrusionDefinition не получен");
        extrusion.SetSketch(sketch);
        if (!extrusion.SetSideParam(true, 0, h, 0, false))
        {
            throw new InvalidOperationException("SetSideParam вернул false");
        }

        feature.Create();
        part.Update();
        doc5.RebuildDocument();
        if (!doc5.SaveAs(path))
        {
            throw new InvalidOperationException($"SaveAs({path}) вернул false");
        }

        return path;
    }

    /// <summary>Inserts a component from a file through the documented <c>IParts7.AddFromFile</c>.</summary>
    private IPart7 Insert(IPart7 top, string path, double[]? matrix)
    {
        var parts = top.Parts ?? throw new InvalidOperationException("IPart7.Parts → null");
        var inserted = parts.AddFromFile(path, true, true)
            ?? throw new InvalidOperationException($"AddFromFile({Path.GetFileName(path)}) → null");
        if (matrix is not null)
        {
            if (inserted.Placement is not { } placement || !placement.InitByMatrix3D(ToVariant(matrix)))
            {
                Record("insert_placement", $"размещение {Path.GetFileName(path)} при вставке",
                    "IPlacement3D.InitByMatrix3D вернул false", "FAIL");
            }

            inserted.UpdatePlacement(false);
            top.RebuildModel(false);
        }

        return inserted;
    }

    private static double[] Place(double x, double y, double z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    private static object ToVariant(double[] matrix)
    {
        var copy = new double[matrix.Length];
        Array.Copy(matrix, copy, matrix.Length);
        return copy;
    }

    private static double[]? Matrix16(object? variant)
    {
        if (variant is string)
        {
            variant = null;
        }

        if (variant is Array array && array.Length >= 16)
        {
            var result = new double[16];
            for (var i = 0; i < 16; i++)
            {
                result[i] = Convert.ToDouble(array.GetValue(i), CultureInfo.InvariantCulture);
            }

            return result;
        }

        return null;
    }

    private static string Origin(double[]? matrix) =>
        matrix is null ? "не читается" : $"({matrix[12]:0.###}, {matrix[13]:0.###}, {matrix[14]:0.###})";

    /// <remarks>MEASURED: the shipped wrapper declares <c>GetSummMatrix(Part7)</c>, not <c>IPart7</c>,
    /// while the help page names <c>IPart7</c>; a component obtained from <c>IParts7</c> IS a
    /// <c>Part7</c>, so the cast is the wrapper's own type, not a guess about the model.</remarks>
    private double[]? ComponentMatrix(IPart7 parent, IPart7 child) =>
        child is Part7 part7 ? Matrix16(parent.GetSummMatrix(part7)) : null;

    private ksPart? Part5At(int index)
    {
        var collection = _doc5?.PartCollection(true) as ksPartCollection;
        return collection?.GetByIndex(index) as ksPart;
    }

    private int Part5Count() => (_doc5?.PartCollection(true) as ksPartCollection)?.GetCount() ?? -1;

    private static double[]? PlacementOf(ksPart part)
    {
        if (part.GetPlacement() is not ksPlacement placement || !placement.GetMatrix3D(out var raw))
        {
            return null;
        }

        return Matrix16(raw);
    }

    // ── recording ─────────────────────────────────────────────────────────────────────────────

    private void Record(string id, string label, string observed, string verdict, string? expected = null)
    {
        _rows.Add(new JsonObject
        {
            ["id"] = id,
            ["label"] = label,
            ["expected"] = expected,
            ["observed"] = observed,
            ["verdict"] = verdict,
        });
        Console.WriteLine($"{verdict,-6} {id,-34} {label} | {observed}");
    }

    private void Fact(string key, object? value) => _facts[key] = JsonValue.Create(value?.ToString());

    private void Write()
    {
        var document = new JsonObject
        {
            ["probe"] = "ASSEMBLY_EXT_RESEARCH",
            ["fixture"] = $"брусок {PartW} x {PartD} мм, высота задаётся",
            ["facts"] = _facts,
            ["rows"] = _rows,
        };
        File.WriteAllText(_outPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"--- записано: {_outPath}");
    }

    private void CloseCurrent()
    {
        try
        {
            _doc7?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
        catch (COMException)
        {
            // The document may already be gone; nothing to close is not a measurement.
        }

        _doc7 = null;
        _doc5 = null;
    }

    public void Dispose()
    {
        CloseCurrent();
        try
        {
            _app5?.Quit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // The probe measures documents, not shutdown.
        }
    }
}
