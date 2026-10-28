namespace KompasMcp.Contracts;

/// <summary>Move faces of one body along their normal by one offset (SM-24.move_faces, profile
/// <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>ifacemover.html</c> — <c>Offset</c> is a distance and <c>Direction</c> is a
/// <c>VARIANT_BOOL</c> «Сменить направление», so the side is named by <c>Reverse</c> and a direction
/// vector cannot be passed through. DOC: <c>ifacemover_setfaces.html</c> — <c>TangentFaces</c> exists, and
/// <c>true</c> is refused by name here (mode <c>SM-24.move_faces.tangent_group</c>).
/// INVARIANT: an empty face list, a zero offset and a face of a foreign body are refused BEFORE COM.
/// History: docs/decisions/direct-faces.md#implementation</remarks>
public sealed record MoveFacesCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Faces of ONE body — <c>face:</c> references. The list must be non-empty.</summary>
    public required IReadOnlyList<string> FaceRefs { get; init; }

    /// <summary>Displacement along the face normal, mm. Zero is refused: it is accepted and changes
    /// nothing, which must not read as a completed move.</summary>
    public required double OffsetMm { get; init; }

    /// <summary>Passed through to <c>IFaceMover.Direction</c> unchanged. Which side <c>false</c> means is
    /// not established, so the result reports the displacement that was actually measured.</summary>
    public bool Reverse { get; init; }

    /// <summary>Tangent faces — <c>IFaceMover.SetFaces(Faces, TangentFaces)</c>. Declared so it is visible
    /// to the caller; <c>true</c> is refused with a named refusal and belongs to
    /// <c>DIRECT_FACES_TANGENT</c>.</summary>
    public bool TangentGroup { get; init; }

    public double? ExpectedVolumeMm3 { get; init; }
}

/// <summary>Move-faces result. Every number is read back from the model: <c>null</c> means "not read",
/// never zero.</summary>
public sealed record MoveFacesResult(
    int FaceCount,
    double OffsetMm,
    bool Reverse,
    double? ObservedOffsetMm,
    double? FaceAreaMm2,
    double? PerimeterMm,
    double? VolumeBeforeMm3,
    double? VolumeAfterMm3,
    double? AreaBeforeMm2,
    double? AreaAfterMm2,
    double? ExpectedVolumeMm3,
    double? ExpectedAreaMm2,
    int BodyCount,
    long RevisionBefore,
    long RevisionAfter,
    VerificationDto Verification,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string>? Warnings = null);
