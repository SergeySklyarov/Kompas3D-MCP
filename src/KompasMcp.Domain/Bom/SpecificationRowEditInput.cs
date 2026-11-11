namespace KompasMcp.Domain.Bom;

/// <summary>The field an edit request named wrong, and the rule it broke.</summary>
public sealed record SpecificationRowEditRefusal(string Field, string Message);

/// <summary>The row an edit addresses, in the shape <c>kompas_specification_add_row</c> publishes it.</summary>
public sealed record SpecificationRowRefValues(string? DocumentId, double? UniqueNumber, string? Marking);

/// <summary>What an edit of a specification row needs before KOMPAS is asked, pure and testable.</summary>
/// <remarks>INVARIANT: an empty change set, a blank value in a field that IS given, a negative position and a
/// reference to another document are all refused as INVALID_ARGUMENT before the journal and before COM. A
/// reference to another document is refused rather than followed: the marking guard below would then compare
/// a row of one model with a reference issued for another.
/// DOC: <c>ksspecification_ksspcobjectedit.html</c> — «Редактирование объекта завершится вызовом
/// ksSpecification::ksSpcObjectEnd»; <c>ksspecification_ksspcposition.html</c> — the position is a number.
/// TEST: SpecificationRowEditTests.
/// History: docs/decisions/bom.md#spec-edit-row</remarks>
public static class SpecificationRowEditInput
{
    /// <summary>The rule as the client reads it: one wording for the Host and the Worker.</summary>
    public const string Rule =
        "Правке нужна ссылка на строку этой же модели (row_ref с unique_number и marking) и хотя бы одно из "
        + "marking, name, quantity, position; заданное текстовое поле не пустое, позиция не отрицательна.";

    /// <summary>The first broken rule, or <c>null</c> when the request is well formed.</summary>
    public static SpecificationRowEditRefusal? Validate(
        string? documentId,
        SpecificationRowRefValues? rowRef,
        string? marking,
        string? name,
        string? quantity,
        int? position)
    {
        if (rowRef is null)
        {
            return new SpecificationRowEditRefusal("row_ref", "Ссылка на строку не задана: " + Rule);
        }

        if (string.IsNullOrWhiteSpace(rowRef.DocumentId))
        {
            return new SpecificationRowEditRefusal(
                "row_ref.document_id", "row_ref.document_id пуст: строка адресуется ссылкой этой модели. " + Rule);
        }

        if (!string.Equals(rowRef.DocumentId, documentId, StringComparison.Ordinal))
        {
            return new SpecificationRowEditRefusal(
                "row_ref.document_id",
                $"row_ref.document_id «{rowRef.DocumentId}» не равен document_id «{documentId}»: ссылка "
                + "принадлежит другому документу, и править по ней эту модель нельзя.");
        }

        if (rowRef.UniqueNumber is not { } unique || double.IsNaN(unique) || unique <= 0)
        {
            return new SpecificationRowEditRefusal(
                "row_ref.unique_number",
                "row_ref.unique_number не задан или не положителен: номер строки присваивает документ, и "
                + "ссылка без него не адресует ничего.");
        }

        if (marking is null && name is null && quantity is null && position is null)
        {
            return new SpecificationRowEditRefusal(
                "row_ref",
                "Набор изменений пуст: не задано ни одного из marking, name, quantity, position, а вызов без "
                + "изменений ничего не доказывает. " + Rule);
        }

        if (marking is not null && string.IsNullOrWhiteSpace(marking))
        {
            return new SpecificationRowEditRefusal(
                "marking", "Обозначение задано пустым: пустая строка стирает значение, а не правит его. " + Rule);
        }

        if (name is not null && string.IsNullOrWhiteSpace(name))
        {
            return new SpecificationRowEditRefusal(
                "name", "Наименование задано пустым: пустая строка стирает значение, а не правит его. " + Rule);
        }

        if (quantity is not null && string.IsNullOrWhiteSpace(quantity))
        {
            return new SpecificationRowEditRefusal(
                "quantity", "Количество задано пустым: пустая строка стирает значение, а не правит его. " + Rule);
        }

        if (position is < 0)
        {
            return new SpecificationRowEditRefusal(
                "position",
                $"Позиция {position} отрицательна: номер позиции — неотрицательное целое или не задан вовсе.");
        }

        return null;
    }
}
