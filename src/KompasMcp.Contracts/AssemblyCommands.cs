namespace KompasMcp.Contracts;

/// <summary>Assembly-domain contracts — block C1 (profile <c>assemblies-minimal-v1</c>, modes
/// <c>ASM-01…ASM-07</c>).</summary> <remarks>INVARIANT: a component is a reference to a file, not a body: what leaves
/// the server is structure — the instance, its source file, placement and multiplicity. Two insertions of one part
/// yield TWO instances of ONE unique part, the criterion separating an assembly from a composition. INVARIANT:
/// placement is a rigid transform (<see cref="TransformDto"/>: origin plus two orthonormal axes), not a "shift from
/// current" — a shift without a frame is not an address and cannot be read back.
/// History: docs/decisions/contracts.md#assembly-contracts</remarks>
public sealed record InsertComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Absolute path to the part's source file inside the allowed root (checked by the Host).</summary>
    public required string SourcePath { get; init; }

    /// <summary>Component placement. <c>null</c> means "not set": the component is inserted at the documented KOMPAS
    /// default, not at a server guess of the origin.</summary>
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

/// <summary>Delete a TOP-LEVEL assembly component.</summary>
/// <remarks>INVARIANT: only the top-level component has an API5 address (see the assembly block LIMIT), so a
/// nested component is refused rather than addressed by a guessed ordinal. INVARIANT: the component's SOURCE
/// FILE is not touched — what is removed is the insertion into this assembly, not the part file.
/// History: docs/decisions/assembly.md#delete-component</remarks>
public sealed record DeleteComponentCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Reference to the instance from <c>kompas_list_components</c>.</summary>
    public required string ComponentRef { get; init; }

    /// <summary>Agreement to delete a component that dependent mates are attached to. <c>false</c> means the
    /// call is refused with the list of those mates and the model is left untouched.</summary>
    public bool ConfirmDependents { get; init; }
}

/// <summary>One assembly-structure row. An empty field means "not read", not zero; the reason is named in <see
/// cref="ListComponentsResult.Notes"/>.</summary>
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

    /// <summary>Number of bodies of the component — <c>ksPart.BodyCollection()</c> (<c>kspart_bodycollection.html</c>).
    /// Published because "the component exists" and "the component has geometry" are different claims: insertion via
    /// <c>CreatePartInAssembly</c> produced a component with ZERO bodies. History:
    /// docs/decisions/contracts.md#component-body-count</summary>
    public int? BodyCount { get; init; }

    /// <summary>Number of faces of the component's first body — <c>ksBody.FaceCollection()</c>.</summary>
    public int? FaceCount { get; init; }

    /// <summary>Component number in the document (<c>IPart7.Reference</c>) — also the argument of
    /// <c>ksPart.GetPart</c>. Published because component addressing rests on it, and "a reference exists but the
    /// number does not" is exactly what makes an address unverifiable.</summary>
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

/// <summary>Result of deleting a component: the structure re-read from the model, not a retelling of the
/// request.</summary>
/// <param name="DependentMatesDisposition">The MEASURED fate of the dependent mates - e.g. that the kernel
/// removed them together with the component. Named, not assumed: an invalid leftover must not read as a
/// whole one.</param>
/// <remarks>INVARIANT: the surviving components' placements are re-read and reported; a component that
/// moved is named, never passed off as "unchanged".
/// History: docs/decisions/assembly.md#delete-component</remarks>
public sealed record DeleteComponentResult(
    ReferenceDto ComponentRef,
    string? SourcePath,
    IReadOnlyList<MateRowDto> DependentMates,
    string DependentMatesDisposition,
    int ComponentsBefore,
    int ComponentsAfter,
    VerificationDto Verification,
    IReadOnlyList<string>? Warnings = null);

/// <summary>One step of the exploded view: which components move, how far and along what.</summary>
/// <remarks>DOC: <c>idismantlestep_parts.html</c> — <c>Parts</c> is a <c>SafeArray VT_ARRAY|VT_DISPATCH</c>
/// of <c>IPart7</c>; <c>idismantlestep_distance.html</c> — <c>Distance</c> is a <c>double</c>;
/// <c>idismantlestep_directionobject.html</c> — <c>DirectionObject</c> is an <c>IModelObject</c>, and
/// <c>Vector</c> is read-only, so a direction is an OBJECT, not three numbers.
/// History: docs/decisions/dismantle.md#probe</remarks>
public sealed record DismantleStepDto
{
    /// <summary>Top-level components of this assembly, as <c>kompas_list_components</c> returns them.</summary>
    public required IReadOnlyList<string> ComponentRefs { get; init; }

    /// <summary>Shift in millimetres along the direction object. MEASURED: zero does not move, a negative
    /// value moves the other way. History: docs/decisions/dismantle.md#probe</summary>
    public required double DistanceMm { get; init; }

    /// <summary>Reference to an auxiliary AXIS of this assembly: the only direction object the product
    /// accepted in the measurement. A plane, an edge, a face and a component were all ignored.</summary>
    public required string DirectionRef { get; init; }
}

/// <summary>Turn the exploded view on or off and write its steps.</summary>
/// <remarks>INVARIANT: a step can be added, deleted and cleared — the documented interface has no «edit a
/// step», so a rewrite is «delete all, then add», and the result says so.
/// History: docs/decisions/dismantle.md#probe</remarks>
public sealed record SetDismantleCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Exploded view on or off; <c>null</c> leaves the mode as it is.</summary>
    public bool? Mode { get; init; }

    /// <summary>Steps to write after the existing ones are removed. <c>null</c> keeps the existing steps,
    /// an empty list clears them.</summary>
    public IReadOnlyList<DismantleStepDto>? Steps { get; init; }

    /// <summary>Remove every step through <c>ClearDismantleParameters()</c> and write nothing.</summary>
    public bool Clear { get; init; }
}

/// <summary>Read the exploded view: the mode and the steps.</summary>
public sealed record ListDismantleStepsCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One step as the model returned it.</summary>
/// <remarks>LIMIT: the direction of a step is NOT returned. <c>DirectionObject</c> reads back <c>null</c>
/// and <c>Vector</c> comes back with <c>ksVector3DUnknown</c> and no parameters, so the direction is
/// write-only in this version — named here rather than replaced by a guess.
/// History: docs/decisions/dismantle.md#probe</remarks>
public sealed record DismantleStepRowDto(
    int Index,
    IReadOnlyList<string> ComponentRefs,
    int ComponentCount,
    double? DistanceMm);

/// <summary>Result of reading the exploded view.</summary>
public sealed record ListDismantleStepsResult(
    bool? DismantleMode,
    int StepsCount,
    IReadOnlyList<DismantleStepRowDto> Steps,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of writing the exploded view: the steps as the model returns them after the write.</summary>
public sealed record SetDismantleResult(
    bool? DismantleMode,
    int StepsCountBefore,
    int StepsCountAfter,
    IReadOnlyList<DismantleStepRowDto> Steps,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);
