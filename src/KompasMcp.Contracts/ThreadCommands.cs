namespace KompasMcp.Contracts;

/// <summary>Native thread designation — block SM-08 (profile <c>threads-minimal-v1</c>).</summary>
/// <remarks>INVARIANT: a thread designation is a LABEL, not geometry — it adds no helical surface and
/// changes no volume, so an unchanged volume is the expected outcome, not a failure. DOC:
/// <c>ithreads_add.html</c>, <c>ithread_propers.html</c>, <c>ithreadsparameters_propers.html</c>.
/// History: docs/decisions/adapter-features.md#thread-route
/// </remarks>
public sealed record ThreadCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    public required string OperationId { get; init; }

    public int? TimeoutMs { get; init; }

    /// <summary>Base as a circular edge of a cylinder. Exactly one of this field and
    /// <see cref="FaceRef"/> is given — both or neither is <c>INVALID_ARGUMENT</c>.</summary>
    public string? EdgeRef { get; init; }

    /// <summary>Base as a cylindrical face. Borders are then mandatory; the type of the thread
    /// (external/internal) is read from the model, never taken as input.</summary>
    public string? FaceRef { get; init; }

    /// <summary>Face the thread is measured from. DOC: the initial border also says where the thread
    /// starts; with a face base both borders are required, with an edge base they are optional.</summary>
    public string? InitialBorderRef { get; init; }

    /// <summary>Face the thread ends at. Required together with <see cref="InitialBorderRef"/> when
    /// <see cref="AutoLength"/> is set.</summary>
    public string? FinalBorderRef { get; init; }

    /// <summary>Nominal diameter, mm. Mutually exclusive with <see cref="AutoDiameter"/>.</summary>
    public double? NominalDiameterMm { get; init; }

    /// <summary>DOC: with auto-detection the value the caller names is not used, so the contract
    /// refuses a call that names both.</summary>
    public bool AutoDiameter { get; init; }

    /// <summary>Thread length, mm. Mutually exclusive with <see cref="AutoLength"/>.</summary>
    public double? LengthMm { get; init; }

    /// <summary>DOC: the length is then taken from the borders, so both are required.</summary>
    public bool AutoLength { get; init; }

    /// <summary>Pitch, mm. Mandatory: the profile does not choose a standard.</summary>
    public required double PitchMm { get; init; }

    /// <summary>Left-hand thread — the product adds «LH» to the designation. Default <c>false</c>;
    /// the response names the value it read.</summary>
    public bool LeftHand { get; init; }
}

/// <summary>Parameters of a thread designation, READ FROM THE MODEL after the write.</summary>
public sealed record ThreadResult
{
    public required ReferenceDto FeatureRef { get; init; }

    /// <summary><c>thread</c> — the family name shared with <c>kompas_get_feature</c>.</summary>
    public required string Family { get; init; }

    /// <summary><c>edge</c> or <c>face</c> — which base the model holds.</summary>
    public required string BaseKind { get; init; }

    public ReferenceDto? BaseRef { get; init; }

    public ReferenceDto? InitialBorderRef { get; init; }

    public ReferenceDto? FinalBorderRef { get; init; }

    public required double? NominalDiameterMm { get; init; }

    public required double? PitchMm { get; init; }

    public required double? LengthMm { get; init; }

    /// <summary>TRUE — internal thread. Read-only in the product, so it is never an input here.</summary>
    public required bool? Internal { get; init; }

    public required bool? LeftHand { get; init; }

    public required bool? AutoDiameter { get; init; }

    public required bool? AutoLength { get; init; }

    public required double? VolumeBeforeMm3 { get; init; }

    public required double? VolumeAfterMm3 { get; init; }

    public required double? FacesBefore { get; init; }

    public required double? FacesAfter { get; init; }

    public required long RevisionBefore { get; init; }

    public required long RevisionAfter { get; init; }

    public required VerificationDto Verification { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Pure rules of the thread contract, shared by validation, schema bounds and tests.</summary>
/// <remarks>INVARIANT: every rule here rejects BEFORE COM, so a refused call leaves no half-built
/// feature. DOC: <c>ithread_propers.html</c>, <c>ithread_autodiameter.html</c>, <c>ithread_autolenght.html</c>.
/// History: docs/decisions/adapter-features.md#thread-route</remarks>
public static class ThreadRules
{
    /// <summary>The family name in <c>kompas_get_feature</c> and <c>kompas_list_features</c>.</summary>
    public const string Family = "thread";

    /// <summary>Exactly one base form: an edge or a face, never both, never neither.</summary>
    public static bool SingleBase(string? edgeRef, string? faceRef) =>
        (edgeRef is not null) ^ (faceRef is not null);

    /// <summary>Both borders, or neither.</summary>
    public static bool BordersComplete(string? initialBorderRef, string? finalBorderRef) =>
        (initialBorderRef is not null) == (finalBorderRef is not null);

    /// <summary>A positive finite number — NaN and infinities are refused here, not by COM.</summary>
    public static bool PositiveFinite(double? value) =>
        value is { } v && double.IsFinite(v) && v > 0d;

    /// <summary>A value OR its auto flag, never both: the product ignores the value when the flag is
    /// set, so accepting both would answer a number the model does not hold.</summary>
    public static bool ValueOrAuto(double? value, bool auto) =>
        auto ? value is null : PositiveFinite(value);

    /// <summary>The same rule for an EDIT, where both sides are optional: a named value must be positive
    /// and must not arrive together with <c>auto = true</c>.</summary>
    public static bool EditValueOrAuto(double? value, bool? auto) =>
        value is null || (auto != true && PositiveFinite(value));
}
