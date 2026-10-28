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

/// <summary>Delete faces of one body, in one of two documented modes (SM-24.delete_faces, profile
/// <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>ifaceremover_savebody.html</c> — <c>SaveBody</c> is a BOOL «Сохранять целостность
/// тела»; <c>ifaceremover_faces.html</c> — the array is <c>VT_DISPATCH</c> for ONE face and
/// <c>VT_ARRAY | VT_DISPATCH</c> for several. INVARIANT: a face list covering the whole body is refused
/// before COM — «delete every face» is not a body operation. History:
/// docs/decisions/direct-faces.md#delete-restore</remarks>
public sealed record DeleteFacesCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>Faces of ONE body — <c>face:</c> references. Must be non-empty and must not name every
    /// face of the body.</summary>
    public required IReadOnlyList<string> FaceRefs { get; init; }

    /// <summary><c>open_shell</c> — <c>SaveBody = false</c>, the body becomes a shell;
    /// <c>keep_body</c> — <c>SaveBody = true</c>, the body must stay solid.</summary>
    public string Mode { get; init; } = DeleteFacesModes.OpenShell;
}

/// <summary>The two documented modes of <c>IFaceRemover.SaveBody</c>.</summary>
public static class DeleteFacesModes
{
    public const string OpenShell = "open_shell";
    public const string KeepBody = "keep_body";
}

/// <summary>Delete-faces result: what was asked, what the model answered, what it reads now.</summary>
public sealed record DeleteFacesResult(
    int FaceCount,
    string Mode,
    bool? UpdateAccepted,
    double? VolumeBeforeMm3,
    double? VolumeAfterMm3,
    double? AreaBeforeMm2,
    double? AreaAfterMm2,
    int FacesBefore,
    int FacesAfter,
    int BodyCountBefore,
    int BodyCountAfter,
    long RevisionBefore,
    long RevisionAfter,
    VerificationDto Verification,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string>? Warnings = null);

/// <summary>Restore a surface from a face of a shelled body (SM-24.restore_faces.by_face, profile
/// <c>direct-faces-v1</c>).</summary>
/// <remarks>DOC: <c>irestoredsurface_face.html</c> — <c>Face</c> is «исходная грань поверхности»;
/// <c>isurfacecontainer_restoredsurfaces.html</c> — the collection gives the restore operations.
/// MEASURED: the operation creates a SEPARATE surface object and does not heal the shell, so the volume
/// is read back but never promised. History: docs/decisions/direct-faces.md#delete-restore</remarks>
public sealed record RestoreFacesCommand
{
    public required string DocumentId { get; init; }

    public required long ExpectedRevision { get; init; }

    /// <summary>The face the surface is restored from — one <c>face:</c> reference.</summary>
    public required string FaceRef { get; init; }

    /// <summary>Only <c>by_face</c> is documented for this collection.</summary>
    public string Mode { get; init; } = "by_face";
}

/// <summary>Restore-faces result. <c>BodyCountAfter</c> growing by one IS the documented effect; the
/// volume is reported as measured, not as recovered.</summary>
public sealed record RestoreFacesResult(
    string FaceRef,
    string Mode,
    bool? UpdateAccepted,
    int BodyCountBefore,
    int BodyCountAfter,
    double? VolumeBeforeMm3,
    double? VolumeAfterMm3,
    double? AreaBeforeMm2,
    double? AreaAfterMm2,
    long RevisionBefore,
    long RevisionAfter,
    VerificationDto Verification,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string>? Warnings = null);
