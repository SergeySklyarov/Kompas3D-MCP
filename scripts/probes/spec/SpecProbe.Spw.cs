using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace SpecProbe;

/// <summary>`.spw` stages: write rows INTO a specification document, extra columns, an edit through
/// each API, a delete through each API, rebuild, and the documented component link.</summary>
/// <remarks>WHY A COPY IN A SANDBOX: the writing stages mutate the file, so the measured file is a copy
/// of a fixture and never a file under docs/acceptance. WHY SEPARATE PROCESSES: KOMPAS keeps a closed
/// document loaded while a reference lives, so "persisted" is only measurable by another process.
/// DOC: the SDK mirror pages cited in each method. History: docs/decisions/bom.md#spec-spw</remarks>
internal sealed partial class SpecProbe
{
    // Values are distinct per stage, so a read that returns a neighbouring stage's text is visible.
    private const string SpwRowAMarking = "SPW.501.001";
    private const string SpwRowAName = "Корпус .spw";
    private const string SpwRowACount = "4";
    private const string SpwRowAFormat = "А4";
    private const string SpwRowANote = "Примечание .spw";
    private const string SpwRowAMass = "1,25";

    private const string SpwRowBMarking = "SPW.501.002";
    private const string SpwRowBName = "Опора .spw";
    private const string SpwRowBCount = "2";

    private const string SpwRowANameApi7 = "Корпус API7";
    private const string SpwRowACountApi7 = "6";
    private const string SpwRowBNameApi5 = "Опора API5";
    private const string SpwRowBCountApi5 = "11";

    private sealed record SpwRow(double Unique, string? Section, string? Marking, string? Name,
        string? Count, string? Position, string? Format, string? Note, string? Mass);

    private string SpwSandboxPath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "spec.spw");

    private string SpwFixturePath => Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "fixture", "base.spw");

    /// <summary>One `.spw` stage; every stage opens the working copy by path and closes it again.</summary>
    private void RunSpwStage(string stage)
    {
        switch (stage)
        {
            case "spw-build":
                SpwBuild();
                break;
            case "spw-create":
                SpwCreate();
                break;
            case "spw-reopen":
                SpwReopen();
                break;
            case "spw-api5":
                SpwApi5();
                break;
            case "spw-delete":
                SpwDelete();
                break;
            case "spw-final":
                SpwFinal();
                break;
            case "spw-sync":
                SpwSync();
                break;
            case "spw-sync-final":
                SpwSyncFinal();
                break;
            case "spw-extra":
                SpwExtra();
                break;
            case "spw-extra-final":
                SpwExtraFinal();
                break;
            case "spw-link":
                SpwLink();
                break;
            default:
                Record("stage", "известное имя стадии .spw", stage, "FAIL");
                break;
        }
    }

    /// <summary>Builds the fixture: an empty specification document and a sandbox copy of it.</summary>
    private void SpwBuild()
    {
        var fixture = SpwFixturePath;
        var sandbox = SpwSandboxPath;
        Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox)!);
        foreach (var path in new[] { fixture, sandbox })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        var documents = _app7!.Documents as IDocuments;
        var spw = documents?.Add(DocumentTypeEnum.ksDocumentSpecification, false);
        Record("spw_add", "IDocuments.Add(ksDocumentSpecification) даёт IKompasDocument",
            spw is null ? "null" : spw.DocumentType.ToString(),
            spw is null ? "FAIL" : "PASS");
        if (spw is null)
        {
            return;
        }

        Record("spw_type", "тип документа - спецификация",
            spw.DocumentType == DocumentTypeEnum.ksDocumentSpecification ? "ksDocumentSpecification" : spw.DocumentType.ToString(),
            spw.DocumentType == DocumentTypeEnum.ksDocumentSpecification ? "PASS" : "FAIL");

        // The description of a fresh .spw may already exist; Add("", 0, "") is the documented default.
        var descriptions = spw.SpecificationDescriptions;
        var description = descriptions?.Active;
        Record("spw_description_exists", "у нового .spw описание спецификации уже есть",
            description is null ? "null" : $"style={description.StyleID}",
            description is null ? "INFO" : "PASS");
        description ??= descriptions?.Add(string.Empty, 0, string.Empty);
        if (description is null)
        {
            Record("spw_description", "описание спецификации .spw доступно", "null", "FAIL");
            return;
        }

        var sections = StyleSections(description);
        Record("spw_sections", "стиль .spw перечисляет разделы",
            string.Join("; ", sections.Select(s => $"{s.Number}={s.Name}")), sections.Count > 0 ? "PASS" : "FAIL");
        RecordStyleColumns(description, "spw");

        spw.SaveAs(fixture);
        Record("spw_fixture_saved", "фикстура .spw записана на диск", File.Exists(fixture).ToString(), "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);

        File.Copy(fixture, sandbox, overwrite: true);
        Record("spw_sandbox_copy", "рабочая копия в песочнице", File.Exists(sandbox).ToString(),
            File.Exists(sandbox) ? "PASS" : "FAIL");
    }

    /// <summary>Creates two rows with the four standard columns and writes three extra columns on A.</summary>
    /// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c> (the object enters the collection and appears
    /// in the document only after <c>Update</c>), <c>ispecificationobject_columns.html</c> and
    /// <c>ispecificationobject_additionalcolumns.html</c> (main and additional column collections),
    /// <c>ksspecificationcolumntypeenum.html</c> (the 17 column types).
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwCreate()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-create");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "spw");
        if (description is not { } active)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var section = SectionFor(active);
        if (active.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            Record("spw_baseobjects", "ISpecificationDescription.BaseObjects даёт ISpecificationBaseObjects",
                "не приводится", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var createdA = baseObjects.Add(section, 0);
        if (createdA is null)
        {
            Record("spw_create_a", "строка A создана в коллекции", "null", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        SetText(createdA, ksSpecificationColumnTypeEnum.ksSColumnMark, SpwRowAMarking);
        SetText(createdA, ksSpecificationColumnTypeEnum.ksSColumnName, SpwRowAName);
        SetText(createdA, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwRowACount);
        SetText(createdA, ksSpecificationColumnTypeEnum.ksSColumnPosition, "1");
        var updatedA = createdA.Update();
        Record("spw_create_a", "API7: строка A создана в .spw, Update() вернул true", updatedA.ToString(),
            updatedA ? "PASS" : "FAIL");

        DescribeColumns(createdA, "spw_a");
        WriteExtraColumns(createdA, "spw_a");

        var createdB = baseObjects.Add(section, 0);
        if (createdB is not null)
        {
            SetText(createdB, ksSpecificationColumnTypeEnum.ksSColumnMark, SpwRowBMarking);
            SetText(createdB, ksSpecificationColumnTypeEnum.ksSColumnName, SpwRowBName);
            SetText(createdB, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwRowBCount);
            var updatedB = createdB.Update();
            Record("spw_create_b", "API7: строка B создана в .spw, Update() вернул true", updatedB.ToString(),
                updatedB ? "PASS" : "FAIL");
        }
        else
        {
            Record("spw_create_b", "строка B создана в коллекции", "null", "FAIL");
        }

        var rows = ReadSpwRows(spw, "spw-created");
        Record("spw_created_rows", "в .spw две строки A и B", rows.Count.ToString(CultureInfo.InvariantCulture),
            rows.Count == 2 ? "PASS" : "FAIL");
        Record("spw_created_a", $"строка A читается как {SpwRowAMarking} / {SpwRowAName}",
            Describe(rows.FirstOrDefault(r => r.Marking == SpwRowAMarking)),
            rows.Any(r => r.Marking == SpwRowAMarking && r.Name == SpwRowAName) ? "PASS" : "FAIL");

        spw.Save();
        Record("spw_saved", "IKompasDocument.Save() .spw", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reads the copy in a new process and edits row A through API7.</summary>
    /// <remarks>DOC: <c>ispecificationcolumnitem_value.html</c> (the element carries the value) and
    /// <c>ispecificationobject_update.html</c> (the page that commits the change).
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwReopen()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-reopen");
        if (spw is null)
        {
            return;
        }

        var rows = ReadSpwRows(spw, "spw-reopened");
        Record("spw_rows_persisted", "обе строки пережили сохранение и переоткрытие",
            rows.Count.ToString(CultureInfo.InvariantCulture), rows.Count == 2 ? "PASS" : "FAIL");
        var rowA = rows.FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_a_persisted", $"строка A со значениями {SpwRowAName}/{SpwRowACount}",
            Describe(rowA), rowA is not null && rowA.Name == SpwRowAName && rowA.Count == SpwRowACount ? "PASS" : "FAIL");
        Record("spw_a_extra_persisted", $"доп. колонки A: формат «{SpwRowAFormat}», примечание «{SpwRowANote}»",
            $"format={rowA?.Format ?? "<нет>"}, note={rowA?.Note ?? "<нет>"}, mass={rowA?.Mass ?? "<нет>"}",
            rowA is not null && rowA.Format == SpwRowAFormat && rowA.Note == SpwRowANote ? "PASS" : "FAIL");

        var target = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects)
            .FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == SpwRowAMarking);
        if (target is null)
        {
            Record("spw_api7_edit", "API7: строка A найдена для правки", "не найдена", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        SetText(target, ksSpecificationColumnTypeEnum.ksSColumnName, SpwRowANameApi7);
        SetText(target, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwRowACountApi7);
        var updated = target.Update();
        Record("spw_api7_edit", "API7: ColumnItems.Value + Update() на существующей строке .spw",
            updated.ToString(), "INFO");
        var inSession = ReadSpwRows(spw, "spw-api7-edited");
        var edited = inSession.FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_api7_edit_session", "в сессии строка A показывает новое имя",
            Describe(edited), edited is not null && edited.Name == SpwRowANameApi7 ? "PASS" : "FAIL");

        spw.Save();
        Record("spw_saved", "IKompasDocument.Save() после правки API7", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Edits row B through API5 on the ACTIVE document, which is the route API5 requires.</summary>
    /// <remarks>DOC: <c>ksspcdocument_getspecification.html</c> (the specification of the spec document),
    /// <c>ksspecification_ksspcobjectedit.html</c> and <c>_ksspcobjectend.html</c> (the edit mode and its
    /// completion), <c>_kssetspcobjectcolumntext.html</c>, <c>_ksspccount.html</c>, <c>_ksspcmassa.html</c>.
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwApi5()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-api5");
        if (spw is null)
        {
            return;
        }

        var rows = ReadSpwRows(spw, "spw-before-api5");
        var rowA = rows.FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_api7_edit_persisted", $"правка API7 «{SpwRowANameApi7}»/{SpwRowACountApi7} на диске",
            Describe(rowA), rowA is not null && rowA.Name == SpwRowANameApi7 ? "PASS" : "FAIL");

        _app7!.ActiveDocument = spw;
        var document = _app5!.SpcActiveDocument() as ksSpcDocument;
        Record("spw_api5_document", "SpcActiveDocument() на активном .spw даёт ksSpcDocument",
            document is null ? "null" : "ksSpcDocument", document is null ? "FAIL" : "PASS");
        if (document is null)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var specification = document.GetSpecification() as ksSpecification;
        Record("spw_api5_specification", "ksSpcDocument.GetSpecification() даёт ksSpecification",
            specification is null ? "null" : "ksSpecification", specification is null ? "FAIL" : "PASS");
        var rowB = rows.FirstOrDefault(r => r.Marking == SpwRowBMarking);
        if (specification is null || rowB is null)
        {
            Record("spw_api5_edit", "API5: строка B найдена", rowB is null ? "не найдена" : "есть", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var pointer = specification.ksGetSpcObject(rowB.Unique);
        Record("spw_api5_pointer", "ksGetSpcObject(UniqueNumber) строки B", pointer.ToString(CultureInfo.InvariantCulture),
            pointer != 0 ? "PASS" : "FAIL");
        var edit = specification.ksSpcObjectEdit(pointer);
        var name = specification.ksSetSpcObjectColumnText((int)ksSpecificationColumnTypeEnum.ksSColumnName, 1, 0, SpwRowBNameApi5);
        var count = specification.ksSpcCount(1, SpwRowBCountApi5);
        // Mass is an ADDITIONAL column: the page says the method answers 0 when the object has no such
        // column, so a zero here is a measured fact about the style, not a probe failure.
        var mass = specification.ksSpcMassa(SpwRowAMass);
        var end = specification.ksSpcObjectEnd();
        Record("spw_api5_edit", "API5: ksSpcObjectEdit + ksSetSpcObjectColumnText + ksSpcCount + ksSpcMassa + ksSpcObjectEnd",
            $"edit={edit}, name={name}, count={count}, mass={mass}, end={end}",
            edit == 1 && name == 1 && count == 1 ? "PASS" : "FAIL");
        Record("spw_api5_mass", "ksSpcMassa заполняет дополнительную колонку Масса", mass.ToString(CultureInfo.InvariantCulture),
            "INFO");

        spw.Save();
        Record("spw_saved", "IKompasDocument.Save() после правки API5", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Deletes row A through API7 <c>Delete</c> and row B through API5 <c>ksDeleteObj</c>.</summary>
    /// <remarks>DOC: <c>ispecificationobject_delete.html</c> (delete from document and collection) and
    /// <c>ksspcdocument_ksdeleteobj.html</c> (delete a specification object by pointer); the latter is the
    /// only API5 delete: <c>ksSpecification - методы</c> lists no delete member.
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwDelete()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-delete");
        if (spw is null)
        {
            return;
        }

        var rows = ReadSpwRows(spw, "spw-before-delete");
        var rowB = rows.FirstOrDefault(r => r.Marking == SpwRowBMarking);
        Record("spw_api5_edit_persisted", $"правка API5 «{SpwRowBNameApi5}»/{SpwRowBCountApi5} на диске",
            Describe(rowB), rowB is not null && rowB.Name == SpwRowBNameApi5 ? "PASS" : "FAIL");
        Record("spw_api5_mass_persisted", $"доп. колонка Масса после ksSpcMassa = «{SpwRowAMass}»",
            rowB?.Mass ?? "<нет>", rowB is not null && rowB.Mass == SpwRowAMass ? "PASS" : "INFO");

        var objects = ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects);
        var target = objects.FirstOrDefault(o => ReadColumn(o, ksSpecificationColumnTypeEnum.ksSColumnMark) == SpwRowAMarking);
        if (target is not null)
        {
            var deleted = target.Delete();
            Record("spw_api7_delete", "API7: ISpecificationObject.Delete() строки A", deleted.ToString(),
                deleted ? "PASS" : "FAIL");
        }
        else
        {
            Record("spw_api7_delete", "API7: строка A найдена для удаления", "не найдена", "FAIL");
        }

        var afterApi7 = ReadSpwRows(spw, "spw-after-api7-delete");
        Record("spw_api7_delete_gone", "после Delete() строки A нет в списке",
            afterApi7.Count.ToString(CultureInfo.InvariantCulture), afterApi7.Count == 1 ? "PASS" : "FAIL");

        _app7!.ActiveDocument = spw;
        var document = _app5!.SpcActiveDocument() as ksSpcDocument;
        var specification = document?.GetSpecification() as ksSpecification;
        var rowBAlive = afterApi7.FirstOrDefault(r => r.Marking == SpwRowBMarking);
        if (specification is not null && rowBAlive is not null)
        {
            var pointer = specification.ksGetSpcObject(rowBAlive.Unique);
            var result = pointer == 0 ? 0 : document!.ksDeleteObj(pointer);
            Record("spw_api5_delete", "API5: ksSpcDocument.ksDeleteObj(pointer) строки B", result.ToString(CultureInfo.InvariantCulture),
                result == 1 ? "PASS" : "INFO");
        }
        else
        {
            Record("spw_api5_delete", "API5: ksDeleteObj применим к строке B",
                specification is null ? "нет ksSpecification" : "строка B не найдена", "INFO");
        }

        var afterApi5 = ReadSpwRows(spw, "spw-after-api5-delete");
        Record("spw_api5_delete_gone", "после ksDeleteObj строк в .spw нет",
            afterApi5.Count.ToString(CultureInfo.InvariantCulture), afterApi5.Count == 0 ? "PASS" : "FAIL");

        spw.Save();
        Record("spw_saved", "IKompasDocument.Save() после удалений", "вызван", "PASS");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reads the copy after the delete, then rebuilds the document and reads again.</summary>
    /// <remarks>DOC: <c>ispecificationdocument_rebuilddocument.html</c> (rebuild), <c>_crossed.html</c>
    /// (the "needs rebuild" flag), <c>idocuments_documentsynchronize.html</c> («Реализовано только для
    /// спецификации») and <c>ispecificationdocument_attacheddocuments.html</c>.
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwFinal()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-final");
        if (spw is null)
        {
            return;
        }

        var rows = ReadSpwRows(spw, "spw-final");
        Record("spw_deleted_persisted", "удаления пережили сохранение: строк нет",
            rows.Count.ToString(CultureInfo.InvariantCulture), rows.Count == 0 ? "PASS" : "FAIL");

        if (spw is ISpecificationDocument specificationDocument)
        {
            var crossedBefore = Safe(() => specificationDocument.Crossed);
            var rebuild = specificationDocument.RebuildDocument();
            var crossedAfter = Safe(() => specificationDocument.Crossed);
            Record("spw_rebuild", "ISpecificationDocument.RebuildDocument() на .spw без строк",
                $"crossed {crossedBefore}->{crossedAfter}, rebuild={rebuild}", "INFO");
            var afterRebuild = ReadSpwRows(spw, "spw-after-rebuild");
            Record("spw_rebuild_rows", "перестроение не вернуло удалённые строки",
                afterRebuild.Count.ToString(CultureInfo.InvariantCulture), afterRebuild.Count == 0 ? "PASS" : "FAIL");
        }
        else
        {
            Record("spw_rebuild", "IKompasDocument приводится к ISpecificationDocument", "не приводится", "INFO");
        }

        var sync = Safe(() => (_app7!.Documents as IDocuments)?.DocumentSynchronize);
        Record("spw_documentsynchronize", "IDocuments.DocumentSynchronize у сеанса", sync ?? "<null>", "INFO");
        var name = Safe(() => spw.SpecificationDescriptions?.Active?.SpecificationDocumentName);
        Record("spw_specification_document_name", "SpecificationDocumentName у .spw", name ?? "<null>", "INFO");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Writes one row into a FRESH copy and rebuilds the document with the row present.</summary>
    /// <remarks>The question the order asks is whether KOMPAS drops a written row when the document is
    /// rebuilt or reopened. DOC: <c>ispecificationdocument_rebuilddocument.html</c> (rebuild),
    /// <c>_crossed.html</c> (needs rebuild), <c>idocuments_documentsynchronize.html</c> («Реализовано
    /// только для спецификации»), <c>ispecificationtuning_linktype.html</c> (link modes).
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwSync()
    {
        File.Copy(SpwFixturePath, SpwSandboxPath, overwrite: true);
        var spw = OpenSpw(SpwSandboxPath, "spw-sync");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "spw");
        if (description is not { } active || active.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var created = baseObjects.Add(SectionFor(active), 0);
        if (created is null)
        {
            Record("spw_sync_create", "строка для проверки перестроения создана", "null", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, SpwRowAMarking);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, SpwRowAName);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwRowACount);
        var updated = created.Update();
        spw.Save();
        Record("spw_sync_create", "строка записана и сохранена до перестроения",
            $"update={updated}, rows={ReadSpwRows(spw, "spw-before-rebuild").Count}", "PASS");

        var link = Safe(() => description.SpecificationTuning?.LinkType);
        Record("spw_linktype", "ISpecificationTuning.LinkType у .spw", link ?? "<null>", "INFO");
        var sync = Safe(() => (_app7!.Documents as IDocuments)?.DocumentSynchronize);
        Record("spw_documentsynchronize", "IDocuments.DocumentSynchronize сеанса", sync ?? "<null>", "INFO");

        if (spw is ISpecificationDocument specificationDocument)
        {
            var crossedBefore = Safe(() => specificationDocument.Crossed);
            var rebuild = specificationDocument.RebuildDocument();
            var crossedAfter = Safe(() => specificationDocument.Crossed);
            Record("spw_rebuild_with_row", "RebuildDocument() при живой строке",
                $"crossed {crossedBefore}->{crossedAfter}, rebuild={rebuild}", rebuild ? "PASS" : "INFO");
            var rows = ReadSpwRows(spw, "spw-after-rebuild");
            Record("spw_rebuild_keeps_row", "перестроение не потеряло записанную строку",
                rows.Count.ToString(CultureInfo.InvariantCulture), rows.Count == 1 ? "PASS" : "FAIL");
        }

        spw.Save();
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens the copy written by <c>spw-sync</c>: the row must be in the file, with its values.</summary>
    private void SpwSyncFinal()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-sync-final");
        if (spw is null)
        {
            return;
        }

        var rows = ReadSpwRows(spw, "spw-sync-reopened");
        var row = rows.FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_sync_row_persisted", $"строка {SpwRowAMarking} / {SpwRowAName} / {SpwRowACount} в файле",
            Describe(row), row is not null && row.Name == SpwRowAName && row.Count == SpwRowACount ? "PASS" : "FAIL");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Writes ALL columns of a row before the FIRST <c>Update</c> and reads the same columns
    /// through three routes, so "written at creation" and "read back" are separate measurements.</summary>
    /// <remarks>The earlier stages wrote extra columns AFTER the row existed. This one asks the other
    /// question: does a main column beyond the four survive when it is set before the object is created?
    /// DOC: <c>ispecificationbaseobjects_add.html</c> (the object appears in the document only after
    /// <c>Update</c>), <c>ispecificationcolumnitem_value.html</c>, <c>ksspecification_ksgetspcobjectcolumntext.html</c>.
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwExtra()
    {
        var path = Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "extra.spw");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(SpwFixturePath, path, overwrite: true);
        var spw = OpenSpw(path, "spw-extra");
        if (spw is null)
        {
            return;
        }

        var description = DescriptionOf(spw, "spw");
        if (description is not { } active || active.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var created = baseObjects.Add(SectionFor(active), 0);
        if (created is null)
        {
            Record("spw_extra_create", "строка создана в коллекции", "null", "FAIL");
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, SpwRowAMarking);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, SpwRowAName);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, SpwRowACount);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnFormat, SpwRowAFormat);
        SetText(created, ksSpecificationColumnTypeEnum.ksSColumnNote, SpwRowANote);
        var updated = created.Update();
        Record("spw_extra_create", "API7: строка с Форматом и Примечанием записана ДО первого Update()",
            updated.ToString(), updated ? "PASS" : "FAIL");

        Record("spw_extra_handle", "та же ручка после Update(): формат и примечание",
            $"format={ReadFrom(created, ksSpecificationColumnTypeEnum.ksSColumnFormat)}, " +
            $"note={ReadFrom(created, ksSpecificationColumnTypeEnum.ksSColumnNote)}", "INFO");
        var reread = ReadSpwRows(spw, "spw-extra-reread").FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_extra_reread", "список документа после Update(): формат и примечание",
            Describe(reread), reread is not null && reread.Format == SpwRowAFormat ? "PASS" : "FAIL");

        var api5 = ReadSpwColumnsViaApi5(spw, reread?.Unique ?? 0, "spw_extra");
        Record("spw_extra_api5", "API5 читает те же колонки в той же сессии", api5, "INFO");

        spw.Save();
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens the file written by <c>spw-extra</c> and reads the extra columns again.</summary>
    private void SpwExtraFinal()
    {
        var path = Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "sandbox", "extra.spw");
        var spw = OpenSpw(path, "spw-extra-final");
        if (spw is null)
        {
            return;
        }

        var row = ReadSpwRows(spw, "spw-extra-reopened").FirstOrDefault(r => r.Marking == SpwRowAMarking);
        Record("spw_extra_persisted", $"в файле: {SpwRowAFormat} / {SpwRowANote}", Describe(row),
            row is not null && row.Format == SpwRowAFormat && row.Note == SpwRowANote ? "PASS" : "FAIL");
        Record("spw_extra_api5_persisted", "API5 в новой сессии",
            ReadSpwColumnsViaApi5(spw, row?.Unique ?? 0, "spw_extra_final"), "INFO");
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reads marking, format, note and mass of one `.spw` row through API5, which is an
    /// independent route: <c>ksGetSpcObjectColumnText</c> by column type.</summary>
    /// <remarks>DOC: <c>ksspcdocument_getspecification.html</c>, <c>ksspecification_ksgetspcobjectcolumntext.html</c>
    /// («Получить строку с текстом из определенной колонки объекта спецификации»).
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private string ReadSpwColumnsViaApi5(IKompasDocument spw, double unique, string label)
    {
        if (unique == 0)
        {
            return "строка не найдена";
        }

        _app7!.ActiveDocument = spw;
        var document = _app5!.SpcActiveDocument() as ksSpcDocument;
        var specification = document?.GetSpecification() as ksSpecification;
        if (specification is null)
        {
            return "нет ksSpecification";
        }

        var pointer = specification.ksGetSpcObject(unique);
        if (pointer == 0)
        {
            return "ksGetSpcObject вернул 0";
        }

        string Text(ksSpecificationColumnTypeEnum type) =>
            Safe(() => specification.ksGetSpcObjectColumnText(pointer, (int)type, 1, 0)) ?? "<null>";

        return $"mark={Text(ksSpecificationColumnTypeEnum.ksSColumnMark)}, " +
               $"format={Text(ksSpecificationColumnTypeEnum.ksSColumnFormat)}, " +
               $"note={Text(ksSpecificationColumnTypeEnum.ksSColumnNote)}, " +
               $"mass={Text(ksSpecificationColumnTypeEnum.ksSColumnMass)} ({label})";
    }

    /// <summary>Checks the documented component link: <c>IPart7</c> -> QI <c>IPropertyKeeper</c> ->
    /// <c>AddWithParam</c> with <c>ISpecificationObjectCreateParam</c>.</summary>
    /// <remarks>DOC: <c>ipropertykeeper.html</c> («Данный интерфейс можно получить у IPart7, IBody7,
    /// IEmbodiment, IMacroObject, IInsertionObject или IKompasDocument2D посредством вызова метода
    /// IUnknown::QueryInterface»), <c>ispecificationobjectcreateparam.html</c> (obtained with
    /// <c>IKompasDocument1::GetInterface</c>), <c>ispecificationbaseobjects_addwithparam.html</c>.
    /// History: docs/decisions/bom.md#spec-spw</remarks>
    private void SpwLink()
    {
        var spw = OpenSpw(SpwSandboxPath, "spw-link");
        if (spw is null)
        {
            return;
        }

        var documents = _app7!.Documents as IDocuments;
        var part = documents?.Add(DocumentTypeEnum.ksDocumentPart, false);
        var container = part as IKompasDocument3D;
        var top = container?.TopPart as IPart7;
        Record("link_tippart", "IDocuments.Add(ksDocumentPart) → TopPart даёт IPart7",
            top is null ? "null" : top.Name, top is null ? "FAIL" : "PASS");
        IPropertyKeeper? keeper = null;
        if (top is not null)
        {
            try
            {
                keeper = (IPropertyKeeper)top;
            }
            catch (InvalidCastException ex)
            {
                Record("link_keeper", "QI(IPart7) даёт IPropertyKeeper", Describe(ex), "INFO");
            }
        }

        Record("link_keeper", "QI(IPart7) даёт IPropertyKeeper",
            keeper is null ? "null" : $"key={keeper.UniqueMetaObjectKey}",
            keeper is null ? "INFO" : "PASS");

        var description = spw.SpecificationDescriptions?.Active;
        var document1 = spw as IKompasDocument1;
        Record("link_document1", "IKompasDocument .spw отвечает QI(IKompasDocument1)",
            document1 is null ? "null" : "IKompasDocument1", document1 is null ? "INFO" : "PASS");
        var param = document1?.GetInterface(KompasAPIObjectTypeEnum.ksSpecificationObjectCreateParam);
        Record("link_createparam", "GetInterface(ksSpecificationObjectCreateParam) даёт параметры создания",
            param?.GetType().Name ?? "null", param is null ? "INFO" : "PASS");

        if (description is null || keeper is null || param is not ISpecificationObjectCreateParam createParam)
        {
            Record("link_addwithparam", "AddWithParam с IPropertyKeeper компонента", "нечем вызвать", "INFO");
            part?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        createParam.Section = SectionFor(description);
        var created = description.BaseObjects?.AddWithParam(
            (SpecificationObjectCreateParam)(object)createParam, keeper, false, null);
        Record("link_addwithparam", "AddWithParam(param, IPropertyKeeper, false) даёт строку .spw",
            created is null ? "null" : $"unique={created.UniqueNumber}", created is null ? "INFO" : "PASS");
        if (created is not null)
        {
            var updated = created.Update();
            Record("link_update", "Update() связанной строки", updated.ToString(), updated ? "PASS" : "INFO");
            Record("link_metaobjectkey", "общий ключ информационного объекта у строки и компонента",
                $"row={Safe(() => created.UniqueMetaObjectKey)}, keeper={keeper.UniqueMetaObjectKey}", "INFO");
            Record("link_sync", "SyncronizeWithProperties связанной строки",
                Safe(() => created.SyncronizeWithProperties) ?? "<null>", "INFO");
            var rows = ReadSpwRows(spw, "spw-linked");
            Record("link_rows", "в .spw строка, созданная AddWithParam",
                rows.Count.ToString(CultureInfo.InvariantCulture), rows.Count == 1 ? "PASS" : "INFO");
        }

        part?.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    // ── `.spw` helpers ──────────────────────────────────────────────────────────────────────────

    private IKompasDocument? OpenSpw(string path, string stage)
    {
        if (!File.Exists(path))
        {
            Record("spw_file", "рабочая копия .spw существует", path, "FAIL");
            return null;
        }

        var documents = _app7!.Documents as IDocuments;
        var spw = documents?.Open(path, false, false);
        Record("spw_open", "IDocuments.Open(.spw) даёт IKompasDocument",
            spw is null ? "null" : $"{spw.DocumentType}, {Path.GetFileName(spw.PathName)}",
            spw is null ? "FAIL" : "PASS");
        if (spw is null)
        {
            return null;
        }

        Record("spw_open_type", "тип открытого документа - спецификация",
            spw.DocumentType == DocumentTypeEnum.ksDocumentSpecification ? "ksDocumentSpecification" : spw.DocumentType.ToString(),
            spw.DocumentType == DocumentTypeEnum.ksDocumentSpecification ? "PASS" : "FAIL");
        Console.WriteLine($"stage {stage}: {Path.GetFileName(path)}");
        return spw;
    }

    private int SectionFor(ISpecificationDescription description)
    {
        var sections = StyleSections(description);
        var details = sections.FirstOrDefault(s => s.Name.Contains("Детали", StringComparison.Ordinal));
        if (details != default)
        {
            return details.Number;
        }

        return sections.Count > 0 ? sections[0].Number : 0;
    }

    /// <summary>Every column of the object with its type and value type, so "no such column" and "an
    /// empty column" stay different answers (the reader of the model rows already keeps them apart).</summary>
    private void RecordStyleColumns(ISpecificationDescription description, string label)
    {
        var style = Safe(() => description.SpecificationStyle);
        Record($"{label}_style", "SpecificationStyle доступен", style is null ? "null" : "есть",
            style is null ? "INFO" : "PASS");
        try
        {
            var main = description.SpecificationStyle?.Columns;
            var additional = description.SpecificationStyle?.AdditionalColumns;
            Record($"{label}_style_columns", "основные колонки стиля .spw", DescribeStyles(main), "INFO");
            Record($"{label}_style_additionalcolumns", "дополнительные колонки стиля .spw",
                DescribeStyles(additional), "INFO");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            Record($"{label}_style_columns", "колонки стиля .spw", Describe(ex), "INFO");
        }
    }

    private static string DescribeStyles(ISpecificationColumnStyles? styles)
    {
        if (styles is null)
        {
            return "<null>";
        }

        var parts = new List<string>();
        for (var index = 0; index < styles.Count; index++)
        {
            if (styles[index] is not ISpecificationColumnStyle style)
            {
                break;
            }

            parts.Add($"{style.ColumnType}[{style.Number}]={style.ValueType}");
        }

        return parts.Count == 0 ? "<пусто>" : string.Join("; ", parts);
    }

    private void DescribeColumns(ISpecificationBaseObject item, string label)
    {
        var main = new List<string>();
        var additional = new List<string>();
        try
        {
            if (item.Columns is { } columns)
            {
                for (var index = 0; index < columns.Count; index++)
                {
                    if (columns[index] is ISpecificationColumn column)
                    {
                        main.Add($"{column.ColumnType}[{column.ColumnTypeNumber}]={column.ValueType}");
                    }
                }
            }

            if (item.AdditionalColumns is { } extra)
            {
                for (var index = 0; index < extra.Count; index++)
                {
                    if (extra[index] is ISpecificationColumn column)
                    {
                        additional.Add($"{column.ColumnType}[{column.ColumnTypeNumber}]={column.ValueType}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            Record($"{label}_columns", "колонки объекта .spw", Describe(ex), "INFO");
            return;
        }

        Record($"{label}_columns", "основные колонки объекта .spw", main.Count == 0 ? "<пусто>" : string.Join("; ", main), "INFO");
        Record($"{label}_additionalcolumns", "дополнительные колонки объекта .spw",
            additional.Count == 0 ? "<пусто>" : string.Join("; ", additional), "INFO");
    }

    /// <summary>Writes three extra columns, each where its collection holds it, and records what the
    /// documented <c>Update()</c> answered. A column the style does not provide is named, not invented.</summary>
    private void WriteExtraColumns(ISpecificationBaseObject item, string label)
    {
        Record($"{label}_write_format", $"доп. колонка Формат = «{SpwRowAFormat}»",
            WriteColumn(item, ksSpecificationColumnTypeEnum.ksSColumnFormat, SpwRowAFormat), "INFO");
        Record($"{label}_write_note", $"доп. колонка Примечание = «{SpwRowANote}»",
            WriteColumn(item, ksSpecificationColumnTypeEnum.ksSColumnNote, SpwRowANote), "INFO");
        Record($"{label}_write_mass", $"доп. колонка Масса = «{SpwRowAMass}»",
            WriteColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMass, SpwRowAMass), "INFO");
        var updated = item.Update();
        Record($"{label}_write_update", "Update() после записи доп. колонок", updated.ToString(),
            updated ? "PASS" : "INFO");
        try
        {
            var rowAfter = new SpwRow(item.UniqueNumber, Safe(() => item.Section),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnPosition),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnFormat),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnNote),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnMass));
            Record($"{label}_write_read", "в сессии доп. колонки прочитаны назад", Describe(rowAfter),
                rowAfter.Format == SpwRowAFormat && rowAfter.Note == SpwRowANote ? "PASS" : "INFO");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            Record($"{label}_write_read", "в сессии доп. колонки прочитаны назад", Describe(ex), "INFO");
        }
    }

    /// <summary>Writes one column value by type: the columns collection the type lives in is searched
    /// (main first, then additional), because the style decides which collection holds it.</summary>
    private static string WriteColumn(ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type, string text)
    {
        foreach (var (columns, additional) in new[]
                 {
                     (SafeColumns(item, additional: false), false),
                     (SafeColumns(item, additional: true), true),
                 })
        {
            if (columns is null || columns.Column[type, 1, 0] is not { } column
                || column.ColumnItems is not ISpecificationColumnItems items)
            {
                continue;
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
                        ? double.Parse(text, NumberStyles.Any, new CultureInfo("ru-RU"))
                        : text;
                return additional ? "записано в дополнительные колонки" : "записано в основные колонки";
            }
        }

        return "колонки нет в стиле";
    }

    private static ISpecificationColumns? SafeColumns(ISpecificationBaseObject item, bool additional)
    {
        try
        {
            return additional ? item.AdditionalColumns : item.Columns;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return null;
        }
    }

    private static string? ReadFrom(ISpecificationBaseObject item, ksSpecificationColumnTypeEnum type)
    {
        foreach (var columns in new[] { SafeColumns(item, additional: false), SafeColumns(item, additional: true) })
        {
            if (columns is null)
            {
                continue;
            }

            try
            {
                if (columns.Column[type, 1, 0] is not { } column || column.ColumnItems is not ISpecificationColumnItems items)
                {
                    continue;
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
                continue;
            }
        }

        return null;
    }

    /// <summary>Rows as the DOCUMENT shows them, through API7 only: the stage that writes must not learn
    /// its own result from the same handle it wrote with.</summary>
    private List<SpwRow> ReadSpwRows(IKompasDocument spw, string label)
    {
        var rows = new List<SpwRow>();
        foreach (var item in ObjectsOf(spw.SpecificationDescriptions?.Active?.Objects))
        {
            rows.Add(new SpwRow(item.UniqueNumber, Safe(() => item.Section),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnPosition),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnFormat),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnNote),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnMass)));
        }

        var json = new JsonArray();
        foreach (var row in rows)
        {
            json.Add(new JsonObject
            {
                ["unique"] = row.Unique,
                ["section"] = row.Section,
                ["marking"] = row.Marking,
                ["name"] = row.Name,
                ["quantity"] = row.Count,
                ["position"] = row.Position,
                ["format"] = row.Format,
                ["note"] = row.Note,
                ["mass"] = row.Mass,
            });
        }

        _reads.Add(new JsonObject { ["label"] = label, ["rows"] = json });
        return rows;
    }

    private static string Describe(SpwRow? row) => row is null
        ? "<строки нет>"
        : $"{row.Marking} / {row.Name} / {row.Count} / pos={row.Position} / fmt={row.Format} / note={row.Note} / mass={row.Mass}";
}
