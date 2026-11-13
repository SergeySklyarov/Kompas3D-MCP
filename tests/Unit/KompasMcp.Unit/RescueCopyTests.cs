using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rescue copies: where the copy taken when a session ends with unsaved changes is written.</summary>
/// <remarks>INVARIANT: a rescue copy lives in the SERVICE directory, in its own <c>rescue</c> sub-folder,
/// and never next to the document. The separate folder is not cosmetic — the mutation path DELETES its
/// control copy after success, so sharing the folder would let a later successful mutation delete the
/// last copy of work that was never saved.
/// History: docs/decisions/files.md#rescue-copies</remarks>
public class RescueCopyTests : IDisposable
{
    private readonly string _directory;

    public RescueCopyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "rescue-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    private DocumentControlCopies Copies() => new(Path.Combine(_directory, "service"));

    [Fact]
    public void RescuePath_LandsInTheRescueSubFolderOfTheServiceDirectory()
    {
        var copies = Copies();

        var path = copies.RescuePath("doc-1", DocumentKind.Part, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            Path.Combine(copies.RootDirectory, "control-copies", "rescue"),
            Path.GetDirectoryName(path));
    }

    /// <summary>The extension states the document KIND, so a client looking for the copy by the
    /// extension of its kind finds it instead of a file that claims another kind.</summary>
    [Theory]
    [InlineData(DocumentKind.Part, ".m3d")]
    [InlineData(DocumentKind.Assembly, ".a3d")]
    [InlineData(DocumentKind.Drawing, ".cdw")]
    [InlineData(DocumentKind.Fragment, ".frw")]
    [InlineData(DocumentKind.Specification, ".spw")]
    public void RescuePath_NamesTheKindOfTheDocument(DocumentKind kind, string extension)
    {
        var copies = Copies();

        var path = copies.RescuePath("doc-1", kind, DateTimeOffset.UnixEpoch);

        Assert.EndsWith(extension, path!, StringComparison.Ordinal);
    }

    /// <summary>A kind with no documented extension gets NO copy: <c>.m3d</c> for an unknown kind would
    /// be a silent substitute, and the caller states the reason instead.</summary>
    [Fact]
    public void RescuePath_OnAKindWithoutAnExtension_ReturnsNull()
    {
        var copies = Copies();

        Assert.Null(copies.RescuePath("doc-1", (DocumentKind)999, DateTimeOffset.UnixEpoch));
    }

    /// <summary>The rescue folder is a DIFFERENT directory from the control-copy folder, so the mutation
    /// path's cleanup cannot reach a rescue copy.</summary>
    [Fact]
    public void RescueFolder_IsNotTheControlCopyFolder()
    {
        var copies = Copies();

        Assert.NotEqual(copies.Folder, copies.RescueFolder);
        Assert.StartsWith(copies.Folder, copies.RescueFolder, StringComparison.Ordinal);
    }

    /// <summary>Two rescues of the SAME document are two files: the moment is part of the name.</summary>
    [Fact]
    public void RescuePath_IsUniquePerMoment()
    {
        var copies = Copies();
        var at = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

        var first = copies.RescuePath("doc-1", DocumentKind.Part, at);
        var second = copies.RescuePath("doc-1", DocumentKind.Part, at.AddMilliseconds(1));

        Assert.NotEqual(first, second);
    }

    /// <summary>A document id carrying path separators must not be able to move the copy out of the
    /// service directory — the id is server-generated today, and the guard is what keeps that from being
    /// the only thing standing between a bad id and a write into the user's tree.</summary>
    [Theory]
    [InlineData("..\\..\\evil")]
    [InlineData("../../evil")]
    [InlineData("C:\\Windows\\evil")]
    [InlineData("doc/with/slashes")]
    public void RescuePath_SanitisesTheDocumentId(string id)
    {
        var copies = Copies();

        var path = copies.RescuePath(id, DocumentKind.Part, DateTimeOffset.UnixEpoch);

        Assert.Equal(copies.RescueFolder, Path.GetDirectoryName(path));
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), Path.GetFileName(path), StringComparison.Ordinal);
        Assert.DoesNotContain("..", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void RescuePath_OnAnIdWithNothingUsable_StillProducesAName()
    {
        var copies = Copies();

        var path = copies.RescuePath("///", DocumentKind.Part, DateTimeOffset.UnixEpoch);

        Assert.False(string.IsNullOrWhiteSpace(Path.GetFileName(path)));
        Assert.EndsWith(".m3d", path, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRescueFolder_CreatesItAndIsIdempotent()
    {
        var copies = Copies();

        Assert.Null(copies.EnsureRescueFolder());
        Assert.True(Directory.Exists(copies.RescueFolder));

        Assert.Null(copies.EnsureRescueFolder());
        Assert.True(Directory.Exists(copies.RescueFolder));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp directory cleanup is best effort.
        }
    }
}
