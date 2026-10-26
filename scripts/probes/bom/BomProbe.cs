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
    // INVARIANT: hidden is TRUE exactly at instance 1 and excluded exactly at instance 2 (collection
    // order, see MarkFilterFlags), FALSE at the other four — "the flag reads" and "the flag sits where
    // it was put" are different facts.
    private const string ExpectedHidden = "0:False,1:True,2:False,3:False,4:False,5:False";
    private const string ExpectedExcluded = "0:False,1:False,2:True,3:False,4:False,5:False";

    // The specification the fixture writes and then reads back: three objects with known marking, name,
    // quantity and position. Known values are the whole point — a read that returns "something" proves
    // nothing. The section is NOT among them: its number belongs to the style and is read from the
    // document (see WriteSpecification).
    private static readonly (string Marking, string Name, string Quantity, int Position)[] SpecObjects =
    [
        ("ABV.301.001", "Пластина", "3", 1),
        ("ABV.301.002", "Плита", "2", 2),
        ("ГОСТ 7798-70", "Болт М8", "6", 3),
    ];

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _reads = new();
    private readonly JsonArray _specReads = new();
    private readonly JsonArray _specWritten = new();
    private (int Number, string Name) _writtenSection;
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
        MarkFilterFlags(asm, hiddenIndex: 1, excludedIndex: 2);
        // The description of a freshly built assembly does not exist yet, so the write adds it; the save
        // afterwards is what makes the objects part of the file the reader opens.
        Save(asm, asmPath);
        WriteSpecification(asm);
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

    /// <summary>Sets the two §1.11 filters to TRUE on two named instances: a flag that was never seen
    /// true cannot be shown to filter anything, and FALSE everywhere proves nothing.</summary>
    /// <remarks>DOC: kspart_hidden.html «iPart.SetHidden (hidden)», kspart_excluded.html
    /// «iEntity.SetExcluded(excluded)». The excluded page warns that the flag takes effect at once, so it
    /// is set last, after the update the hidden flag needs. History: docs/decisions/bom.md#probe</remarks>
    private void MarkFilterFlags((ksDocument3D Doc5, IKompasDocument3D Doc7) document, int hiddenIndex,
        int excludedIndex)
    {
        var parts = Components(document.Doc5);
        parts[hiddenIndex].hidden = true;
        parts[hiddenIndex].Update();
        parts[excludedIndex].excluded = true;
        var hidden = Safe(() => parts[hiddenIndex].hidden);
        var excluded = Safe(() => parts[excludedIndex].excluded);
        Record("filters_set", "hidden=True и excluded=True читаются сразу после записи",
            $"hidden={hidden}, excluded={excluded}",
            hidden == "True" && excluded == "True" ? "PASS" : "FAIL");
    }

    /// <summary>Writes the specification objects the read side must then find.</summary>
    /// <remarks>DOC: <c>ispecificationdescriptions_add.html</c> («Если LayoutName и StyleID не заданы, то
    /// добавляется описание спецификации по умолчанию»), <c>ispecificationstyle_sections.html</c> and
    /// <c>ispecificationsectionstyle_number.html</c> (the section numbers of the style),
    /// <c>ksspecification_ksspcobjectcreate.html</c> (creation ends with <c>ksSpcObjectEnd</c>),
    /// <c>ksspecification_kssetspcobjectcolumntext.html</c>, <c>ksspecification_ksspccount.html</c>,
    /// <c>ksspecification_ksspcposition.html</c>.
    /// MEASURED: a fresh assembly has NO description and NO objects, and the number a section carries is
    /// not its place in the specification (Документация is 5, Детали is 20), so the numbers are read from
    /// the style instead of taken from a list of names. History: docs/decisions/bom.md#spec-read</remarks>
    private void WriteSpecification((ksDocument3D Doc5, IKompasDocument3D Doc7) document)
    {
        var specification = document.Doc5.GetSpecification() as ksSpecification;
        Record("specification_write_route", "ksDocument3D.GetSpecification() даёт ksSpecification",
            specification is null ? "не приводится" : "ksSpecification",
            specification is null ? "FAIL" : "PASS");
        if (specification is null)
        {
            return;
        }

        var description = DefaultDescription(document.Doc7);
        var sections = description is null ? [] : StyleSections(description);
        Record("specification_style_sections", "стиль перечисляет разделы спецификации",
            string.Join("; ", sections.Select(s => $"{s.Number}={s.Name}")),
            sections.Count > 0 ? "PASS" : "FAIL");
        if (sections.Count == 0)
        {
            return;
        }

        // The object goes into a section other than the first one, so a row that lands in the wrong
        // section is visible instead of being hidden by a single-section specification.
        _writtenSection = sections.Count > 1 ? sections[1] : sections[0];
        var written = 0;
        foreach (var item in SpecObjects)
        {
            // typeObj 0 — базовый объект; numb 0 — тип атрибута по умолчанию.
            var created = specification.ksSpcObjectCreate(string.Empty, 0, _writtenSection.Number, 0, 0, 0);
            Record($"specification_create_{written}", $"ksSpcObjectCreate в разделе {_writtenSection.Number}",
                created.ToString(CultureInfo.InvariantCulture), created == 1 ? "PASS" : "FAIL");
            if (created != 1)
            {
                continue;
            }

            specification.ksSetSpcObjectColumnText(
                (int)ksSpecificationColumnTypeEnum.ksSColumnMark, 1, 0, item.Marking);
            specification.ksSetSpcObjectColumnText(
                (int)ksSpecificationColumnTypeEnum.ksSColumnName, 1, 0, item.Name);
            specification.ksSpcCount(1, item.Quantity);
            specification.ksSpcPosition(item.Position);
            // The return of ksSpcObjectEnd is not documented as 1-on-success (measured 0), so it is
            // RECORDED as is; the check rests on what the document shows afterwards.
            Record($"specification_end_{written}", "ksSpcObjectEnd() вернул",
                specification.ksSpcObjectEnd().ToString(CultureInfo.InvariantCulture), "INFO");
            _specWritten.Add(item.Marking);
            written++;
        }

        Record("specification_written", $"объектов спецификации записано {SpecObjects.Length}",
            written.ToString(CultureInfo.InvariantCulture), written == SpecObjects.Length ? "PASS" : "FAIL");
    }

    /// <summary>The description of the document, added in the default style when the document has none.</summary>
    /// <remarks>DOC: <c>ispecificationdescriptions_add.html</c> — the empty layout name and style 0 add the
    /// DEFAULT description; <c>ispecificationdescriptions_active.html</c> — the current one.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static ISpecificationDescription? DefaultDescription(IKompasDocument3D document7)
    {
        try
        {
            var descriptions = document7.SpecificationDescriptions;
            if (descriptions is null)
            {
                return null;
            }

            var active = descriptions.Active;
            if (active is not null)
            {
                return active;
            }

            return descriptions.Add(string.Empty, 0, string.Empty) as ISpecificationDescription;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.WriteLine("INFO description: " + Describe(ex));
            return null;
        }
    }

    /// <summary>The sections of the specification style, in style order, with the columns each carries.</summary>
    /// <remarks>DOC: <c>ispecificationstyle_sections.html</c> and
    /// <c>ispecificationsectionstyle_number.html</c>/<c>_name.html</c>. The collection is walked by index
    /// from 0 until it ends — the base collection documents index 0 as the first element
    /// (<c>ikompascollection.html</c>), and no page gives it a count.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private static List<(int Number, string Name)> StyleSections(ISpecificationDescription description)
    {
        var sections = new List<(int Number, string Name)>();
        try
        {
            var collection = description.SpecificationStyle?.Sections;
            for (var index = 0; collection is not null; index++)
            {
                if (collection[index] is not ISpecificationSectionStyle section)
                {
                    break;
                }

                sections.Add(((int)section.Number, section.Name ?? string.Empty));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.WriteLine("INFO sections: " + Describe(ex));
        }

        return sections;
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
        ReadSpecification(label, doc5);
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

        var hiddenFlags = Flags(parts, part => part.hidden);
        var excludedFlags = Flags(parts, part => part.excluded);
        Record($"{label}_hidden", ExpectedHidden, hiddenFlags, hiddenFlags == ExpectedHidden ? "PASS" : "FAIL");
        Record($"{label}_excluded", ExpectedExcluded, excludedFlags,
            excludedFlags == ExpectedExcluded ? "PASS" : "FAIL");

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
            ["hidden_flags"] = hiddenFlags,
            ["excluded_flags"] = excludedFlags,
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

    /// <summary>Reads the specification back through the documented route, field by field, and compares
    /// it with what the fixture wrote.</summary>
    /// <remarks>DOC: <c>ikompasdocument_specificationdescriptions.html</c>,
    /// <c>ispecificationdescriptions_active.html</c>, <c>ispecificationdescription_objects.html</c>,
    /// <c>ispecificationobject_uniquenumber.html</c>, <c>ksspecification_ksgetspcobject.html</c>,
    /// <c>ksspecification_ksgetspcobjectcolumntext.html</c>, <c>ksspecification_ksgetspcsectionname.html</c>.
    /// The read is INDEPENDENT of the product: the same documented calls, compared with the fixture.
    /// History: docs/decisions/bom.md#spec-read</remarks>
    private void ReadSpecification(string label, ksDocument3D doc5)
    {
        var specification = doc5.GetSpecification() as ksSpecification;
        var document7 = _app5!.TransferInterface(doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument;
        var objects = ObjectsOf(document7?.SpecificationDescriptions?.Active);
        // The document also carries the objects КОМПАС builds for the components, so the number is a lower
        // bound: "at least the three the fixture wrote" is the claim, not "exactly three".
        Record($"{label}_specification_objects",
            $"список объектов спецификации содержит не меньше {SpecObjects.Length} строк",
            objects.Count.ToString(CultureInfo.InvariantCulture),
            objects.Count >= SpecObjects.Length ? "PASS" : "FAIL");

        var rows = new JsonArray();
        foreach (var item in objects)
        {
            var unique = item.UniqueNumber;
            var pointer = specification!.ksGetSpcObject(unique);
            rows.Add(new JsonObject
            {
                ["section"] = Safe(() => specification.ksGetSpcSectionName(pointer)),
                ["section_number"] = Safe(() => item.Section),
                ["position"] = Safe(() => specification.ksGetSpcObjectColumnText(
                    pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnPosition, 1, 0)),
                ["marking"] = Safe(() => specification.ksGetSpcObjectColumnText(
                    pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnMark, 1, 0)),
                ["name"] = Safe(() => specification.ksGetSpcObjectColumnText(
                    pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnName, 1, 0)),
                ["quantity"] = Safe(() => specification.ksGetSpcObjectColumnText(
                    pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnCount, 1, 0)),
            });
        }

        _specReads.Add(new JsonObject { ["label"] = label, ["rows"] = rows.DeepClone() });

        // Field by field: a count that matches while the values do not is exactly what a summary hides.
        foreach (var expected in SpecObjects)
        {
            var match = rows.OfType<JsonObject>().FirstOrDefault(r =>
                r["marking"]?.GetValue<string>() == expected.Marking);
            var actual = match is null
                ? "<строка не найдена>"
                : $"section={match["section"]}, name={match["name"]}, quantity={match["quantity"]}, "
                  + $"position={match["position"]}, section_number={match["section_number"]}";
            var ok = match is not null
                && match["name"]?.GetValue<string>() == expected.Name
                && match["quantity"]?.GetValue<string>() == expected.Quantity
                && !string.IsNullOrWhiteSpace(match["section"]?.GetValue<string>());
            Record($"{label}_specification_{expected.Marking}", expected.Name, actual, ok ? "PASS" : "FAIL");
        }
    }

    private static List<ISpecificationBaseObject> ObjectsOf(ISpecificationDescription? description) =>
        description?.Objects switch
        {
            Array array => array.Cast<object?>().OfType<ISpecificationBaseObject>().ToList(),
            ISpecificationBaseObject single => [single],
            _ => [],
        };

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

    /// <summary>One flag per instance in collection order, so the check names the instance a flag must
    /// sit on instead of counting trues.</summary>
    private static string Flags(List<ksPart> parts, Func<ksPart, object?> read) =>
        string.Join(",", parts.Select((part, index) => $"{index}:{Safe(() => read(part))}"));

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
            ["specification_reads"] = _specReads,
            ["spec_written"] = _specWritten,
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
