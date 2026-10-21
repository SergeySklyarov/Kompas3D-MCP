namespace KompasMcp.Contracts;

/// <summary>One step of a dynamic cross-section: a plane and the direction of the cut.</summary>
/// <remarks>DOC: <c>idynamiccrosssection_addstep.html</c> — a step is added with a
/// <c>ksDynamicCrossSectionStepBuildingTypeEnum</c>; <c>idynamiccrosssectionstepparametersbyoffsetplane_props.html</c>
/// — <c>BaseStep</c>, <c>OffsetPlane</c>, <c>PlaneBuildingType</c>;
/// <c>idynamiccrosssectionstepparametersbyoffsetplane_offsetplane.html</c> — <c>OffsetPlane</c> is an
/// <c>IPlane3DByOffset</c>, read only, so the plane is configured through that interface.
/// History: docs/decisions/sections.md#contract</remarks>
public sealed record SectionStepDto
{
    /// <summary>The plane of the cut, addressed exactly as a sketch support is: a standard plane with an
    /// offset, or a reference to an existing plane.</summary>
    public required PlaneRefDto Plane { get; init; }

    /// <summary>Cut the other way through the same plane. DOC:
    /// <c>idynamiccrosssectionstepparametersbyoffsetplane_reversedirection.html</c> — <c>BOOL
    /// ReverseDirection()</c>, a METHOD, not a property.</summary>
    public bool ReverseDirection { get; init; }
}

/// <summary>Create a dynamic cross-section and optionally turn its display mode on.</summary>
/// <remarks>MEASURED: <c>AddDynamicCrossSection()</c> answered null once a section already existed, and
/// after that section was deleted — a second section per document was not obtained by this release, so the
/// tool creates the FIRST one and names the limit instead of guessing.
/// History: docs/decisions/sections.md#probe</remarks>
public sealed record CreateSectionCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Steps of the section. Empty or absent — the section is created without steps.</summary>
    public IReadOnlyList<SectionStepDto>? Steps { get; init; }

    /// <summary>Fill the cut planes. <c>null</c> leaves the value the model gives.</summary>
    public bool? FillCutPlanes { get; init; }

    /// <summary>Section display mode on or off; <c>null</c> leaves the mode as it is.</summary>
    public bool? Mode { get; init; }
}

/// <summary>Turn the section display mode on or off.</summary>
public sealed record SetSectionModeCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required bool Mode { get; init; }
}

/// <summary>Read the sections of a model.</summary>
public sealed record ListSectionsCommand
{
    public required string DocumentId { get; init; }
}

/// <summary>One step as the model returned it.</summary>
/// <remarks>LIMIT: only the offset-plane kind is returned; the other kinds of step are outside this
/// release and are named in the tool description. History: docs/decisions/sections.md#contract</remarks>
public sealed record SectionStepRowDto(
    int Index,
    string? StepKind,
    string? PlaneBuildingType,
    string? BasePlaneName,
    double? OffsetMm,
    bool? Direction);

/// <summary>One section as the model returned it.</summary>
public sealed record SectionRowDto(
    int Index,
    int StepsCount,
    bool? FillCutPlanes,
    IReadOnlyList<SectionStepRowDto> Steps);

/// <summary>Result of reading the sections.</summary>
public sealed record ListSectionsResult(
    bool? Mode,
    int SectionsCount,
    IReadOnlyList<SectionRowDto> Sections,
    string Route,
    IReadOnlyList<string> Notes);

/// <summary>Result of creating a section: the sections as the model returns them after the write.</summary>
public sealed record CreateSectionResult(
    bool? Mode,
    int SectionsCountBefore,
    int SectionsCountAfter,
    IReadOnlyList<SectionRowDto> Sections,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);

/// <summary>Result of turning the mode: what the model answers after the write.</summary>
public sealed record SetSectionModeResult(
    bool Mode,
    VerificationDto Verification,
    IReadOnlyList<string> Notes);
