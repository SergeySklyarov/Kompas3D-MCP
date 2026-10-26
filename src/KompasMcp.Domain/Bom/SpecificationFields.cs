namespace KompasMcp.Domain.Bom;

/// <summary>Field → the documented read call behind it, for the rows
/// <c>kompas_get_specification</c> publishes.</summary>
/// <remarks>INVARIANT: this table and the published contract are one set — the unit test fails when a
/// field is added without a page, so "no documented route" cannot turn into a null field.
/// DOC: <c>ispecificationdescription_objects.html</c>, <c>ispecificationdescriptions_active.html</c>.
/// History: docs/decisions/bom.md#spec-read</remarks>
public static class SpecificationFieldPages
{
    public const string Section = "ksspecification_ksgetspcsectionname.html";

    public const string SectionNumber = "ispecificationobject_section.html";

    public const string Position = "ksspecification_ksgetspcobjectcolumntext.html";

    public const string Marking = "ksspecification_ksgetspcobjectcolumntext.html";

    public const string Name = "ksspecification_ksgetspcobjectcolumntext.html";

    public const string Quantity = "ksspecification_ksgetspcobjectcolumntext.html";

    /// <summary>The table in the order the contract publishes the fields.</summary>
    public static IReadOnlyList<(string Field, string Page)> All { get; } =
    [
        ("section", Section),
        ("section_number", SectionNumber),
        ("position", Position),
        ("marking", Marking),
        ("name", Name),
        ("quantity", Quantity),
    ];
}

/// <summary>Column types the rows are read by, from <c>ksSpecificationColumnTypeEnum</c>.</summary>
/// <remarks>INVARIANT: a column is named by its documented TYPE, never by an ordinal position — the
/// ordinal depends on the specification style, the type does not.
/// DOC: <c>ksspecificationcolumntypeenum.html</c> lists the values; the type is passed to
/// <c>ksSpecification.ksGetSpcObjectColumnText</c> (<c>ksspecification_ksgetspcobjectcolumntext.html</c>).
/// History: docs/decisions/bom.md#spec-read</remarks>
public static class SpecificationColumnNumbers
{
    /// <summary>The first column of a type: «номер колонки данного типа, начиная с 1».</summary>
    public const int FirstOfType = 1;

    /// <summary>The block of исполнения: «номер блока исполнений, начиная с 0».</summary>
    public const int MainBlock = 0;
}
