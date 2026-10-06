using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The export-shape rule behind <c>kompas_export_drawing</c> — whether a produced file actually
/// carries the requested format.</summary>
/// <remarks>The rule is a PURE function in <c>KompasMcp.Domain</c>, so the deterministic lane calls it
/// DIRECTLY. This is the rule that must CATCH THE WRONG FORMAT: a native KOMPAS drawing is a ZIP container,
/// and presenting a renamed container as a DXF or a DWG was the defect. INVARIANT: the extension is never
/// the evidence. History: docs/decisions/drawings.md#export-formats</remarks>
public sealed class ExportFormatShapeTests
{
    /// <summary>Detected content shape against requested format.</summary>
    /// <remarks>MEASURED shapes the rows encode: a documented export writes «0 SECTION» for DXF and
    /// «AC1032» for DWG; a native drawing begins «PK\x03\x04».</remarks>
    [Theory]
    // A real DXF is expected shape for dxf, and for nothing else.
    [InlineData(ExportShape.DxfSectionHeader, ExportFormatShape.Dxf, true)]
    [InlineData(ExportShape.DxfSectionHeader, ExportFormatShape.Dwg, false)]
    // A real DWG is expected shape for dwg, and for nothing else.
    [InlineData(ExportShape.DwgAc10xx, ExportFormatShape.Dwg, true)]
    [InlineData(ExportShape.DwgAc10xx, ExportFormatShape.Dxf, false)]
    // The ZIP container (a renamed native drawing) is expected shape for NEITHER published format.
    [InlineData("zip_container(нативная модель, не DXF/DWG)", ExportFormatShape.Dxf, false)]
    [InlineData("zip_container(нативная модель, не DXF/DWG)", ExportFormatShape.Dwg, false)]
    // Anything unrecognised, including a missing/empty signature, is a refusal.
    [InlineData(ExportShape.Unknown, ExportFormatShape.Dxf, false)]
    [InlineData(ExportShape.Unknown, ExportFormatShape.Dwg, false)]
    [InlineData(null, ExportFormatShape.Dxf, false)]
    [InlineData(null, ExportFormatShape.Dwg, false)]
    // An unpublished format name is never expected shape, even with a plausible signature.
    [InlineData(ExportShape.DxfSectionHeader, "pdf", false)]
    [InlineData(ExportShape.DwgAc10xx, null, false)]
    public void Matches_IsExactOnTheContentShape(string? signature, string? format, bool expected)
    {
        Assert.Equal(expected, ExportFormatShape.Matches(signature, format));
    }

    /// <summary>The acceptance rule requires the shape AND the documented converter success code.</summary>
    /// <remarks>INVARIANT: a correct-looking file with <c>Convert = 0</c> is a discrepancy, not a success;
    /// a container with <c>Convert = 1</c> is likewise refused. Both must hold together.</remarks>
    [Theory]
    [InlineData(ExportShape.DxfSectionHeader, ExportFormatShape.Dxf, 1, true)]
    [InlineData(ExportShape.DwgAc10xx, ExportFormatShape.Dwg, 1, true)]
    // Right file, documented failure code — refused.
    [InlineData(ExportShape.DxfSectionHeader, ExportFormatShape.Dxf, 0, false)]
    // Documented success code, wrong shape — refused.
    [InlineData("zip_container(нативная модель, не DXF/DWG)", ExportFormatShape.Dxf, 1, false)]
    [InlineData(ExportShape.Unknown, ExportFormatShape.Dwg, 1, false)]
    // Wrong format for the signature — refused even at success code.
    [InlineData(ExportShape.DxfSectionHeader, ExportFormatShape.Dwg, 1, false)]
    public void IsAccepted_RequiresShapeAndConvertSuccess(
        string? signature, string? format, int convertResult, bool expected)
    {
        Assert.Equal(expected, ExportFormatShape.IsAccepted(signature, format, convertResult));
    }
}
