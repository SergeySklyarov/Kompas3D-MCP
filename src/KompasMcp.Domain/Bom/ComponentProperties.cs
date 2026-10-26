namespace KompasMcp.Domain.Bom;

/// <summary>Field → help page of <c>IPart</c> (API5), for the properties
/// <c>kompas_get_component_properties</c> publishes.</summary>
/// <remarks>INVARIANT: this table and the fields of the published contract are one set — the unit test
/// fails when a field is added without a page, so "no documented route" cannot turn into a null field.
/// DOC: all eight pages are the property pages of the same interface, <c>kspart_props.html</c> lists them.
/// History: docs/decisions/bom.md#component-properties</remarks>
public static class ComponentPropertyPages
{
    public const string File = "kspart_filename.html";

    public const string Name = "kspart_name.html";

    public const string Marking = "kspart_marking.html";

    public const string Material = "kspart_material.html";

    public const string Hidden = "kspart_hidden.html";

    public const string Excluded = "kspart_excluded.html";

    public const string Standard = "kspart_standardcomponent.html";

    public const string MultiBody = "kspart_multibodyparts.html";

    /// <summary>The table in the order the contract publishes the fields.</summary>
    public static IReadOnlyList<(string Field, string Page)> All { get; } =
    [
        ("file", File),
        ("name", Name),
        ("marking", Marking),
        ("material", Material),
        ("hidden", Hidden),
        ("excluded", Excluded),
        ("standard", Standard),
        ("multi_body", MultiBody),
    ];
}
