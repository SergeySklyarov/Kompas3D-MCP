using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace SpecProbe;

/// <summary>Specification probe: creates, edits and deletes rows of a model specification and of a
/// <c>.spw</c> document through documented routes, and reads every row back through an INDEPENDENT
/// route (API7 objects/columns and API5 <c>ksSpecification</c>).</summary>
/// <remarks>WHY STAGES IN SEPARATE PROCESSES: KOMPAS refuses to reopen a path that is still loaded, and a
/// closed document stays loaded while any reference to it lives. Each stage therefore opens the file on
/// disk in its own process, so "persisted" means "written to the file", not "visible in this session".
/// DOC: SPEC_EXT_RESEARCH_DEVELOPER_PROMPT.md §1.4; the SDK mirror pages cited in each method.
/// History: docs/decisions/bom.md#spec-ext</remarks>
internal sealed class SpecProbe : IDisposable
{
    // Known values are the whole point: a read that returns "something" proves nothing. Markings do not
    // repeat the components' own markings, so a row of the probe is never confused with the row KOMPAS
    // builds for an inserted component.
    private static readonly (string Marking, string Name, string Quantity, int Position)[] Rows =
    [
        ("SPC.301.001", "Пластина", "3", 1),
        ("SPC.301.002", "Плита", "2", 2),
        ("SPC.301.003", "Опора", "4", 3),
    ];

    private const string Api5Name = "Пластина API5";
    private const string Api5Quantity = "9";
    private const string Api7Name = "Пластина API7";
    private const string Api7TextName = "Пластина IText";
    private const string SpwMarking = "SPC.401.001";
    private const string SpwName = "Строка спецификации";
    private const string SpwQuantity = "7";

    private readonly string _outPath;
    private readonly JsonArray _rows = new();
    private readonly JsonArray _reads = new();
    private KompasObject? _app5;
    private IApplication? _app7;

    public SpecProbe(string outPath)
    {
        _outPath = Path.GetFullPath(outPath);
    }

    public int Run(string stage, string modelPath, string spwPath)
    {
        // INVARIANT: paths are absolute before any SaveAs - KOMPAS's SaveAs answers FALSE on a path it
        // cannot resolve against its own working directory, and a false here would look like a refusal.
        modelPath = Path.GetFullPath(modelPath);
        spwPath = Path.GetFullPath(spwPath);
        try
        {
            Launch();
            switch (stage)
            {
                case "build":
                    Build(modelPath, spwPath);
                    break;
                case "create":
                    StageAfterCreate(modelPath);
                    break;
                case "api7edit":
                    StageAfterApi5Edit(modelPath);
                    break;
                case "delete":
                    StageAfterApi7Edit(modelPath);
                    break;
                case "closed":
                    StageFinal(modelPath);
                    VerifySpw(spwPath);
                    break;
                default:
                    Record("stage", "известное имя стадии", stage, "FAIL");
                    break;
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
        _app7 = _app5.ksGetApplication7() as IApplication;
        Record("app7", "ksGetApplication7() даёт IApplication", _app7 is null ? "null" : "IApplication",
            _app7 is null ? "FAIL" : "PASS");
        if (_app7 is null)
        {
            throw new InvalidOperationException("API7 is not reachable");
        }
    }

    // ── stage: build ────────────────────────────────────────────────────────────────────────────

    private void Build(string modelPath, string spwPath)
    {
        var root = RepoRoot();
        var fixtureDir = Path.Combine(root, "scratch", "spec-probe", "fixture");
        if (Directory.Exists(fixtureDir))
        {
            Directory.Delete(fixtureDir, recursive: true);
        }

        Directory.CreateDirectory(fixtureDir);
        var part1 = Path.Combine(fixtureDir, "p1.m3d");
        var part2 = Path.Combine(fixtureDir, "p2.m3d");
        NewPart("Пластина", "ABV.301.001", part1);
        NewPart("Плита", "ABV.301.002", part2);

        var asm = NewDocument(isDetail: false);
        if (asm.Doc5.GetPart(-1) is ksPart top)
        {
            top.name = "Сборка спецификации";
            top.marking = "ASM-SPEC";
            top.Update();
        }

        asm.Doc5.RebuildDocument();
        Insert(asm, part1, 2);
        Insert(asm, part2, 1);
        Save(asm, modelPath);
        WriteModelRows(asm);
        // INVARIANT: the second save is what puts the rows in the file - Close(kdDoNotSaveChanges) after
        // the writes would discard them, and the next stage would measure an empty specification.
        Save(asm, modelPath);
        Close(asm);

        var spw = _app7!.Documents.Add(DocumentTypeEnum.ksDocumentSpecification, false);
        Record("spw_add", "IDocuments.Add(ksDocumentSpecification, Visible=false) даёт IKompasDocument",
            spw is null ? "null" : $"{spw.DocumentType}, Visible={spw.Visible}, Active={spw.Active}",
            spw is null ? "FAIL" : "PASS");
        if (spw is null)
        {
            return;
        }

        BuildSpw(spw);
        spw.SaveAs(spwPath);
        var saved = File.Exists(spwPath) ? new FileInfo(spwPath).Length : -1;
        Record("spw_save", "IKompasDocument.SaveAs(.spw)", $"{spwPath}; bytes={saved}", saved > 0 ? "PASS" : "FAIL");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        Record("spw_close", "IKompasDocument.Close(kdDoNotSaveChanges) на .spw", "closed", "PASS");
    }

    private void NewPart(string name, string marking, string path)
    {
        var document = NewDocument(isDetail: true);
        if (document.Doc5.GetPart(-1) is ksPart part)
        {
            part.name = name;
            part.marking = marking;
            part.Update();
        }

        document.Doc5.RebuildDocument();
        Save(document, path);
        Close(document);
    }

    private (ksDocument3D Doc5, IKompasDocument Doc7) NewDocument(bool isDetail)
    {
        var doc5 = (ksDocument3D)_app5!.Document3D();
        if (!doc5.Create(true, isDetail))
        {
            throw new InvalidOperationException($"ksDocument3D.Create(true, isDetail={isDetail}) вернул false");
        }

        var doc7 = _app5.TransferInterface(doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument
            ?? throw new InvalidOperationException("TransferInterface(документ → API7) не дал IKompasDocument");
        return (doc5, doc7);
    }

    /// <summary>Inserts one component per call through the documented <c>IParts7.AddFromFile</c> route.</summary>
    private void Insert((ksDocument3D Doc5, IKompasDocument Doc7) document, string path, int times)
    {
        if (document.Doc7 is not IKompasDocument3D modern || modern.TopPart?.Parts is not { } parts7)
        {
            Record("insert", "IPart7.Parts доступен", "null", "FAIL");
            return;
        }

        for (var index = 0; index < times; index++)
        {
            var inserted = parts7.AddFromFile(path, ExternalFile: true, Redraw: true);
            Record("insert", $"AddFromFile({Path.GetFileName(path)}) #{index + 1}",
                inserted is null ? "null" : "Part7", inserted is null ? "FAIL" : "PASS");
        }
    }

    /// <summary>Creates rows through the documented API7 route: <c>BaseObjects.Add</c> makes the object live
    /// in the collection only, and <c>Update</c> is what creates it in the document.</summary>
    /// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c> («объект будет добавлен в коллекцию, но в
    /// документе не появится... вызвать ISpecificationObject::Update»), <c>ispecificationobject_update.html</c>,
    /// <c>ispecificationcolumns_column.html</c>, <c>ispecificationcolumnitem_value.html</c>.
    /// History: docs/decisions/bom.md#spec-ext</remarks>
    private void WriteModelRows((ksDocument3D Doc5, IKompasDocument Doc7) model)
    {
        var description = DescriptionOf(model.Doc7, "model");
        if (description is null)
        {
            return;
        }

        var sections = StyleSections(description);
        Record("model_style_sections", "стиль спецификации перечисляет разделы",
            string.Join("; ", sections.Select(s => $"{s.Number}={s.Name}")),
            sections.Count > 0 ? "PASS" : "FAIL");
        // INVARIANT: the rows go into Детали - the only section of the default style that carries the
        // Количество and Позиция columns the check needs. Marking and name alone would not prove that.
        var section = sections.FirstOrDefault(s => s.Name.Contains("Детали", StringComparison.Ordinal));
        if (section == default)
        {
            section = sections[1];
        }

        Record("model_section", "раздел для строк - Детали", $"{section.Number}={section.Name}",
            section.Name.Contains("Детали", StringComparison.Ordinal) ? "PASS" : "FAIL");

        if (description.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            Record("model_baseobjects", "ISpecificationDescription.BaseObjects", "null", "FAIL");
            return;
        }

        var index = 0;
        foreach (var row in Rows)
        {
            var created = baseObjects.Add(section.Number, 0);
            Record($"model_add_{index}", $"BaseObjects.Add(раздел {section.Number}) в коллекцию",
                created is null ? "null" : "base object", created is null ? "FAIL" : "PASS");
            if (created is null)
            {
                index++;
                continue;
            }

            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, row.Marking);
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, row.Name);
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, row.Quantity);
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnPosition,
                row.Position.ToString(CultureInfo.InvariantCulture));

            var updated = created.Update();
            Record($"model_update_{index}", "ISpecificationObject.Update() создаёт объект в документе",
                updated.ToString(), updated ? "PASS" : "FAIL");
            index++;
        }

        ReadModel(model.Doc5, model.Doc7, "built");
        RecordLinkSurfaces(model.Doc7, description, "model");
    }

    private void BuildSpw(IKompasDocument spw)
    {
        var descriptions = spw.SpecificationDescriptions;
        Record("spw_descriptions", "IKompasDocument.SpecificationDescriptions у документа-спецификации",
            descriptions is null ? "null" : $"{descriptions.Count} описаний", descriptions is null ? "FAIL" : "PASS");
        var description = descriptions?.Active ?? descriptions?.Add(string.Empty, 0, string.Empty);
        if (description is null)
        {
            Record("spw_description", "описание спецификации в .spw", "null", "FAIL");
            return;
        }

        var sections = StyleSections(description);
        Record("spw_style_sections", "стиль .spw перечисляет разделы",
            string.Join("; ", sections.Select(s => $"{s.Number}={s.Name}")), sections.Count > 0 ? "PASS" : "FAIL");
        var section = sections.FirstOrDefault(s => s.Name.Contains("Детали", StringComparison.Ordinal));
        if (section == default)
        {
            section = sections.FirstOrDefault();
        }

        if (description.BaseObjects is ISpecificationBaseObjects baseObjects && section != default)
        {
            var created = baseObjects.Add(section.Number, 0);
            if (created is not null)
            {
                SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, SpwMarking);
                SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, SpwName);
                SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwQuantity);
                var updated = created.Update();
                Record("spw_create_row", "API7: строка .spw создана, Update() вернул true", updated.ToString(),
                    updated ? "PASS" : "FAIL");
            }
        }

        ReadSpw(spw, "spw-built", 1);
        RecordSpwSurfaces(spw, description, "spw");
    }

    // ── stages that read the file on disk ───────────────────────────────────────────────────────

    /// <summary>Creation check, then an edit through API5 on a REOPENED document, where the API5 view has
    /// already seen every row (in the writing session it had not).</summary>
    private void StageAfterCreate(string modelPath)
    {
        var (doc5, doc7) = OpenModel(modelPath, "create");
        if (doc7 is null)
        {
            return;
        }

        ReadModel(doc5!, doc7, "reopened");
        var created = RowsOf(doc5!, doc7).FirstOrDefault(r => r.Marking == Rows[0].Marking);
        Record("create_persisted", $"строка {Rows[0].Marking} на диске",
            created is null ? "нет" : $"name={created.Name}, qty={created.Quantity}",
            created is not null && created.Name == Rows[0].Name ? "PASS" : "FAIL");

        // Edit through API5: the object is opened with ksSpcObjectEdit, the columns are changed, and
        // ksSpcObjectEnd closes the edit.
        var specification = doc5!.GetSpecification() as ksSpecification;
        var target = RowsOf(doc5, doc7).FirstOrDefault(r => r.Marking == Rows[1].Marking);
        if (specification is not null && target is not null)
        {
            var pointer = specification.ksGetSpcObject(target.Unique);
            Record("api5_edit_pointer", "ksGetSpcObject(UniqueNumber) на переоткрытом документе",
                pointer.ToString(CultureInfo.InvariantCulture), pointer != 0 ? "PASS" : "FAIL");
            var edit = specification.ksSpcObjectEdit(pointer);
            var name = specification.ksSetSpcObjectColumnText(
                (int)ksSpecificationColumnTypeEnum.ksSColumnName, 1, 0, Api5Name);
            var count = specification.ksSpcCount(1, Api5Quantity);
            var end = specification.ksSpcObjectEnd();
            Record("api5_edit", "API5: ksSpcObjectEdit + ksSetSpcObjectColumnText + ksSpcCount + ksSpcObjectEnd",
                $"edit={edit}, name={name}, count={count}, end={end}",
                edit == 1 && name == 1 && count == 1 ? "PASS" : "FAIL");
            ReadModel(doc5, doc7, "after-api5-edit");
        }
        else
        {
            Record("api5_edit", "API5: ksSpecification и строка для правки", "нет", "FAIL");
        }

        SaveModel(doc5!, modelPath);
        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    private void StageAfterApi5Edit(string modelPath)
    {
        var (doc5, doc7) = OpenModel(modelPath, "api7edit");
        if (doc7 is null)
        {
            return;
        }

        ReadModel(doc5!, doc7, "reopened");
        var row = RowsOf(doc5!, doc7).FirstOrDefault(r => r.Marking == Rows[1].Marking);
        Record("api5_edit_persisted", $"правка API5 «{Api5Name}»/«{Api5Quantity}» на диске",
            row is null ? "<строка не найдена>" : $"name={row.Name}, qty={row.Quantity}",
            row is not null && row.Name == Api5Name && row.Quantity == Api5Quantity ? "PASS" : "FAIL");

        // Edit through API7 on the same row: the column element carries the value, Update() commits it.
        var objects = ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects);
        var target = objects.FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == Rows[1].Marking);
        if (target is not null)
        {
            SetText(target, ksSpecificationColumnTypeEnum.ksSColumnName, Api7Name);
            var updated = target.Update();
            Record("api7_edit_columns", "API7: ColumnItems.Value + Update() на существующей строке",
                updated.ToString(), "INFO");
        }
        else
        {
            Record("api7_edit_columns", "API7: строка для правки найдена", "не найдена", "INFO");
        }

        // The same edit through the OTHER documented text property: ISpecificationColumn.Text is an IText
        // with a settable Str. Two write routes are measured apart, on two different rows.
        var textTarget = objects.FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == Rows[0].Marking);
        Record("api7_edit_text", "API7: ISpecificationColumn.Text.Str + Update() на существующей строке",
            textTarget is null ? "строка не найдена" : TrySetTextViaIText(textTarget, Api7TextName), "INFO");

        SaveModel(doc5!, modelPath);
        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    private void StageAfterApi7Edit(string modelPath)
    {
        var (doc5, doc7) = OpenModel(modelPath, "delete");
        if (doc7 is null)
        {
            return;
        }

        ReadModel(doc5!, doc7, "reopened");
        var columnsRow = RowsOf(doc5!, doc7).FirstOrDefault(r => r.Marking == Rows[1].Marking);
        Record("api5_edit_persisted_again", $"правка API5 «{Api5Name}» пережила запись API7",
            columnsRow is null ? "<строка не найдена>" : $"name={columnsRow.Name}",
            columnsRow is not null && columnsRow.Name == Api5Name ? "PASS" : "FAIL");
        Record("api7_columns_persisted", $"правка через ColumnItems «{Api7Name}» на диске",
            columnsRow is null ? "<строка не найдена>" : $"name={columnsRow.Name}",
            columnsRow is not null && columnsRow.Name == Api7Name ? "PASS" : "INFO");
        var textRow = RowsOf(doc5!, doc7).FirstOrDefault(r => r.Marking == Rows[0].Marking);
        Record("api7_text_persisted", $"правка через IText «{Api7TextName}» на диске",
            textRow is null ? "<строка не найдена>" : $"name={textRow.Name}",
            textRow is not null && textRow.Name == Api7TextName ? "PASS" : "INFO");

        // Section move: Section is documented read-only and "moving is impossible", while
        // ISpecificationBaseObject::SetSection is documented to set the number. Measure which one holds.
        var description = doc7.SpecificationDescriptions?.Active;
        var sections = description is null ? [] : StyleSections(description);
        var current = RowsOf(doc5!, doc7).FirstOrDefault(r => r.Marking == Rows[0].Marking);
        var other = sections.FirstOrDefault(s => current?.Section is not null
            && s.Number.ToString(CultureInfo.InvariantCulture) != current.Section);
        var target = ObjectsOf(description?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == Rows[0].Marking);
        if (target is not null && other != default)
        {
            var moved = target.SetSection(other.Number);
            Record("move_section", $"ISpecificationBaseObject.SetSection({other.Number}={other.Name})",
                $"{moved}, Section={Safe(() => target.Section)}", moved ? "PASS" : "FAIL");
            ReadModel(doc5!, doc7, "after-move");
        }
        else
        {
            Record("move_section", "SetSection: строка и другой раздел найдены", "нет", "INFO");
        }

        var deletedRow = ObjectsOf(description?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == Rows[2].Marking);
        if (deletedRow is not null)
        {
            var deleted = deletedRow.Delete();
            Record("api7_delete", "API7: ISpecificationObject.Delete()", deleted.ToString(),
                deleted ? "PASS" : "FAIL");
        }
        else
        {
            Record("api7_delete", "API7: строка для удаления найдена", "не найдена", "FAIL");
        }

        SaveModel(doc5!, modelPath);
        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    private void StageFinal(string modelPath)
    {
        var (doc5, doc7) = OpenModel(modelPath, "closed");
        if (doc7 is null)
        {
            return;
        }

        // The deleted row is EXPECTED to be missing here; the generic per-row check must not call it a failure.
        ReadModel(doc5!, doc7, "final", Rows[2].Marking);
        var rows = RowsOf(doc5!, doc7);
        var moved = rows.FirstOrDefault(r => r.Marking == Rows[0].Marking);
        Record("moved_section_persisted", "раздел строки после записи и переоткрытия не Детали",
            moved is null ? "<строка не найдена>" : $"section={moved.Section}({moved.SectionName})",
            moved is not null && moved.SectionName is not null
            && !moved.SectionName.Contains("Детали", StringComparison.Ordinal) ? "PASS" : "INFO");
        Record("deleted_absent", $"удалённая строка {Rows[2].Marking} отсутствует на диске",
            rows.Any(r => r.Marking == Rows[2].Marking) ? "присутствует" : "отсутствует",
            rows.Any(r => r.Marking == Rows[2].Marking) ? "FAIL" : "PASS");
        Record("created_present", $"созданные строки {Rows[0].Marking} и {Rows[1].Marking} на диске",
            string.Join(", ", rows.Where(r => r.Marking is not null && r.Marking.StartsWith("SPC.", StringComparison.Ordinal)).Select(r => r.Marking)),
            rows.Any(r => r.Marking == Rows[0].Marking) && rows.Any(r => r.Marking == Rows[1].Marking) ? "PASS" : "FAIL");
        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    private (ksDocument3D? Doc5, IKompasDocument? Doc7) OpenModel(string modelPath, string stage)
    {
        if (!File.Exists(modelPath))
        {
            Record($"{stage}_open", "файл сборки существует", modelPath, "FAIL");
            return (null, null);
        }

        var doc5 = (ksDocument3D)_app5!.Document3D();
        var opened = doc5.Open(Path.GetFullPath(modelPath), true);
        Record($"{stage}_open", "ksDocument3D.Open() открывает сохранённую сборку",
            opened ? "true" : "false", opened ? "PASS" : "FAIL");
        if (!opened)
        {
            return (null, null);
        }

        var doc7 = _app5.TransferInterface(doc5, (int)ksAPITypeEnum.ksAPI7Dual, 0) as IKompasDocument;
        Record($"{stage}_api7", "TransferInterface даёт IKompasDocument", doc7 is null ? "null" : "IKompasDocument",
            doc7 is null ? "FAIL" : "PASS");
        return (doc5, doc7);
    }

    private void SaveModel(ksDocument3D doc5, string modelPath)
    {
        var ok = doc5.SaveAs(Path.GetFullPath(modelPath));
        Record("save", $"SaveAs({Path.GetFileName(modelPath)})", ok ? "true" : "false", ok ? "PASS" : "FAIL");
    }

    // ── reading ─────────────────────────────────────────────────────────────────────────────────

    private sealed record RowRead(string? Marking, string? Name, string? Quantity, double Unique, string? Section,
        string? SectionName, string? MarkingApi5, string? QuantityApi5, int Pointer);

    /// <summary>Reads the model specification through BOTH documented routes at once and compares each
    /// field with what the probe wrote.</summary>
    /// <remarks>DOC: <c>ispecificationdescription_objects.html</c>, <c>ispecificationobject_uniquenumber.html</c>,
    /// <c>ispecificationcolumnitems_item.html</c>, <c>ispecificationcolumnitem_value.html</c>,
    /// <c>ksspecification_ksgetspcobject.html</c>, <c>ksspecification_ksgetspcobjectcolumntext.html</c>,
    /// <c>ksspecification_ksgetspcsectionname.html</c>. History: docs/decisions/bom.md#spec-ext</remarks>
    private List<RowRead> ReadModel(ksDocument3D doc5, IKompasDocument doc7, string label, string? absentMarking = null)
    {
        var specification = doc5.GetSpecification() as ksSpecification;
        Record($"{label}_api5_route", "ksDocument3D.GetSpecification() даёт ksSpecification",
            specification is null ? "нет" : "ksSpecification", specification is null ? "FAIL" : "PASS");

        var rows = new List<RowRead>();
        var json = new JsonArray();
        foreach (var item in ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects))
        {
            var unique = (double)item.UniqueNumber;
            var pointer = specification?.ksGetSpcObject(unique) ?? 0;
            var row = new RowRead(
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
                unique,
                Safe(() => item.Section),
                specification is null ? null : Safe(() => specification.ksGetSpcSectionName(pointer)),
                specification is null ? null : Safe(() =>
                    specification.ksGetSpcObjectColumnText(pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnMark, 1, 0)),
                specification is null ? null : Safe(() =>
                    specification.ksGetSpcObjectColumnText(pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnCount, 1, 0)),
                pointer);
            rows.Add(row);
            json.Add(new JsonObject
            {
                ["unique"] = unique,
                ["pointer"] = pointer,
                ["section"] = row.Section,
                ["section_name"] = row.SectionName,
                ["marking_api7"] = row.Marking,
                ["name_api7"] = row.Name,
                ["quantity_api7"] = row.Quantity,
                ["marking_api5"] = row.MarkingApi5,
                ["quantity_api5"] = row.QuantityApi5,
            });
        }

        _reads.Add(new JsonObject { ["label"] = label, ["rows"] = json.DeepClone() });
        Record($"{label}_objects", "объекты спецификации перечислены", rows.Count.ToString(CultureInfo.InvariantCulture),
            rows.Count > 0 ? "PASS" : "FAIL");

        foreach (var expected in Rows.Where(r => r.Marking != absentMarking))
        {
            var found = rows.FirstOrDefault(r => r.Marking == expected.Marking);
            var ok = found is not null && found.MarkingApi5 == expected.Marking;
            Record($"{label}_row_{expected.Marking}", $"{expected.Name}: API7 и API5 видят строку",
                found is null
                    ? "<строка не найдена>"
                    : $"name_api7={found.Name}, qty_api7={found.Quantity}, qty_api5={found.QuantityApi5}, "
                      + $"pointer={found.Pointer}, section={found.Section}({found.SectionName})",
                ok ? "PASS" : "FAIL");
        }

        return rows;
    }

    private List<RowRead> RowsOf(ksDocument3D doc5, IKompasDocument doc7)
    {
        var specification = doc5.GetSpecification() as ksSpecification;
        return ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects).Select(item =>
        {
            var unique = (double)item.UniqueNumber;
            var pointer = specification?.ksGetSpcObject(unique) ?? 0;
            return new RowRead(
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
                unique,
                Safe(() => item.Section),
                specification is null ? null : Safe(() => specification.ksGetSpcSectionName(pointer)),
                null,
                null,
                pointer);
        }).ToList();
    }

    private void ReadSpw(IKompasDocument spw, string label, int expectedRows)
    {
        var objects = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects);
        var json = new JsonArray();
        foreach (var item in objects)
        {
            json.Add(new JsonObject
            {
                ["section"] = Safe(() => item.Section),
                ["marking"] = ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ["name"] = ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ["quantity"] = ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
            });
        }

        _reads.Add(new JsonObject { ["label"] = label, ["rows"] = json.DeepClone() });
        Record($"{label}_objects", $"объектов спецификации в .spw не меньше {expectedRows}",
            objects.Count.ToString(CultureInfo.InvariantCulture),
            objects.Count >= expectedRows ? "PASS" : "FAIL");

        var row = objects.Select(o => new
        {
            Marking = ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark),
            Name = ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnName),
            Quantity = ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnCount),
        }).FirstOrDefault(o => o.Marking == SpwMarking);
        Record($"{label}_row", $"{SpwMarking} / {SpwName} / {SpwQuantity}",
            row is null ? "<строка не найдена>" : $"name={row.Name}, qty={row.Quantity}",
            row is not null && row.Name == SpwName ? "PASS" : "FAIL");
    }

    private void VerifySpw(string spwPath)
    {
        if (!File.Exists(spwPath))
        {
            Record("spw_open", "файл .spw существует", spwPath, "FAIL");
            return;
        }

        var documents = _app7!.Documents as IDocuments;
        var spw = documents?.Open(Path.GetFullPath(spwPath), false, false);
        Record("spw_open", "IDocuments.Open(.spw) даёт IKompasDocument",
            spw is null ? "null" : $"{spw.DocumentType}, {Path.GetFileName(spw.PathName)}",
            spw is null ? "FAIL" : "PASS");
        if (spw is null)
        {
            return;
        }

        Record("spw_type", "тип открытого документа - спецификация", $"{spw.DocumentType}",
            spw.DocumentType == DocumentTypeEnum.ksDocumentSpecification ? "PASS" : "FAIL");
        Record("spw_interface", "IKompasDocument приводится к ISpecificationDocument",
            spw is ISpecificationDocument specificationDocument
                ? $"Crossed={specificationDocument.Crossed}"
                : "не приводится",
            spw is ISpecificationDocument ? "PASS" : "INFO");

        ReadSpw(spw, "spw-reopened", 1);

        // The API5 route addresses the ACTIVE document, so the probe makes the .spw active first through
        // the documented IApplication.ActiveDocument setter and records what the route then returns.
        _app7.ActiveDocument = spw;
        var active = _app5!.SpcActiveDocument() as ksSpcDocument;
        Record("spw_api5_active", "SpcActiveDocument() на активном .spw",
            active is null ? "null" : "ksSpcDocument", active is null ? "INFO" : "PASS");
        if (active is not null)
        {
            var specification = active.GetSpecification() as ksSpecification;
            Record("spw_api5_getspecification", "ksSpcDocument.GetSpecification() даёт ksSpecification",
                specification is null ? "null" : "ksSpecification", specification is null ? "FAIL" : "PASS");
            var objects = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects);
            var pointer = specification is not null && objects.Count > 0
                ? specification.ksGetSpcObject((double)objects[0].UniqueNumber)
                : 0;
            var marking = pointer == 0
                ? null
                : Safe(() => specification!.ksGetSpcObjectColumnText(
                    pointer, (int)ksSpecificationColumnTypeEnum.ksSColumnMark, 1, 0));
            Record("spw_api5_row", $"API5 читает маркировку строки .spw = {SpwMarking}",
                marking ?? "<не прочитано>", marking == SpwMarking ? "PASS" : "FAIL");
        }

        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    // ── link surfaces (documented, reported as measured values) ───────────────────────────────

    /// <summary>What the documented link properties answer on the model and on the spec document.</summary>
    /// <remarks>DOC: <c>ispecificationdescription_specificationdocumentname.html</c> (connected .spw file),
    /// <c>ispecificationbaseobject_documents.html</c> (documents whose geometry is attached to the object;
    /// the page says the list exists only for objects in a spec document).
    /// History: docs/decisions/bom.md#spec-ext</remarks>
    private void RecordLinkSurfaces(IKompasDocument document, ISpecificationDescription description, string label)
    {
        Record($"{label}_specification_document_name", "SpecificationDocumentName у модели",
            Safe(() => description.SpecificationDocumentName) ?? "<null>", "INFO");
        foreach (var item in ObjectsOf(description.Objects))
        {
            var marking = ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark);
            if (marking is not null && marking.StartsWith("SPC.", StringComparison.Ordinal))
            {
                Record($"{label}_row_documents", $"Documents строки {marking} (модель)", Safe(() => item.Documents) ?? "<null>",
                    "INFO");
            }
        }
    }

    private void RecordSpwSurfaces(IKompasDocument spw, ISpecificationDescription description, string label)
    {
        Record($"{label}_specification_document_name", "SpecificationDocumentName у .spw",
            Safe(() => description.SpecificationDocumentName) ?? "<null>", "INFO");
        Record($"{label}_attached_documents", "AttachedDocuments у .spw через ISpecificationDocument",
            spw is ISpecificationDocument specDoc
                ? Safe(() => specDoc.AttachedDocuments) ?? "<null>"
                : "<нет интерфейса>",
            "INFO");
        foreach (var item in ObjectsOf(description.Objects))
        {
            var marking = ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark);
            if (marking == SpwMarking)
            {
                Record($"{label}_row_documents", $"Documents строки {marking} (.spw)",
                    Safe(() => item.Documents) ?? "<null>", "INFO");
            }
        }
    }


    // ── documented-route helpers ───────────────────────────────────────────────────────────────

    private ISpecificationDescription? DescriptionOf(IKompasDocument document, string label)
    {
        try
        {
            var descriptions = document.SpecificationDescriptions;
            var description = descriptions?.Active ?? descriptions?.Add(string.Empty, 0, string.Empty);
            Record($"{label}_description", "описание спецификации (или Add по умолчанию) доступно",
                description is null ? "null" : $"style={description.StyleID}",
                description is null ? "FAIL" : "PASS");
            return description;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record($"{label}_description", "описание спецификации", Describe(ex), "FAIL");
            return null;
        }
    }

    /// <summary>Writes the column text through the documented <c>ISpecificationColumn.Text</c> property,
    /// which returns an <c>IText</c> with a settable <c>Str</c>, and returns what happened.</summary>
    /// <remarks>DOC: <c>ispecificationcolumn_text.html</c> («позволяет получить и изменить текст колонки
    /// объекта спецификации»). History: docs/decisions/bom.md#spec-ext</remarks>
    private static string TrySetTextViaIText(ISpecificationBaseObject item, string text)
    {
        try
        {
            var columns = item.Columns;
            if (columns is null || columns.Column[ksSpecificationColumnTypeEnum.ksSColumnName, 1, 0] is not { } column)
            {
                return "колонка Наименование не найдена";
            }

            var itext = column.Text;
            if (itext is null)
            {
                return "column.Text == null";
            }

            itext.Str = text;
            var updated = item.Update();
            return $"update={updated}, Str={Safe(() => itext.Str)}";
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return Describe(ex);
        }
    }

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

    /// <summary>Writes one column element of the object: the element carries the value and its declared
    /// <c>ValueType</c> decides the boxed type, as the page says (VT_I4 / VT_R8 / VT_BSTR).</summary>
    /// <remarks>DOC: <c>ispecificationcolumnitem_value.html</c> («в зависимости от типа значения ValueType
    /// возвращаемое значение имеет тип: ksValueTypeInteger - VT_I4...»). History: docs/decisions/bom.md#spec-ext</remarks>
    private static void SetText(ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type, string text)
    {
        var columns = item.Columns;
        if (columns is null || columns.Column[type, 1, 0] is not { } column
            || column.ColumnItems is not ISpecificationColumnItems items)
        {
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var element = items[index];
            if (element is null)
            {
                continue;
            }

            var valueType = element.ValueType.ToString();
            element.Value = valueType.Contains("Integer", StringComparison.Ordinal)
                ? int.Parse(text, CultureInfo.InvariantCulture)
                : valueType.Contains("Float", StringComparison.Ordinal)
                    ? double.Parse(text, CultureInfo.InvariantCulture)
                    : text;
            return;
        }
    }

    /// <summary>Reads a column back through API7: every element of that column, joined in order.</summary>
    private static string? ReadColumn(ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type)
    {
        try
        {
            var columns = item.Columns;
            if (columns is null || columns.Column[type, 1, 0] is not { } column
                || column.ColumnItems is not ISpecificationColumnItems items)
            {
                return null;
            }

            var values = new List<string>();
            for (var index = 0; index < items.Count; index++)
            {
                var value = items[index]?.Value;
                if (value is not null)
                {
                    values.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }

            return string.Join("|", values);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return "<" + Describe(ex) + ">";
        }
    }

    private static List<ISpecificationBaseObject> ObjectsOf(object? objects) => objects switch
    {
        Array array => array.Cast<object?>().OfType<ISpecificationBaseObject>().ToList(),
        ISpecificationBaseObject single => [single],
        _ => [],
    };

    private void Save((ksDocument3D Doc5, IKompasDocument Doc7) document, string path)
    {
        var ok = document.Doc5.SaveAs(path);
        Record("save", $"SaveAs({Path.GetFileName(path)})", ok ? "true" : "false", ok ? "PASS" : "FAIL");
    }

    private void Close((ksDocument3D Doc5, IKompasDocument Doc7) document)
    {
        var ok = document.Doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        Record("close", "IKompasDocument.Close(kdDoNotSaveChanges)", ok ? "true" : "false", ok ? "PASS" : "FAIL");
    }

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
            ["probe"] = "spec",
            ["measured_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
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

        if (_app5 is not null)
        {
            Marshal.FinalReleaseComObject(_app5);
        }
    }
}





