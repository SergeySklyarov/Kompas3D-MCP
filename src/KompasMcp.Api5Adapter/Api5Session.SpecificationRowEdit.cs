using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.Com;

namespace KompasMcp.Api5Adapter;

/// <summary>Edit of one row of a MODEL specification — block <c>SPEC_EDIT_ROW</c>.</summary>
/// <remarks>DOC: <c>ksspecification_ksspcobjectedit.html</c> — «После вызова данного метода у объекта можно
/// изменить атрибуты, геометрию, линии выноски, позицию, массу»; «Редактирование объекта завершится вызовом
/// метода ksSpecification::ksSpcObjectEnd»; <c>ksspecification_ksspcobjectend.html</c> — «Возвращаемое
/// значение: указатель на объект спецификации», поэтому возврат не судится как вердикт;
/// <c>ksspecification_kssetspcobjectcolumntext.html</c> — текст
/// кладётся в колонку по её ТИПУ; <c>ksspecification_ksspccount.html</c>/<c>ksspecification_ksspcposition.html</c>
/// — количество по номеру исполнения и номер позиции.
/// MEASURED: an API7 column write answers <c>Update() = false</c> on an existing row and does not reach the
/// file, while this road does, so API5 is the only write route here.
/// INVARIANT: success is what the document answers through BOTH roads after <c>ksSpcObjectEnd</c>, never what
/// the setters returned; an unconfirmed row is a refusal with a snapshot.
/// History: docs/decisions/bom.md#spec-edit-row</remarks>
public partial class Api5Session
{
    private const string SpecificationRowEditRoute =
        "api5:ksDocument3D.GetSpecification() (модель) или SpcActiveDocument().GetSpecification() (.spw) → "
        + "ksGetSpcObject(unique_number) → ksGetSpcObjectNumber (сверка указателя) → ksSpcObjectEdit → "
        + "ksSetSpcObjectColumnText(тип колонки, 1, 0) / ksSpcCount(1) / ksSpcPosition → ksSpcObjectEnd; до и "
        + "после правки строка читается обеими дорогами: API7 (SpecificationDescriptions.Active → Objects → "
        + "Columns → ColumnItems) и API5 (ksGetSpcObjectColumnText + ksGetSpcSectionName)";

    /// <summary>Edit the row the reference addresses and answer what the document holds afterwards.</summary>
    public EditSpecificationRowResult EditSpecificationRow(EditSpecificationRowCommand command)
    {
        // The columns beyond the four fields carry their own rule: the same pure parser the creation uses, not
        // a copy, so an unknown key or one of the four row fields is the same refusal here.
        // History: docs/decisions/bom.md#spec-spw-columns
        var extra = SpecificationExtraColumns.Parse(command.ExtraColumns, out var extraRefused) ?? [];
        if (extraRefused is not null)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                extraRefused.Message,
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = extraRefused.Field });
        }

        // The same pure rule the Host applies, repeated where COM is about to be asked.
        if (SpecificationRowEditInput.Validate(
                command.DocumentId, RefOf(command.RowRef), command.Marking, command.Name, command.Quantity,
                command.Position, extra.Count) is { } refused)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                refused.Message,
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = refused.Field });
        }

        var document = RequireRowWriteDocument(command.DocumentId, "правит");
        var notes = new List<string>();
        var checks = new List<NamedCheck>();
        var warnings = new List<string>();
        var unique = command.RowRef.UniqueNumber;
        var model = document.Kind is DocumentKind.Assembly or DocumentKind.Part;

        // The API5 road is resolved BEFORE any check that could turn into a mutation: for a .spw it exists
        // only while the document is active, and the refusal names the remedy.
        // History: docs/decisions/bom.md#spec-spw-row-write
        var specification = SpecificationForRowWrite(document, notes);

        var description = ActiveSpecificationDescription(document, notes)
            ?? throw StaleRowRefusal(document, command,
                "У документа нет описания спецификации (Active = NULL): строки, которую адресует ссылка, "
                + "не существует, документ не тронут.", null);

        // The row is found in the list and then through the API5 pointer, and the pointer answers its own
        // number: a foreign row under this number would be edited silently otherwise.
        var listed = ObjectsWithNumber(description, unique, out var candidates);
        if (listed is null)
        {
            throw StaleRowRefusal(document, command, candidates == 0
                ? $"Строки с номером {Fmt(unique)} в документе нет: ссылка устарела, документ не тронут."
                : $"Строк с номером {Fmt(unique)} в документе несколько ({candidates}): ссылка не адресует "
                  + "одну строку, документ не тронут.", null);
        }

        var pointer = Int(() => specification.ksGetSpcObject(unique));
        var confirmed = pointer is > 0
            && SameUniqueNumber(Double(() => specification.ksGetSpcObjectNumber(pointer.Value)), unique);
        if (!confirmed)
        {
            throw StaleRowRefusal(document, command,
                $"ksGetSpcObject({Fmt(unique)}) + ksGetSpcObjectNumber не подтвердили строку: указатель "
                + (pointer is > 0 ? "отвечает другим номером или не отвечает" : "не получен")
                + ". Чужую строку под этим номером не правим, документ не тронут.", null);
        }

        var previous = ReadSpecificationLine(specification, listed, notes)
            ?? throw StaleRowRefusal(document, command,
                "Строка есть в списке, но её поля не прочитаны документированным маршрутом: сверять ссылку "
                + "и сравнивать правку не с чем, документ не тронут.", null);

        if (!RowMarkingMatches(command.RowRef.Marking, previous.Marking))
        {
            throw StaleRowRefusal(document, command,
                $"Маркировка строки «{previous.Marking ?? "не прочитана"}» не равна маркировке ссылки "
                + $"«{command.RowRef.Marking}»: ссылку выдали на прежнее состояние строки, документ не тронут.",
                previous);
        }

        var fields = EditFields(command);
        EnsureEditableColumns(listed, fields, extra, model, document, command, previous);

        // The columns as the document has them BEFORE the first setter: a column this call does not name must
        // come back as it was, and a difference found afterwards is a refusal, not a success.
        // History: docs/decisions/bom.md#spec-spw-columns
        var beforeColumns = Api7ColumnPublication(listed, model, notes);

        var snapshot = new Dictionary<string, object?>
        {
            ["document_id"] = document.Id,
            ["unique_number"] = unique,
            ["row_ref_marking"] = command.RowRef.Marking,
            ["requested"] = new Dictionary<string, object?>
            {
                ["marking"] = command.Marking,
                ["name"] = command.Name,
                ["quantity"] = command.Quantity,
                ["position"] = command.Position,
                ["extra_columns"] = extra.Select(column => new Dictionary<string, object?>
                {
                    ["type"] = SpecificationColumnKeys.KeyOf(column.Type, column.Number),
                    ["number"] = column.Number,
                    ["block"] = column.Block,
                    ["value"] = column.Value,
                }).ToArray(),
            },
            ["before"] = previous,
        };

        // ksSpcObjectEdit = 0 means the object did not open for editing: nothing was written, so this refusal
        // carries no partial effect. DOC: «0 - в случае неудачи».
        var opened = Int(() => specification.ksSpcObjectEdit(pointer!.Value));
        if (opened != 1)
        {
            snapshot["ksSpcObjectEdit"] = opened;
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ksSpecification.ksSpcObjectEdit вернул "
                + (opened is null ? "непрочитанный результат" : opened.ToString())
                + ": строка на редактирование не открыта, документ не тронут.",
                RetryPolicy.Never,
                partialEffects: false,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        var applied = new List<string>();
        string? failedField = null;
        string? failedReason = null;
        foreach (var (field, text, type) in fields)
        {
            int? answer;
            try
            {
                answer = field switch
                {
                    "quantity" => Int(() =>
                        specification.ksSpcCount(SpecificationColumnNumbers.FirstOfType, text)),
                    "position" => Int(() => specification.ksSpcPosition(command.Position!.Value)),
                    _ => Int(() => specification.ksSetSpcObjectColumnText(
                        (int)type, SpecificationColumnNumbers.FirstOfType, SpecificationColumnNumbers.MainBlock,
                        text)),
                };
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                failedField = field;
                failedReason = "вызов прервался: " + ex.Message;
                break;
            }

            if (answer != 1)
            {
                // DOC: every setter answers 1 «в случае удачного завершения»; anything else is a refusal.
                failedField = field;
                failedReason = answer is null ? "вызов не вернул результат" : $"вернул {answer}";
                break;
            }

            applied.Add(field);
        }

        // The extra columns are written in the SAME open edit, so the whole row is one edit of the document:
        // a mass goes through its own documented setter, every other type through the column text by type,
        // number and block. A setter that does not answer 1 stops the sequence and is named.
        // DOC: ksspecification_kssetspcobjectcolumntext.html, ksspecification_ksspcmassa.html.
        // History: docs/decisions/bom.md#spec-spw-columns
        foreach (var column in extra)
        {
            if (failedField is not null)
            {
                break;
            }

            var key = SpecificationColumnKeys.KeyOf(column.Type, column.Number);
            int? answer;
            try
            {
                answer = column.Type == (int)ksSpecificationColumnTypeEnum.ksSColumnMass
                    ? Int(() => specification.ksSpcMassa(column.Value))
                    : Int(() => specification.ksSetSpcObjectColumnText(
                        column.Type, column.Number, column.Block, column.Value));
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                failedField = key;
                failedReason = "вызов прервался: " + ex.Message;
                break;
            }

            if (answer != 1)
            {
                failedField = key;
                failedReason = answer is null ? "вызов не вернул результат" : $"вернул {answer}";
                break;
            }

            applied.Add(key);
        }

        // The edit MUST be closed even when a setter refused: the object is open for editing, and leaving it
        // open would be an effect the answer hid. DOC: ksspecification_ksspcobjectend.html names the return as
        // «указатель на объект спецификации», not 1, so it is named in the answer, never read as a verdict.
        int? ended = null;
        string? endError = null;
        try
        {
            ended = Int(() => specification.ksSpcObjectEnd());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            endError = ex.Message;
        }

        // The document may have changed even when a setter refused: the revision moves before any read-back,
        // so a refusal still names the revision the caller has to continue from.
        BumpRevision(document, "specification.edit_row");

        if (failedField is null && endError is not null)
        {
            failedField = "ksSpcObjectEnd";
            failedReason = "вызов прервался: " + endError;
        }

        if (failedField is not null)
        {
            var now = TryReadRow(document, specification, unique, notes);
            snapshot["applied_by_setters"] = applied.ToArray();
            snapshot["failed_field"] = failedField;
            snapshot["ksSpcObjectEnd"] = ended is null
                ? "указатель не прочитан"
                : "вернул указатель " + ended.Value.ToString(CultureInfo.InvariantCulture);
            snapshot["after"] = now;
            snapshot["revision"] = document.Revision;
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Правка прервалась на поле {failedField} ({failedReason}); ksSpcObjectEnd "
                + (endError is null ? "вызван" : "прервался: " + endError) + ". Сеттеры подтвердили изменения "
                + (applied.Count == 0 ? "ни одного поля" : "полей: " + string.Join(", ", applied))
                + ". В строке сейчас: " + DescribeRow(now) + ". Отказ с частичными эффектами: строку "
                + "принимайте по перечитанному, а не по возврату сеттера.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failed_field"] = failedField,
                    ["failure_snapshot"] = snapshot,
                });
        }

        var after = ReadRowAfterEdit(document, specification, unique, notes);
        var api7 = new Dictionary<string, string?>(StringComparer.Ordinal);
        var api5 = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (field, text, type) in fields)
        {
            api7[field] = Api7Column(after.Listed, type, field, notes);
            api5[field] = Column(after.Specification, after.Pointer, type);
            var matches = SpecificationValueMatches(text, api7[field])
                          && SpecificationValueMatches(text, api5[field]);
            checks.Add(new NamedCheck(field + "_read_back", matches, api7[field] + " / " + api5[field], text));
        }

        // INVARIANT: a field this call did NOT name must come back as it was. Something else changed means the
        // model holds more than the request, and reporting the requested values as the whole answer hides it.
        foreach (var (field, was, now) in UntouchedFields(fields, previous, after.Row))
        {
            checks.Add(new NamedCheck(field + "_unchanged", SameSpecificationValue(was, now), now, was));
        }

        // The extra columns are verified with the TYPE of their element — a mass is a number, so «1.25» and
        // «1,25» are one value — and every column this call did NOT name is compared with what it was before.
        // History: docs/decisions/bom.md#spec-spw-columns
        var published = Api7ColumnPublication(after.Listed, model, notes);
        foreach (var column in extra)
        {
            var key = SpecificationColumnKeys.KeyOf(column.Type, column.Number);
            var api7Value = published.Values.TryGetValue(key, out var value) ? value : null;
            var api5Value = Column(
                after.Specification, after.Pointer, (ksSpecificationColumnTypeEnum)column.Type, column.Number,
                column.Block);
            var valueType = published.Types.TryGetValue(key, out var typeName) ? typeName : null;
            var matches = ColumnValueMatches(column.Value, api7Value, valueType)
                          && ColumnValueMatches(column.Value, api5Value, valueType);
            checks.Add(new NamedCheck(
                key + "_read_back", matches, $"{FormatValue(api7Value)} / {api5Value}", column.Value));
            api7[key] = FormatValue(api7Value);
            api5[key] = api5Value;
        }

        var named = extra.Select(column => SpecificationColumnKeys.KeyOf(column.Type, column.Number))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pair in beforeColumns.Values.Where(pair => !named.Contains(pair.Key)
                     && !SpecificationColumnKeys.RowFields.Contains(pair.Key)))
        {
            var now = published.Values.TryGetValue(pair.Key, out var value) ? value : null;
            var valueType = beforeColumns.Types.TryGetValue(pair.Key, out var typeName) ? typeName : null;
            var same = SameColumnValue(pair.Value, now, valueType);
            checks.Add(new NamedCheck(
                pair.Key + "_unchanged", same, FormatValue(now), FormatValue(pair.Value)));
        }

        if (checks.Any(check => !check.Passed))
        {
            snapshot["api7"] = api7;
            snapshot["api5"] = api5;
            snapshot["after"] = after.Row;
            snapshot["checks"] = checks.Where(check => !check.Passed)
                .Select(check => new { name = check.Name, observed = check.Observed, expected = check.Expected })
                .ToArray();
            snapshot["revision"] = document.Revision;
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Прочитанные назад значения расходятся с запрошенными либо изменилось поле, которого вызов "
                + "не называл: документ держит не то, что просил клиент. Публиковать это как успех нельзя.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        AddRowOrigin(after.Listed, notes);
        notes.Insert(0, $"specification_row_edited — строка {Fmt(unique)} перечитана обеими дорогами; "
            + "сеттер подтвердил поля: " + string.Join(", ", applied));

        // The row the answer publishes carries its columns, as the creation's row already does: a client that
        // wrote one column sees every column the document read, not only the four fields.
        // History: docs/decisions/bom.md#spec-spw-columns
        var answerRow = after.Row with
        {
            Columns = published.Values,
            ColumnTypes = published.Types,
            ColumnEditable = published.Editable,
        };

        if (!string.Equals(
                (command.RowRef.Marking ?? string.Empty).Trim(),
                (after.Row.Marking ?? string.Empty).Trim(),
                StringComparison.Ordinal))
        {
            notes.Add("specification_row_marking_changed — обозначение строки сменилось этой правкой: "
                + $"row_ref выдан на «{after.Row.Marking}», прежним «{command.RowRef.Marking}» строку больше "
                + "не адресовать (STALE_REFERENCE)");
        }

        // A written position of a .spw row is the DOCUMENT's number, not ours: the settings are read, and the
        // answer names it instead of presenting the in-session read-back as persisted.
        // History: docs/decisions/bom.md#spec-spw-columns
        if (command.Position is not null && !model)
        {
            if (PositionAssignedByDocument(description, previous.SectionNumber, notes) is { } positionWarning)
            {
                warnings.Add(positionWarning);
            }
        }

        // LIMIT: the tool works in memory; the file is written by kompas_save_document, so survival of the edit
        // across a save and a reopen is the acceptance's business, not this call's.
        var unverified = new List<string>
        {
            "row_not_written_to_file — правка живёт в документе до kompas_save_document; переживание "
            + "сохранения и переоткрытия проверяет приёмка (SPEC-03.save_reopen для модели, "
            + "SPEC-06.save_reopen для .spw), а не этот вызов",
        };

        if (warnings.Count > 0)
        {
            // The position of a .spw row is the document's number, so the file WILL hold a different value than
            // the one this call wrote: saying «save it and you get 4» would be the false success the block
            // forbids. The aspect is named with the value the document gives it.
            unverified.Add("position_not_kept_by_spw_file — записанная позиция строки .spw читается обеими "
                + "дорогами в сеансе, но файл держит номер документа (номер строки в разделе от "
                + "ISpecificationTuning.InitialPosition), поэтому переживание сохранения этой правкой не "
                + "заявлено");
        }

        return new EditSpecificationRowResult(
            new SpecificationRowRefDto(document.Id, unique, answerRow.Marking),
            previous,
            answerRow,
            SpecificationRowEditRoute,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            notes.Distinct(StringComparer.Ordinal).ToList(),
            unverified,
            warnings.Count == 0 ? null : warnings.ToArray());
    }

    /// <summary>The reference as the pure rule sees it.</summary>
    private static SpecificationRowRefValues? RefOf(SpecificationRowRefDto? rowRef) =>
        rowRef is null ? null : new SpecificationRowRefValues(rowRef.DocumentId, rowRef.UniqueNumber, rowRef.Marking);

    /// <summary>The requested fields in the order the setters write them.</summary>
    /// <remarks>DOC: <c>ksspecification_kssetspcobjectcolumntext.html</c> (marking and name are COLUMN texts of
    /// their type), <c>ksspecification_ksspccount.html</c> (quantity), <c>ksspecification_ksspcposition.html</c>
    /// (position). <c>ksspecificationcolumntypeenum.html</c> names the types.</remarks>
    private static List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> EditFields(
        EditSpecificationRowCommand command)
    {
        var fields = new List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)>();
        if (command.Marking is { } marking)
        {
            fields.Add(("marking", marking.Trim(), ksSpecificationColumnTypeEnum.ksSColumnMark));
        }

        if (command.Name is { } name)
        {
            fields.Add(("name", name.Trim(), ksSpecificationColumnTypeEnum.ksSColumnName));
        }

        if (command.Quantity is { } quantity)
        {
            fields.Add(("quantity", quantity.Trim(), ksSpecificationColumnTypeEnum.ksSColumnCount));
        }

        if (command.Position is { } position)
        {
            fields.Add(("position", position.ToString(CultureInfo.InvariantCulture),
                ksSpecificationColumnTypeEnum.ksSColumnPosition));
        }

        return fields;
    }

    /// <summary>Every requested field and column needs its column and a value the element accepts, BEFORE any
    /// setter.</summary>
    /// <remarks>DOC: the column is found by TYPE on the object itself
    /// (<c>ispecificationcolumns_column.html</c>: «type, ColumnNumber, BlockNumber»), and the element's type
    /// follows <c>ValueType</c> (<c>ispecificationcolumnitem_value.html</c>).
    /// INVARIANT: a missing column, a type the route was not measured to write, or a value of the wrong type is
    /// refused before <c>ksSpcObjectEdit</c>, so the row is untouched and the refusal carries no partial effect.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private void EnsureEditableColumns(
        ISpecificationBaseObject listed,
        List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> fields,
        IReadOnlyList<SpecificationExtraColumn> extra, bool model, DocumentEntry document,
        EditSpecificationRowCommand command, SpecificationLineDto previous)
    {
        foreach (var (field, text, type) in fields)
        {
            var column = ColumnOf(
                listed, type, SpecificationColumnNumbers.FirstOfType, SpecificationColumnNumbers.MainBlock);
            var items = column?.ColumnItems as ISpecificationColumnItems;
            if (items is not { Count: > 0 })
            {
                throw MissingColumnRefusal(field, (int)type, document, command, previous);
            }

            var valueType = SafeRead.EnumOrNull<ksValueTypeEnum>(() => items[0].ValueType);
            if (valueType is null)
            {
                throw UnreadValueTypeRefusal(field, document, command, previous);
            }

            EnsureValueFits(field, text, valueType.Value, document, command, previous);
        }

        foreach (var column in extra)
        {
            var key = SpecificationColumnKeys.KeyOf(column.Type, column.Number);
            var type = (ksSpecificationColumnTypeEnum)column.Type;
            if (!SpecificationColumnKeys.IsEditable(column.Type, model))
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Колонка «{key}» правкой не пишется: маршрут правки (ksSetSpcObjectColumnText/ksSpcMassa) "
                    + "замером подтверждён только для "
                    + $"{SpecificationColumnKeys.DescribeEditableExtra(model)}"
                    + (model ? " в спецификации модели" : " в документе-спецификации .spw")
                    + "; тип «" + key + "» замером не подтверждён, и выдавать его запись за успех нельзя. "
                    + "Строка не тронута.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["unique_number"] = command.RowRef.UniqueNumber,
                        ["unwritten_column"] = key,
                        ["column_type"] = column.Type,
                        ["writable_keys"] = SpecificationColumnKeys.DescribeEditableExtra(model),
                        ["failure_snapshot"] = new Dictionary<string, object?>
                        {
                            ["document_id"] = document.Id,
                            ["unique_number"] = command.RowRef.UniqueNumber,
                            ["before"] = previous,
                            ["unwritten_column"] = key,
                            ["column_type"] = column.Type,
                        },
                    });
            }

            // ksSpcMassa has no number and no block: it fills «additional column mass», so an address beyond the
            // first main-block mass column cannot be honored by it and is refused instead of written into it.
            if (type == ksSpecificationColumnTypeEnum.ksSColumnMass
                && (column.Number != SpecificationColumnNumbers.FirstOfType
                    || column.Block != SpecificationColumnNumbers.MainBlock))
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"Масса адресуется колонкой «{key}» (номер {column.Number}, блок {column.Block}), а "
                    + "ksSpcMassa не принимает ни номера, ни блока: он заполняет первую колонку массы "
                    + "основного блока. Строка не тронута.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["unique_number"] = command.RowRef.UniqueNumber,
                        ["mass_number"] = column.Number,
                        ["mass_block"] = column.Block,
                    });
            }

            var found = ColumnOf(listed, type, column.Number, column.Block);
            var items = found?.ColumnItems as ISpecificationColumnItems;
            if (items is null || items.Count == 0 || column.Block >= items.Count)
            {
                throw MissingColumnRefusal(key, column.Type, document, command, previous);
            }

            var element = SafeRead.Ref(() => items[column.Block]);
            var valueType = element is null ? null : SafeRead.EnumOrNull<ksValueTypeEnum>(() => element.ValueType);
            if (valueType is null)
            {
                throw UnreadValueTypeRefusal(key, document, command, previous);
            }

            EnsureValueFits(key, column.Value, valueType.Value, document, command, previous);
        }
    }

    /// <summary>The refusal of a column the object does not have, with the ones it has.</summary>
    private static KompasContractException MissingColumnRefusal(
        string key, int type, DocumentEntry document, EditSpecificationRowCommand command,
        SpecificationLineDto previous) =>
        new(
            ErrorCodes.CapabilityUnavailable,
            $"Колонки «{key}» (тип {type}) у объекта нет: в стиле спецификации такой колонки не задано, значение "
            + "записать некуда. Строка не тронута.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["unique_number"] = command.RowRef.UniqueNumber,
                ["missing_column"] = key,
                ["column_type"] = type,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = command.RowRef.UniqueNumber,
                    ["before"] = previous,
                    ["missing_column"] = key,
                    ["column_type"] = type,
                },
            });

    /// <summary>The refusal of an element whose value type the document did not answer.</summary>
    private static KompasContractException UnreadValueTypeRefusal(
        string key, DocumentEntry document, EditSpecificationRowCommand command, SpecificationLineDto previous) =>
        new(
            ErrorCodes.CapabilityUnavailable,
            $"Тип значения колонки «{key}» не прочитан: принимать ли этой колонкой значение — неизвестно, строка "
            + "не тронута.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["unique_number"] = command.RowRef.UniqueNumber,
                ["unread_value_type"] = key,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = command.RowRef.UniqueNumber,
                    ["before"] = previous,
                },
            });

    /// <summary>The value must fit the element, and a value type the route does not write is refused.</summary>
    /// <remarks>LIMIT: a column of type «Запись» (<c>ksValueTypeRecord</c>) carries a structured element, not a
    /// text; the contract writes text and has no fixture with such a column, so it is refused by name.
    /// DOC: <c>ispecificationcolumnitem_value.html</c>, <c>ispecificationcolumn_valuetype.html</c>.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private static void EnsureValueFits(
        string key, string text, ksValueTypeEnum valueType, DocumentEntry document,
        EditSpecificationRowCommand command, SpecificationLineDto previous)
    {
        if (valueType == ksValueTypeEnum.ksValueTypeRecord)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Колонка «{key}» имеет тип значения «Запись» (ksValueTypeRecord): правка пишет текст, а такой "
                + "элемент составной и замером не проверен — запись не выполняется, строка не тронута.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = command.RowRef.UniqueNumber,
                    ["record_column"] = key,
                    ["failure_snapshot"] = new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["unique_number"] = command.RowRef.UniqueNumber,
                        ["before"] = previous,
                        ["record_column"] = key,
                    },
                });
        }

        if (TryColumnValue(text, valueType, out _))
        {
            return;
        }

        throw new KompasContractException(
            ErrorCodes.InvalidArgument,
            $"Значение «{text}» не подходит колонке «{key}»: её элемент ждёт {DescribeValueType(valueType)}. "
            + "Строка не тронута.",
            RetryPolicy.Never,
            details: new Dictionary<string, object?>
            {
                ["field"] = key,
                ["value"] = text,
                ["value_type"] = valueType.ToString(),
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = command.RowRef.UniqueNumber,
                    ["before"] = previous,
                    ["field"] = key,
                    ["value"] = text,
                },
            });
    }

    /// <summary>The row of the document that carries this number — or null, with how many carried it.</summary>
    private static ISpecificationBaseObject? ObjectsWithNumber(
        ISpecificationDescription description, double unique, out int candidates)
    {
        var matches = SpecificationObjects(description, new List<string>())
            .Where(item => SameUniqueNumber(Double(() => item.UniqueNumber), unique))
            .ToList();
        candidates = matches.Count;
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The row as the document answers it, with the API5 pointer the column texts come from.</summary>
    /// <remarks>INVARIANT: the description is taken from the document AGAIN after the edit — the collection read
    /// before it does not have to answer for a row the edit touched — and the pointer is confirmed by
    /// <c>ksGetSpcObjectNumber</c> before any of its texts are published.
    /// History: docs/decisions/bom.md#spec-edit-row</remarks>
    private (ISpecificationBaseObject Listed, SpecificationLineDto Row, ksSpecification Specification, int Pointer)
        ReadRowAfterEdit(
            DocumentEntry document, ksSpecification specification, double unique, List<string> notes)
    {
        var description = ActiveSpecificationDescription(document, notes)
            ?? throw RowEditReadRefusal(document, unique,
                "После правки у документа пропало описание спецификации (Active = NULL): строку не прочитать "
                + "и правку подтвердить нечем.", null);

        var listed = ObjectsWithNumber(description, unique, out var candidates)
            ?? throw RowEditReadRefusal(document, unique, candidates == 0
                ? $"После правки строки с номером {Fmt(unique)} в документе нет: правка её не сохранила."
                : $"После правки строк с номером {Fmt(unique)} в документе {candidates}: одна ссылка больше не "
                  + "адресует одну строку.", null);

        var pointer = Int(() => specification.ksGetSpcObject(unique));
        if (pointer is null or <= 0)
        {
            throw RowEditReadRefusal(document, unique,
                $"После правки ksGetSpcObject({Fmt(unique)}) не дал указатель: тексты колонок не прочитаны.", null);
        }

        var row = ReadSpecificationLine(specification, listed, notes)
            ?? throw RowEditReadRefusal(document, unique,
                "После правки поля строки не прочитаны документированным маршрутом: подтверждать нечего.", null);

        return (listed, row, specification, pointer.Value);
    }

    /// <summary>The row after a refused edit, for the snapshot only: a failure here must not hide the refusal.</summary>
    private (ISpecificationBaseObject Listed, SpecificationLineDto Row)? TryReadRow(
        DocumentEntry document, ksSpecification specification, double unique, List<string> notes)
    {
        try
        {
            var description = ActiveSpecificationDescription(document, notes);
            if (description is null)
            {
                return null;
            }

            var listed = ObjectsWithNumber(description, unique, out _);
            if (listed is null)
            {
                return null;
            }

            var row = ReadSpecificationLine(specification, listed, notes);
            return row is null ? null : (listed, row);
        }
        catch (KompasContractException)
        {
            return null;
        }
    }

    /// <summary>The fields the call did NOT name, as before and after.</summary>
    private static List<(string Field, string? Was, string? Now)> UntouchedFields(
        List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> fields,
        SpecificationLineDto before, SpecificationLineDto after)
    {
        var named = fields.Select(field => field.Field).ToHashSet(StringComparer.Ordinal);
        var pairs = new (string Field, string? Was, string? Now)[]
        {
            ("marking", before.Marking, after.Marking),
            ("name", before.Name, after.Name),
            ("quantity", before.Quantity, after.Quantity),
            ("position", before.Position, after.Position),
            ("section", before.Section, after.Section),
            ("section_number",
                before.SectionNumber?.ToString(CultureInfo.InvariantCulture),
                after.SectionNumber?.ToString(CultureInfo.InvariantCulture)),
        };

        return pairs.Where(pair => !named.Contains(pair.Field)).ToList();
    }

    /// <summary>The reference matches the marking the document shows.</summary>
    /// <remarks>INVARIANT: both sides are compared as TEXT after trimming, because a marking is an identifier
    /// («007» and «7» are two markings), and an empty reference marking matches an empty one.</remarks>
    private static bool RowMarkingMatches(string? reference, string? documentMarking) =>
        string.Equals(
            (reference ?? string.Empty).Trim(), (documentMarking ?? string.Empty).Trim(), StringComparison.Ordinal);

    /// <summary>Whether two values the document answered are the same value.</summary>
    /// <remarks>INVARIANT: a number is compared AS a number («03» and «3» are one quantity) and text as text;
    /// a value that was not read equals only another value that was not read.</remarks>
    private static bool SameSpecificationValue(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : SpecificationValueMatches(left, right);

    /// <summary>The row in one line, for the sentence of a refusal.</summary>
    private static string DescribeRow((ISpecificationBaseObject Listed, SpecificationLineDto Row)? state) =>
        state is not { } found
            ? "строка не найдена после правки"
            : $"маркировка «{found.Row.Marking}», наименование «{found.Row.Name}», количество "
              + $"«{found.Row.Quantity}», позиция «{found.Row.Position}»";

    /// <summary>What the document itself says about where the row's data comes from.</summary>
    /// <returns>True when the document does not call the row its own, so a deletion may not hold.</returns>
    /// <remarks>DOC: <c>ispecificationobject_state.html</c> — ksSpecificationObjectStateEnum: «объект создан в
    /// данном документе», «объект пришел из вставки другого документа», «объект из вставки другого документа,
    /// но редактировался в данном документе»; <c>ispecificationbaseobject_syncronizewithproperties.html</c> —
    /// «Брать данные из свойств и передавать данные в свойства объекта».
    /// INVARIANT: neither member NAMES a component, so the note reports them as READ and no row is refused for
    /// its origin. The link itself is documented and made by its own tool (<c>AddWithParam</c> +
    /// <c>IPropertyKeeper</c>), not queried here. History: docs/decisions/bom.md#spec-edit-row,
    /// docs/decisions/bom.md#spec-spw</remarks>
    private static bool AddRowOrigin(ISpecificationBaseObject listed, List<string> notes)
    {
        var state = SafeRead.EnumOrNull<ksSpecificationObjectStateEnum>(() => listed.State);
        var syncs = SafeRead.Bool(() => listed.SyncronizeWithProperties);
        var origin = "specification_row_origin — что документ сам говорит о происхождении строки: состояние "
            + SpecificationObjectStateName(state) + ", синхронизация со свойствами объекта — "
            + (syncs is null ? "не прочитана" : syncs.Value ? "включена" : "выключена")
            + "; к какому компоненту строка относится, документ не называет";
        notes.Add(origin);
        return (state is not null && state != ksSpecificationObjectStateEnum.ksObjectStateIndependent)
               || syncs == true;
    }

    /// <summary>The state in the words of its help page; a value the page does not name is named as such.</summary>
    /// <remarks>DOC: <c>ispecificationobject_state.html</c> lists three meanings; the shipped interop also
    /// declares a fourth value, which the page does not describe and which is therefore not given a meaning.
    /// </remarks>
    private static string SpecificationObjectStateName(ksSpecificationObjectStateEnum? state) => state switch
    {
        null => "не прочитано",
        ksSpecificationObjectStateEnum.ksObjectStateIndependent => "объект создан в данном документе (0)",
        ksSpecificationObjectStateEnum.ksObjectStateFromInsert =>
            "объект пришёл из вставки другого документа (1)",
        ksSpecificationObjectStateEnum.ksObjectStateEdit =>
            "объект из вставки, но редактировался в данном документе (2)",
        _ => $"значение {(int)state.Value} (страница справки его не называет)",
    };

    /// <summary>The refusal of a reference the document no longer answers.</summary>
    /// <remarks>INVARIANT: a stale reference is refused BEFORE any setter, so this refusal carries no partial
    /// effect — the model is untouched.</remarks>
    private static KompasContractException StaleRowRefusal(
        DocumentEntry document, EditSpecificationRowCommand command, string message,
        SpecificationLineDto? current) =>
        new(
            ErrorCodes.StaleReference,
            message,
            RetryPolicy.ReacquireContext,
            partialEffects: false,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["unique_number"] = command.RowRef.UniqueNumber,
                ["failure_snapshot"] = new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["row_ref"] = command.RowRef,
                    ["current"] = current,
                },
            });

    /// <summary>The refusal of a read-back that did not confirm the edited row.</summary>
    private static KompasContractException RowEditReadRefusal(
        DocumentEntry document, double unique, string message, Dictionary<string, object?>? snapshot) =>
        new(
            ErrorCodes.GeometryFailed,
            message,
            RetryPolicy.ReacquireContext,
            partialEffects: true,
            details: new Dictionary<string, object?>
            {
                ["document_id"] = document.Id,
                ["unique_number"] = unique,
                ["failure_snapshot"] = snapshot ?? new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                },
            });

    /// <summary>Whether a column the call did not name still carries the value it had before the edit.</summary>
    /// <remarks>INVARIANT: compared with the TYPE of the column's element, so the document's own normalization
    /// («1,25» and 1.25, an empty mass as 0) does not read as a change of a column nobody named.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private static bool SameColumnValue(object? before, object? after, string? valueType)
    {
        var left = FormatValue(before);
        var right = FormatValue(after);
        return left is null || right is null
            ? left is null && right is null
            : ColumnValueMatches(left, right, valueType);
    }

    /// <summary>Why a <c>.spw</c> row's written position is not what the file keeps, read from the settings.</summary>
    /// <returns>The warning text when the DOCUMENT assigns the row's number, otherwise <c>null</c>.</returns>
    /// <remarks>DOC: <c>ispecificationtuning_sections.html</c> («доступно только для чтения»),
    /// <c>ispecificationtuningsection_putposition.html</c> (TRUE — «в разделе возможен автоматический расчет
    /// номеров позиций»), <c>ispecificationtuning_initialposition.html</c> («номер позиции, который должен
    /// иметь первый объект спецификации»). MEASURED: a written 4 reads back in the session, and the saved file
    /// gives the row its number in the section (1, 2). INVARIANT: an unread setting is a NOTE, never a guess.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    private static string? PositionAssignedByDocument(
        ISpecificationDescription description, int? sectionNumber, List<string> notes)
    {
        var tuning = SafeRead.Ref(() => description.SpecificationTuning);
        var initial = tuning is null ? null : Int(() => tuning.InitialPosition);
        bool? putPosition = null;
        if (tuning is not null && sectionNumber is { } section)
        {
            var sections = SafeRead.Ref(() => tuning.Sections);
            for (var index = 0; sections is not null && index < sections.Count; index++)
            {
                var item = SafeRead.Ref(() => sections[index] as ISpecificationTuningSection);
                if (item is null || Int(() => item.Number) != section)
                {
                    continue;
                }

                putPosition = SafeRead.Bool(() => item.PutPosition);
                break;
            }
        }

        // ASSUMPTION: the numbering belongs to the row's own section, so a section whose settings did not read is
        // NOT guessed to be automatic.
        if (putPosition is null)
        {
            notes.Add("specification_position_settings_unread — настройки раздела "
                + $"{sectionNumber?.ToString(CultureInfo.InvariantCulture) ?? "?"} не прочитаны "
                + "(ISpecificationTuning.Sections): кто ставит номер позиции строки, не установлено");
            return null;
        }

        if (putPosition != true)
        {
            return null;
        }

        return "specification_position_set_by_document — позицию строки этого документа ставит ДОКУМЕНТ: "
            + $"у раздела {sectionNumber} включена автоматическая простановка номеров "
            + "(ISpecificationTuningSection.PutPosition = TRUE), а первый объект получает номер InitialPosition "
            + $"{initial?.ToString(CultureInfo.InvariantCulture) ?? "?"}. Записанное значение читается обеими "
            + "дорогами в сеансе, но файл после сохранения держит номер документа (измерено: 4 → номер строки в "
            + "разделе), поэтому правку позиции у .spw не считайте переживающей сохранение.";
    }
}
