namespace KompasMcp.Contracts;

/// <summary>Variable and material contracts — block VM (profile <c>variables-material-minimal-v1</c>).</summary>
/// <remarks>DOC: the v24 help route — <c>IPart.VariableCollection()</c> gives the array of EXTERNAL variables
/// of the component, read through <c>GetCount</c>/<c>GetByIndex</c>/<c>GetByName</c>, changed through
/// <c>IVariable.value</c>/<c>IVariable.Expression</c>, applied with <c>ksPart.RebuildModel</c>; material is
/// <c>ksPart.material</c>, <c>ksPart.SetMaterial(name, density)</c> and the density read through
/// <c>ksPart.CalcMassInertiaProperties(ST_MIX_M|ST_MIX_KG).r</c>. LIMIT: NOT a model-wide parameter editor —
/// only the component's external variables are read or written; <c>AddNewVariable</c> is documented for
/// <c>ksFeature</c>/<c>IFeature</c> only. History: docs/decisions/variables-material.md</remarks>

/// <summary>Read the external variables of the top component of an open part.</summary>
public sealed record ListVariablesCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Maximum number of variables to read. The full collection size is reported regardless, so
    /// a truncated answer is never mistaken for a short collection.</summary>
    public int Limit { get; init; } = 200;
}

/// <summary>Change the value or expression of exactly one external variable, addressed by exact name.</summary>
/// <remarks>INVARIANT: exactly one of <see cref="Value"/> and <see cref="Expression"/> is present; a refusal
/// for both or neither happens BEFORE COM and is named by a code.
/// INVARIANT (value mode): <c>value</c> is written only for a variable WITHOUT an active expression — a
/// non-empty <c>Expression</c> refuses the request and is returned, never overwritten silently.
/// DOC: <c>GetByName(name, TRUE, FALSE)</c> — matched FULL and case-sensitively; <c>RebuildModel</c> must run
/// before the change reaches the model, and its TRUE return is an outcode, NOT a confirmation.
/// History: docs/decisions/variables-material.md</remarks>
public sealed record SetVariableCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Exact variable name, case-sensitive (<c>testFullName = TRUE</c>).</summary>
    public required string Name { get; init; }

    /// <summary>New numeric value in the variable's own dimension. Null when writing an expression.</summary>
    public double? Value { get; init; }

    /// <summary>New expression string, evaluated by KOMPAS. Null when writing a plain value.</summary>
    public string? Expression { get; init; }
}

/// <summary>Before/after state of one variable plus the verification of the applied change.</summary>
/// <remarks>INVARIANT: <see cref="ExpressionBefore"/> and <see cref="ValueBefore"/> are read BEFORE the write
/// and come from that read, not from the request; the after values come from a FRESHLY FETCHED collection
/// after the rebuild, not from the object that was written to.
/// INVARIANT: <see cref="RebuildResult"/> is the outcode of the documented call and is reported as such;
/// it is never the evidence that the model accepted the new value.
/// History: docs/decisions/variables-material.md#set-variable</remarks>
public sealed record SetVariableResult(
    string Name,
    string? ExpressionBefore,
    double? ValueBefore,
    string? ExpressionAfter,
    double? ValueAfter,
    string Mode,
    bool? RebuildResult,
    bool ReadBackVerified,
    long RevisionBefore,
    long RevisionAfter,
    IReadOnlyList<string> Diagnostics);

/// <summary>One external variable as read from the collection.</summary>
/// <remarks>INVARIANT: a field that was not read is <c>null</c> and its reason travels in the enclosing read's
/// diagnostics; it is never left at 0, an empty string, or a value carried over from a previous call.
/// MEASURED: the collection is addressed by the DOCUMENT id, and after a reopen the variables are read from
/// the new document rather than from a stale collection.
/// History: docs/decisions/variables-material.md#read-variables</remarks>
public sealed record VariableRowDto(
    string Name,
    double? Value,
    string? Expression,
    bool? HasExpression,
    string? DisplayName,
    string? Note);

/// <summary>Result of reading the external variables of the top component.</summary>
/// <remarks>INVARIANT: <see cref="Total"/> and <see cref="Returned"/> are reported together so a truncated
/// answer is never mistaken for a short collection; <see cref="Scope"/> names the collection that was
/// actually read, so the list is not passed off as the model's full parameter set.
/// History: docs/decisions/variables-material.md#read-variables</remarks>
public sealed record ListVariablesResult(
    IReadOnlyList<VariableRowDto> Variables,
    int Total,
    int Returned,
    bool Truncated,
    string Scope,
    long Revision,
    IReadOnlyList<string> Diagnostics);

/// <summary>Material name and the physical density of the top component of an open part.</summary>
/// <remarks>DOC: <c>kspart_material.html</c> — «Обозначение материала можно получить только у детали».
/// DOC (density): <c>kspart_calcmassinertiaproperties.html</c> takes a <c>bitVector</c> that «определяет
/// размерность длины, размерность массы», and <c>ksmassinertiaparam.html</c> note 3 states the length and
/// mass dimensions of the returned data are set by it; <c>ksmassinertiaparam_props.html</c> names <c>r</c>
/// as «Плотность материала». So at <c>ST_MIX_M|ST_MIX_KG</c> the reading IS kg/m3 and the server rescales
/// nothing. MEASURED: the reading equals the written kg/m3 at M|KG, scales with the length unit as its
/// cube, and survives save→close→reopen.
/// INVARIANT: the name read and the density read are reported SEPARATELY — a successful name read does not
/// imply a successful density read. A failed density read is never substituted from a reference table, and
/// a zero/non-finite <c>r</c> is a named failure, not a measured density of zero.
/// <see cref="GetDensityRawDiagnostic"/> carries the legacy <c>ksPart.GetDensity()</c> reading as a
/// DIAGNOSTIC only, with the unit its page names (<c>g/mm3</c>), which the kernel does NOT return — kept so
/// the divergence stays visible rather than hidden.
/// History: docs/decisions/variables-material.md#units-mci</remarks>
public sealed record GetMaterialCommand
{
    public required string DocumentId { get; init; }
}

public sealed record GetMaterialResult(
    string? MaterialName,
    bool NameRead,
    double? DensityKgPerM3,
    bool DensityRead,
    string DensityUnit,
    string DensityRoute,
    double? GetDensityRawDiagnostic,
    string GetDensityRawUnitDocumented,
    long Revision,
    IReadOnlyList<string> Diagnostics);

/// <summary>Assign material name and explicit density to the top component of a part.</summary>
/// <remarks>DOC: <c>SetMaterial(name, density)</c> takes the density in <b>g/cm3</b>, so the caller's kg/m3 is
/// converted by the one documented conversion. DOC: the change «вступает в силу после вызова метода
/// <c>ksPart::Update</c>».
/// INVARIANT: the response is built from a RE-READ after <c>Update</c>, never by echoing the request. The
/// re-read density comes from the documented M|KG route, so it is in the SAME unit as the request and the
/// confirmation is a like-for-like comparison inside <see cref="DensityUnits.ReadBackToleranceKgPerM3"/>.
/// The material NAME is confirmed separately.
/// LIMIT: the component must be a PART and must not be a library model or a standard element; in a part the
/// method acts on the TOP component, so it is not extended to a subassembly.
/// History: docs/decisions/variables-material.md#material</remarks>
public sealed record SetMaterialCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string MaterialName { get; init; }

    /// <summary>Density in kg/m3; converted to the documented g/cm3 before the call. Positive and finite.</summary>
    public required double DensityKgPerM3 { get; init; }
}

/// <summary>Outcome of assigning a material: the confirmed name, the confirmed density, the call outcodes.</summary>
/// <remarks>INVARIANT: <see cref="NameMatches"/> confirms the material NAME from a re-read, not from the
/// request. <see cref="DensityMatches"/> confirms the PHYSICAL density by comparing the re-read
/// <see cref="ReadDensityKgPerM3After"/> with the request in the SAME unit (kg/m3) inside
/// <see cref="DensityToleranceKgPerM3"/>; a density that did not take, or could not be re-read, withholds the
/// confirmation by name rather than reporting success.
/// History: docs/decisions/variables-material.md#material</remarks>
public sealed record SetMaterialResult(
    string RequestedName,
    double RequestedDensityKgPerM3,
    double WrittenDensityGPerCm3,
    string? ReadNameAfter,
    bool NameMatches,
    double? ReadDensityKgPerM3After,
    bool DensityMatches,
    double DensityToleranceKgPerM3,
    string DensityUnit,
    string DensityRoute,
    bool? SetMaterialReturned,
    bool? UpdateReturned,
    long RevisionBefore,
    long RevisionAfter,
    IReadOnlyList<string> Diagnostics);
