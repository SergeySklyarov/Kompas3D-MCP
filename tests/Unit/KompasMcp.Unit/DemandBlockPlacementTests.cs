using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Where the technical-requirements block sits on its sheet: the four numbers the kernel
/// reports for the block and for the free area, and the move into the free area's top-left corner.</summary>
/// <remarks>TEST: only numbers go in, so a substituted reading exercises every branch without КОМПАС.
/// History: docs/decisions/drawings.md#technical-demand-placement</remarks>
public class DemandBlockPlacementTests
{
    /// <summary>The A4 sheet as the acceptance drawing reported it: free area x[20..205] y[60..278].</summary>
    private static readonly double[] Frames = { 20d, 60d, 205d, 278d };

    [Fact]
    public void WithinSheet_IsTrueForABlockInsideTheFreeArea()
    {
        Assert.True(DemandBlockPlacement.WithinSheet(new[] { 30d, 70d, 130d, 200d }, Frames));
    }

    /// <summary>The defect: the block sat at X = −166 mm, outside the frame on every side but the top.</summary>
    [Fact]
    public void WithinSheet_IsFalseForTheMeasuredBlockOutsideTheFrame()
    {
        Assert.False(DemandBlockPlacement.WithinSheet(new[] { -180d, 20d, -5d, 277d }, Frames));
    }

    /// <summary>A block exactly on the frame is not "outside": the comparison carries a tolerance.</summary>
    [Fact]
    public void WithinSheet_AcceptsABlockSittingExactlyOnTheFrame()
    {
        Assert.True(DemandBlockPlacement.WithinSheet(new[] { 20d, 60d, 205d, 278d }, Frames));
    }

    /// <summary>The four numbers are corner PAIRS — {0,2} is X and {1,3} is Y — in either order.</summary>
    [Fact]
    public void WithinSheet_ReadsTheFourNumbersAsCornerPairsInEitherOrder()
    {
        Assert.True(DemandBlockPlacement.WithinSheet(
            new[] { 130d, 200d, 30d, 70d }, new[] { 205d, 278d, 20d, 60d }));
    }

    /// <summary>A reading without four numbers is a NAMED gap (null), never "outside the sheet".</summary>
    [Fact]
    public void WithinSheet_IsNullWhenAReadingDidNotCarryFourNumbers()
    {
        Assert.Null(DemandBlockPlacement.WithinSheet(new[] { 30d, 70d }, Frames));
        Assert.Null(DemandBlockPlacement.WithinSheet(null, Frames));
        Assert.Null(DemandBlockPlacement.WithinSheet(new[] { 30d, 70d, 130d, 200d }, null));
    }

    [Fact]
    public void Describe_NamesTheBlockAndTheSheetBounds()
    {
        var text = DemandBlockPlacement.Describe(new[] { -180d, 20d, -5d, 277d }, Frames);

        Assert.Contains("блок x[-180.0..-5.0] y[20.0..277.0]", text, StringComparison.Ordinal);
        Assert.Contains("лист x[20.0..205.0] y[60.0..278.0]", text, StringComparison.Ordinal);
    }

    /// <summary>The measured move: a 175 x 257 block lands at x[20..195] y[21..278], keeping its size.</summary>
    [Fact]
    public void MoveToTopLeft_PutsTheBlockInTheCornerKeepingItsSize()
    {
        var moved = DemandBlockPlacement.MoveToTopLeft(new[] { -180d, 20d, -5d, 277d }, Frames);

        Assert.Equal(new[] { 20d, 21d, 195d, 278d }, moved);
    }
}
