namespace KompasMcp.Contracts;

/// <summary>Read the BOM of an assembly — block <c>BOM_MINIMAL</c>, profile <c>bom-minimal-v1</c>,
/// mode <c>BOM-01</c>.</summary>
/// <remarks>INVARIANT: the walk is the documented <c>ksDocument3D.PartCollection(true)</c> over
/// <c>ksPart</c>, and the filters are the TOOL's work: an excluded or hidden instance stays in the
/// collection and in the instance count, so it is skipped here and counted in <c>skipped</c>.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public sealed record GetBomCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Include hidden instances instead of skipping them.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Include instances excluded from the calculation.</summary>
    public bool IncludeExcluded { get; init; }

    /// <summary>Include standard (library) components.</summary>
    public bool IncludeStandard { get; init; }

    /// <summary>Walk nested sub-assemblies by opening their files.</summary>
    public bool Recurse { get; init; }

    /// <summary><c>file</c> or <c>file+designation</c>.</summary>
    public string GroupBy { get; init; } = "file";
}

/// <summary>Write the same rows to a CSV or JSON file — mode <c>BOM-02</c>.</summary>
/// <remarks>INVARIANT: the file is written from the SAME rows as the <c>kompas_get_bom</c> answer: one
/// builder, so a divergence between the answer and the file cannot exist by construction.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public sealed record ExportBomCommand
{
    public required string DocumentId { get; init; }

    public bool IncludeHidden { get; init; }

    public bool IncludeExcluded { get; init; }

    public bool IncludeStandard { get; init; }

    public bool Recurse { get; init; }

    public string GroupBy { get; init; } = "file";

    /// <summary>Absolute path of the output file; the extension selects the format.</summary>
    public required string OutputPath { get; init; }
}

/// <summary>Read one component's properties — block <c>BOM_PROPERTIES</c>, mode <c>BOM-03</c>.</summary>
/// <remarks>INVARIANT: the reference is the one <c>kompas_list_components</c> issued and is resolved by
/// the SAME registry as <c>kompas_delete_component</c>; the address is the ordinal in the flat
/// <c>ksDocument3D.PartCollection(true)</c>. History: docs/decisions/bom.md#component-properties</remarks>
public sealed record GetComponentPropertiesCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Component reference from <c>kompas_list_components</c>.</summary>
    public required string ComponentRef { get; init; }
}

/// <summary>The properties as <c>ksPart</c> answers them. A null value means "not read", never empty,
/// zero or false.</summary>
/// <remarks>INVARIANT: every field here rests on its own help page of <c>IPart</c>; a field with no page
/// does not enter the contract, and the unit test holds this list to the page table.
/// History: docs/decisions/bom.md#component-properties</remarks>
public sealed record ComponentPropertiesDto(
    string? File,
    string? Name,
    string? Marking,
    string? Material,
    bool? Hidden,
    bool? Excluded,
    bool? Standard,
    bool? MultiBody);

/// <summary>One component's documented properties, the route they were read by, and the notes.</summary>
public sealed record GetComponentPropertiesResult(
    ComponentPropertiesDto Properties,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>One BOM row. A null field means "not read", not an empty value.</summary>
public sealed record BomLineDto(string? File, string? Name, string? Marking, string? Material, int Instances);

/// <summary>Instances left out by the filters, by reason.</summary>
public sealed record BomSkippedDto(int Hidden, int Excluded, int Standard);

/// <summary>The BOM answer and its totals.</summary>
/// <remarks>INVARIANT: <c>instances + skipped</c> equals the number of instances the walk enumerated —
/// stated as a field so the caller can check the sum instead of trusting it.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public sealed record GetBomResult(
    IReadOnlyList<BomLineDto> Rows,
    int Instances,
    int UniqueFiles,
    BomSkippedDto Skipped,
    int InstancesWalked,
    string GroupBy,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>The written file as it is on disk: size and hash of the bytes, not a retelling of the request.</summary>
public sealed record ExportBomResult(
    string OutputPath,
    string Format,
    long ByteLength,
    string Sha256,
    int Rows,
    int Instances,
    IReadOnlyList<string> Notes);
