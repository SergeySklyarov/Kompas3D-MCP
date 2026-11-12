using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The comparison between the technical-requirements text a client asked for and the one the
/// document returns, and the display state an empty text asks for.</summary>
/// <remarks>TEST: plain strings, so every branch is exercised without КОМПАС.
/// History: docs/decisions/drawings.md#technical-demand-text</remarks>
public class DemandTextTests
{
    /// <summary>The defect: the kernel wrapped one long line, and a byte comparison called it a lost text.</summary>
    [Fact]
    public void Matches_ForgivesAWrapperLineBreakAndNothingElse()
    {
        const string requested = "Размеры в миллиметрах. Геометрия по модели inductor_core.m3d.";
        const string wrapped = "Размеры в миллиметрах. Геометрия по модели\ninductor_core.m3d.";

        Assert.True(DemandText.Matches(requested, wrapped));
        Assert.False(DemandText.Matches(requested, wrapped.Replace("inductor_core", "", StringComparison.Ordinal)));
    }

    [Fact]
    public void Matches_IsCaseAndSpaceSensitiveBeyondWhitespaceRuns()
    {
        Assert.False(DemandText.Matches("ОСТРЫЕ КРОМКИ", "острые кромки"));
        Assert.True(DemandText.Matches("1.  Острые   кромки", "1. Острые\r\nкромки"));
    }

    /// <summary>The wrap is reported as formatting, so the answer can separate it from a loss.</summary>
    [Fact]
    public void FormattingOnly_IsTrueForAWrapAlone()
    {
        Assert.True(DemandText.FormattingOnly("а б", "а\nб"));
        Assert.False(DemandText.FormattingOnly("а б", "а б"));
        Assert.False(DemandText.FormattingOnly("а б", "а в"));
    }

    /// <summary>An empty text asks for a hidden block: IsCreated=false is then the requested state.</summary>
    [Fact]
    public void ExpectCreated_FollowsTheRequestedText()
    {
        Assert.True(DemandText.ExpectCreated("1. Острые кромки притупить."));
        Assert.False(DemandText.ExpectCreated(string.Empty));
        Assert.False(DemandText.ExpectCreated("   \r\n\t "));
        Assert.False(DemandText.ExpectCreated(null));
    }

    [Fact]
    public void Normalize_CollapsesRunsAndTrims()
    {
        Assert.Equal("а б в", DemandText.Normalize("  а\tб\r\n в "));
        Assert.Equal(string.Empty, DemandText.Normalize(null));
    }
}
