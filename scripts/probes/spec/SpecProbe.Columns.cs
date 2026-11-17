using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace SpecProbe;

/// <summary>Which columns the EDIT route (<c>ksSpcObjectEdit</c> … <c>ksSpcObjectEnd</c>) writes into the
/// FILE, and what the document does with a written position.</summary>
/// <remarks>WHY SEPARATE PROCESSES: KOMPAS keeps a closed document loaded while a reference to it lives, so
/// "in the file" is measurable only by a process that never wrote. Every stage opens its own sandbox copy.
/// DOC: <c>ksspecification_kssetspcobjectcolumntext.html</c> (columnType, ispoln, block, text; 1 - success),
/// <c>ksspecification_ksspcmassa.html</c> (the mass column only),
/// <c>ispecificationtuning_calculateposition.html</c>, <c>ispecificationtuningsection_putposition.html</c>.
/// History: docs/decisions/bom.md#spec-spw-columns</remarks>
internal sealed partial class SpecProbe
{
    private const string ColumnsRowA = "SPW.701.001";
    private const string ColumnsRowB = "SPW.701.002";
    private const string ModelColumnsRow = "SPC.702.001";

    /// <summary>The documented column types of the contract, each with its OWN value: a read that answers a
    /// neighbour's text then shows the difference instead of hiding it.</summary>
    private static readonly (ksSpecificationColumnTypeEnum Type, string Key, string Value)[] EditedColumns =
    [
        (ksSpecificationColumnTypeEnum.ksSColumnFormat, "format", "Ф5"),
        (ksSpecificationColumnTypeEnum.ksSColumnZone, "zone", "Z1"),
        (ksSpecificationColumnTypeEnum.ksSColumnNote, "note", "Пр.1"),
        (ksSpecificationColumnTypeEnum.ksSColumnMass, "mass", "2,5"),
        (ksSpecificationColumnTypeEnum.ksSColumnUser, "user", "U1"),
    ];

    private string SpwColumnsPath =>
        Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "columns.spw");

    private string ModelColumnsDir => Path.Combine(RepoRoot(), "scratch", "spec-probe", "sandbox", "columns");

    private string ModelColumnsPath => Path.Combine(ModelColumnsDir, "spec-asm.m3d");

    /// <summary>Creates two rows in a fresh copy and writes every contract column through the edit route,
    /// each value in its OWN edit session, so a setter's answer belongs to that one type.</summary>
    /// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c> (the object appears after <c>Update</c>),
    /// <c>ksspecification_ksspcobjectedit.html</c> («Редактирование объекта завершится вызовом метода
    /// ksSpecification::ksSpcObjectEnd»).
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void SpwColumnsWrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SpwColumnsPath)!);
        File.Copy(SpwFixturePath, SpwColumnsPath, overwrite: true);
        var spw = OpenSpw(SpwColumnsPath, "spw-columns-write");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "columns");
        if (description is not { } active || active.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            Record("columns_baseobjects", "описание спецификации и BaseObjects доступны", "нет", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        RecordTuning(active, SectionFor(active), "columns");
        CreateSpwRow(baseObjects, 20, ColumnsRowA, "Колонки .spw", "3", "1");
        CreateSpwRow(baseObjects, 20, ColumnsRowB, "Соседняя .spw", "1", "2");

        var rows = ReadSpwRows(spw, "columns-created");
        var rowA = rows.FirstOrDefault(r => r.Marking == ColumnsRowA);
        Record("columns_row_created", $"строка {ColumnsRowA} создана и прочитана списком",
            Describe(rowA), rowA is not null ? "PASS" : "FAIL");
        if (rowA is null)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var specification = ActivateSpecification(spw, "columns");
        if (specification is null)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        foreach (var (type, key, value) in EditedColumns)
        {
            var answer = EditColumn(specification, rowA.Unique, type, value);
            Record($"columns_write_{key}", $"правка пишет колонку {key} = «{value}»", answer.Text,
                answer.Setter == 1 ? "PASS" : "FAIL");
        }

        foreach (var (type, key, _) in EditedColumns)
        {
            Record($"columns_session_api5_{key}", $"API5 читает колонку {key} в сеансе правки",
                Api5Text(specification, rowA.Unique, type) ?? "<null>", "INFO");
        }

        var reread = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ColumnsRowA);
        foreach (var (type, key, _) in EditedColumns)
        {
            Record($"columns_session_api7_{key}", $"API7 читает колонку {key} в сеансе правки",
                (reread is null ? null : ReadFrom(reread, type)) ?? "<null>", "INFO");
        }

        spw.Save();
        Record("columns_saved", "IKompasDocument.Save() после правки колонок", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens the file written by <c>spw-columns-write</c> and reads every column back through both
    /// roads: the file, not the session, is what "written" means.</summary>
    /// <remarks>Also edits the position of the second row here, so the NEXT process can say whether the
    /// document keeps a written position or replaces it with its own numbering.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void SpwColumnsRead()
    {
        var spw = OpenSpw(SpwColumnsPath, "spw-columns-read");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "columns_final");
        if (description is not { } active)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        RecordTuning(active, SectionFor(active), "columns_final");
        var rows = ReadSpwRows(spw, "columns-reopened");
        var rowA = rows.FirstOrDefault(r => r.Marking == ColumnsRowA);
        var rowB = rows.FirstOrDefault(r => r.Marking == ColumnsRowB);
        Record("columns_row_persisted", $"строка {ColumnsRowA} пережила сохранение",
            Describe(rowA), rowA is not null ? "PASS" : "FAIL");
        if (rowA is null)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var objects = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects);
        var itemA = objects.FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ColumnsRowA);
        var specification = ActivateSpecification(spw, "columns_final");
        foreach (var (type, key, value) in EditedColumns)
        {
            var api7 = itemA is null ? null : ReadFrom(itemA, type);
            var api5 = specification is null ? null : Api5Text(specification, rowA.Unique, type);
            var ok = ColumnMatches(value, api7) && ColumnMatches(value, api5);
            Record($"columns_persisted_{key}", $"колонка {key} = «{value}» в файле (API7 и API5)",
                $"api7={api7 ?? "<null>"}, api5={api5 ?? "<null>"}", ok ? "PASS" : "INFO");
        }

        if (rowB is not null && specification is not null)
        {
            var pointer = specification.ksGetSpcObject(rowB.Unique);
            var edit = pointer == 0 ? 0 : specification.ksSpcObjectEdit(pointer);
            var position = specification.ksSpcPosition(4);
            var end = specification.ksSpcObjectEnd();
            Record("position_edit", "правка позиции строки 4 через ksSpcPosition",
                $"pointer={pointer}, edit={edit}, position={position}, end={end}",
                edit == 1 && position == 1 ? "PASS" : "FAIL");
            var session = ReadSpwRows(spw, "position-edited").FirstOrDefault(r => r.Marking == ColumnsRowB);
            Record("position_session", "позиция строки в сеансе правки", session?.Position ?? "<null>", "INFO");
        }

        spw.Save();
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reads the file a second time: the columns again, and the position the document gives the
    /// edited row after a save.</summary>
    /// <remarks>MEASURED QUESTION: a written position reads back in the session, so only a new process can
    /// tell «the document replaced it» from «it never reached the file».
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void SpwColumnsFinal()
    {
        var spw = OpenSpw(SpwColumnsPath, "spw-columns-final");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "columns_third");
        if (description is { } active)
        {
            RecordTuning(active, SectionFor(active), "columns_third");
        }

        var rows = ReadSpwRows(spw, "columns-final");
        var rowA = rows.FirstOrDefault(r => r.Marking == ColumnsRowA);
        var rowB = rows.FirstOrDefault(r => r.Marking == ColumnsRowB);
        var itemA = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ColumnsRowA);
        foreach (var (type, key, value) in EditedColumns)
        {
            var api7 = itemA is null ? null : ReadFrom(itemA, type);
            var ok = api7 is not null && ColumnMatches(value, api7);
            Record($"columns_final_{key}", $"колонка {key} = «{value}» в файле после второй записи",
                api7 ?? "<null>", ok ? "PASS" : "INFO");
        }

        Record("position_after_save", "позиция строки после правки 4, записи и переоткрытия",
            $"marking={rowB?.Marking ?? "<нет>"}, position={rowB?.Position ?? "<null>"}",
            rowB?.Position is not null ? "PASS" : "FAIL");
        Record("position_neighbours", "позиции строк раздела для сверки с нумерацией документа",
            string.Join("; ", rows.Select(r => $"{r.Marking}={r.Position}")), "INFO");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Writes one extra column of a MODEL row through the same API5 edit route, in its own copy of
    /// the assembly, so the model road is measured instead of assumed.</summary>
    /// <remarks>DOC: <c>ksdocument3d_getspecification.html</c> (the model's own specification),
    /// <c>ksspecification_ksspcobjectedit.html</c>, <c>ksspecification_kssetspcobjectcolumntext.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void ModelColumnsWrite()
    {
        Directory.CreateDirectory(ModelColumnsDir);
        var source = Path.Combine(RepoRoot(), "scratch", "spec-probe", "fixture");
        foreach (var name in new[] { "spec-asm.m3d", "p1.m3d", "p2.m3d" })
        {
            File.Copy(Path.Combine(source, name), Path.Combine(ModelColumnsDir, name), overwrite: true);
        }

        var (doc5, doc7) = OpenModel(ModelColumnsPath, "model_columns");
        if (doc5 is null || doc7 is null)
        {
            return;
        }

        var description = DescriptionOf(doc7, "model_columns");
        if (description is not { } active)
        {
            doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        RecordStyleColumns(active, "model_columns");
        RecordTuning(active, SectionFor(active), "model_columns_tuning");
        if (active.BaseObjects is ISpecificationBaseObjects baseObjects)
        {
            var created = CreateRow(baseObjects, SectionFor(active), ModelColumnsRow, "Колонки модели", "2");
            Record("model_columns_row", $"строка {ModelColumnsRow} создана в модели",
                created is null ? "null" : created.UniqueNumber.ToString(CultureInfo.InvariantCulture),
                created is not null ? "PASS" : "FAIL");
        }

        var specification = doc5.GetSpecification() as ksSpecification;
        Record("model_columns_route", "ksDocument3D.GetSpecification() даёт ksSpecification",
            specification is null ? "нет" : "ksSpecification", specification is null ? "FAIL" : "PASS");
        if (specification is null)
        {
            doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var row = ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ModelColumnsRow);
        if (row is null)
        {
            Record("model_columns_row_found", "строка модели найдена в описании", "не найдена", "FAIL");
            doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var unique = (double)row.UniqueNumber;
        foreach (var (type, key, value) in EditedColumns)
        {
            if (ColumnOf(row, type) is null)
            {
                Record($"model_columns_write_{key}", $"в стиле модели нет колонки {key}", "колонки нет", "INFO");
                continue;
            }

            var answer = EditColumn(specification, unique, type, value);
            Record($"model_columns_write_{key}", $"правка пишет колонку модели {key} = «{value}»", answer.Text,
                answer.Setter == 1 ? "PASS" : "FAIL");
        }

        // In-session read-back, so «the setter did not write» and «the file dropped it» stay apart.
        var reread = ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ModelColumnsRow);
        foreach (var (type, key, _) in EditedColumns)
        {
            Record($"model_columns_session_api7_{key}", $"API7 читает колонку модели {key} в сеансе правки",
                (reread is null ? null : ReadFrom(reread, type)) ?? "<null>", "INFO");
            Record($"model_columns_session_api5_{key}", $"API5 читает колонку модели {key} в сеансе правки",
                Api5Text(specification, unique, type) ?? "<null>", "INFO");
        }

        SaveModel(doc5, ModelColumnsPath);
        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens the model copy: which of the written columns are still there.</summary>
    private void ModelColumnsRead()
    {
        var (doc5, doc7) = OpenModel(ModelColumnsPath, "model_columns_final");
        if (doc5 is null || doc7 is null)
        {
            return;
        }

        var description = DescriptionOf(doc7, "model_columns_final");
        if (description is { } active)
        {
            RecordStyleColumns(active, "model_columns_final");
        }

        var row = ObjectsOf(doc7.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == ModelColumnsRow);
        Record("model_columns_persisted_row", $"строка {ModelColumnsRow} пережила сохранение",
            row is null ? "<нет>" : $"unique={row.UniqueNumber}, name={ReadColumn(row, ksSpecificationColumnTypeEnum.ksSColumnName)}",
            row is null ? "FAIL" : "PASS");
        foreach (var (type, key, value) in EditedColumns)
        {
            var api7 = row is null ? null : ReadFrom(row, type);
            var ok = api7 is not null && ColumnMatches(value, api7);
            Record($"model_columns_persisted_{key}", $"колонка модели {key} = «{value}» в файле",
                api7 ?? "<null>", ok ? "PASS" : "INFO");
        }

        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document settings that decide whether the document itself puts positions.</summary>
    /// <remarks>DOC: <c>ispecificationtuning_calculateposition.html</c> (TRUE - the automatic calculation
    /// does NOT happen, FALSE - it does), <c>ispecificationtuning_calculatezone.html</c> (TRUE - the zone
    /// numbers are updated by the automatic position numbering), <c>ispecificationtuning_sections.html</c>
    /// («доступно только для чтения»), <c>ispecificationtuningsection_putposition.html</c> (TRUE - the section
    /// allows the automatic calculation of positions), <c>ispecificationtuning_initialposition.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void RecordTuning(ISpecificationDescription description, int section, string label)
    {
        Record($"{label}_calculateposition",
            "ISpecificationTuning.CalculatePosition (TRUE - расчета нет, FALSE - расчет автоматический)",
            Safe(() => description.SpecificationTuning?.CalculatePosition) ?? "<null>", "INFO");
        Record($"{label}_calculatezone",
            "ISpecificationTuning.CalculateZone (TRUE - зоны обновляются автоматической простановкой позиций)",
            Safe(() => description.SpecificationTuning?.CalculateZone) ?? "<null>", "INFO");
        Record($"{label}_initialposition", "ISpecificationTuning.InitialPosition",
            Safe(() => description.SpecificationTuning?.InitialPosition) ?? "<null>", "INFO");
        Record($"{label}_linktype", "ISpecificationTuning.LinkType",
            Safe(() => description.SpecificationTuning?.LinkType) ?? "<null>", "INFO");

        var sections = SafeRef(() => description.SpecificationTuning?.Sections);
        if (sections is null)
        {
            Record($"{label}_tuning_sections", "ISpecificationTuning.Sections доступен", "null", "INFO");
            return;
        }

        for (var index = 0; index < sections.Count; index++)
        {
            var item = SafeRef(() => sections[index] as ISpecificationTuningSection);
            var number = item is null ? null : Safe(() => item.Number);
            if (item is null || number != section.ToString(CultureInfo.InvariantCulture))
            {
                continue;
            }

            Record($"{label}_section_putposition", $"раздел {section}: ISpecificationTuningSection.PutPosition",
                Safe(() => item.PutPosition) ?? "<null>", "INFO");
            Record($"{label}_section_independentposition",
                $"раздел {section}: ISpecificationTuningSection.IndependentPosition",
                Safe(() => item.IndependentPosition) ?? "<null>", "INFO");
        }
    }

    /// <summary>Writes one column in its own edit session and names what every documented call answered.</summary>
    private static (int Setter, string Text) EditColumn(
        ksSpecification specification, double unique, ksSpecificationColumnTypeEnum type, string value)
    {
        var pointer = specification.ksGetSpcObject(unique);
        if (pointer == 0)
        {
            return (0, "ksGetSpcObject вернул 0");
        }

        var edit = specification.ksSpcObjectEdit(pointer);
        var setter = type == ksSpecificationColumnTypeEnum.ksSColumnMass
            ? specification.ksSpcMassa(value)
            : specification.ksSetSpcObjectColumnText((int)type, 1, 0, value);
        var end = specification.ksSpcObjectEnd();
        return (setter, $"pointer={pointer}, edit={edit}, set={setter}, end={end}");
    }

    private static string? Api5Text(ksSpecification specification, double unique, ksSpecificationColumnTypeEnum type)
    {
        var pointer = specification.ksGetSpcObject(unique);
        return pointer == 0
            ? null
            : Safe(() => specification.ksGetSpcObjectColumnText(pointer, (int)type, 1, 0));
    }

    private static ISpecificationColumn? ColumnOf(ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type)
    {
        foreach (var columns in new[] { SafeColumns(item, additional: false), SafeColumns(item, additional: true) })
        {
            if (columns is not null && SafeRef(() => columns.Column[type, 1, 0]) is ISpecificationColumn column)
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>The value the document answered equals what was written: a mass compares AS a number.</summary>
    private static bool ColumnMatches(string written, string? readBack)
    {
        if (readBack is null)
        {
            return false;
        }

        if (string.Equals(written.Trim(), readBack.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return double.TryParse(written.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
               && double.TryParse(readBack.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture,
                   out var right)
               && Math.Abs(left - right) < 1e-9;
    }

    private ISpecificationBaseObject? CreateSpwRow(
        ISpecificationBaseObjects baseObjects, int section, string marking, string name, string quantity,
        string position)
    {
        var created = CreateRow(baseObjects, section, marking, name, quantity);
        if (created is null)
        {
            return null;
        }

        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnPosition, position);
        var updated = created.Update();
        Record($"columns_row_position_{marking}", $"Update() после позиции строки {marking}", updated.ToString(),
            updated ? "PASS" : "FAIL");
        return updated ? created : null;
    }

    private ISpecificationBaseObject? CreateRow(
        ISpecificationBaseObjects baseObjects, int section, string marking, string name, string quantity)
    {
        var created = baseObjects.Add(section, 0);
        if (created is null)
        {
            Record($"columns_row_{marking}", $"строка {marking} создана в коллекции", "null", "FAIL");
            return null;
        }

        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, marking);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, name);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, quantity);
        var updated = created.Update();
        Record($"columns_row_{marking}", $"Update() создаёт строку {marking} в документе", updated.ToString(),
            updated ? "PASS" : "FAIL");
        return updated ? created : null;
    }

    /// <summary>The API5 specification of the ACTIVE document: the edit route needs the document active.</summary>
    /// <remarks>DOC: <c>kompasobject_spcactivedocument.html</c> («Если документ спецификации не активен,
    /// функция возвращает NULL»), <c>ksspcdocument_getspecification.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private ksSpecification? ActivateSpecification(IKompasDocument spw, string label)
    {
        _app7!.ActiveDocument = spw;
        var document = _app5!.SpcActiveDocument() as ksSpcDocument;
        var specification = document?.GetSpecification() as ksSpecification;
        Record($"{label}_api5_route", "SpcActiveDocument().GetSpecification() на активном .spw",
            specification is null ? "null" : "ksSpecification", specification is null ? "FAIL" : "PASS");
        return specification;
    }

    /// <summary>Reads a member that once throws is «not read», without inventing a value.</summary>
    private static T? SafeRef<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            return null;
        }
    }
}
