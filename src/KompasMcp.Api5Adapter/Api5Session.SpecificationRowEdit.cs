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
        "api5:ksDocument3D.GetSpecification() → ksGetSpcObject(unique_number) → ksGetSpcObjectNumber (сверка "
        + "указателя) → ksSpcObjectEdit → ksSetSpcObjectColumnText(тип колонки, 1, 0) / ksSpcCount(1) / "
        + "ksSpcPosition → ksSpcObjectEnd; до и после правки строка читается обеими дорогами: API7 "
        + "(SpecificationDescriptions.Active → Objects → Columns → ColumnItems) и API5 "
        + "(ksGetSpcObjectColumnText + ksGetSpcSectionName)";

    /// <summary>Edit the row the reference addresses and answer what the document holds afterwards.</summary>
    public EditSpecificationRowResult EditSpecificationRow(EditSpecificationRowCommand command)
    {
        // The same pure rule the Host applies, repeated where COM is about to be asked.
        if (SpecificationRowEditInput.Validate(
                command.DocumentId, RefOf(command.RowRef), command.Marking, command.Name, command.Quantity,
                command.Position) is { } refused)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                refused.Message,
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = refused.Field });
        }

        var document = RequireModelForRowWrite(command.DocumentId, "правит");
        var notes = new List<string>();
        var checks = new List<NamedCheck>();
        var unique = command.RowRef.UniqueNumber;

        var description = ActiveSpecificationDescription(document, notes)
            ?? throw StaleRowRefusal(document, command,
                "У документа нет описания спецификации (Active = NULL): строки, которую адресует ссылка, "
                + "не существует, модель не тронута.", null);

        // The row is found in the list and then through the API5 pointer, and the pointer answers its own
        // number: a foreign row under this number would be edited silently otherwise.
        var listed = ObjectsWithNumber(description, unique, out var candidates);
        if (listed is null)
        {
            throw StaleRowRefusal(document, command, candidates == 0
                ? $"Строки с номером {Fmt(unique)} в документе нет: ссылка устарела, модель не тронута."
                : $"Строк с номером {Fmt(unique)} в документе несколько ({candidates}): ссылка не адресует "
                  + "одну строку, модель не тронута.", null);
        }

        var specification = Specification5(document);
        var pointer = Int(() => specification.ksGetSpcObject(unique));
        var confirmed = pointer is > 0
            && SameUniqueNumber(Double(() => specification.ksGetSpcObjectNumber(pointer.Value)), unique);
        if (!confirmed)
        {
            throw StaleRowRefusal(document, command,
                $"ksGetSpcObject({Fmt(unique)}) + ksGetSpcObjectNumber не подтвердили строку: указатель "
                + (pointer is > 0 ? "отвечает другим номером или не отвечает" : "не получен")
                + ". Чужую строку под этим номером не правим, модель не тронута.", null);
        }

        var previous = ReadSpecificationLine(specification, listed, notes)
            ?? throw StaleRowRefusal(document, command,
                "Строка есть в списке, но её поля не прочитаны документированным маршрутом: сверять ссылку "
                + "и сравнивать правку не с чем, модель не тронута.", null);

        if (!RowMarkingMatches(command.RowRef.Marking, previous.Marking))
        {
            throw StaleRowRefusal(document, command,
                $"Маркировка строки «{previous.Marking ?? "не прочитана"}» не равна маркировке ссылки "
                + $"«{command.RowRef.Marking}»: ссылку выдали на прежнее состояние строки, модель не тронута.",
                previous);
        }

        var fields = EditFields(command);
        EnsureEditableColumns(listed, fields, document, command, previous);

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
            var now = TryReadRow(document, unique, notes);
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

        var after = ReadRowAfterEdit(document, unique, notes);
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

        if (!string.Equals(
                (command.RowRef.Marking ?? string.Empty).Trim(),
                (after.Row.Marking ?? string.Empty).Trim(),
                StringComparison.Ordinal))
        {
            notes.Add("specification_row_marking_changed — обозначение строки сменилось этой правкой: "
                + $"row_ref выдан на «{after.Row.Marking}», прежним «{command.RowRef.Marking}» строку больше "
                + "не адресовать (STALE_REFERENCE)");
        }

        // LIMIT: the tool works in memory; the file is written by kompas_save_document, so survival of the edit
        // across a save and a reopen is the acceptance's business, not this call's.
        var unverified = new List<string>
        {
            "row_not_written_to_file — правка живёт в модели до kompas_save_document; переживание "
            + "сохранения и переоткрытия проверяет приёмка (SPEC-03.save_reopen), а не этот вызов",
        };

        return new EditSpecificationRowResult(
            new SpecificationRowRefDto(document.Id, unique, after.Row.Marking),
            previous,
            after.Row,
            SpecificationRowEditRoute,
            new VerificationDto(
                checks.All(check => check.Passed) ? VerificationLevel.StructureChecked : VerificationLevel.CallReturned,
                checks,
                unverified),
            notes.Distinct(StringComparer.Ordinal).ToList(),
            unverified);
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

    /// <summary>Every requested field needs its column and a value the element accepts, BEFORE any setter.</summary>
    /// <remarks>DOC: the column is found by TYPE on the object itself
    /// (<c>ispecificationcolumns_column.html</c>: «type, ColumnNumber, BlockNumber»), and the element's type
    /// follows <c>ValueType</c> (<c>ispecificationcolumnitem_value.html</c>).
    /// INVARIANT: a missing column or a value of the wrong type is refused before <c>ksSpcObjectEdit</c>, so the
    /// row is untouched and the refusal carries no partial effect.</remarks>
    private void EnsureEditableColumns(
        ISpecificationBaseObject listed,
        List<(string Field, string Text, ksSpecificationColumnTypeEnum Type)> fields,
        DocumentEntry document, EditSpecificationRowCommand command, SpecificationLineDto previous)
    {
        foreach (var (field, text, type) in fields)
        {
            var columns = SafeRead.Ref(() => listed.Columns);
            var column = columns is null
                ? null
                : SafeRead.Ref(() => columns.Column[type, SpecificationColumnNumbers.FirstOfType,
                    SpecificationColumnNumbers.MainBlock]);
            var items = column?.ColumnItems as ISpecificationColumnItems;
            if (items is not { Count: > 0 })
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    $"В стиле нет колонки {field} (тип {(int)type}): значение некуда записать, строка не тронута.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["unique_number"] = command.RowRef.UniqueNumber,
                        ["missing_column"] = field,
                        ["column_type"] = (int)type,
                        ["failure_snapshot"] = new Dictionary<string, object?>
                        {
                            ["document_id"] = document.Id,
                            ["unique_number"] = command.RowRef.UniqueNumber,
                            ["before"] = previous,
                            ["missing_column"] = field,
                            ["column_type"] = (int)type,
                        },
                    });
            }

            if (field != "quantity")
            {
                continue;
            }

            var valueType = SafeRead.EnumOrNull<ksValueTypeEnum>(() => items[0].ValueType);
            if (valueType is null)
            {
                throw new KompasContractException(
                    ErrorCodes.CapabilityUnavailable,
                    "Тип значения колонки quantity не прочитан: принимать ли этой колонкой «" + text
                    + "» — неизвестно, строка не тронута.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["document_id"] = document.Id,
                        ["unique_number"] = command.RowRef.UniqueNumber,
                        ["unread_value_type"] = field,
                    });
            }

            if (!TryColumnValue(text, valueType.Value, out _))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Значение «{text}» не подходит колонке {field}: её элемент ждёт "
                    + $"{DescribeValueType(valueType.Value)}. Строка не тронута.",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?>
                    {
                        ["field"] = field,
                        ["value"] = text,
                        ["value_type"] = valueType.Value.ToString(),
                    });
            }
        }
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
        ReadRowAfterEdit(DocumentEntry document, double unique, List<string> notes)
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

        var specification = Specification5(document);
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
        DocumentEntry document, double unique, List<string> notes)
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

            var row = ReadSpecificationLine(Specification5(document), listed, notes);
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
    /// its origin. LIMIT: a documented sign tying a row to a component does not exist in COM (<c>IPropertyKeeper</c>
    /// has no entry point), so none is searched. History: docs/decisions/bom.md#spec-edit-row</remarks>
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
}
