namespace KompasMcp.Contracts;

/// <summary>Assembly-domain contracts — block C1 (profile <c>assemblies-minimal-v1</c>, modes <c>ASM-01…ASM-07</c>).</summary>
/// <remarks>INVARIANT: a component is a reference to a file, not a body. What leaves the server is therefore structure —
/// the component instance, its source file, placement and multiplicity. Two insertions of one part yield TWO instances
/// of ONE unique part: this is the substantive criterion that separates an assembly from a composition of bodies in one part.
/// INVARIANT: placement is a rigid transform (<see cref="TransformDto"/>: origin plus two orthonormal axes), not a
/// "shift from current" — a shift without a coordinate frame is not an address and cannot be read back.</remarks>
public sealed record InsertComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Absolute path to the part's source file inside the allowed root (checked by the Host).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Component placement. <c>null</c> means "not set": the component is inserted at the
    /// documented KOMPAS default, not at a server guess of the origin.</summary>
    public TransformDto? Transform { get; init; }

    /// <summary>Fix the component after insertion. A fixed component cannot be moved.</summary>
    public bool Fixed { get; init; } = true;
}

/// <summary>Enumerate the assembly structure.</summary>
public sealed record ListComponentsCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Whether to walk nested sub-assemblies.</summary>
    public bool Recursive { get; init; }
}

/// <summary>Set a component's placement with a rigid transform and read it back.</summary>
public sealed record SetComponentPlacementCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string ComponentRef { get; init; }

    public required TransformDto Transform { get; init; }
}

/// <summary>Replace a component's source while keeping its placement.</summary>
public sealed record ReplaceComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string ComponentRef { get; init; }

    /// <summary>New source file inside the allowed root.</summary>
    public required string SourcePath { get; init; }
}

/// <summary>Check component links against their source files.</summary>
public sealed record CheckComponentLinksCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One assembly-structure row. An empty field means "not read", not zero; the reason is named in
/// <see cref="ListComponentsResult.Notes"/>.</summary>
public sealed record ComponentRowDto
{
    /// <summary>Opaque reference to the instance, bound to the revision.</summary>
    public required string ComponentRef { get; init; }

    /// <summary>Reference to the parent node (sub-assembly); null for a top-level component.</summary>
    public string? ParentRef { get; init; }

    /// <summary>Nesting depth: 0 is the top level.</summary>
    public required int Depth { get; init; }

    public string? Name { get; init; }

    /// <summary>Component designation (<c>IPart7.Marking</c>).</summary>
    public string? Marking { get; init; }

    /// <summary>Source file name (<c>IPart7.FileName</c>).</summary>
    public string? SourcePath { get; init; }

    /// <summary>Part/assembly flag (<c>IPart7.Detail</c> or <c>ksPart.IsDetail</c>).</summary>
    public bool? IsDetail { get; init; }

    /// <summary>Multiplicity: number of insertions of this part (<c>IPart7.InstanceCount</c>).</summary>
    public int? InstanceCount { get; init; }

    /// <summary>Number of bodies of the component — <c>ksPart.BodyCollection()</c>
    /// (<c>kspart_bodycollection.html</c>). Published because "the component exists" and "the component
    /// has geometry" are different claims: insertion via <c>CreatePartInAssembly</c> produced a component
    /// with ZERO bodies. MEASURED: 05.10.2026.</summary>
    public int? BodyCount { get; init; }

    /// <summary>Number of faces of the component's first body — <c>ksBody.FaceCollection()</c>.</summary>
    public int? FaceCount { get; init; }

    /// <summary>Component number in the document (<c>IPart7.Reference</c>) — also the argument of
    /// <c>ksPart.GetPart</c>. Published because component addressing rests on it, and "a reference exists
    /// but the number does not" is exactly what makes an address unverifiable.</summary>
    public int? ReferenceNumber { get; init; }

    /// <summary>Fixation state (<c>IPart7.Fixed</c>).</summary>
    public bool? Fixed { get; init; }

    /// <summary>Source load state (<c>IPart7.LoadState</c>, <c>ksLoadStateEnum</c>).</summary>
    public string? LoadState { get; init; }

    /// <summary>Cumulative placement matrix (16 numbers, 4×4), if read.</summary>
    public IReadOnlyList<double>? Matrix { get; init; }
}

/// <summary>Answer to an assembly-structure enumeration.</summary>
public sealed record ListComponentsResult(
    IReadOnlyList<ComponentRowDto> Components,
    int UniquePartCount,
    int InstanceCount,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of inserting a component: re-read from the model, not a retelling of the request.</summary>
public sealed record InsertComponentResult(
    ReferenceDto ComponentRef,
    ComponentRowDto Component,
    int ComponentCount,
    VerificationDto Verification);

/// <summary>Result of setting placement: placement BEFORE and AFTER from one instant.</summary>
public sealed record SetComponentPlacementResult(
    ReferenceDto ComponentRef,
    // Matrices are optional: "placement not read" and "placement is the zero matrix" are different
    // claims, and substituting the second for the first is as forbidden here as in any COM read.
    IReadOnlyList<double>? PlacementBeforeMatrix,
    IReadOnlyList<double>? PlacementAfterMatrix,
    VerificationDto Verification);

/// <summary>Result of replacing a component's source while keeping its placement.</summary>
public sealed record ReplaceComponentResult(
    ReferenceDto ComponentRef,
    string? SourcePathBefore,
    string? SourcePathAfter,
    IReadOnlyList<double>? PlacementBeforeMatrix,
    IReadOnlyList<double>? PlacementAfterMatrix,
    int ComponentCount,
    VerificationDto Verification);

/// <summary>One row checking a component's link to its source file.</summary>
public sealed record ComponentLinkDto(
    string ComponentRef,
    string? Name,
    string? SourcePath,
    bool? SourceExists,
    string? LoadState,
    string Verdict);

/// <summary>Result of a link check: the list with a verdict per link.</summary>
public sealed record CheckComponentLinksResult(
    IReadOnlyList<ComponentLinkDto> Links,
    int BrokenCount,
    VerificationDto Verification);
