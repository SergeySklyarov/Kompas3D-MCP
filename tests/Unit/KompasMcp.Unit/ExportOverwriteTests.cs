using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The overwrite rule of a file export (spec 1.12), without KOMPAS.</summary>
/// <remarks>TEST: an existing file without an explicit <c>overwrite</c> is refused BEFORE anything is built
/// or written, so the destination keeps its bytes; with the flag the new bytes are published.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public class ExportOverwriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "kompas-mcp-tests", "export-" + Guid.NewGuid().ToString("N")[..8]);

    public ExportOverwriteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is noise, not a failed assertion.
        }
    }

    private string Target(string name) => Path.Combine(_directory, name);

    [Fact]
    public void ExistingFile_WithoutTheFlag_IsRefusedAndKeepsItsBytes()
    {
        var path = Target("bom.csv");
        File.WriteAllText(path, "file;name\nold;1\n");
        var before = FileHash.Sha256(path);

        var refusal = Assert.Throws<KompasContractException>(
            () => AtomicPublish.RequireWritableTarget(path, allowOverwrite: false));

        Assert.Equal(ErrorCodes.PathNotAllowed, refusal.Code);
        Assert.Contains("перезапись без флага overwrite запрещена", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, FileHash.Sha256(path));
        Assert.Equal("file;name\nold;1\n", File.ReadAllText(path));
    }

    [Fact]
    public void ExistingFile_WithTheFlag_IsPublishedOver()
    {
        var path = Target("bom.json");
        File.WriteAllText(path, "[{\"name\":\"old\"}]");
        var before = FileHash.Sha256(path);

        AtomicPublish.RequireWritableTarget(path, allowOverwrite: true);
        var temporary = AtomicPublish.TemporarySibling(path);
        File.WriteAllText(temporary, "[{\"name\":\"new\"}]");
        AtomicPublish.WriteFrom(temporary, path, allowOverwrite: true);

        Assert.NotEqual(before, FileHash.Sha256(path));
        Assert.Equal("[{\"name\":\"new\"}]", File.ReadAllText(path));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void MissingFile_NeedsNoFlag()
    {
        var path = Target("new.csv");

        AtomicPublish.RequireWritableTarget(path, allowOverwrite: false);

        Assert.False(File.Exists(path));
    }
}
