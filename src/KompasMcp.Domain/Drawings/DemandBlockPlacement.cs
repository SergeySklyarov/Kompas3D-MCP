using System.Globalization;

namespace KompasMcp.Domain.Drawings;

/// <summary>Where the technical-requirements block sits on its sheet, from the four numbers the kernel
/// reports for the block and for the sheet's free area.</summary>
/// <remarks>DOC: <c>itechnicaldemand_blocksgabarits.html</c> — <c>BlocksGabarits</c> is the block's
/// gabarits; <c>ilayoutsheet_getplaceinsideframes.html</c> — <c>GetPlaceInsideFrames(Left, Top, Right,
/// Bottom)</c> is «свободное место внутри рамок», Left/Top being the upper-left point. MEASURED: the
/// four numbers are two corner pairs — {0,2} is X and {1,3} is Y. INVARIANT: only numbers go in, so a
/// substituted reading exercises every branch.
/// History: docs/decisions/drawings.md#technical-demand-placement</remarks>
public static class DemandBlockPlacement
{
    /// <summary>Half a tenth of a millimetre: a block touching the frame is not "outside the sheet".
    /// The kernel returns gabarits and frames as doubles, so an exact comparison would refuse a block
    /// that sits exactly on the frame because of floating-point noise.</summary>
    public const double ToleranceMm = 0.05d;

    /// <summary>Whether the block lies inside the sheet's free area. <c>null</c> means one of the two
    /// readings did not carry four numbers — a NAMED gap, never "outside the sheet".</summary>
    public static bool? WithinSheet(IReadOnlyList<double>? gabarits, IReadOnlyList<double>? frames)
    {
        if (gabarits is not { Count: 4 } || frames is not { Count: 4 })
        {
            return null;
        }

        var (blockLeft, blockRight) = Span(gabarits[0], gabarits[2]);
        var (blockBottom, blockTop) = Span(gabarits[1], gabarits[3]);
        var (frameLeft, frameRight) = Span(frames[0], frames[2]);
        var (frameBottom, frameTop) = Span(frames[1], frames[3]);

        return blockLeft >= frameLeft - ToleranceMm
            && blockRight <= frameRight + ToleranceMm
            && blockBottom >= frameBottom - ToleranceMm
            && blockTop <= frameTop + ToleranceMm;
    }

    /// <summary>The bounds as one line for the client, in sheet mm; <paramref name="what"/> names what
    /// was measured («блок» for the technical-requirements block, «вид» for a drawing view).</summary>
    public static string Describe(
        IReadOnlyList<double> gabarits, IReadOnlyList<double> frames, string what = "блок")
    {
        var (blockLeft, blockRight) = Span(gabarits[0], gabarits[2]);
        var (blockBottom, blockTop) = Span(gabarits[1], gabarits[3]);
        var (frameLeft, frameRight) = Span(frames[0], frames[2]);
        var (frameBottom, frameTop) = Span(frames[1], frames[3]);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{8} x[{0:F1}..{1:F1}] y[{2:F1}..{3:F1}], лист x[{4:F1}..{5:F1}] y[{6:F1}..{7:F1}]",
            blockLeft, blockRight, blockBottom, blockTop,
            frameLeft, frameRight, frameBottom, frameTop, what);
    }

    /// <summary>The four numbers that move the block into the free area's top-left corner, keeping the
    /// block's own size.</summary>
    public static double[] MoveToTopLeft(IReadOnlyList<double> gabarits, IReadOnlyList<double> frames)
    {
        var (blockLeft, blockRight) = Span(gabarits[0], gabarits[2]);
        var (blockBottom, blockTop) = Span(gabarits[1], gabarits[3]);
        var (frameLeft, _) = Span(frames[0], frames[2]);
        var (_, frameTop) = Span(frames[1], frames[3]);

        var width = blockRight - blockLeft;
        var height = blockTop - blockBottom;
        return new[] { frameLeft, frameTop - height, frameLeft + width, frameTop };
    }

    private static (double Low, double High) Span(double first, double second) =>
        first <= second ? (first, second) : (second, first);
}
