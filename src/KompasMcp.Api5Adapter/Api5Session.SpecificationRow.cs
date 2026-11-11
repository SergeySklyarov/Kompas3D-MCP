using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.Com;

namespace KompasMcp.Api5Adapter;

/// <summary>Creation of one row of a MODEL specification — block <c>SPEC_ADD_ROW</c>.</summary>
/// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c> — «После вызова метода Add объект будет
/// добавлен в коллекцию, но в документе не появится. Для отображения объекта в документе нужно задать его
/// параметры и вызвать метод ISpecificationObject::Update»; <c>ispecificationobject_update.html</c> — тем
/// же методом создаётся объект; <c>ispecificationcolumnitem_value.html</c> — значение элемента по
/// <c>ValueType</c>.
/// INVARIANT: a row whose values the document did not answer back — through API7 AND through API5 — is not
/// a success: the answer is a refusal with a snapshot of what the model holds.
/// History: docs/decisions/bom.md#spec-add-row</remarks>
public partial class Api5Session
{
    private const string SpecificationRowRoute =
        "api7:IKompasDocument.SpecificationDescriptions.Active (или Add(\"\", 0, \"\")) → "
        + "ISpecificationDescription.BaseObjects.Add(section, 0) → ISpecificationObject.Columns → "
        + "ColumnItems.Value по типу колонки → Update(); перечитывание - описание заново, строка = "
        + "новый объект Objects, затем api5:ksDocument3D.GetSpecification().ksGetSpcObject → "
        + "ksGetSpcObjectNumber + ksGetSpcObjectColumnText";

    /// <summary>Create the row and answer what the document holds afterwards.</summary>
    public AddSpecificationRowResult AddSpecificationRow(AddSpecificationRowCommand command)
    {
        // The same pure rule the Host applies, repeated where COM is about to be asked.
        if (SpecificationRowInput.Validate(
                command.SectionNumber, command.Marking, command.Name, command.Quantity, command.Position)
            is { } refused)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                refused.Message,
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = refused.Field });
        }

        var document = RequireModelForRowWrite(command.DocumentId, "создаёт");
        var notes = new List<string>();
        var checks = new List<NamedCheck>();
        var descriptionCreated = false;
        var description = SpecificationDescriptionForWrite(document, notes, out descriptionCreated);

        // An unknown section is refused by the STYLE's own list, before anything enters the collection.
        var sections = SpecificationSectionNames(description, notes);
        if (sections.Count > 0 && !sections.ContainsKey(command.SectionNumber))
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Раздела {command.SectionNumber} нет в стиле спецификации документа "
                + $"«{command.DocumentId}». Доступные разделы: {DescribeSections(sections)}. Строку можно "
                + "добавить только в существующий раздел (ispecificationbaseobjects_add.html).",
                RetryPolicy.Never,
                partialEffects: descriptionCreated,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["section_number"] = command.SectionNumber,
                    ["available_sections"] = DescribeSections(sections),
                    ["description_created"] = descriptionCreated,
                    ["failure_snapshot"] = new Dictionary<string, object?>
                    {
                        ["requested_section"] = command.SectionNumber,
                        ["available_sections"] = sections.OrderBy(pair => pair.Key)
                            .Select(pair => new { number = pair.Key, name = pair.Value }).ToArray(),
                    },
                });
        }

        // The numbers the document already had: the row this call creates is the one that appears
        // afterwards. MEASURED: the number the created object reports is NOT the number the document gives
        // the row, so the handle cannot be trusted as the row's identity; the difference of the two lists
        // can. History: docs/decisions/bom.md#spec-add-row
        var numbersBefore = SpecificationObjects(description, new List<string>())
            .Select(item => Double(() => item.UniqueNumber))
            .Where(number => number is not null)
            .Select(number => number!.Value)
            .ToHashSet();

        var baseObjects = BaseObjectsOf(description, document, command, descriptionCreated);
        var created = AddBaseObject(baseObjects, document, command, descriptionCreated);
        var fields = RowFields(command);
        WriteRowFields(created, fields, document, command, descriptionCreated);

        var updated = UpdateRow(created, document, command, descriptionCreated);

        // The document changed: the revision moves before anything is read back, so a read-back failure
        // still reports the revision the client has to continue from.
        BumpRevision(document, "specification.add_row");

        var handleUnique = ReadCreatedRow(created, document, command, updated);
        var (row, documentUnique) = VerifyRowBack(
            document, handleUnique, numbersBefore, fields, command, checks, notes);

        notes.Insert(0, $"specification_row_created — строка создана и прочитана обратно: "
            + $"unique_number={Fmt(documentUnique)}, раздел {command.SectionNumber}");

        // LIMIT: the tool works in memory; the file is written by kompas_save_document, so survival of the
        // row across a save and a reopen is the acceptance's business, not this call's.
        var unverified = new List<string>
        {
            "row_not_written_to_file — строка живёт в модели до kompas_save_document; переживание "
            + "сохранения и переоткрытия проверяет приёмка (SPEC-02.save_reopen), а не этот вызов",
        };

        return new AddSpecificationRowResult(
            new SpecificationRowRefDto(document.Id, documentUnique, row.Marking),
            row,
            SpecificationRowRoute,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            notes.Distinct(StringComparer.Ordinal).ToList(),
            unverified);
    }

    /// <summary>A model document: a specification belongs to an assembly or a part, not to a <c>.spw</c>.</summary>
    /// <param name="operation">The verb of the call («создаёт», «правит») — the refusal has to name what was
    /// refused, and two tools writing rows must not answer with one another's sentence.</param>
    /// <remarks>LIMIT: a drawing is refused too — its specification description is a different level and is
    /// not in this contract. The refusal is <c>WRONG_DOCUMENT_KIND</c>: the same code the read tool answers,
    /// so two calls on one wrong document do not speak two vocabularies.</remarks>
    private DocumentEntry RequireModelForRowWrite(string documentId, string operation)
    {
        var document = RequireDocument(documentId);
        if (document.Kind is DocumentKind.Assembly or DocumentKind.Part)
        {
            return document;
        }

        throw new KompasContractException(
            ErrorCodes.WrongDocumentKind,
            $"Документ «{documentId}» имеет тип {document.Kind}: строку спецификации {operation} только "
            + "документ-модель (сборка или деталь). У документа-спецификации .spw свои строки, и этот "
            + "инструмент их не пишет.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["kind"] = document.Kind.ToString(),
                ["supported_kinds"] = new[] { "assembly", "part" },
            });
    }

    /// <summary>The description the row goes into: the document's own, or the DEFAULT one created here.</summary>
    /// <remarks>DOC: <c>ispecificationdescriptions_add.html</c> — «Если LayoutName и StyleID не заданы, то
    /// добавляется описание спецификации по умолчанию». A model without a description is a legal state, so
    /// the absence is not a refusal — but the creation IS a change of the model and is NAMED in the answer.
    /// History: docs/decisions/bom.md#spec-add-row</remarks>
    private ISpecificationDescription SpecificationDescriptionForWrite(
        DocumentEntry document, List<string> notes, out bool created)
    {
        created = false;
        var document7 = DocumentAsApi7(document);
        if (document7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Документ «{document.Id}» не переносится в API7: описания спецификации недоступны.",
                RetryPolicy.ReacquireContext);
        }

        ISpecificationDescriptions? descriptions;
        try
        {
            descriptions = document7.SpecificationDescriptions;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Описание спецификации не читается через API7: " + ex.Message,
                RetryPolicy.ReacquireContext);
        }

        if (descriptions is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "IKompasDocument.SpecificationDescriptions вернул null: описание спецификации не "
                + "прочитано, строку некуда добавить.",
                RetryPolicy.Never);
        }

        try
        {
            if (descriptions.Active is { } active)
            {
                return active;
            }

            var added = descriptions.Add(string.Empty, 0, string.Empty)
                ?? throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "ISpecificationDescriptions.Add(\"\", 0, \"\") вернул null: описание спецификации по "
                    + "умолчанию не создано, строку некуда добавить.",
                    RetryPolicy.Never);

            created = true;
            notes.Add("specification_description_created — у документа не было описания спецификации "
                + "(Active = NULL): оно создано документированным ISpecificationDescriptions.Add(\"\", 0, "
                + "\"\"): иначе строку некуда добавить");
            return added;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Описание спецификации не создаётся через API7: " + ex.Message,
                RetryPolicy.Never,
                partialEffects: true);
        }
    }

    /// <summary>The collection rows enter, or a named refusal.</summary>
    private static ISpecificationBaseObjects BaseObjectsOf(
        ISpecificationDescription description, DocumentEntry document, AddSpecificationRowCommand command,
        bool descriptionCreated)
    {
        try
        {
            if (description.BaseObjects is ISpecificationBaseObjects baseObjects)
            {
                return baseObjects;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ISpecificationDescription.BaseObjects прервался: " + ex.Message,
                RetryPolicy.Never,
                partialEffects: descriptionCreated);
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            "ISpecificationDescription.BaseObjects не дал ISpecificationBaseObjects: строку "
            + "документированным маршрутом не создать.",
            RetryPolicy.Never,
            partialEffects: descriptionCreated,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["section_number"] = command.SectionNumber,
                ["description_created"] = descriptionCreated,
            });
    }

    /// <summary><c>BaseObjects.Add</c> puts the object into the COLLECTION — the document shows nothing yet.</summary>
    /// <remarks>DOC: <c>ispecificationbaseobjects_add.html</c>, including the note that section 0 cannot be
    /// used. A null answer means the section did not take the object: the call names the section and the
    /// available ones rather than reporting a bare failure.</remarks>
    private ISpecificationBaseObject AddBaseObject(
        ISpecificationBaseObjects baseObjects, DocumentEntry document, AddSpecificationRowCommand command,
        bool descriptionCreated)
    {
        ISpecificationBaseObject? created;
        try
        {
            created = baseObjects.Add(command.SectionNumber, 0);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"BaseObjects.Add({command.SectionNumber}, 0) прервался: {ex.Message}. Строка в документ "
                + "не добавлена.",
                RetryPolicy.Never,
                partialEffects: descriptionCreated,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["section_number"] = command.SectionNumber,
                    ["description_created"] = descriptionCreated,
                });
        }

        if (created is not null)
        {
            return created;
        }

        throw new KompasContractException(
            ErrorCodes.CapabilityUnavailable,
            $"ISpecificationBaseObjects.Add({command.SectionNumber}, 0) вернул null: объект в коллекцию не "
            + "добавлен. Раздел должен существовать в стиле спецификации.",
            RetryPolicy.Never,
            partialEffects: descriptionCreated,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["section_number"] = command.SectionNumber,
                ["description_created"] = descriptionCreated,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["requested_section"] = command.SectionNumber,
                    ["add_returned_null"] = true,
                },
            });
    }

    /// <summary>The fields the call writes, in the order the probe wrote them.</summary>
    private static List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> RowFields(
        AddSpecificationRowCommand command)
    {
        var fields = new List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)>
        {
            ("marking", command.Marking.Trim(), ksSpecificationColumnTypeEnum.ksSColumnMark),
            ("name", command.Name.Trim(), ksSpecificationColumnTypeEnum.ksSColumnName),
            ("quantity", command.Quantity.Trim(), ksSpecificationColumnTypeEnum.ksSColumnCount),
        };

        if (command.Position is { } position)
        {
            fields.Add(("position", position.ToString(CultureInfo.InvariantCulture),
                ksSpecificationColumnTypeEnum.ksSColumnPosition));
        }

        return fields;
    }

    /// <summary>Write every requested value into the column of its TYPE.</summary>
    /// <remarks>DOC: <c>ispecificationcolumns_column.html</c> («type, ColumnNumber, BlockNumber»),
    /// <c>ispecificationcolumnitems_item.html</c>, <c>ispecificationcolumnitem_value.html</c> (the value
    /// type follows <c>ValueType</c>).
    /// INVARIANT: a style that has no column of the requested type cannot receive the value, so the object
    /// that only entered the collection is DELETED again and the refusal says whether that worked.
    /// History: docs/decisions/bom.md#spec-add-row</remarks>
    private void WriteRowFields(
        ISpecificationBaseObject created,
        List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> fields,
        DocumentEntry document, AddSpecificationRowCommand command, bool descriptionCreated)
    {
        foreach (var (field, text, type) in fields)
        {
            var columns = SafeRead.Ref(() => created.Columns);
            var column = columns is null
                ? null
                : SafeRead.Ref(() => columns.Column[type, SpecificationColumnNumbers.FirstOfType,
                    SpecificationColumnNumbers.MainBlock]);
            var items = column?.ColumnItems as ISpecificationColumnItems;
            var element = items is { Count: > 0 } ? items[0] : null;
            if (element is null)
            {
                throw RowWriteRefusal(
                    ErrorCodes.CapabilityUnavailable,
                    $"В стиле нет колонки {field} (тип {(int)type}): значение записать некуда. Строка в "
                    + "документ не добавлена.",
                    created, document, command, descriptionCreated,
                    new Dictionary<string, object?> { ["missing_column"] = field, ["column_type"] = (int)type });
            }

            var valueType = SafeRead.EnumOrNull<ksValueTypeEnum>(() => element.ValueType);
            if (valueType is null)
            {
                throw RowWriteRefusal(
                    ErrorCodes.CapabilityUnavailable,
                    $"Тип значения колонки {field} (тип {(int)type}) не прочитан: значение записывать "
                    + "нечем. Строка в документ не добавлена.",
                    created, document, command, descriptionCreated,
                    new Dictionary<string, object?> { ["unread_value_type"] = field });
            }

            if (!TryColumnValue(text, valueType.Value, out var value))
            {
                throw RowWriteRefusal(
                    ErrorCodes.InvalidArgument,
                    $"Значение «{text}» не подходит колонке {field}: её элемент ждёт "
                    + $"{DescribeValueType(valueType.Value)}. Строка в документ не добавлена.",
                    created, document, command, descriptionCreated,
                    new Dictionary<string, object?>
                    {
                        ["field"] = field,
                        ["value"] = text,
                        ["value_type"] = valueType.Value.ToString(),
                    });
            }

            try
            {
                element.Value = value;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
            {
                throw RowWriteRefusal(
                    ErrorCodes.CapabilityUnavailable,
                    $"ISpecificationColumnItem.Value колонки {field} прервался: {ex.Message}. Строка в "
                    + "документ не добавлена.",
                    created, document, command, descriptionCreated,
                    new Dictionary<string, object?> { ["field"] = field, ["value"] = text });
            }
        }
    }

    /// <summary><c>Update()</c> is what creates the object in the document — a false answer is not success.</summary>
    /// <remarks>DOC: <c>ispecificationobject_update.html</c>. A refused creation removes the object from the
    /// collection again and names whether it managed to; the model is never presented as clean when it is
    /// not. History: docs/decisions/bom.md#spec-add-row</remarks>
    private bool? UpdateRow(
        ISpecificationBaseObject created, DocumentEntry document, AddSpecificationRowCommand command,
        bool descriptionCreated)
    {
        bool? updated;
        try
        {
            updated = Bool(() => created.Update());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            throw RowWriteRefusal(
                ErrorCodes.CapabilityUnavailable,
                "ISpecificationObject.Update() прервался: " + ex.Message + ". Строка в документ не "
                + "добавлена тем вызовом, которым её создавали.",
                created, document, command, descriptionCreated,
                new Dictionary<string, object?> { ["update"] = "вызов прервался" });
        }

        if (updated == true)
        {
            return updated;
        }

        throw RowWriteRefusal(
            ErrorCodes.CapabilityUnavailable,
            "ISpecificationObject.Update() вернул "
            + (updated is null ? "непрочитанный результат" : "false")
            + ": документ строку не создал. Это отказ ядра, а не успех: строка, которой нет, не "
            + "опубликована.",
            created, document, command, descriptionCreated,
            new Dictionary<string, object?> { ["update"] = updated });
    }

    /// <summary>The unique number of the created row — it is what a later edit or delete addresses.</summary>
    /// <remarks>DOC: <c>ispecificationobject_uniquenumber.html</c> — «присваивается при создании».
    /// History: docs/decisions/bom.md#spec-add-row</remarks>
    private double ReadCreatedRow(
        ISpecificationBaseObject created, DocumentEntry document, AddSpecificationRowCommand command,
        bool? updated)
    {
        var unique = Double(() => created.UniqueNumber);
        if (unique is not null)
        {
            return unique.Value;
        }

        throw new KompasContractException(
            ErrorCodes.GeometryFailed,
            "Строка создана (Update() = " + (updated?.ToString() ?? "не прочитан") + "), но её UniqueNumber "
            + "не прочитан: строку нельзя ни назвать, ни найти обратным чтением.",
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["section_number"] = command.SectionNumber,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["unique_number"] = null,
                    ["marking"] = command.Marking,
                    ["update"] = updated,
                },
            });
    }

    /// <summary>Read the row back through API7 and API5 and compare both with the request.</summary>
    /// <remarks>INVARIANT: the row must BE in the description's list, its pointer must answer the same
    /// unique number, and BOTH roads must show the requested values. A disagreement is
    /// <c>GEOMETRY_FAILED</c> with the snapshot, because the model then holds something other than what the
    /// client asked for. History: docs/decisions/bom.md#spec-add-row</remarks>
    private (SpecificationLineDto Row, double Unique) VerifyRowBack(
        DocumentEntry document, double handleUnique, HashSet<double> numbersBefore,
        List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> fields,
        AddSpecificationRowCommand command, List<NamedCheck> checks, List<string> notes)
    {
        var readNotes = new List<string>();

        // The description is read AGAIN from the document: the collection seen before Update() does not
        // have to answer for the row that Update() created, and a lookup on the old handle would report a
        // created row as missing.
        // History: docs/decisions/bom.md#spec-add-row
        var description = ActiveSpecificationDescription(document, readNotes);
        var objects = description is null
            ? new List<ISpecificationBaseObject>()
            : SpecificationObjects(description, readNotes);
        var observed = objects.Select(item => new Dictionary<string, object?>
        {
            ["unique_number"] = Double(() => item.UniqueNumber),
            ["section_number"] = Int(() => item.Section),
            ["marking"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnMark, "marking", new List<string>()),
            ["name"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnName, "name", new List<string>()),
        }).ToList();

        // The row is the object the document did NOT have before this call. MEASURED: the number the
        // created handle reports differs from the number the document gives the row (the handle stays a
        // collection-time number), so identity comes from the DIFFERENCE of the lists, not from the handle.
        // History: docs/decisions/bom.md#spec-add-row
        var appeared = objects
            .Where(item => Double(() => item.UniqueNumber) is { } number && !numbersBefore.Contains(number))
            .ToList();
        var listed = appeared.Count == 1 ? appeared[0] : null;
        var unique = listed is null ? handleUnique : Double(() => listed.UniqueNumber) ?? handleUnique;
        if (listed is null)
        {
            // A second road to the same identity: the values THIS call wrote, and only a single match.
            // A pre-existing duplicate leaves this empty on purpose — the answer is then a refusal with
            // the list the document holds, never a pick.
            var byValues = objects.Where(item => RowMatchesRequest(item, command)).ToList();
            if (byValues.Count == 1)
            {
                listed = byValues[0];
                unique = Double(() => listed.UniqueNumber) ?? handleUnique;
                notes.Add("specification_row_found_by_values — строку не удалось отличить по номеру "
                    + $"(появилось объектов: {appeared.Count}, у созданного — {Fmt(handleUnique)}): она "
                    + $"найдена по запрошенным значениям, номер взят у документа: {Fmt(unique)}");
            }
        }
        else if (!SameUniqueNumber(unique, handleUnique))
        {
            notes.Add("specification_row_number_from_document — UniqueNumber созданного объекта "
                + $"({Fmt(handleUnique)}) — номер коллекции, а не строки: документ дал строке "
                + $"{Fmt(unique)}, и этот номер — адрес строки (ksGetSpcObjectNumber подтверждает его)");
        }

        checks.Add(new NamedCheck("row_listed_after_update", listed is not null));
        if (listed is null)
        {
            throw RowReadRefusal(document, command, unique,
                "Строка после Update() в ISpecificationDescription.Objects не опознана: новых объектов "
                + $"{appeared.Count}, совпадений по значениям — "
                + $"{objects.Count(item => RowMatchesRequest(item, command))}. Документ не показал ровно "
                + "одну новую строку, и угадывать её нельзя.",
                readNotes, null, null, observed);
        }

        var specification5 = Specification5(document);
        var pointer = Int(() => specification5.ksGetSpcObject(unique));
        var numberReadBack = pointer is > 0 ? Double(() => specification5.ksGetSpcObjectNumber(pointer.Value)) : null;
        var pointerMatches = pointer is > 0 && SameUniqueNumber(numberReadBack, unique);
        checks.Add(new NamedCheck("unique_number_read_back", pointerMatches,
            numberReadBack?.ToString(CultureInfo.InvariantCulture),
            Fmt(unique)));
        if (!pointerMatches)
        {
            throw RowReadRefusal(document, command, unique,
                "ksGetSpcObject(" + Fmt(unique) + ") + ksGetSpcObjectNumber не подтвердили строку: указатель "
                + (pointer is > 0 ? "отвечает номером " + Fmt(numberReadBack) : "не получен")
                + ". Тексты чужой строки под этим номером не публикуются.",
                readNotes, null, null, observed);
        }

        var api7 = new Dictionary<string, string?>(StringComparer.Ordinal);
        var api5 = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, text, type) in fields)
        {
            api7[field] = Api7Column(listed, type, field, readNotes);
            api5[field] = Column(specification5, pointer!.Value, type);
            var matches = SpecificationValueMatches(text, api7[field]) &&
                          SpecificationValueMatches(text, api5[field]);
            checks.Add(new NamedCheck(field + "_read_back", matches, api7[field] + " / " + api5[field], text));
        }

        var row = ReadSpecificationLine(specification5, listed, readNotes);
        checks.Add(new NamedCheck("row_fields_read_back", row is not null));
        if (row is null)
        {
            throw RowReadRefusal(document, command, unique,
                "Строка найдена, но её поля не прочитаны документированным маршрутом (API5-указатель и "
                + "тексты колонок): прочитанное не публикуется как строка.",
                readNotes, api7, api5, observed);
        }

        if (checks.Any(check => !check.Passed))
        {
            throw RowReadRefusal(document, command, unique,
                "Прочитанные назад значения расходятся с запрошенными: документ держит не то, что просил "
                + "клиент. Публиковать это как успех нельзя.",
                readNotes, api7, api5, observed);
        }

        notes.AddRange(readNotes);
        return (row, unique);
    }

    /// <summary>Whether a row of the document carries exactly the values this call wrote.</summary>
    /// <remarks>Used ONLY to find the row when the number read from the created handle is not the number
    /// the document shows; a second row with the same values makes the answer a refusal, not a pick.
    /// </remarks>
    private static bool RowMatchesRequest(ISpecificationBaseObject item, AddSpecificationRowCommand command)
    {
        var notes = new List<string>();
        return SpecificationValueMatches(command.Marking.Trim(),
                   Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnMark, "marking", notes))
               && SpecificationValueMatches(command.Name.Trim(),
                   Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnName, "name", notes))
               && SpecificationValueMatches(command.Quantity.Trim(),
                   Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnCount, "quantity", notes));
    }

    /// <summary>The refusal of a write that already touched the collection: it says what was removed.</summary>
    private static KompasContractException RowWriteRefusal(
        string code, string message, ISpecificationBaseObject created, DocumentEntry document,
        AddSpecificationRowCommand command, bool descriptionCreated, Dictionary<string, object?> snapshot)
    {
        var deleted = SafeRead.Bool(() => (created as ISpecificationObject)?.Delete() == true);
        snapshot["collection_object_deleted"] = deleted;
        snapshot["document_id"] = document.Id;
        snapshot["section_number"] = command.SectionNumber;
        snapshot["marking"] = command.Marking;
        snapshot["name"] = command.Name;
        snapshot["quantity"] = command.Quantity;
        snapshot["position"] = command.Position;
        snapshot["description_created"] = descriptionCreated;

        // The object entered no document: it existed only in the collection, and the attempt to remove it
        // is reported. A failed removal IS a partial effect, and saying otherwise would hide it.
        return new KompasContractException(
            code,
            message + (deleted == true
                ? " Объект, попавший в коллекцию, удалён обратно."
                : " Объект остался в коллекции (Delete() не подтвердил удаление) — его нужно убрать."),
            RetryPolicy.Never,
            partialEffects: deleted != true || descriptionCreated,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["section_number"] = command.SectionNumber,
                ["description_created"] = descriptionCreated,
                ["collection_object_deleted"] = deleted,
                ["failure_snapshot"] = snapshot,
            });
    }

    /// <summary>The refusal of a read-back that did not confirm the row.</summary>
    private static KompasContractException RowReadRefusal(
        DocumentEntry document, AddSpecificationRowCommand command, double unique, string message,
        IReadOnlyList<string> readNotes, Dictionary<string, string?>? api7, Dictionary<string, string?>? api5,
        IReadOnlyList<Dictionary<string, object?>>? observed = null) =>
        new(
            ErrorCodes.GeometryFailed,
            message,
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["unique_number"] = unique,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["section_number"] = command.SectionNumber,
                    ["requested"] = new Dictionary<string, object?>
                    {
                        ["marking"] = command.Marking,
                        ["name"] = command.Name,
                        ["quantity"] = command.Quantity,
                        ["position"] = command.Position,
                    },
                    ["api7"] = api7,
                    ["api5"] = api5,
                    ["read_notes"] = readNotes.ToArray(),
                    ["observed_objects"] = observed,
                },
            });

    /// <summary>Section number to "number «name»" for the refusal that lists what DOES exist.</summary>
    private static string DescribeSections(Dictionary<int, string> sections) =>
        string.Join(", ", sections.OrderBy(pair => pair.Key)
            .Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) + " «" + pair.Value + "»"));

    /// <summary>Two unique numbers are the same number; COM hands a double and it is compared as one.</summary>
    private static bool SameUniqueNumber(double? left, double right) =>
        left is not null && Math.Abs(left.Value - right) <= double.Epsilon;

    /// <summary>The value the element of a column accepts, in the type its <c>ValueType</c> names.</summary>
    /// <remarks>DOC: <c>ispecificationcolumnitem_value.html</c> — <c>ksValueTypeInteger</c> is VT_I4,
    /// <c>ksValueTypeFloat</c> is VT_R8, <c>ksValueTypeString</c> is VT_BSTR.</remarks>
    private static bool TryColumnValue(string text, ksValueTypeEnum type, out object? value)
    {
        switch (type)
        {
            case ksValueTypeEnum.ksValueTypeInteger:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    value = integer;
                    return true;
                }

                break;
            case ksValueTypeEnum.ksValueTypeFloat:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                {
                    value = real;
                    return true;
                }

                break;
            default:
                value = text;
                return true;
        }

        value = null;
        return false;
    }

    /// <summary>The type an element expects, in the words the client reads in the refusal.</summary>
    private static string DescribeValueType(ksValueTypeEnum type) => type switch
    {
        ksValueTypeEnum.ksValueTypeInteger => "целое число (ksValueTypeInteger, VT_I4)",
        ksValueTypeEnum.ksValueTypeFloat => "вещественное число (ksValueTypeFloat, VT_R8)",
        ksValueTypeEnum.ksValueTypeString => "строку (ksValueTypeString, VT_BSTR)",
        _ => $"значение типа {type} (справка его не описывает)",
    };

    /// <summary>Whether the document answered the requested value, in the type the column carries.</summary>
    /// <remarks>INVARIANT: a number is compared AS a number — «03» and «3» are one quantity — while text is
    /// compared as text, because «007» and «7» are two different markings.
    /// MEASURED: the probe wrote values through the typed element, so the document answers their canonical
    /// form; a byte-for-byte comparison would refuse a legitimate call whose text was «03».
    /// History: docs/decisions/bom.md#spec-add-row</remarks>
    private static bool SpecificationValueMatches(string requested, string? readBack)
    {
        if (readBack is null)
        {
            return false;
        }

        var left = requested.Trim();
        var right = readBack.Trim();
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        return long.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leftInteger)
               && long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rightInteger)
               && leftInteger == rightInteger;
    }
}
