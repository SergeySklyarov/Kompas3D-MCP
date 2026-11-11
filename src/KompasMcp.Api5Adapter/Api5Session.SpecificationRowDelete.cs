using System.Globalization;
using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.Com;

namespace KompasMcp.Api5Adapter;

/// <summary>Deletion of one row of a MODEL specification — block <c>SPEC_DELETE_ROW</c>.</summary>
/// <remarks>DOC: <c>ispecificationobject_delete.html</c> — «Метод позволяет удалить базовый или
/// вспомогательный объект спецификации из документа и из коллекции»; <c>ispecificationobject_uniquenumber.html</c>
/// — the number is the row's address; <c>ispecificationobject_state.html</c> and
/// <c>ispecificationbaseobject_syncronizewithproperties.html</c> — what the document says about where the row's
/// data comes from.
/// MEASURED: the deletion survives a save and a reopen, and the neighbours stay in place.
/// INVARIANT: success is the ABSENCE the document answers through BOTH roads afterwards, never the answer of
/// <c>Delete()</c>; a row that is still there is a refusal with a snapshot, not a success.
/// History: docs/decisions/bom.md#spec-delete-row</remarks>
public partial class Api5Session
{
    private const string SpecificationRowDeleteRoute =
        "api7:ISpecificationDescription.Objects (строка находится по row_ref.unique_number тем же общим кодом, "
        + "что у правки) → api5:ksGetSpcObject(unique_number) + ksGetSpcObjectNumber (сверка указателя) → "
        + "сверка маркировки со ссылкой → ISpecificationObject.Delete(); после удаления отсутствие строки "
        + "перечитывается обеими дорогами: API7 Objects (строки с этим номером нет, строк на одну меньше, "
        + "прочие на месте) и API5 ksGetSpcObject по удалённому номеру (указателя нет)";

    /// <summary>Delete the row the reference addresses and answer what the document holds afterwards.</summary>
    public DeleteSpecificationRowResult DeleteSpecificationRow(DeleteSpecificationRowCommand command)
    {
        // The same pure rule the Host applies, repeated where COM is about to be asked.
        if (SpecificationRowDeleteInput.Validate(command.DocumentId, RefOf(command.RowRef)) is { } refused)
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                refused.Message,
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = refused.Field });
        }

        var document = RequireModelForRowWrite(command.DocumentId, "удаляет");
        var notes = new List<string>();
        var checks = new List<NamedCheck>();
        var unique = command.RowRef.UniqueNumber;

        var description = ActiveSpecificationDescription(document, notes)
            ?? throw StaleDeleteRefusal(document, command,
                "У документа нет описания спецификации (Active = NULL): строки, которую адресует ссылка, "
                + "не существует, модель не тронута.", null);

        var listed = ObjectsWithNumber(description, unique, out var candidates);
        if (listed is null)
        {
            throw StaleDeleteRefusal(document, command, candidates == 0
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
            throw StaleDeleteRefusal(document, command,
                $"ksGetSpcObject({Fmt(unique)}) + ksGetSpcObjectNumber не подтвердили строку: указатель "
                + (pointer is > 0 ? "отвечает другим номером или не отвечает" : "не получен")
                + ". Чужую строку под этим номером не удаляем, модель не тронута.", null);
        }

        var previous = ReadSpecificationLine(specification, listed, notes)
            ?? throw StaleDeleteRefusal(document, command,
                "Строка есть в списке, но её поля не прочитаны документированным маршрутом: сверять ссылку "
                + "и называть удалённые значения не с чем, модель не тронута.", null);

        if (!RowMarkingMatches(command.RowRef.Marking, previous.Marking))
        {
            throw StaleDeleteRefusal(document, command,
                $"Маркировка строки «{previous.Marking ?? "не прочитана"}» не равна маркировке ссылки "
                + $"«{command.RowRef.Marking}»: ссылку выдали на прежнее состояние строки, модель не тронута.",
                previous);
        }

        var before = RowCensus(description, notes);
        var beforeOther = before.Where(row => !IsDeletedRow(row, unique)).ToList();
        var snapshot = new Dictionary<string, object?>
        {
            ["document_id"] = document.Id,
            ["unique_number"] = unique,
            ["row_ref_marking"] = command.RowRef.Marking,
            ["before"] = previous,
            ["rows_before"] = before.Count,
            ["others_before"] = beforeOther,
        };

        // The values of the row are verified on BOTH roads BEFORE the object is gone: after Delete() the row
        // cannot be read at all, so a disagreement between the roads is only visible here.
        // History: docs/decisions/bom.md#spec-delete-row
        var api7 = Api7Values(listed, notes);
        foreach (var (field, api5) in Api5Values(previous))
        {
            checks.Add(new NamedCheck(
                field + "_read_back",
                SameSpecificationValue(api5, api7[field]),
                api7[field],
                api5));
        }

        // DOC: the documented member is on ISpecificationObject and says it removes a BASE object too.
        // A listed object that does not carry the interface is refused BEFORE the call: nothing is touched.
        if (listed is not ISpecificationObject deletable)
        {
            snapshot["api7"] = api7;
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Объект списка не приводит к ISpecificationObject: документированного Delete() у него нет, "
                + "строка не тронута.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        // Read BEFORE the deletion: the object is gone afterwards, and a note about a row that may return
        // cannot be produced from what the document no longer has.
        var mayReturn = AddRowOrigin(listed, notes);

        bool? deleted;
        try
        {
            deleted = Bool(() => deletable.Delete());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            BumpRevision(document, "specification.delete_row");
            var still = TryReadRow(document, unique, notes);
            snapshot["delete"] = "вызов прервался: " + ex.Message;
            snapshot["after"] = still?.Row;
            snapshot["revision"] = document.Revision;
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ISpecificationObject.Delete() прервался: " + ex.Message + ". Удалена ли строка — вызов не "
                + "сказал; перечитано: " + DescribeRow(still) + ". Отказ с частичными эффектами: строку "
                + "принимайте по перечитанному списку.",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        // The document may have changed even when Delete() answered false: the revision moves before any
        // read-back, so the refusal carries the revision the caller has to continue from.
        BumpRevision(document, "specification.delete_row");

        if (deleted != true)
        {
            var still = TryReadRow(document, unique, notes);
            snapshot["delete"] = deleted;
            snapshot["after"] = still?.Row;
            snapshot["revision"] = document.Revision;
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "ISpecificationObject.Delete() вернул "
                + (deleted is null ? "непрочитанный результат" : "false")
                + ": документ строку не удалил. Это отказ ядра, а не успех. Перечитано: " + DescribeRow(still)
                + ".",
                RetryPolicy.Never,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        var after = ReadAfterDelete(document, unique, notes);
        var missing = beforeOther
            .Where(row => !after.Rows.Any(kept => SameRowNumber(kept, row)))
            .Select(row => row.Unique)
            .ToArray();
        var changed = beforeOther
            .Where(row => after.Rows.FirstOrDefault(kept => SameRowNumber(kept, row)) is { } kept
                          && !SameRowValues(kept, row))
            .Select(row => row.Unique)
            .ToArray();
        var appeared = after.Rows
            .Where(row => !before.Any(held => SameRowNumber(held, row)))
            .Select(row => row.Unique)
            .ToArray();

        checks.Add(new NamedCheck("row_absent_api7", after.AbsentApi7));
        checks.Add(new NamedCheck("row_absent_api5", after.AbsentApi5));
        checks.Add(new NamedCheck(
            "rows_before_minus_one", after.Rows.Count == before.Count - 1,
            after.Rows.Count.ToString(CultureInfo.InvariantCulture),
            (before.Count - 1).ToString(CultureInfo.InvariantCulture)));
        checks.Add(new NamedCheck("no_other_row_removed", missing.Length == 0, DescribeNumbers(missing), "0"));
        checks.Add(new NamedCheck("no_other_row_changed", changed.Length == 0, DescribeNumbers(changed), "0"));
        checks.Add(new NamedCheck("no_other_row_added", appeared.Length == 0, DescribeNumbers(appeared), "0"));

        if (checks.Any(check => !check.Passed))
        {
            snapshot["api7"] = api7;
            snapshot["rows_after"] = after.Rows.Count;
            snapshot["others_after"] = after.Rows;
            snapshot["row_absent_api7"] = after.AbsentApi7;
            snapshot["row_absent_api5"] = after.AbsentApi5;
            snapshot["removed_others"] = missing;
            snapshot["changed_others"] = changed;
            snapshot["added_rows"] = appeared;
            snapshot["revision"] = document.Revision;
            throw new KompasContractException(
                ErrorCodes.GeometryFailed,
                "Удаление не подтвердилось перечитыванием: строки с номером " + Fmt(unique) + " в списке "
                + (after.AbsentApi7 ? "нет" : "ЕСТЬ")
                + ", через API5 она " + (after.AbsentApi5 ? "не отвечает" : "ОТВЕЧАЕТ")
                + ", строк было " + before.Count + " и стало " + after.Rows.Count
                + (missing.Length == 0 ? "" : ", исчезли ещё строки: " + DescribeNumbers(missing))
                + (changed.Length == 0 ? "" : ", изменились строки: " + DescribeNumbers(changed))
                + (appeared.Length == 0 ? "" : ", появились строки: " + DescribeNumbers(appeared))
                + ". Публиковать это как успех нельзя.",
                RetryPolicy.ReacquireContext,
                partialEffects: true,
                details: new Dictionary<string, object?>
                {
                    ["document_id"] = document.Id,
                    ["unique_number"] = unique,
                    ["failure_snapshot"] = snapshot,
                });
        }

        if (mayReturn)
        {
            notes.Add("specification_row_may_return — строка объявляет происхождение не «создан в этом "
                + "документе» либо включённую синхронизацию со свойствами: документ может перезаполнить её "
                + "данные из свойств, а строку, которую он ведёт от компонента, — восстановить при "
                + "перестроении или сохранении. Удаление в модели выполнено; признака связи с компонентом в "
                + "COM нет, поэтому появление этой строки позже не будет ошибкой этого вызова");
        }

        notes.Insert(0, $"specification_row_deleted — строка {Fmt(unique)} удалена, её отсутствие перечитано "
            + $"обеими дорогами; строк было {before.Count}, стало {after.Rows.Count}");

        // LIMIT: the tool works in memory; the file is written by kompas_save_document, so survival of the
        // deletion across a save and a reopen is the acceptance's business, not this call's.
        var unverified = new List<string>
        {
            "row_deletion_not_written_to_file — удаление живёт в модели до kompas_save_document; переживание "
            + "сохранения и переоткрытия проверяет приёмка (SPEC-04.save_reopen), а не этот вызов",
        };

        return new DeleteSpecificationRowResult(
            previous,
            before.Count,
            after.Rows.Count,
            SpecificationRowDeleteRoute,
            new VerificationDto(
                checks.All(check => check.Passed)
                    ? VerificationLevel.StructureChecked
                    : VerificationLevel.CallReturned,
                checks,
                unverified),
            notes.Distinct(StringComparer.Ordinal).ToList(),
            unverified);
    }

    /// <summary>One row of the description as a census entry: its number and the four published values.</summary>
    /// <remarks>INVARIANT: <c>Unique</c> is <c>NaN</c> when the document did not answer a number — such a row
    /// is compared with nothing, because an unread number is not a number.</remarks>
    private sealed record RowSnapshot(
        double Unique, string? Marking, string? Name, string? Quantity, string? Position, int? SectionNumber);

    /// <summary>Every row of the description, in display order, with the values the API7 road answers.</summary>
    /// <remarks>INVARIANT: the census is taken with the SAME extractor before and after the call, so a
    /// difference between the two lists is the deletion and nothing else.
    /// History: docs/decisions/bom.md#spec-delete-row</remarks>
    private static List<RowSnapshot> RowCensus(ISpecificationDescription description, List<string> notes)
    {
        var rows = new List<RowSnapshot>();
        foreach (var item in SpecificationObjects(description, notes))
        {
            var unique = Double(() => item.UniqueNumber);
            if (unique is null)
            {
                notes.Add("specification_row_number_unread — у объекта списка уникальный номер не прочитан: "
                    + "он не участвует в сверке строк до и после удаления");
            }

            rows.Add(new RowSnapshot(
                unique ?? double.NaN,
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnMark, "marking", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnName, "name", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnCount, "quantity", notes),
                Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnPosition, "position", notes),
                Int(() => item.Section)));
        }

        return rows;
    }

    /// <summary>The four values of the target row as the API7 road answers them.</summary>
    private static Dictionary<string, string?> Api7Values(ISpecificationBaseObject item, List<string> notes) =>
        new(StringComparer.Ordinal)
        {
            ["marking"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnMark, "marking", notes),
            ["name"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnName, "name", notes),
            ["quantity"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnCount, "quantity", notes),
            ["position"] = Api7Column(item, ksSpecificationColumnTypeEnum.ksSColumnPosition, "position", notes),
        };

    /// <summary>The same four values as the API5 road answered them for the row before the deletion.</summary>
    private static IEnumerable<(string Field, string? Value)> Api5Values(SpecificationLineDto row)
    {
        yield return ("marking", row.Marking);
        yield return ("name", row.Name);
        yield return ("quantity", row.Quantity);
        yield return ("position", row.Position);
    }

    /// <summary>Whether a census row is the row the call deletes.</summary>
    private static bool IsDeletedRow(RowSnapshot row, double unique) => SameUniqueNumber(row.Unique, unique);

    /// <summary>Two census rows are one row: the same number the document assigned.</summary>
    private static bool SameRowNumber(RowSnapshot left, RowSnapshot right) =>
        !double.IsNaN(left.Unique) && !double.IsNaN(right.Unique)
        && Math.Abs(left.Unique - right.Unique) <= double.Epsilon;

    /// <summary>Whether a row the document kept still carries the values it had before the deletion.</summary>
    private static bool SameRowValues(RowSnapshot left, RowSnapshot right) =>
        SameSpecificationValue(left.Marking, right.Marking)
        && SameSpecificationValue(left.Name, right.Name)
        && SameSpecificationValue(left.Quantity, right.Quantity)
        && SameSpecificationValue(left.Position, right.Position)
        && left.SectionNumber == right.SectionNumber;

    private static string DescribeNumbers(IReadOnlyCollection<double> numbers) =>
        numbers.Count == 0
            ? "0"
            : string.Join(", ", numbers.Select(number => Fmt(number)).ToArray());

    /// <summary>What the document holds after the deletion, read through both roads.</summary>
    /// <remarks>INVARIANT: the list is read from the document AGAIN — the collection seen before the call does
    /// not have to answer for a list the deletion changed — and the API5 pointer of the deleted number must no
    /// longer answer it. A road that cannot be read is a refusal, never an assumed absence.
    /// History: docs/decisions/bom.md#spec-delete-row</remarks>
    private (List<RowSnapshot> Rows, bool AbsentApi7, bool AbsentApi5) ReadAfterDelete(
        DocumentEntry document, double unique, List<string> notes)
    {
        var description = ActiveSpecificationDescription(document, notes)
            ?? throw DeleteReadRefusal(document, unique,
                "После удаления у документа пропало описание спецификации (Active = NULL): отсутствие "
                + "строки подтвердить нечем.");

        var rows = RowCensus(description, notes);
        var absentApi7 = !rows.Any(row => IsDeletedRow(row, unique));

        var specification = Specification5(document);
        var pointer = Int(() => specification.ksGetSpcObject(unique));
        var pointerNumber = pointer is > 0
            ? Double(() => specification.ksGetSpcObjectNumber(pointer.Value))
            : null;
        var absentApi5 = pointer is null or <= 0 || !SameUniqueNumber(pointerNumber, unique);

        return (rows, absentApi7, absentApi5);
    }

    /// <summary>The refusal of a reference the document no longer answers.</summary>
    /// <remarks>INVARIANT: a stale reference is refused BEFORE <c>Delete()</c>, so this refusal carries no
    /// partial effect — the model is untouched.</remarks>
    private static KompasContractException StaleDeleteRefusal(
        DocumentEntry document, DeleteSpecificationRowCommand command, string message,
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

    /// <summary>The refusal of a read that cannot confirm the deletion.</summary>
    private static KompasContractException DeleteReadRefusal(
        DocumentEntry document, double unique, string message) =>
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
                    ["deleted"] = null,
                },
            });
}
