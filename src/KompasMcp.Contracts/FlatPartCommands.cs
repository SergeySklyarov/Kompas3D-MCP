namespace KompasMcp.Contracts;

/// <summary>Export the contour of a flat part of constant thickness to a DXF file.</summary>
/// <remarks>DOC: <c>iviews_addstandartviews.html</c> (the view is built from a FILE; the second argument is
/// the model orientation), <c>projectiontype.html</c> (<c>vp_NormalTo = 0</c>),
/// <c>iconverter_convert.html</c>.
/// INVARIANT: the view is built from the SAVED part file, so an unsaved part is refused by name instead of
/// exported as an older file. History: docs/decisions/flat-part.md#tool</remarks>
public sealed record ExportFlatDxfCommand
{
    public required string DocumentId { get; init; }

    /// <summary>The body of a multi-body part. May be omitted when the part holds exactly one body.</summary>
    public string? BodyRef { get; init; }

    /// <summary>Absolute <c>.dxf</c> path inside the allowed export root.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Overwrite an existing file. Default false: rewriting without an explicit flag is a silent
    /// data loss.</summary>
    public bool Overwrite { get; init; }
}

/// <summary>Result of the flat-part export: the file, the contour inside it, and the model values it was
/// compared against.</summary>
/// <remarks>INVARIANT: <c>null</c> in the model values means «не прочитано», never «совпало»; the level is
/// lowered and the reason is named. History: docs/decisions/flat-part.md#tool</remarks>
public sealed record ExportFlatDxfResult(
    string OutputPath,
    long ByteLength,
    double[] Normal,
    double ThicknessMm,
    FlatDxfContourDto? Contour,
    int Loops,
    int OuterLoops,
    int InnerLoops,
    double? ContourAreaMm2,
    double? FaceAreaMm2,
    IReadOnlyDictionary<string, int> EntityCounts,
    string Layer,
    VerificationLevel ReachedLevel,
    VerificationDto Verification);
