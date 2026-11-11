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

    /// <summary>Overwrite an existing file. Default false: rewriting without an explicit flag is a silent
    /// data loss (spec 1.12, the same rule the other exports follow).</summary>
    public bool Overwrite { get; init; }
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

/// <summary>Read the rows of a <c>.spw</c> DOCUMENT: an already-open one, or a file opened by this call.</summary>
/// <remarks>INVARIANT: exactly one of the two fields names the source. Both or neither is refused before
/// COM — the server never picks a source for the caller. History: docs/decisions/bom.md#spec-read-spw</remarks>
public sealed record GetSpecificationDocumentCommand
{
    /// <summary>Absolute path of a <c>.spw</c>, already checked by the read path policy.</summary>
    public string? Path { get; init; }

    /// <summary>A specification document this session registered, from kompas_open_document.</summary>
    public string? DocumentId { get; init; }
}

/// <summary>One specification object as the document answers it. A null field means "not read", never
/// an empty string and never zero.</summary>
/// <remarks>INVARIANT: every field rests on its own documented read call; a field without one does not
/// enter the contract. <c>section_number</c> is the number the object sits in, <c>section</c> is the name
/// the document gives for it — both are read, neither is derived from the other.
/// INVARIANT: <c>unique_number</c> is the number the DOCUMENT assigned («присваивается при создании»), and
/// the address a write tool takes back: a row that was not created by this session is addressable only
/// because the row publishes it. History: docs/decisions/bom.md#spec-edit-row</remarks>
public sealed record SpecificationLineDto(
    string? Section,
    int? SectionNumber,
    string? Position,
    string? Marking,
    string? Name,
    string? Quantity,
    double? UniqueNumber = null);

/// <summary>The specification rows, their number, the route and the notes.</summary>
/// <remarks>INVARIANT: <c>count</c> is the number of rows in the answer, so a caller reads one number
/// instead of recounting. An empty specification is an empty list with a note, not a refusal.
/// History: docs/decisions/bom.md#spec-read</remarks>
public sealed record GetSpecificationResult(
    IReadOnlyList<SpecificationLineDto> Rows,
    int Count,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>The document the rows came from and who opened it.</summary>
/// <param name="OpenedByTool">True only when THIS call opened the file; the closing then stays with the
/// client, exactly as for every other open. A document that was already open answers false.</param>
public sealed record SpecificationDocumentDto(
    string? Path,
    DocumentKind Kind,
    bool OpenedByTool);

/// <summary>Rows of a <c>.spw</c>, the document they belong to, the routes walked and what stayed
/// unverified.</summary>
/// <remarks>INVARIANT: the second, independent road (API5) is read only while the document IS active; when
/// it is unavailable the answer NAMES that in <c>UnverifiedAspects</c> instead of implying one road was
/// two. History: docs/decisions/bom.md#spec-read-spw</remarks>
public sealed record GetSpecificationDocumentResult(
    SpecificationDocumentDto Document,
    IReadOnlyList<SpecificationLineDto> Rows,
    int Count,
    string Route,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> UnverifiedAspects);

/// <summary>Create one row of a MODEL specification — block <c>SPEC_ADD_ROW</c>, mode
/// <c>SPEC-02.row.create</c>.</summary>
/// <remarks>ROUTE: <c>ISpecificationDescription.BaseObjects.Add(section, 0)</c> puts the object into the
/// COLLECTION only; the columns and <c>Update()</c> are what create it in the document
/// (<c>ispecificationbaseobjects_add.html</c>, <c>ispecificationobject_update.html</c>).
/// INVARIANT: the answer is not "the call returned" — the row is read back through BOTH documented roads
/// and compared with the request; a disagreement is a refusal with a snapshot, never a success.
/// History: docs/decisions/bom.md#spec-add-row</remarks>
public sealed record AddSpecificationRowCommand
{
    /// <summary>A part or an assembly: a specification belongs to a model.</summary>
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>The number a section carries in the specification STYLE, not its ordinal.</summary>
    public required int SectionNumber { get; init; }

    public required string Marking { get; init; }

    public required string Name { get; init; }

    public required string Quantity { get; init; }

    /// <summary>Row position; null leaves the numbering to the document.</summary>
    public int? Position { get; init; }
}

/// <summary>The row that was created, named the way the rest of the contract names a row.</summary>
/// <param name="UniqueNumber">Read from the object itself and confirmed by <c>ksGetSpcObjectNumber</c>;
/// it is what a later edit/delete addresses.</param>
public sealed record SpecificationRowRefDto(string DocumentId, double UniqueNumber, string? Marking);

/// <summary>The created row, read back through both roads, plus what stayed unverified.</summary>
/// <remarks>INVARIANT: <c>Row</c> is the row the DOCUMENT answered after <c>Update()</c>, not a retelling of
/// the request; a field the document did not give stays null. The verification level names only what the
/// checks confirmed, and the aspects list what they do not cover (the file itself is not written here).
/// History: docs/decisions/bom.md#spec-add-row</remarks>
public sealed record AddSpecificationRowResult(
    SpecificationRowRefDto RowRef,
    SpecificationLineDto Row,
    string Route,
    VerificationDto Verification,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> UnverifiedAspects);

/// <summary>Edit one row of a MODEL specification — block <c>SPEC_EDIT_ROW</c>.</summary>
/// <remarks>ROUTE: API5 only — the documented road whose write SURVIVES the file: <c>ksSpcObjectEdit</c> →
/// <c>ksSetSpcObjectColumnText</c>/<c>ksSpcCount</c>/<c>ksSpcPosition</c> → <c>ksSpcObjectEnd</c>
/// (<c>ksspecification_ksspcobjectedit.html</c>). The API7 column write answers <c>Update() = false</c> on an
/// existing row (measured), so it is not a write road here.
/// INVARIANT: the row is addressed by the reference the creation and the read publish; a row that is gone,
/// or whose marking no longer matches it, is <c>STALE_REFERENCE</c> with the model untouched.
/// History: docs/decisions/bom.md#spec-edit-row</remarks>
public sealed record EditSpecificationRowCommand
{
    /// <summary>A part or an assembly: a specification belongs to a model.</summary>
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Row reference issued by <c>kompas_specification_add_row</c> or the read, of THIS document.</summary>
    public required SpecificationRowRefDto RowRef { get; init; }

    public string? Marking { get; init; }

    public string? Name { get; init; }

    public string? Quantity { get; init; }

    /// <summary>Row position; null leaves the position as it is.</summary>
    public int? Position { get; init; }
}

/// <summary>The row after the edit, the row as it was before it, and what stays unverified.</summary>
/// <remarks>INVARIANT: <c>Row</c> is what the DOCUMENT answered through both roads after
/// <c>ksSpcObjectEnd</c>, and <c>Previous</c> is what it answered before the first setter; a field the
/// document did not give stays null in both. The reference is reissued because the marking may have changed.
/// History: docs/decisions/bom.md#spec-edit-row</remarks>
public sealed record EditSpecificationRowResult(
    SpecificationRowRefDto RowRef,
    SpecificationLineDto Previous,
    SpecificationLineDto Row,
    string Route,
    VerificationDto Verification,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> UnverifiedAspects);

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
