using System.Text.RegularExpressions;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: the tool list published in the overview documents is GENERATED from the catalog,
/// not written by hand. A manual list drifts silently — the documents named 50 tools while the catalog
/// held 63, and neither the build nor acceptance compares a document with the registry.</summary>
/// <remarks>TEST: the same class is guarded for the published index by
/// <c>scripts/verify-publish-set.py</c> check 16; this test runs without a git index, so it reads the
/// working tree. Two instruments, one class — the generator itself is checked here against the source
/// it claims to describe.
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

    /// <summary>The generated body between the markers, as published.</summary>
    private static string BlockBody(string document)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, document));
        var begin = text.IndexOf(ToolListing.BeginMarker, StringComparison.Ordinal);
        var end = text.IndexOf(ToolListing.EndMarker, StringComparison.Ordinal);

        Assert.True(begin >= 0 && end > begin, $"{document}: нет блока между метками списка инструментов.");
        return text[(begin + ToolListing.BeginMarker.Length)..end].Trim('\n', '\r');
    }

    [Theory]
    [InlineData("KOMPAS3D_MCP.md")]
    [InlineData("README.md")]
    public void GeneratedBlock_MatchesTheCatalog(string document)
    {
        var expected = ToolListing.Markdown(ToolCatalog.All).TrimEnd('\n');

        Assert.Equal(expected, BlockBody(document));
    }

    [Fact]
    public void GeneratedBlock_NamesEveryToolExactlyOnce()
    {
        var names = Regex.Matches(BlockBody("KOMPAS3D_MCP.md"), "`(kompas_[a-z_]+)`")
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
