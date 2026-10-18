using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Whether a drawing view lies inside the sheet's free area: the same four-number rule the
/// technical-requirements block uses, fed with a view's gabarit from <c>ksGetObjGabaritRect</c>.</summary>
/// <remarks>TEST: a view's gabarit is four sheet-mm numbers, so a substituted reading exercises every
/// side without КОМПАС. INVARIANT: the rule is shared — a second copy would let a view pass where a block
/// is refused. History: docs/decisions/drawings.md#view-sheet-placement</remarks>
public class ViewSheetPlacementTests
{
    /// <summary>The A4 sheet's free area as the kernel reports it: x[20..205] y[60..278].</summary>
    private static readonly double[] Frames = { 20d, 60d, 205d, 278d };

    [Fact]
    public void WithinSheet_IsTrueForAViewInsideTheFreeArea()
    {
        Assert.True(DemandBlockPlacement.WithinSheet(new[] { 40d, 80d, 140d, 220d }, Frames));
    }

    [Fact]
    public void WithinSheet_IsFalseForAViewOverTheLeftFrame()
    {
        Assert.False(DemandBlockPlacement.WithinSheet(new[] { 10d, 80d, 140d, 220d }, Frames));
    }

    [Fact]
    public void WithinSheet_IsFalseForAViewOverTheRightFrame()
    {
        Assert.False(DemandBlockPlacement.WithinSheet(new[] { 40d, 80d, 215d, 220d }, Frames));
    }

    [Fact]
    public void WithinSheet_IsFalseForAViewOverTheBottomFrame()
    {
        Assert.False(DemandBlockPlacement.WithinSheet(new[] { 40d, 50d, 140d, 220d }, Frames));
    }

    [Fact]
    public void WithinSheet_IsFalseForAViewOverTheTopFrame()
    {
        Assert.False(DemandBlockPlacement.WithinSheet(new[] { 40d, 80d, 140d, 290d }, Frames));
    }

    /// <summary>A view whose gabarit the kernel did not return is a NAMED gap, never «outside».</summary>
    [Fact]
    public void WithinSheet_IsNullForAViewWithoutAGabarit()
    {
        Assert.Null(DemandBlockPlacement.WithinSheet(null, Frames));
        Assert.Null(DemandBlockPlacement.WithinSheet(new[] { 40d, 80d }, Frames));
    }

    [Fact]
    public void Describe_NamesTheViewAndTheSheetBounds()
    {
        var text = DemandBlockPlacement.Describe(new[] { 40d, 80d, 215d, 220d }, Frames, "вид");

        Assert.Contains("вид x[40.0..215.0] y[80.0..220.0]", text, StringComparison.Ordinal);
        Assert.Contains("лист x[20.0..205.0] y[60.0..278.0]", text, StringComparison.Ordinal);
    }
}
