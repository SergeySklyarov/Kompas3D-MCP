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
/// the SAME registry as <c>kompas_delete_component</c>; the address is the index in the flat
/// <c>ksDocument3D.PartCollection(true)</c>, found by matching the record with the instance.
/// History: docs/decisions/bom.md#component-properties</remarks>
public sealed record GetComponentPropertiesCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Component reference from <c>kompas_list_components</c>.</summary>
    public required string ComponentRef { get; init; }
}

/// <summary>Read the native specification objects of a model — block <c>BOM_SPEC_READ</c>, mode
/// <c>BOM-04</c>.</summary>
/// <remarks>INVARIANT: this is level (b) of the BOM — what the designer put into the specification. It
/// counts nothing and is never mixed with the component tree of <c>kompas_get_bom</c>.
/// History: docs/decisions/bom.md#spec-read</remarks>
public sealed record GetSpecificationCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One specification object as the document answers it. A null field means "not read", never
/// an empty string and never zero.</summary>
/// <remarks>INVARIANT: every field rests on its own documented read call; a field without one does not
/// enter the contract. <c>section_number</c> is the number the object sits in, <c>section</c> is the name
/// the document gives for it — both are read, neither is derived from the other.
/// History: docs/decisions/bom.md#spec-read</remarks>
public sealed record SpecificationLineDto(
    string? Section,
    int? SectionNumber,
    string? Position,
    string? Marking,
    string? Name,
    string? Quantity);

/// <summary>The specification rows, their number, the route and the notes.</summary>
/// <remarks>INVARIANT: <c>count</c> is the number of rows in the answer, so a caller reads one number
/// instead of recounting. An empty specification is an empty list with a note, not a refusal.
/// History: docs/decisions/bom.md#spec-read</remarks>
public sealed record GetSpecificationResult(
    IReadOnlyList<SpecificationLineDto> Rows,
    int Count,
    string Route,
    IReadOnlyList<string> Notes);

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
