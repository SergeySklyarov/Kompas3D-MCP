using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The arithmetic between a view's scale and a dimension's numbers: the radius in sheet mm,
/// the number the dimension shows, and the comparison of a re-read dimension with the request.</summary>
/// <remarks>TEST: only numbers go in, so a substituted reading exercises every branch without КОМПАС.
/// History: docs/decisions/drawings.md#drawing-dimensions-scale</remarks>
public class ViewDimensionScaleTests
{
    [Theory]
    [InlineData(1d, 7d, 7d)]
    [InlineData(0.5d, 7d, 3.5d)]
    [InlineData(2d, 7d, 14d)]
    [InlineData(0.5d, 10d, 5d)]
    public void SheetRadius_IsTheModelRadiusTimesTheViewScale(double scale, double modelRadius, double expected)
    {
        Assert.Equal(expected, ViewDimensionScale.SheetRadius(modelRadius, scale), 12);
    }

    [Theory]
    [InlineData(1d, 7d, 7d)]
    [InlineData(0.5d, 3.5d, 7d)]
    [InlineData(2d, 14d, 7d)]
    public void ModelRadius_IsTheReadSheetRadiusOverTheScale(double scale, double sheetRadius, double expected)
    {
        Assert.Equal(expected, ViewDimensionScale.ModelRadius(sheetRadius, scale), 12);
    }

    /// <summary>The defect the order fixed: at model radius 7 in a 1:2 view the sheet radius is 3.5,
    /// and the number a diametral dimension shows is Ø14 — not the sheet radius.</summary>
    [Fact]
    public void Nominal_IsTheRadiusForRadialAndItsDoubleForDiametral()
    {
        Assert.Equal(7d, ViewDimensionScale.Nominal(7d, isRadial: true), 12);
        Assert.Equal(14d, ViewDimensionScale.Nominal(7d, isRadial: false), 12);
    }

    [Theory]
    [InlineData(1d, false)]
    [InlineData(1.0000000001d, false)]
    [InlineData(0.5d, true)]
    [InlineData(2d, true)]
    public void NominalMustBeWritten_OnlyAwayFromOneToOne(double scale, bool expected)
    {
        Assert.Equal(expected, ViewDimensionScale.NominalMustBeWritten(scale));
    }

    [Theory]
    [InlineData("14", 14d)]
    [InlineData("7", 7d)]
    [InlineData("14,5", 14.5d)]
    [InlineData(" 14.5 ", 14.5d)]
    public void ParseNumber_ReadsTheCaptionTheKernelWrites(string text, double expected)
    {
        Assert.Equal(expected, ViewDimensionScale.ParseNumber(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ø14")]
    [InlineData("R7")]
    public void ParseNumber_IsNullWhenTheCaptionIsNotAPlainNumber(string? text)
    {
        Assert.Null(ViewDimensionScale.ParseNumber(text));
    }

    [Fact]
    public void Near_AcceptsTheSameValueAndRefusesADifferentOne()
    {
        Assert.True(ViewDimensionScale.Near(7d, 7d));
        Assert.True(ViewDimensionScale.Near(7d + 1e-9d, 7d));
        Assert.False(ViewDimensionScale.Near(7.5d, 7d));
        Assert.False(ViewDimensionScale.Near(3.5d, 7d));
    }

    [Fact]
    public void NearPair_IsFalseWhenNothingWasRead()
    {
        Assert.True(ViewDimensionScale.NearPair(new[] { 7d, 7d }, new[] { 7d, 7d }));
        Assert.False(ViewDimensionScale.NearPair(null, new[] { 7d, 7d }));
        Assert.False(ViewDimensionScale.NearPair(new[] { 7d, 8d }, new[] { 7d, 7d }));
    }

    /// <summary>The measured 1:2 case — centre 10/20, sheet radius 3.5, number 14: a read-back that
    /// agrees with all three is not a mismatch.</summary>
    [Fact]
    public void Mismatches_IsEmptyWhenTheDimensionReadBackAsAsked()
    {
        var mismatches = ViewDimensionScale.Mismatches(
            10d, 20d, 3.5d, 14d, nominalWritten: true, 10d, 20d, 3.5d, 14d);

        Assert.Empty(mismatches);
    }

    /// <summary>The defect: the radius came back as the model radius 7 instead of the sheet radius 3.5,
    /// and that alone is enough for the caller to refuse the creation.</summary>
    [Fact]
    public void Mismatches_NamesTheRadiusThatCameBackWrong()
    {
        var mismatches = ViewDimensionScale.Mismatches(
            10d, 20d, 3.5d, 14d, nominalWritten: true, 10d, 20d, 7d, 14d);

        var only = Assert.Single(mismatches);
        Assert.Contains("радиус", only, StringComparison.Ordinal);
    }

    [Fact]
    public void Mismatches_NamesEveryWrongPart()
    {
        var mismatches = ViewDimensionScale.Mismatches(
            10d, 20d, 3.5d, 14d, nominalWritten: true, 11d, 21d, 7d, 28d);

        Assert.Equal(4, mismatches.Count);
    }

    /// <summary>An unread value is a mismatch, not a pass: "not read" is not evidence that it matched.</summary>
    [Fact]
    public void Mismatches_TreatsAnUnreadValueAsAMismatch()
    {
        var mismatches = ViewDimensionScale.Mismatches(
            10d, 20d, 3.5d, 14d, nominalWritten: true, null, null, null, null);

        Assert.Equal(4, mismatches.Count);
    }

    /// <summary>At 1:1 the number is the kernel's own, so it is not compared — but the radius still is.</summary>
    [Fact]
    public void Mismatches_IgnoresTheNumberWhenItWasNotWritten()
    {
        var mismatches = ViewDimensionScale.Mismatches(
            10d, 20d, 7d, 14d, nominalWritten: false, 10d, 20d, 7d, 999d);

        Assert.Empty(mismatches);
    }
}
