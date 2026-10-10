using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Restoring a document file from its control copy: the decision as a table, the behaviour on files.</summary>
/// <remarks>INVARIANT: a restore never overwrites a document opened <c>access=read_only</c>, and never runs for a
/// refusal that never reached COM — writing into a user file without cause.
/// History: docs/decisions/tests.md#control-copy-2</remarks>
public class ControlCopyTests : IDisposable
{
    private readonly string _directory;

    public ControlCopyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "copies-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    private DocumentControlCopies Copies() => new(Path.Combine(_directory, "service"));

    private string DocumentFile(string content)
    {
        var path = Path.Combine(_directory, "model-" + Guid.NewGuid().ToString("N")[..6] + ".m3d");
        File.WriteAllText(path, content);
        return path;
    }

    // Decision (pure function).

    [Fact]
    public void ReadOnlyDocument_IsNeverRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.ReadOnly, ErrorCodes.GeometryFailed, partialEffects: true,
            openInKompas: true);

        Assert.False(decision.Restore);
        Assert.Equal(ControlCopyRestorePolicy.DocumentReadOnly, decision.Code);
        Assert.Contains("read_only", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanRefusalBeforeCom_IsNotRestored()
    {
        // REVISION_CONFLICT arrives before COM: the file did not change, so a restore would be a needless write.
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, ErrorCodes.RevisionConflict, partialEffects: false,
            openInKompas: true);

        Assert.False(decision.Restore);
        Assert.Equal(ControlCopyRestorePolicy.FailureBeforeCom, decision.Code);
        Assert.Contains(ErrorCodes.RevisionConflict, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ErrorCodes.RevisionConflict)]
    [InlineData(ErrorCodes.InvalidArgument)]
    [InlineData(ErrorCodes.StaleReference)]
    [InlineData(ErrorCodes.PathNotAllowed)]
    [InlineData(ErrorCodes.DocumentNotFound)]
    public void RefusalsThatNeverTouchedTheModel_AreNotRestored(string code)
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, code, partialEffects: false, openInKompas: true);

        Assert.False(decision.Restore);
    }

    /// <summary>A document OPEN IN KOMPAS is not overwritten from the copy, even on a partial effect.</summary>
    /// <remarks>INVARIANT: the copy is KEPT and the refusal names the manual rollback. MEASURED: whether the
    /// overwrite succeeds is machine-dependent (it succeeded in every saved acceptance run and failed with an
    /// IOException for the client), and even a successful overwrite returns only the FILE — the in-memory
    /// model stays changed. So the server does not promise what it cannot honour.
    /// History: docs/decisions/files.md#control-copies</remarks>
    [Fact]
    public void PartialEffect_OnDocumentOpenInKompas_KeepsTheCopyAndDoesNotOverwriteTheFile()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, ErrorCodes.NoGeometryChange, partialEffects: true,
            openInKompas: true);

        Assert.False(decision.Restore);
        Assert.Equal(ControlCopyRestorePolicy.CopyKeptDocumentOpen, decision.Code);
        Assert.Contains("открыт в КОМПАС", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("не откатывалась", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialEffectOnEditableDocumentNotOpenInKompas_IsRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, ErrorCodes.GeometryFailed, partialEffects: true,
            openInKompas: false);

        Assert.True(decision.Restore);
        Assert.Equal(ControlCopyRestorePolicy.FileRestored, decision.Code);
    }

    [Fact]
    public void UnknownOutcome_IsRestored()
    {
        // Unexpected exception: no code, outcome unknown — the file is returned.
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, errorCode: null, partialEffects: false, openInKompas: false);

        Assert.True(decision.Restore);
    }

    [Fact]
    public void NoCopy_IsNotRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: false, DocumentAccess.Edit, ErrorCodes.GeometryFailed, partialEffects: true,
            openInKompas: true);

        Assert.False(decision.Restore);
        Assert.Equal(ControlCopyRestorePolicy.CopyNotMade, decision.Code);
    }

    /// <summary>Negative control: a partial effect does NOT override the ban on writing to a read_only
    /// document. LIMIT: the order of the checks is part of the contract — overwriting a file around the
    /// path policy is worse than no rollback, and the reason is spoken, not withheld.</summary>
    [Fact]
    public void PartialEffect_DoesNotOverrideTheReadOnlyAccess()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.ReadOnly, ErrorCodes.GeometryFailed, partialEffects: true,
            openInKompas: true);

        Assert.False(decision.Restore);
    }

    // Behaviour on files.

    /// <summary>The copy lands in <c>&lt;root&gt;/control-copies/</c> — the documented layout, not next to
    /// the document and not one level deeper.</summary>
    /// <remarks>INVARIANT: the folder name is appended ONCE by <see cref="DocumentControlCopies"/>.
    /// MEASURED: the shipped example config and the Host default both ended in <c>control-copies</c>,
    /// so the effective path was <c>…\control-copies\control-copies\…</c>. This test pins the layout so a
    /// root that already names the folder is caught here rather than in an acceptance run.
    /// History: docs/decisions/tests.md#control-copy-layout</remarks>
    [Fact]
    public void Copy_LandsInTheControlCopiesFolderUnderTheRoot_NotOneLevelDeeper()
    {
        var root = Path.Combine(_directory, "service");
        var copies = new DocumentControlCopies(root);
        var document = DocumentFile("original");

        var copy = copies.Before(document, "doc-1", 1);

        Assert.True(copy.Made);
        Assert.Equal(Path.Combine(root, "control-copies"), Path.GetDirectoryName(copy.Path!));
    }

    /// <summary>A root already ending in <c>control-copies</c> doubles the folder: this is the caller
    /// error the example config used to make, and it is shown here rather than left to be discovered
    /// as a mystery path.</summary>
    [Fact]
    public void RootAlreadyEndingInControlCopies_DoublesTheFolder()
    {
        var root = Path.Combine(_directory, "service", "control-copies");
        var copies = new DocumentControlCopies(root);
        var document = DocumentFile("original");

        var copy = copies.Before(document, "doc-1", 1);

        Assert.True(copy.Made);
        Assert.EndsWith(Path.Combine("control-copies", "control-copies"), Path.GetDirectoryName(copy.Path!),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_RefusesToOverwriteAReadOnlyDocument()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");

        // Access mode read_only: the restore must refuse and NOT touch the document file.
        var failure = copies.Restore(document, copy.Path, DocumentAccess.ReadOnly);

        Assert.NotNull(failure);
        Assert.Contains("read_only", failure, StringComparison.Ordinal);
        Assert.Equal("mutated", File.ReadAllText(document));
    }

    [Fact]
    public void Restore_OnEditableDocument_ReturnsTheFileToTheCopy()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");

        var failure = copies.Restore(document, copy.Path, DocumentAccess.Edit);

        Assert.Null(failure);
        Assert.Equal("original", File.ReadAllText(document));
    }

    [Fact]
    public void DeleteAfterSuccess_RemovesTheCopySoTheDirectoryDoesNotGrow()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);
        Assert.True(File.Exists(copy.Path!));

        var failure = copies.DeleteAfterSuccess(copy.Path);

        Assert.Null(failure);
        Assert.False(File.Exists(copy.Path!), "копия успешной мутации обязана удаляться");
    }

    [Fact]
    public void DeleteAfterSuccess_OnMissingCopy_IsNotAFailure()
    {
        var copies = Copies();

        Assert.Null(copies.DeleteAfterSuccess(Path.Combine(_directory, "нет-такого-файла")));
        Assert.Null(copies.DeleteAfterSuccess(null));
    }

    /// <summary>A document file held open by ANOTHER handle is still restored when the OS permits the
    /// overwrite: the old probe opened the file with <c>FileShare.None</c> and refused a perfectly writable
    /// path with "вне записываемого корня".</summary>
    /// <remarks>MEASURED in client acceptance: <c>restore_attempted=true</c> and <c>restored=false</c> while
    /// <c>save_document</c> wrote the same path happily. The holder here stands in for KOMPAS, which keeps the
    /// document file open while the document is loaded.
    /// History: docs/decisions/files.md#control-copies</remarks>
    [Fact]
    public void Restore_SucceedsWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");

        // A holder that permits readers and writers: exactly how the old probe's false negative arose.
        string? failure;
        using (var holder = new FileStream(document, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            failure = copies.Restore(document, copy.Path, DocumentAccess.Edit);
        }

        Assert.Null(failure);
        Assert.Equal("original", File.ReadAllText(document));
    }

    /// <summary>When the overwrite really is refused, the reason is the OS one — never a claim about the path
    /// policy, which the restore does not evaluate at all.</summary>
    [Fact]
    public void Restore_WhenTheOverwriteIsRefused_NamesTheOsReasonNotThePathPolicy()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");

        // A holder that forbids writing: the overwrite must fail, and it must say so with the OS message.
        string? failure;
        using (var holder = new FileStream(document, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = copies.Restore(document, copy.Path, DocumentAccess.Edit);
        }

        Assert.NotNull(failure);
        Assert.DoesNotContain("вне записываемого корня", failure, StringComparison.Ordinal);
        Assert.Contains("занят", failure, StringComparison.Ordinal);
        Assert.Equal("mutated", File.ReadAllText(document));
    }

    [Fact]
    public void Restore_RefusesAFileCarryingTheReadOnlyAttribute()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");
        File.SetAttributes(document, FileAttributes.ReadOnly);
        try
        {
            var failure = copies.Restore(document, copy.Path, DocumentAccess.Edit);

            Assert.NotNull(failure);
            Assert.Contains("только чтение", failure, StringComparison.Ordinal);
            Assert.Equal("mutated", File.ReadAllText(document));
        }
        finally
        {
            File.SetAttributes(document, FileAttributes.Normal);
        }
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
