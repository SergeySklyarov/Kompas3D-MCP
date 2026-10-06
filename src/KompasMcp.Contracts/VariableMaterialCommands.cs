namespace KompasMcp.Contracts;

/// <summary>Variable and material contracts — block VM (profile <c>variables-material-minimal-v1</c>).</summary>
/// <remarks>DOC: the v24 help route — <c>IPart.VariableCollection()</c> gives the array of EXTERNAL variables
/// of the component, read through <c>GetCount</c>/<c>GetByIndex</c>/<c>GetByName</c>, changed through
/// <c>IVariable.value</c>/<c>IVariable.Expression</c>, applied with <c>ksPart.RebuildModel</c>; material is
/// <c>ksPart.material</c>, <c>ksPart.GetDensity()</c>, <c>ksPart.SetMaterial(name, density)</c>.
/// LIMIT: NOT a model-wide parameter editor — only the component's external variables are read or written;
/// <c>AddNewVariable</c> is documented for <c>ksFeature</c>/<c>IFeature</c> only, so creating a variable in
/// the top <c>ksPart</c> collection is NOT part of this block. History: docs/decisions/variables-material.md</remarks>

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

/// <summary>Material name and the RAW density reading of the top component of an open part.</summary>
/// <remarks>DOC: <c>kspart_getdensity.html</c> names the return of <c>GetDensity()</c> as «плотность
/// (г/куб.мм)»; <c>imassinertiaparam7_density.html</c> names <c>Density</c> the same. MEASURED: BOTH getters
/// return the value in g/cm3 on the installed build (steel 7850 kg/m3 reads 7.85), and the kernel's own mass
/// agrees with that reading — so no v24 source establishes the unit that is actually returned.
/// INVARIANT: therefore <see cref="DensityRaw"/> is published as a RAW reading with
/// <see cref="DensityRawUnitDocumented"/> naming the page's unit, and
/// <see cref="DensityNormalizedKgPerM3"/> is NOT published: reinterpreting the reading as g/cm3 would be the
/// server's own unit claim, not a documented one. A failed read is never substituted from a reference table;
/// a successful name read does not imply a successful density read.
/// History: docs/decisions/variables-material.md#units</remarks>
public sealed record GetMaterialCommand
{
    public required string DocumentId { get; init; }
}

public sealed record GetMaterialResult(
    string? MaterialName,
    bool NameRead,
    double? DensityRaw,
    bool DensityRead,
    string DensityRawUnitDocumented,
    string DensityUnitStatus,
    double? DensityNormalizedKgPerM3,
    long Revision,
    IReadOnlyList<string> Diagnostics);

/// <summary>Assign material name and explicit density to the top component of a part.</summary>
/// <remarks>DOC: <c>SetMaterial(name, density)</c> takes the density in <b>g/cm3</b>, so the caller's kg/m3 is
/// converted by the one documented conversion. DOC: the change «вступает в силу после вызова метода
/// <c>ksPart::Update</c>».
/// INVARIANT: the response keeps the CONFIRMED apart from the UNCONFIRMED. Confirmed are the material NAME
/// (re-read and compared) and the outcodes of the calls; the physical DENSITY is NOT — the unit of the
/// re-read raw value is not established (see <see cref="GetMaterialResult"/>), so equal raw numbers are a
/// fact about two numbers, not proof that the assigned density took. The raw comparison travels as a
/// DIAGNOSTIC only and <see cref="SetMaterialResult.DensityNormalizedKgPerM3"/> stays empty.
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

/// <summary>Outcome of assigning a material: the confirmed name, the unconfirmed density, the call outcodes.</summary>
/// <remarks>INVARIANT: <see cref="NameMatches"/> is the confirmation of the material NAME, taken from a
/// re-read, not from the request. <see cref="DensityRawNumericMatches"/> is a DIAGNOSTIC of numeric equality
/// between the written and the re-read raw values ONLY: with the unit of the reading unestablished it is NOT
/// a confirmation of the physical density, and it must not be read as one. The density's state travels in
/// <see cref="DensityUnitStatus"/> and <see cref="DensityNormalizedKgPerM3"/> is never filled.
/// History: docs/decisions/variables-material.md#material</remarks>
public sealed record SetMaterialResult(
    string RequestedName,
    double RequestedDensityKgPerM3,
    double WrittenDensityGPerCm3,
    string? ReadNameAfter,
    bool NameMatches,
    double? ReadDensityRawAfter,
    string DensityRawUnitDocumented,
    string DensityUnitStatus,
    double? DensityNormalizedKgPerM3,
    bool DensityRawNumericMatches,
    bool? SetMaterialReturned,
    bool? UpdateReturned,
    long RevisionBefore,
    long RevisionAfter,
    IReadOnlyList<string> Diagnostics);
