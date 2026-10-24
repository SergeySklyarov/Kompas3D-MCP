using KompasMcp.Domain.Files;

namespace KompasMcp.Domain.SheetMetal;

/// <summary>What the comparison between the DXF contour and the model's own extent says.</summary>
public enum FlatDxfVerdict
{
    /// <summary>No independent value was read: nothing is claimed.</summary>
    NotChecked = 0,

    /// <summary>The contour has the flat pattern's size.</summary>
    Match = 1,

    /// <summary>The contour has the BENT body's size on a part with bends: the view is the front view,
    /// not the unfold.</summary>
    Substituted = 2,

    /// <summary>An independent flat extent was read and the contour disagrees with it.</summary>
    Mismatch = 3,
}

/// <summary>The rule that separates a flat pattern from a substituted front view.</summary>
/// <remarks>MEASURED: <c>IViews.AddStandartViews</c> ignores the projection name and builds the front view,
/// so the fact that a file appeared proves nothing — the size of the contour decides. INVARIANT: a missing
/// independent extent is never a match. History: docs/decisions/sheet-metal.md#flat-dxf-tool</remarks>
public static class FlatDxfPolicy
{
    /// <summary>Allowed difference between the contour and the model extent, in millimetres.</summary>
    public const double ToleranceMm = 0.01d;

    /// <summary>The window a view's entities are measured in is widened by this much: the frame's own
    /// numbers are not the contour, and a contour point may sit on its border.</summary>
    public const double WindowToleranceMm = 0.5d;

    /// <summary>Only <c>.dxf</c> is written: the route converts a drawing into that one format.</summary>
    public static bool IsDxfPath(string path) =>
        string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Judge the contour against what the model says. <paramref name="flat"/> is the unfolded
    /// body's box and is passed only when the body IS unfolded; <paramref name="bent"/> is the bent body's
    /// box and is passed only when the part has bends.</summary>
    public static FlatDxfVerdict Judge(DxfBox? contour, DxfBox? flat, DxfBox? bent, bool hasBends)
    {
        if (contour is null)
        {
            return FlatDxfVerdict.NotChecked;
        }

        if (flat is not null)
        {
            return contour.SameSize(flat, ToleranceMm) ? FlatDxfVerdict.Match : FlatDxfVerdict.Mismatch;
        }

        return hasBends && bent is not null && contour.SameSize(bent, ToleranceMm)
            ? FlatDxfVerdict.Substituted
            : FlatDxfVerdict.NotChecked;
    }
}
