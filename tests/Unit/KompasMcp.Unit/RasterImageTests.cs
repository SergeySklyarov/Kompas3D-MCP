using System.Text.Json.Nodes;
using KompasMcp.Domain.Imaging;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Raster header parsing and the format list — what the <c>kompas_export_image</c> acceptance rests on.</summary>
/// <remarks>INVARIANT: the image size is what the server PUBLISHES, so taking it from the request would
/// publish an intention instead of a fact. LIMIT: header parsing is easy to write "by eye" and miss a byte
/// order swap, after which the answer carries plausible but wrong numbers that a live run cannot catch —
/// there is nothing to compare against. Here there is: bytes assembled in the test from the format spec.</remarks>
public class RasterImageTests
{
    private static byte[] Png(int width, int height, byte bitDepth = 8, byte colorType = 2)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        data[11] = 13;                                   // IHDR chunk length
        data[12] = (byte)'I';
        data[13] = (byte)'H';
        data[14] = (byte)'D';
        data[15] = (byte)'R';
        data[16] = (byte)(width >> 24);
        data[17] = (byte)(width >> 16);
        data[18] = (byte)(width >> 8);
        data[19] = (byte)width;
        data[20] = (byte)(height >> 24);
        data[21] = (byte)(height >> 16);
        data[22] = (byte)(height >> 8);
        data[23] = (byte)height;
        data[24] = bitDepth;
        data[25] = colorType;
        return data;
    }

    private static byte[] Bmp(int width, int height)
    {
        var data = new byte[54];
        data[0] = 0x42;                                  // 'B'
        data[1] = 0x4D;                                  // 'M'
        data[14] = 40;                                   // BITMAPINFOHEADER size
        BitConverter.GetBytes(width).CopyTo(data, 18);
        BitConverter.GetBytes(height).CopyTo(data, 22);
        BitConverter.GetBytes((short)24).CopyTo(data, 28);
        return data;
    }

    [Theory]
    [InlineData("png", RasterFormats.CodePng, "image/png")]
    [InlineData("jpg", RasterFormats.CodeJpg, "image/jpeg")]
    [InlineData("bmp", RasterFormats.CodeBmp, "image/bmp")]
    [InlineData("tif", RasterFormats.CodeTif, "image/tiff")]
    public void KnownFormats_ResolveToTheVendorEnumCode(string wire, short expectedCode, string mimeType)
    {
        Assert.True(RasterFormats.TryResolve(wire, out var spec));
        Assert.Equal(expectedCode, spec.Code);
        Assert.Equal(mimeType, spec.MimeType);
        Assert.Equal(expectedCode, RasterFormats.ByCode(expectedCode)!.Code);
    }

    [Theory]
    [InlineData("wmf")]
    [InlineData("emf")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownFormats_AreRefusedRatherThanDefaulted(string? wire)
    {
        // INVARIANT: a silent format substitution is indistinguishable to the caller from fulfilling the
        // request. MEASURED (probe P5): the kernel accepts a value OUTSIDE the list and substitutes another
        // format (99 gave BMP, 440886 bytes), so an unknown name must be refused here, before COM.
        Assert.False(RasterFormats.TryResolve(wire, out _));
    }

    [Fact]
    public void PngDimensions_AreReadFromIhdr()
    {
        var facts = RasterImageReader.Inspect(Png(328, 448), RasterFormats.Png);
        Assert.True(facts.MagicMatches);
        Assert.Equal(328, facts.PixelWidth);
        Assert.Equal(448, facts.PixelHeight);
        Assert.Equal(8, facts.BitDepth);
        Assert.Equal(2, facts.ColorType);
        Assert.StartsWith("89504e470d0a1a0a", facts.MagicHex);
    }

    [Fact]
    public void WrongMagic_IsReportedRatherThanIgnored()
    {
        // A file named PNG that is not one: exactly the case the magic check in the adapter exists for. The
        // kernel accepts a value outside the list silently (P5), so "the kernel answered success" says nothing
        // about the format.
        var facts = RasterImageReader.Inspect(Bmp(100, 80), RasterFormats.Png);
        Assert.False(facts.MagicMatches);
        Assert.Null(facts.PixelWidth);
    }

    [Fact]
    public void BmpDimensions_AreReadFromBitmapInfoHeader()
    {
        var facts = RasterImageReader.Inspect(Bmp(655, 894), RasterFormats.Bmp);
        Assert.True(facts.MagicMatches);
        Assert.Equal(655, facts.PixelWidth);
        Assert.Equal(894, facts.PixelHeight);
    }

    [Fact]
    public void NegativeBmpHeight_IsReportedAsMagnitudeWithAReason()
    {
        var facts = RasterImageReader.Inspect(Bmp(100, -80), RasterFormats.Bmp);
        Assert.Equal(80, facts.PixelHeight);
        Assert.NotNull(facts.DimensionNote);
    }

    [Fact]
    public void JpegDimensions_StayUnreadRatherThanZero()
    {
        // INVARIANT: "not read" and "zero" are different claims, and a silent zero would be a lie — a 9961-byte
        // JPG with a 0×0 size would look like an empty image.
        var data = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        var facts = RasterImageReader.Inspect(data, RasterFormats.Jpg);
        Assert.True(facts.MagicMatches);
        Assert.Null(facts.PixelWidth);
        Assert.Null(facts.PixelHeight);
        Assert.NotNull(facts.DimensionNote);
    }

    [Fact]
    public void TruncatedPng_ReportsNoDimensionsAndSaysWhy()
    {
        var truncated = Png(328, 448).Take(20).ToArray();
        var facts = RasterImageReader.Inspect(truncated, RasterFormats.Png);
        Assert.Null(facts.PixelWidth);
        Assert.NotNull(facts.DimensionNote);
    }

    [Fact]
    public void Limits_AreTheOnesTheContractPublishes()
    {
        Assert.Equal(1600, RasterLimits.MaxLongSidePixels);
        Assert.Equal(2 * 1024 * 1024, RasterLimits.MaxBase64Characters);
    }

    /// <summary>INVARIANT: the tool schema and the Domain format list must name the SAME formats. Diverging,
    /// they would give the client a format the adapter rejects, and the refusal would look like a product
    /// defect rather than a mismatch of two lists.</summary>
    [Fact]
    public void ExportImageSchema_PublishesExactlyTheSupportedFormats()
    {
        var tool = ToolCatalog.All.Single(t => t.Name == "kompas_export_image");
        var format = (tool.InputSchema["properties"] as JsonObject)!["format"] as JsonObject;
        var published = (format!["enum"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(RasterFormats.WireNames.ToArray(), published);
    }

    [Fact]
    public void ExportImage_IsAMutationThatDemandsRevisionAndOperationId()
    {
        var tool = ToolCatalog.All.Single(t => t.Name == "kompas_export_image");
        var properties = (tool.InputSchema["properties"] as JsonObject)!;
        var required = (tool.InputSchema["required"] as JsonArray)!.Select(n => n!.GetValue<string>()).ToArray();

        Assert.True(tool.IsMutation);
        Assert.True(tool.Behaviour.RequiresExpectedRevision);
        Assert.Contains("operation_id", required);
        Assert.Contains("expected_revision", required);
        Assert.Contains("document_id", required);
        Assert.Contains("format", required);
        Assert.True(properties.ContainsKey("save_path"));
        Assert.True(properties.ContainsKey("return_image_content"));
        Assert.True(properties.ContainsKey("resolution"));
        Assert.True(properties.ContainsKey("scale"));
    }
}
