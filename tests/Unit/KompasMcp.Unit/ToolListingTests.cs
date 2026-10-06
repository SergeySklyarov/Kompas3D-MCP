using System.Text.RegularExpressions;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: published tool listings must match the catalog to prevent documentation drift.</summary>
/// <remarks>TEST: the full generated block is compared with the working tree; publish check 16
/// compares tool names in the index.
/// History: docs/decisions/tests.md#tool-listing</remarks>
public sealed class ToolListingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KompasMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }

    private static string BlockBody(string document) =>
        BlockBody(File.ReadAllText(Path.Combine(RepoRoot, document)), document);

    /// <summary>INVARIANT: normalize CRLF to LF so checkout line endings cannot cause content failures.</summary>
    /// <remarks>DOC: .gitattributes specifies CRLF for Markdown; ToolListing.Markdown emits LF.
    /// TEST: GeneratedBlock_MatchesTheCatalog_WithEitherLineEnding covers both representations.</remarks>
    private static string BlockBody(string text, string document)
    {
        var begin = text.IndexOf(ToolListing.BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(ToolListing.EndMarker, StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin, $"{document}: нет блока между метками списка инструментов.");
        return text[(begin + ToolListing.BeginMarker.Length)..end]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim('\n');
    }

    [Theory]
    [InlineData("docs/TOOLS.md")]
    public void GeneratedBlock_MatchesTheCatalog(string document)
    {
        var expected = ToolListing.Markdown(ToolCatalog.All).TrimEnd('\n');

        Assert.Equal(expected, BlockBody(document));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void GeneratedBlock_MatchesTheCatalog_WithEitherLineEnding(string lineEnding)
    {
        const string document = "docs/TOOLS.md";
        var text = File.ReadAllText(Path.Combine(RepoRoot, document))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", lineEnding, StringComparison.Ordinal);
        var expected = ToolListing.Markdown(ToolCatalog.All).TrimEnd('\n');

        Assert.Equal(expected, BlockBody(text, document));
    }

    [Fact]
    public void GeneratedBlock_NamesEveryToolExactlyOnce()
    {
        var names = Regex.Matches(BlockBody("docs/TOOLS.md"), "`(kompas_[a-z_]+)`")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(ToolCatalog.All.Count, names.Count);
        Assert.Equal(
            ToolCatalog.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void GeneratedBlock_HasNoEmDash()
    {
        Assert.DoesNotContain("—", ToolListing.Markdown(ToolCatalog.All), StringComparison.Ordinal);
    }

    [Fact]
    public void SchemasFolder_HasOneFilePerTool()
    {
        // INVARIANT: schemas/ is the published CONTRACT — a file per catalog entry, no more, no fewer.
        // A reader takes the contract from this folder; the folder is an export, not an input.
        var folder = Path.Combine(RepoRoot, "schemas");
        var onDisk = Directory.EnumerateFiles(folder, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var fromCatalog = ToolCatalog.All.Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(fromCatalog, onDisk);
    }

    [Fact]
    public void Summary_CutsLongSentencesAtAWordBoundary()
    {
        var longText = string.Join(" ", Enumerable.Range(0, 120).Select(i => $"слово{i}"));
        var summary = ToolListing.Summary(longText);

        Assert.EndsWith("…", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= ToolListing.SummaryLimit + 1, summary);
        Assert.DoesNotContain("слово119ё", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstSentence_DoesNotCutOnAnAbbreviation()
    {
        // A period inside "т. е." is followed by a lowercase letter, so it is not a sentence boundary.
        const string text = "Первое предложение т. е. с сокращением. Второе предложение.";

        Assert.Equal("Первое предложение т. е. с сокращением.", ToolListing.FirstSentence(text));
    }
}
