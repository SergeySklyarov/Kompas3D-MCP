namespace KompasMcp.Domain.Imaging;

/// <summary>Which carrier a raster snapshot is taken on.</summary>
public enum RasterDocumentRoute
{
    /// <summary>Drawing that holds a live API5 2D handle (<c>ksDocument2D</c>).</summary>
    Drawing2D,

    /// <summary>Drawing created through API7: it holds no API5 handle, so the 2D one comes from the
    /// documented API7 → API5 transfer.</summary>
    DrawingTransfer,

    /// <summary>Drawing with neither handle: a named DRAWING refusal, never the 3D route.</summary>
    DrawingUnresolvable,

    /// <summary>Model: the snapshot runs on <c>ksDocument3D</c>.</summary>
    Model3D,
}

/// <summary>Picks the snapshot carrier by DOCUMENT KIND, not by the presence of a handle.</summary>
/// <remarks>INVARIANT: the kind decides. A drawing made through API7 <c>IDocuments.Add</c> legitimately
/// has no <c>ksDocument2D</c>; reading that absence as "not a drawing" sent the call into the 3D route
/// and refused it as <c>WRONG_DOCUMENT_KIND</c>. LIMIT: the drawing branch is chosen for every kind
/// other than <see cref="Contracts.DocumentKind.Drawing"/> as a model, which is what the 3D handles
/// answer for; a kind without a 3D handle is refused by <c>Document3D</c> itself.
/// History: docs/decisions/adapter-core.md#image-export</remarks>
public static class RasterDocumentRouting
{
    /// <summary>PURE: no COM, no state.</summary>
    public static RasterDocumentRoute Choose(
        Contracts.DocumentKind kind, bool hasApi5DrawingHandle, bool hasApi7DrawingHandle)
    {
        if (kind != Contracts.DocumentKind.Drawing)
        {
            return RasterDocumentRoute.Model3D;
        }

        if (hasApi5DrawingHandle)
        {
            return RasterDocumentRoute.Drawing2D;
        }

        return hasApi7DrawingHandle
            ? RasterDocumentRoute.DrawingTransfer
            : RasterDocumentRoute.DrawingUnresolvable;
    }
}
