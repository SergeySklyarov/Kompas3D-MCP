namespace KompasMcp.Domain.Bom;

/// <summary>The field a delete request named wrong, and the rule it broke.</summary>
public sealed record SpecificationRowDeleteRefusal(string Field, string Message);

/// <summary>What the deletion of a specification row needs before KOMPAS is asked, pure and testable.</summary>
/// <remarks>INVARIANT: a missing reference, a reference to another document, a non-positive row number and a
/// reference without the marking to check are refused as INVALID_ARGUMENT before the journal and before COM.
/// The marking is demanded because the reference is verified against the document by it: without one a stale
/// reference could not be told from a live one, and the wrong row would be deleted.
/// DOC: <c>ispecificationobject_delete.html</c> — «включает удалить базовый или вспомогательный объект
/// спецификации из документа и из коллекции»; <c>ispecificationobject_uniquenumber.html</c> — the number is
/// what addresses the row.
/// TEST: SpecificationRowDeleteTests.
/// History: docs/decisions/bom.md#spec-delete-row</remarks>
public static class SpecificationRowDeleteInput
{
    /// <summary>The rule as the client reads it: one wording for the Host and the Worker.</summary>
    public const string Rule =
        "Удалению нужна ссылка на строку этой же модели (row_ref с unique_number и marking); номер строки "
        + "положителен, обозначение задано.";

    /// <summary>The first broken rule, or <c>null</c> when the request is well formed.</summary>
    public static SpecificationRowDeleteRefusal? Validate(string? documentId, SpecificationRowRefValues? rowRef)
    {
        if (rowRef is null)
        {
            return new SpecificationRowDeleteRefusal("row_ref", "Ссылка на строку не задана: " + Rule);
        }

        if (string.IsNullOrWhiteSpace(rowRef.DocumentId))
        {
            return new SpecificationRowDeleteRefusal(
                "row_ref.document_id", "row_ref.document_id пуст: строка адресуется ссылкой этой модели. " + Rule);
        }

        if (!string.Equals(rowRef.DocumentId, documentId, StringComparison.Ordinal))
        {
            return new SpecificationRowDeleteRefusal(
                "row_ref.document_id",
                $"row_ref.document_id «{rowRef.DocumentId}» не равен document_id «{documentId}»: ссылка "
                + "принадлежит другому документу, и удалять по ней строку этой модели нельзя.");
        }

        if (rowRef.UniqueNumber is not { } unique || double.IsNaN(unique) || unique <= 0)
        {
            return new SpecificationRowDeleteRefusal(
                "row_ref.unique_number",
                "row_ref.unique_number не задан или не положителен: номер строки присваивает документ, и "
                + "ссылка без него не адресует ничего.");
        }

        if (rowRef.Marking is null)
        {
            return new SpecificationRowDeleteRefusal(
                "row_ref.marking",
                "row_ref.marking не задан: обозначение — единственный признак, которым ссылка сверяется с "
                + "документом до удаления, и без него устаревшую ссылку от живой не отличить. " + Rule);
        }

        return null;
    }
}
