namespace KompasMcp.Domain.Bom;

/// <summary>The field a row request named wrong, and the rule it broke.</summary>
public sealed record SpecificationRowRefusal(string Field, string Message);

/// <summary>What a specification row needs before KOMPAS is asked, pure and testable.</summary>
/// <remarks>INVARIANT: a blank required field and a section number below 1 are the SAME refusal class —
/// INVALID_ARGUMENT before the journal and before COM — and the zero section keeps the vendor's wording.
/// DOC: <c>ispecificationbaseobjects_add.html</c> — «Объект c номером раздела 0.0. добавить нельзя.
/// Нужно добавлять объект спецификации в существующий раздел».
/// TEST: SpecificationRowTests.
/// History: docs/decisions/bom.md#spec-add-row</remarks>
public static class SpecificationRowInput
{
    /// <summary>The refusal of section 0, quoted from the help page rather than rephrased.</summary>
    public const string ZeroSection =
        "Объект c номером раздела 0.0. добавить нельзя (ispecificationbaseobjects_add.html): нужен номер "
        + "существующего раздела стиля спецификации, обычно 20 для «Деталей». Список разделов называет "
        + "отказ этого инструмента по неизвестному номеру.";

    /// <summary>The rule as the client reads it: one wording for the Host and the Worker.</summary>
    public const string Rule =
        "Строке нужны непустые marking, name и quantity и section_number существующего раздела (не 0).";

    /// <summary>The first broken rule, or <c>null</c> when the request is well formed.</summary>
    public static SpecificationRowRefusal? Validate(
        int sectionNumber, string? marking, string? name, string? quantity, int? position)
    {
        if (sectionNumber <= 0)
        {
            return new SpecificationRowRefusal("section_number", ZeroSection);
        }

        if (string.IsNullOrWhiteSpace(marking))
        {
            return new SpecificationRowRefusal(
                "marking", "Обозначение строки пустое: " + Rule);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return new SpecificationRowRefusal(
                "name", "Наименование строки пустое: " + Rule);
        }

        if (string.IsNullOrWhiteSpace(quantity))
        {
            return new SpecificationRowRefusal(
                "quantity", "Количество пустое: " + Rule);
        }

        if (position is < 0)
        {
            return new SpecificationRowRefusal(
                "position", $"Позиция {position} отрицательна: номер позиции — неотрицательное целое "
                + "или не задан вовсе (тогда позицию ставит документ).");
        }

        return null;
    }
}
