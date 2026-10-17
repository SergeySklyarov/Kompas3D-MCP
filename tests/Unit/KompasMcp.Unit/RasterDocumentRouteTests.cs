using KompasMcp.Contracts;
using KompasMcp.Domain.Imaging;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The snapshot carrier is chosen by the DOCUMENT KIND, not by the presence of a handle.</summary>
/// <remarks>INVARIANT: a drawing never lands in the 3D route, whatever handles it holds — an API7-created
/// drawing has no <c>ksDocument2D</c> and was refused as <c>WRONG_DOCUMENT_KIND</c> about 3D. A drawing
/// without any handle is a named DRAWING refusal, not a fallback to the model route.
/// History: docs/decisions/adapter-core.md#image-export</remarks>
public class RasterDocumentRouteTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DrawingWithApi5Handle_RendersOnThe2DHandle(bool api7, bool api5)
    {
        Assert.Equal(
            RasterDocumentRoute.Drawing2D,
            RasterDocumentRouting.Choose(DocumentKind.Drawing, api5, api7));
    }

    [Fact]
    public void DrawingCreatedThroughApi7_ResolvesTheHandleByTransfer()
    {
        Assert.Equal(
            RasterDocumentRoute.DrawingTransfer,
            RasterDocumentRouting.Choose(DocumentKind.Drawing, hasApi5DrawingHandle: false,
                hasApi7DrawingHandle: true));
    }

    [Fact]
    public void DrawingWithoutAnyHandle_IsADrawingRefusalNotThe3DRoute()
    {
        Assert.Equal(
            RasterDocumentRoute.DrawingUnresolvable,
            RasterDocumentRouting.Choose(DocumentKind.Drawing, hasApi5DrawingHandle: false,
                hasApi7DrawingHandle: false));
    }

    [Theory]
    [InlineData(DocumentKind.Part)]
    [InlineData(DocumentKind.Assembly)]
    [InlineData(DocumentKind.Fragment)]
    public void Model_TakesThe3DRoute(DocumentKind kind)
    {
        Assert.Equal(
            RasterDocumentRoute.Model3D,
            RasterDocumentRouting.Choose(kind, hasApi5DrawingHandle: false, hasApi7DrawingHandle: false));
    }
}
