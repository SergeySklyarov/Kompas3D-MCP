namespace KompasMcp.Contracts;

/// <summary>Variable creation and feature-parameter binding - block G3 (profile
/// <c>variables-bind-minimal-v1</c>).</summary>
/// <remarks>DOC: <c>IPart7.AddVariable(Name, Value, Note)</c> creates a part variable (the third argument is
/// a NOTE, not a formula); <c>IVariable7.External</c> and <c>Expression</c> follow. A feature's parameter
/// variables come from <c>ksEntity.GetFeature()</c> → <c>ksFeature.VariableCollection</c>, and a parameter is
/// bound by writing a variable name into its <c>Expression</c>.
/// LIMIT: NOT a model-wide parameter editor and NOT a variable deleter; KOMPAS evaluates expressions.
/// History: docs/decisions/variables-material.md#g3-route</remarks>

/// <summary>Create an external variable of the top component of an open part.</summary>
/// <remarks>INVARIANT: the name is checked with <c>IPart7.IsVariableNameValid</c> BEFORE any write, and an
/// existing variable of the same name is REFUSED rather than overwritten.
/// INVARIANT: an empty expression is refused before COM - the kernel does not support it (writing an empty
/// string reports success, keeps the old expression and drops the variable from the collection).
/// DOC: <c>ipart7_addvariable.html</c>, <c>ipart7_isvariablenamevalid.html</c>, <c>ivariable7_external.html</c>,
/// <c>ivariable7_expression.html</c>. History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record CreateVariableCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Variable name: Latin letters, digits and <c>_</c>, first character a letter or <c>_</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Finite numeric value in the variable's own dimension.</summary>
    public required double Value { get; init; }

    /// <summary>Optional free-form note (the third argument of <c>AddVariable</c>).</summary>
    public string? Note { get; init; }

    /// <summary>Optional expression, evaluated by KOMPAS. When omitted a constant expression equal to
    /// <see cref="Value"/> is used, because an empty expression is not a supported state.</summary>
    public string? Expression { get; init; }

    /// <summary>Whether the variable is external (default true when omitted) - the flag that makes it
    /// visible in <c>ksPart.VariableCollection</c> and usable by <c>kompas_list_variables</c>.</summary>
    public bool? External { get; init; }
}

/// <summary>The created variable as it was RE-READ from a freshly fetched collection.</summary>
/// <remarks>INVARIANT: every field is taken from the document AFTER the write, never echoed from the
/// request; the requested values travel in their own fields so a mismatch is visible rather than smoothed.
/// A variable that was created but is NOT external, or that is missing from the collection, is a NAMED
/// refusal with its consequences, never a false success.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record CreateVariableResult(
    string RequestedName,
    double RequestedValue,
    string? RequestedExpression,
    bool RequestedExternal,
    string? Name,
    double? Value,
    string? Expression,
    bool? External,
    string? Note,
    bool ReadBackVerified,
    long RevisionBefore,
    long RevisionAfter,
    VerificationDto Verification,
    IReadOnlyList<string> Diagnostics);

/// <summary>Read the parameter variables of one feature (operation).</summary>
/// <remarks>DOC: <c>ksentity_getfeature.html</c> - <c>GetFeature()</c> returns the tree object bound to the
/// model object; <c>ksfeature_variablecollection.html</c> - <c>VariableCollection()</c> returns the array of
/// that feature's variables. A feature whose <c>GetFeature</c> is not obtained is
/// <c>CAPABILITY_UNAVAILABLE</c> with a cause; a stale reference is <c>STALE_REFERENCE</c>.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record ListFeatureParametersCommand
{
    public required string DocumentId { get; init; }

    /// <summary>Feature reference from <c>kompas_list_features</c>.</summary>
    public required string FeatureRef { get; init; }
}

/// <summary>One parameter variable of a feature.</summary>
/// <remarks>INVARIANT: a field that was not read is <c>null</c> with its reason in the enclosing read's
/// diagnostics; it is never left at 0 or an empty string. <see cref="Ordinal"/> is the position in the
/// collection and is NOT an address.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record FeatureParameterRowDto(
    int Ordinal,
    string? Name,
    string? DisplayName,
    string? ParameterNote,
    double? Value,
    string? Expression,
    bool? External);

/// <summary>The parameter variables of one feature, as read.</summary>
/// <remarks>INVARIANT: a feature with no parameters is an EMPTY list with a stated reason, not a refusal -
/// "the collection exists and is empty" and "the collection could not be read" are different facts.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record ListFeatureParametersResult(
    string FeatureRef,
    string? FeatureName,
    IReadOnlyList<FeatureParameterRowDto> Parameters,
    int Total,
    long Revision,
    IReadOnlyList<string> Diagnostics);

/// <summary>Bind one parameter of a feature to an expression (a variable name, a formula or a constant).</summary>
/// <remarks>INVARIANT: the parameter is addressed by its EXACT <see cref="ParameterName"/> (the <c>name</c>
/// reported by <c>kompas_list_feature_parameters</c>), never by value or position: two parameters with an
/// equal value are indistinguishable by value. Zero or several exact matches is <c>INVALID_ARGUMENT</c> with
/// the list of names, and NO write happens.
/// INVARIANT: an empty expression is refused before COM - the kernel does not support it. Unbinding is done
/// by passing a numeric constant as the expression; no separate mode is introduced.
/// DOC: <c>variables_in_tree.html</c> - «В ячейке Выражение введите … ссылку на другую переменную».
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record BindParameterCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string FeatureRef { get; init; }

    /// <summary>Exact <c>name</c> of the parameter, as read by <c>kompas_list_feature_parameters</c>.</summary>
    public required string ParameterName { get; init; }

    /// <summary>Non-empty expression; a numeric constant unbinds the parameter.</summary>
    public required string Expression { get; init; }

    /// <summary>Optional declared expectation of the model volume after the change, in mm3. A mismatch is a
    /// failed check and a warning, never a refusal (the declared-expectation rule).</summary>
    public double? ExpectedVolumeMm3 { get; init; }
}

/// <summary>The outcome of binding a parameter: the re-read expression and value, the rebuild outcode and the
/// measured volume.</summary>
/// <remarks>INVARIANT: «written» and «applied» are different claims. <see cref="ExpressionReadBack"/> and
/// <see cref="ValueAfter"/> come from a freshly fetched collection after the rebuild; an expression that read
/// back differently, a value that did not compute or a rebuild that failed is a NAMED refusal with the model
/// state (revision after, in details), never <c>succeeded</c>.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public sealed record BindParameterResult(
    string FeatureRef,
    string ParameterName,
    string? ParameterNote,
    string RequestedExpression,
    string? ExpressionReadBack,
    double? ValueAfter,
    bool RebuildSucceeded,
    double? VolumeBeforeMm3,
    double? VolumeAfterMm3,
    double? VolumeDeltaMm3,
    double? ExpectedVolumeMm3,
    bool? ExpectedVolumeMatched,
    long RevisionBefore,
    long RevisionAfter,
    VerificationDto Verification,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string>? Warnings = null);
