using KompasMcp.Contracts;

namespace KompasMcp.Domain.Bom;

/// <summary>The column types of a specification, by the stable key the contract uses.</summary>
/// <remarks>INVARIANT: a column is addressed by its documented TYPE and its number, never by an ordinal —
/// the ordinal depends on the specification style, the type does not.
/// DOC: <c>ksspecificationcolumntypeenum.html</c> lists the 16 numbers and their names.
/// History: docs/decisions/bom.md#spec-spw-row-write</remarks>
public static class SpecificationColumnKeys
{
    public static IReadOnlyList<(string Key, int Type)> All { get; } =
    [
        ("format", 1),
        ("zone", 2),
        ("position", 3),
        ("marking", 4),
        ("name", 5),
        ("quantity", 6),
        ("note", 7),
        ("mass", 8),
        ("material", 9),
        ("user", 10),
        ("code", 11),
        ("factory", 12),
        ("document_number", 13),
        ("document_name", 14),
        ("document_code", 15),
        ("code_okp", 16),
    ];

    /// <summary>The keys of the four fields the row carries as fields, so they cannot also be columns.</summary>
    public static IReadOnlyList<string> RowFields { get; } = ["position", "marking", "name", "quantity"];

    /// <summary>The key of a column: the type's key, with the number appended when it is not the first.</summary>
    /// <remarks>INVARIANT: two columns of one type are told apart by their NUMBER («если разные колонки имеют
    /// одинаковый тип, то номера колонок этого типа должны быть разными»), so the key carries it.
    /// DOC: <c>ispecificationcolumn_columntypenumber.html</c>.</remarks>
    public static string KeyOf(int type, int number)
    {
        var key = All.FirstOrDefault(pair => pair.Type == type).Key ?? $"type{type}";
        return number > SpecificationColumnNumbers.FirstOfType ? key + number : key;
    }

    /// <summary>The type of a key, or <c>null</c> when the contract does not name it.</summary>
    public static int? TypeOf(string? key)
    {
        if (key is null)
        {
            return null;
        }

        foreach (var (name, type) in All)
        {
            if (string.Equals(name, key.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>The keys a client may name, for the refusal that lists them.</summary>
    public static string Describe() =>
        string.Join(", ", All.Where(pair => !RowFields.Contains(pair.Key)).Select(pair => pair.Key));

    /// <summary>The column types the EDIT route writes into the file — the one place the list lives.</summary>
    /// <remarks>MEASURED: every one of them was written through <c>ksSpcObjectEdit</c> →
    /// <c>ksSetSpcObjectColumnText</c>/<c>ksSpcMassa</c> → <c>ksSpcObjectEnd</c>, saved, reopened by another
    /// process and read back through API7 and API5. INVARIANT: a type outside the list is refused BEFORE the
    /// mutation and named in <c>editable</c>: the contract does not present an unmeasured write as a success.
    /// The four row fields are here too — the same route writes them, through their own setters.
    /// DOC: <c>ksspecification_kssetspcobjectcolumntext.html</c> (1 — «в случае удачного завершения»),
    /// <c>ksspecification_ksspcmassa.html</c>; the types outside the list have no fixture column.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    public static IReadOnlyList<int> Editable { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 10];

    /// <summary>The types the same route does NOT write in a MODEL — measured, with the zone as the one gap.</summary>
    /// <remarks>MEASURED: in a model <c>ksSetSpcObjectColumnText(ksSColumnZone, …)</c> answers 1 while the
    /// column stays empty in the session and in the file, while the same call fills it in a <c>.spw</c>. A
    /// success that writes nothing is the false success this contract refuses, so the type is named here
    /// instead of being discovered by the read-back after the mutation.
    /// History: docs/decisions/bom.md#spec-spw-columns</remarks>
    public static IReadOnlyList<int> NotWrittenInModel { get; } = [2];

    /// <summary>The types whose text cell holds only a few characters: «Формат» and «Зона».</summary>
    /// <remarks>MEASURED: the cell holds up to three characters; a longer value is NOT written into it — KOMPAS
    /// puts «@100~)» there and appends the whole text to «Примечание», and the file keeps that. The setter still
    /// answers 1, and creation and the API5 edit behave the same, so the class is judged before any write.
    /// DOC: <c>ksspecification_kssetspcobjectcolumntext.html</c> promises only «1 - в случае удачного завершения»
    /// and says nothing about a text that does not fit; <c>ispecificationcolumnitem_value.html</c> gives no width.
    /// LIMIT: the capacity is the measured cell of the acceptance style; the API exposes no per-column text width
    /// by type, so a wider style is refused falsely until it is re-measured.
    /// History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    public static IReadOnlyList<int> NarrowTextTypes { get; } = [1, 2];

    /// <summary>How many characters the narrow cells hold (MEASURED: three fit, four move to the note).</summary>
    public const int NarrowTextCapacity = 3;

    /// <summary>Whether the value is longer than the measured cell of its column holds.</summary>
    public static bool OverflowsNarrowTextCell(int type, string value) =>
        NarrowTextTypes.Contains(type) && value.Length > NarrowTextCapacity;

    /// <summary>The allowed class of a narrow column, for the refusal that names it.</summary>
    public static string DescribeNarrowTextClass() =>
        "«Формат» и «Зона» этой постановки вмещают до " + NarrowTextCapacity
        + " символов (измерено: три пишутся в колонку, четыре КОМПАС переносит в «Примечание»)";

    /// <summary>The refusal text for a value over the narrow cell, or <c>null</c> when the value fits.</summary>
    /// <remarks>INVARIANT: the text names the observed behaviour — the setter answers 1, puts «@100~)» into the
    /// column and appends the whole text to «Примечание» — so a client sees why a legal-looking value is refused
    /// and where the long text belongs instead. History: docs/decisions/bom.md#spec-spw-zone-text</remarks>
    public static string? NarrowTextRefusalMessage(string key, int type, string value) =>
        OverflowsNarrowTextCell(type, value)
            ? $"Значение «{value}» ({value.Length} симв.) длиннее, чем вмещает колонка «{key}»: "
              + DescribeNarrowTextClass() + ". Запись не выполняется: иначе КОМПАС положил бы в «{key}» «@100~)», "
              + "а текст дописал бы в «Примечание» — колонку, которую вызов не называл. Длинный текст пишите "
              + "в колонку note."
            : null;

    /// <summary>Whether the edit route is measured to write a column of this type in this kind of document.</summary>
    public static bool IsEditable(int type, bool model) =>
        Editable.Contains(type) && !(model && NotWrittenInModel.Contains(type));

    /// <summary>The keys <c>extra_columns</c> accepts and the route writes, for the refusal that lists them.</summary>
    public static string DescribeEditableExtra(bool model) =>
        string.Join(", ", All.Where(pair => IsEditable(pair.Type, model) && !RowFields.Contains(pair.Key))
            .Select(pair => pair.Key));
}

/// <summary>One column of a row that is not one of its four fields, resolved to its documented type.</summary>
/// <param name="Type">The value of <c>ksSpecificationColumnTypeEnum</c>.</param>
/// <param name="Number">«номер колонки данного типа, начиная с 1».</param>
/// <param name="Block">«номер блока исполнений, начиная с 0».</param>
/// <param name="Value">The requested value as text; the element converts it by its <c>ValueType</c>.</param>
public sealed record SpecificationExtraColumn(int Type, int Number, int Block, string Value);

/// <summary>What <c>extra_columns</c> means before KOMPAS is asked — pure and testable.</summary>
/// <remarks>INVARIANT: an unknown key, one of the four row fields, a non-positive number, a negative block,
/// a blank value and the same column twice are the SAME refusal class — INVALID_ARGUMENT before the journal
/// and before COM — and each names the field it broke.
/// DOC: <c>ksspecificationcolumntypeenum.html</c> (the types), <c>ispecificationcolumns_column.html</c>
/// ([type, number, block]), <c>ispecificationcolumnitem_value.html</c> (the value follows ValueType).
/// TEST: SpecificationExtraColumnTests.
/// History: docs/decisions/bom.md#spec-spw-row-write</remarks>
public static class SpecificationExtraColumns
{
    /// <summary>The rule as the client reads it.</summary>
    public static string Rule =>
        "extra_columns — колонки сверх marking, name, quantity и position: type из "
        + "ksSpecificationColumnTypeEnum ключом (" + SpecificationColumnKeys.Describe() + "), необязательные "
        + "number (с 1) и block (с 0) и непустое value.";

    /// <summary>The requested columns, or the first broken rule.</summary>
    public static IReadOnlyList<SpecificationExtraColumn>? Parse(
        IReadOnlyList<SpecificationExtraColumnDto>? requested, out SpecificationRowRefusal? refusal)
    {
        refusal = null;
        var columns = new List<SpecificationExtraColumn>();
        foreach (var item in requested ?? [])
        {
            var type = SpecificationColumnKeys.TypeOf(item.Type);
            if (item.Type is null)
            {
                refusal = new SpecificationRowRefusal("type", "У колонки extra_columns не задан type: " + Rule);
                return null;
            }

            if (SpecificationColumnKeys.RowFields.Contains(item.Type.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                refusal = new SpecificationRowRefusal(
                    "type",
                    $"Колонка «{item.Type}» адресуется полем строки, а не extra_columns: её значение пишется "
                    + "полем " + item.Type.Trim() + ". " + Rule);
                return null;
            }

            if (type is null)
            {
                refusal = new SpecificationRowRefusal(
                    "type",
                    $"Тип колонки «{item.Type}» не из ksspecificationcolumntypeenum.html: сервер не угадывает "
                    + "тип, которого нет в документированном перечне. Доступные ключи: "
                    + SpecificationColumnKeys.Describe() + ".");
                return null;
            }

            var number = item.Number ?? SpecificationColumnNumbers.FirstOfType;
            if (number < SpecificationColumnNumbers.FirstOfType)
            {
                refusal = new SpecificationRowRefusal(
                    "number",
                    $"Номер колонки {number} — не положительный: «номер колонки данного типа, начиная с 1» "
                    + "(ispecificationcolumns_column.html).");
                return null;
            }

            var block = item.Block ?? SpecificationColumnNumbers.MainBlock;
            if (block < SpecificationColumnNumbers.MainBlock)
            {
                refusal = new SpecificationRowRefusal(
                    "block",
                    $"Номер блока {block} отрицателен: «номер блока исполнений, начиная с 0» "
                    + "(ispecificationcolumns_column.html).");
                return null;
            }

            if (string.IsNullOrWhiteSpace(item.Value))
            {
                refusal = new SpecificationRowRefusal(
                    "value",
                    $"Значение колонки «{item.Type}» пустое: записывать нечего, а пустая строка и «колонки нет» "
                    + "— разные ответы чтения.");
                return null;
            }

            if (columns.Any(column => column.Type == type && column.Number == number && column.Block == block))
            {
                refusal = new SpecificationRowRefusal(
                    "type",
                    $"Колонка «{item.Type}» с номером {number} и блоком {block} названа дважды: одна колонка — "
                    + "одно значение, иначе какое из них писать — не решает сервер.");
                return null;
            }

            columns.Add(new SpecificationExtraColumn(type.Value, number, block, item.Value.Trim()));
        }

        return columns;
    }
}
