using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;

namespace SpecProbe;

/// <summary>Link stages: one row created by <c>AddWithParam</c> from a component, measured across
/// <c>Save()</c> and a reopen by ANOTHER process - the question the order asks before any product code.</summary>
/// <remarks>WHY BOTH DOCUMENT KINDS AND BOTH <c>fill_text</c> VALUES: a model row and a <c>.spw</c> row go
/// through the same API7 collection but different descriptions and files; <c>fill_text=true</c> makes the
/// document write the column texts from the object's own properties, so "the link survived" and "the texts
/// survived" are two different facts. WHY TWO COMPONENTS: two rows linked to ONE object would carry one
/// key, and a duplicate key cannot tell "both survived" from "one survived". WHY A SECOND SAVE VARIANT:
/// this project already measured that a row written into a model reaches the FILE on the next save, so
/// "not survived" and "not written yet" have to be told apart.
/// DOC: <c>ispecificationbaseobjects_addwithparam.html</c> («общий ключ информационного объекта»,
/// <c>FillText</c> - «заполнить тексты колонок объекта спецификации по свойствам объекта»),
/// <c>ipropertykeeper.html</c> (QI at <c>IPart7</c>), <c>ispecificationobjectcreateparam_section.html</c>,
/// <c>ispecificationdocument_rebuilddocument.html</c>.
/// History: docs/decisions/bom.md#spec-spw-link-row</remarks>
internal sealed partial class SpecProbe
{
    private const string LinkNote = "LNK.NOTE";
    private const string LinkModelMarking = "LNK.601.001";
    private const string LinkModelName = "Строка связи модели";
    private const string LinkModelCount = "3";
    private const string LinkSpwMarking = "LNK.602.001";
    private const string LinkSpwName = "Строка связи .spw";
    private const string LinkSpwCount = "5";

    private sealed record LinkRow(double Unique, string? Key, string? Sync, string? Section, string? Marking,
        string? Name, string? Count, string? Note);

    private string LinkDir => Path.Combine(RepoRoot(), "scratch", "spec-link-probe");

    private string LinkModelPath => Path.Combine(LinkDir, "sandbox", "model-link.m3d");

    private string LinkModelTwicePath => Path.Combine(LinkDir, "sandbox", "model-link-twice.m3d");

    private string LinkSpwPath => Path.Combine(LinkDir, "sandbox", "spw-link.spw");

    private string LinkSpwTwicePath => Path.Combine(LinkDir, "sandbox", "spw-link-twice.spw");

    private string LinkAssemblyPath => Path.Combine(LinkDir, "sandbox", "assembly-source.m3d");

    /// <summary>One link stage; every stage is a separate process, so "persisted" is never read by the
    /// session that wrote it.</summary>
    private void RunLinkStage(string stage)
    {
        switch (stage)
        {
            case "link-model-write":
                LinkModelWrite(LinkModelPath, saveTwice: false, "link_model");
                break;
            case "link-model-write-twice":
                LinkModelWrite(LinkModelTwicePath, saveTwice: true, "link_model_twice");
                break;
            case "link-model-read":
                LinkModelRead();
                break;
            case "link-spw-write":
                LinkSpwWrite(LinkSpwPath, saveTwice: false, "link_spw");
                break;
            case "link-spw-write-twice":
                LinkSpwWrite(LinkSpwTwicePath, saveTwice: true, "link_spw_twice");
                break;
            case "link-spw-read":
                LinkSpwRead();
                break;
            case "link-key-check":
                LinkKeyCheck();
                break;
            default:
                Record("stage", "известное имя стадии связи", stage, "FAIL");
                break;
        }
    }

    /// <summary>Writes two linked rows (explicit texts and <c>fill_text=true</c>) into a copy of the
    /// assembly's own specification and saves the copy - once, or twice.</summary>
    private void LinkModelWrite(string path, bool saveTwice, string label)
    {
        LinkSandbox();
        File.Copy(Path.Combine(RepoRoot(), "scratch", "spec-probe", "fixture", "spec-asm.m3d"),
            path, overwrite: true);
        var (doc5, doc7) = OpenModel(path, label);
        if (doc7 is null || doc5 is null)
        {
            return;
        }

        var description = doc7.SpecificationDescriptions?.Active;
        var section = description is null ? 0 : SectionFor(description);
        Record($"{label}_section", "раздел стиля сборки для связанных строк",
            $"{section}", section > 0 ? "PASS" : "FAIL");
        var components = Components7(doc7, label);
        LinkTwoRows(doc7, description, components, section, label,
            LinkModelMarking, LinkModelName, LinkModelCount);

        SaveModel(doc5, path);
        if (saveTwice)
        {
            SaveModel(doc5, path);
        }

        doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens both saved copies in a NEW process and compares each row's information-object key
    /// with the component's key read fresh from the assembly.</summary>
    private void LinkModelRead()
    {
        foreach (var (path, label) in new[]
                 {
                     (LinkModelPath, "link_model_read"),
                     (LinkModelTwicePath, "link_model_twice_read"),
                 })
        {
            var (doc5, doc7) = OpenModel(path, label);
            if (doc7 is null || doc5 is null)
            {
                continue;
            }

            var components = Components7(doc7, label);
            LinkRead(doc7, components, label, LinkModelMarking, LinkModelName, LinkModelCount);
            RebuildCheck(doc5, doc7, components, label);
            doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
    }

    /// <summary>Writes two linked rows into a copy of the empty <c>.spw</c>, with the component taken from a
    /// copy of the assembly - the document kind the order names second.</summary>
    private void LinkSpwWrite(string path, bool saveTwice, string label)
    {
        LinkSandbox();
        File.Copy(Path.Combine(RepoRoot(), "scratch", "spec-spw-probe", "fixture", "base.spw"),
            path, overwrite: true);
        File.Copy(Path.Combine(RepoRoot(), "scratch", "spec-probe", "fixture", "spec-asm.m3d"),
            LinkAssemblyPath, overwrite: true);

        var (assembly5, assembly7) = OpenModel(LinkAssemblyPath, label + "_assembly");
        if (assembly7 is null || assembly5 is null)
        {
            return;
        }

        var spw = OpenSpw(path, label);
        if (spw is null)
        {
            assembly7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
            return;
        }

        var description = spw.SpecificationDescriptions?.Active;
        var section = description is null ? 0 : SectionFor(description);
        Record($"{label}_section", "раздел стиля .spw для связанных строк",
            $"{section}", section > 0 ? "PASS" : "FAIL");
        var components = Components7(assembly7, label + "_assembly");
        LinkTwoRows(spw, description, components, section, label,
            LinkSpwMarking, LinkSpwName, LinkSpwCount);

        spw.Save();
        Record($"{label}_saved", "IKompasDocument.Save() .spw со связанными строками", "вызван", "PASS");
        if (saveTwice)
        {
            spw.Save();
            Record($"{label}_saved_again", "второй IKompasDocument.Save() .spw", "вызван", "PASS");
        }

        spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        assembly7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Reopens both <c>.spw</c> copies and the assembly they were linked to, in a NEW process, and
    /// compares the row keys with the component keys.</summary>
    private void LinkSpwRead()
    {
        var (assembly5, assembly7) = OpenModel(LinkAssemblyPath, "link_spw_read_assembly");
        if (assembly7 is null || assembly5 is null)
        {
            return;
        }

        var components = Components7(assembly7, "link_spw_read_assembly");
        foreach (var (path, label) in new[]
                 {
                     (LinkSpwPath, "link_spw_read"),
                     (LinkSpwTwicePath, "link_spw_twice_read"),
                 })
        {
            var spw = OpenSpw(path, label);
            if (spw is null)
            {
                continue;
            }

            LinkRead(spw, components, label, LinkSpwMarking, LinkSpwName, LinkSpwCount);
            RebuildCheck(null, spw, components, label);
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }

        assembly7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
    }

    /// <summary>Whether <c>UniqueMetaObjectKey</c> is a PERSISTENT identity at all: the saved files are read
    /// twice in one process. The keys are compared between the two reads, so "the link did not survive" is
    /// told apart from "the key is a fresh value on every access".</summary>
    private void LinkKeyCheck()
    {
        var (doc5, doc7) = OpenModel(LinkModelPath, "link_key_model");
        if (doc7 is not null)
        {
            var components = Components7(doc7, "link_key_model");
            RecordKeysTwice(doc7, "link_key_model", components);
            doc7.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }

        var spw = OpenSpw(LinkSpwPath, "link-key-spw");
        if (spw is not null)
        {
            RecordKeysTwice(spw, "link_key_spw", []);
            spw.Close(DocumentCloseOptions.kdDoNotSaveChanges);
        }
    }

    private void RecordKeysTwice(IKompasDocument document, string label, List<IPart7> components)
    {
        var first = KeysOf(document);
        var second = KeysOf(document);
        var stable = first.Count == second.Count
                     && first.Zip(second).All(pair => pair.First == pair.Second);
        Record($"{label}_keys_stable", "ключи строк одинаковы при ДВУХ чтениях в одном процессе",
            $"{first.Count} строк; first={string.Join(",", first)}; second={string.Join(",", second)}",
            stable ? "PASS" : "FAIL");

        var componentKeys = components
            .Select(component => Safe(() => ((IPropertyKeeper)component).UniqueMetaObjectKey))
            .Where(key => key is not null)
            .ToList();
        Record($"{label}_component_keys", "ключи компонентов в этом процессе",
            componentKeys.Count == 0 ? "<компоненты не прочитаны>" : string.Join(", ", componentKeys), "INFO");
        var matching = first.Count(key => key is not null && componentKeys.Contains(key));
        Record($"{label}_row_key_matches_component", "ключ строки совпадает с ключом компонента после сохранения",
            $"совпало {matching} из {first.Count}", matching > 0 ? "PASS" : "FAIL");
    }

    private static List<string?> KeysOf(IKompasDocument document) =>
        ObjectsOf(document.SpecificationDescriptions?.Active?.Objects)
            .Select(item => Safe(() => item.UniqueMetaObjectKey))
            .ToList();

    /// <summary>Does the documented rebuild of the reopened document bring the link back? The research
    /// measured that a rebuild keeps a written row; this asks whether it also restores the LINK, which would
    /// mean the file carries it and the row only answers a different key.</summary>
    private void RebuildCheck(
        ksDocument3D? model5, IKompasDocument document, List<IPart7> components, string label)
    {
        var keysBefore = KeysOf(document);
        var rebuilt = model5 is not null
            ? Safe(() => model5.RebuildDocument())
            : document is ISpecificationDocument specification
                ? Safe(() => specification.RebuildDocument())
                : "<нет маршрута перестроения>";
        var keysAfter = KeysOf(document);
        var componentKeys = components
            .Select(component => Safe(() => ((IPropertyKeeper)component).UniqueMetaObjectKey))
            .Where(key => key is not null)
            .ToList();
        var restored = keysAfter.Count(key => key is not null && componentKeys.Contains(key));
        Record($"{label}_rebuild", "перестроение переоткрытого документа возвращает общий ключ компонента",
            $"rebuild={rebuilt}, ключей до={keysBefore.Count}, после={keysAfter.Count}, совпало с компонентом "
            + $"до={keysBefore.Count(key => key is not null && componentKeys.Contains(key))}, после={restored}",
            restored > 0 ? "PASS" : "FAIL");
    }

    // ── shared stages ──────────────────────────────────────────────────────────────────────────

    private void LinkSandbox() => Directory.CreateDirectory(Path.Combine(LinkDir, "sandbox"));

    /// <summary>The top level components of a 3D document, through the documented recursive collection
    /// <c>IPart7.Parts</c> (<c>iparts7_part.html</c>: «VT_I4 - индекс компонента»).</summary>
    private List<IPart7> Components7(IKompasDocument document, string label)
    {
        var components = new List<IPart7>();
        try
        {
            var top = (document as IKompasDocument3D)?.TopPart as IPart7;
            var parts = top?.Parts;
            var count = parts?.Count ?? 0;
            for (var index = 0; index < count; index++)
            {
                if (parts!.get_Part(index) is IPart7 part)
                {
                    components.Add(part);
                }
            }

            Record($"{label}_components", "IPart7.Parts перечисляет компоненты верхнего уровня",
                $"{components.Count} из {count}", components.Count >= 2 ? "PASS" : "FAIL");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record($"{label}_components", "IPart7.Parts перечисляет компоненты", Describe(ex), "FAIL");
        }

        return components;
    }

    /// <summary>Two rows through the documented <c>AddWithParam</c>: the first with EXPLICIT texts, the
    /// second with <c>fill_text=true</c>, both with one extra column written BEFORE the first
    /// <c>Update()</c> - the order that carried extra columns into the file for <c>Add</c>.</summary>
    private void LinkTwoRows(
        IKompasDocument target, ISpecificationDescription? description, List<IPart7> components, int section,
        string label, string marking, string name, string count)
    {
        if (description is null || description.BaseObjects is not ISpecificationBaseObjects baseObjects)
        {
            Record($"{label}_baseobjects", "ISpecificationDescription.BaseObjects даёт ISpecificationBaseObjects",
                description is null ? "нет описания" : "не приводится", "FAIL");
            return;
        }

        if (components.Count < 2)
        {
            Record($"{label}_components_enough", "для двух строк нужны два компонента",
                components.Count.ToString(CultureInfo.InvariantCulture), "FAIL");
            return;
        }

        var document1 = target as IKompasDocument1;
        Record($"{label}_document1", "IKompasDocument отвечает QI(IKompasDocument1)",
            document1 is null ? "null" : "IKompasDocument1", document1 is null ? "FAIL" : "PASS");
        var param = document1?.GetInterface(KompasAPIObjectTypeEnum.ksSpecificationObjectCreateParam);
        Record($"{label}_createparam", "GetInterface(ksSpecificationObjectCreateParam) даёт параметры создания",
            param?.GetType().Name ?? "null", param is ISpecificationObjectCreateParam ? "PASS" : "FAIL");
        if (param is not ISpecificationObjectCreateParam createParam)
        {
            return;
        }

        LinkOne(baseObjects, createParam, section, components[0], fillText: false, marking, name, count,
            label + "_explicit");
        LinkOne(baseObjects, createParam, section, components[1], fillText: true, marking, name, count,
            label + "_filltext");
    }

    private void LinkOne(
        ISpecificationBaseObjects baseObjects, ISpecificationObjectCreateParam param, int section,
        IPart7 component, bool fillText, string marking, string name, string count, string label)
    {
        IPropertyKeeper? keeper = null;
        try
        {
            keeper = (IPropertyKeeper)component;
        }
        catch (InvalidCastException ex)
        {
            Record($"{label}_keeper", "QI(IPart7) даёт IPropertyKeeper", Describe(ex), "FAIL");
        }

        if (keeper is null)
        {
            return;
        }

        var componentKey = Safe(() => keeper.UniqueMetaObjectKey);
        Record($"{label}_keeper", "QI(IPart7) даёт IPropertyKeeper", $"ключ компонента {componentKey}",
            componentKey is null ? "INFO" : "PASS");

        param.Section = section;
        ISpecificationBaseObject? created;
        try
        {
            created = baseObjects.AddWithParam(
                (SpecificationObjectCreateParam)(object)param, keeper, fillText, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Record($"{label}_added", $"AddWithParam(fill_text={fillText}) создаёт строку в коллекции",
                Describe(ex), "FAIL");
            return;
        }

        Record($"{label}_added", $"AddWithParam(fill_text={fillText}) создаёт строку в коллекции",
            created is null ? "null" : $"unique={created.UniqueNumber}", created is null ? "FAIL" : "PASS");
        if (created is null)
        {
            return;
        }

        if (!fillText)
        {
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnMark, marking);
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnName, name);
            SetText(created, ksSpecificationColumnTypeEnum.ksSColumnCount, count);
        }

        // The extra column goes BEFORE the first Update(): that is what carried main columns into the file
        // for Add (measured in the columns order), and the same question is asked of a linked row.
        Record($"{label}_note_written", $"доп. колонка Примечание = «{LinkNote}»",
            WriteColumn(created, ksSpecificationColumnTypeEnum.ksSColumnNote, LinkNote), "INFO");

        var updated = created.Update();
        Record($"{label}_update", "ISpecificationObject.Update() связанной строки", updated.ToString(),
            updated ? "PASS" : "FAIL");

        var rowKey = Safe(() => created.UniqueMetaObjectKey);
        Record($"{label}_key", "ключ информационного объекта строки равен ключу компонента",
            $"row={rowKey}, component={componentKey}",
            rowKey is not null && rowKey == componentKey ? "PASS" : "FAIL");
        Record($"{label}_sync", "SyncronizeWithProperties связанной строки",
            Safe(() => created.SyncronizeWithProperties) ?? "<null>", "INFO");
        Record($"{label}_fields_session", "поля связанной строки в СЕАНСЕ", DescribeLinked(created), "INFO");
    }

    /// <summary>Reads the saved document in a NEW process: every row's key, synchronization flag and fields,
    /// and both rows the write stage made, each compared with the component it was linked to.</summary>
    private void LinkRead(
        IKompasDocument document, List<IPart7> components, string label, string marking, string name,
        string count)
    {
        var description = document.SpecificationDescriptions?.Active;
        var rows = new List<LinkRow>();
        var json = new JsonArray();
        foreach (var item in ObjectsOf(description?.Objects))
        {
            var row = new LinkRow((double)item.UniqueNumber, Safe(() => item.UniqueMetaObjectKey),
                Safe(() => item.SyncronizeWithProperties), Safe(() => item.Section),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName),
                ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount),
                ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnNote));
            rows.Add(row);
            json.Add(new JsonObject
            {
                ["unique"] = row.Unique,
                ["key"] = row.Key,
                ["sync"] = row.Sync,
                ["section"] = row.Section,
                ["marking"] = row.Marking,
                ["name"] = row.Name,
                ["quantity"] = row.Count,
                ["note"] = row.Note,
            });
        }

        _reads.Add(new JsonObject { ["label"] = label + "-reopened", ["rows"] = json.DeepClone() });
        Record($"{label}_rows", "строк спецификации в переоткрытом документе",
            rows.Count.ToString(CultureInfo.InvariantCulture), rows.Count > 0 ? "PASS" : "FAIL");

        if (components.Count < 2)
        {
            Record($"{label}_components_enough", "компоненты для сверки ключей прочитаны",
                components.Count.ToString(CultureInfo.InvariantCulture), "FAIL");
            return;
        }

        var keyExplicit = Safe(() => ((IPropertyKeeper)components[0]).UniqueMetaObjectKey);
        var keyFill = Safe(() => ((IPropertyKeeper)components[1]).UniqueMetaObjectKey);
        Record($"{label}_component_keys", "ключи компонентов, прочитанные в НОВОМ процессе",
            $"explicit={keyExplicit}, fill_text={keyFill}",
            keyExplicit is not null && keyFill is not null ? "PASS" : "FAIL");

        LinkRow? explicitRow = keyExplicit is null ? null : rows.Find(r => r.Key == keyExplicit);
        Record($"{label}_explicit_key", "строка с явными текстами сохранила ОБЩИЙ ключ компонента",
            explicitRow is null
                ? "<строка с таким ключом не найдена>"
                : $"unique={explicitRow.Unique}, key={explicitRow.Key}",
            explicitRow is null ? "FAIL" : "PASS");
        if (explicitRow is not null)
        {
            Record($"{label}_explicit_texts", $"тексты строки «{marking}» / «{name}» / «{count}» пережили сохранение",
                $"marking={explicitRow.Marking}, name={explicitRow.Name}, count={explicitRow.Count}, "
                + $"note={explicitRow.Note}, sync={explicitRow.Sync}",
                explicitRow.Marking == marking && explicitRow.Name == name && explicitRow.Count == count
                    ? "PASS" : "FAIL");
            Record($"{label}_explicit_note", $"доп. колонка Примечание = «{LinkNote}» пережила сохранение",
                explicitRow.Note ?? "<нет>", explicitRow.Note == LinkNote ? "PASS" : "INFO");
        }

        LinkRow? fillRow = keyFill is null ? null : rows.Find(r => r.Key == keyFill);
        Record($"{label}_filltext_key", "строка fill_text=true сохранила ОБЩИЙ ключ компонента",
            fillRow is null
                ? "<строка с таким ключом не найдена>"
                : $"unique={fillRow.Unique}, key={fillRow.Key}",
            fillRow is null ? "FAIL" : "PASS");
        if (fillRow is null)
        {
            return;
        }

        var component = components[1];
        var componentMarking = Safe(() => component.Marking);
        var componentName = Safe(() => component.Name);
        Record($"{label}_filltext_texts", "тексты строки fill_text=true заполнены ПО СВОЙСТВАМ компонента",
            $"row={fillRow.Marking} / {fillRow.Name} / {fillRow.Count}, component={componentMarking} / "
            + $"{componentName}, note={fillRow.Note}, sync={fillRow.Sync}",
            fillRow.Marking == componentMarking && fillRow.Name == componentName ? "PASS" : "FAIL");
    }

    /// <summary>The fields of one base object, for the session read of the write stage.</summary>
    private static string DescribeLinked(ISpecificationBaseObject item) =>
        $"unique={item.UniqueNumber}, marking={ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnMark)}, "
        + $"name={ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnName)}, "
        + $"count={ReadColumn(item, ksSpecificationColumnTypeEnum.ksSColumnCount)}, "
        + $"note={ReadFrom(item, ksSpecificationColumnTypeEnum.ksSColumnNote)}, "
        + $"key={Safe(() => item.UniqueMetaObjectKey)}, sync={Safe(() => item.SyncronizeWithProperties)}";
}
