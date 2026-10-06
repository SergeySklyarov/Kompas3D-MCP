namespace KompasMcp.Domain.Files;

/// <summary>The shapes a file can have, as detected from its CONTENT (never its extension).</summary>
public static class ExportShape
{
    /// <summary>A textual DXF whose first section begins «0 SECTION».</summary>
    public const string DxfSectionHeader = "dxf_section_header";

    /// <summary>A binary DWG whose header begins «AC10xx».</summary>
    public const string DwgAc10xx = "dwg_ac10xx";

    /// <summary>Nothing recognisable (unreadable, empty, or an unexpected first token).</summary>
    public const string Unknown = "unknown";
}

/// <summary>The export-shape rule — a PURE function: given what the file's content turned out to be and
/// which format was requested, does the file actually have that format?</summary>
/// <remarks>INVARIANT: the extension is NEVER the evidence. A native KOMPAS drawing (<c>.cdw</c>) is a ZIP
/// container («PK\x03\x04»); accepting it as a DXF or a DWG is the very defect this rule exists to
/// prevent, so a container is reported by its own shape and is expected shape for NO requested format.
/// MEASURED: the documented route (library path + the library's own command id) writes a textual DXF
/// («0 SECTION») and a binary DWG («AC1032»); a container is neither.
/// History: docs/decisions/drawings.md#export-formats</remarks>
public static class ExportFormatShape
{
    /// <summary>The two formats the server publishes for export, as they appear on the wire.</summary>
    public const string Dxf = "dxf";

    /// <summary>The DWG wire name.</summary>
    public const string Dwg = "dwg";

    /// <summary>Whether the detected <paramref name="signature"/> is the expected shape for
    /// <paramref name="format"/>.</summary>
    /// <remarks>The comparison is exact and on the CONTENT shape: any container, any unknown token, and
    /// any mismatch between the two published formats is a refusal.

    /// </remarks>
    public static bool Matches(string? signature, string? format) => format switch
    {
        Dxf => signature == ExportShape.DxfSectionHeader,
        Dwg => signature == ExportShape.DwgAc10xx,
        _ => false,
    };

    /// <summary>Whether the file shape must be accepted as a successful export: the shape matches AND the
    /// converter returned its documented success code (1 per <c>iconverter_convert.html</c>).</summary>
    /// <remarks>INVARIANT: a correct-looking file with a documented FAILURE code (0) is a discrepancy, not
    /// a success — the two are required together, so neither a renamed container nor a file that merely
    /// looks right can close the export. History: docs/decisions/drawings.md#export-formats</remarks>
    public static bool IsAccepted(string? signature, string? format, int convertResult)
        => Matches(signature, format) && convertResult == 1;
}
