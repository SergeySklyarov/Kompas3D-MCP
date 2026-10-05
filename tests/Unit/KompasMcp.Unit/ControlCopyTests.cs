using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Restoring a document file from its control copy: the decision as a table, the behaviour on files.</summary>
/// <remarks>INVARIANT (defect H4, review 05.10.2026): a restore never overwrites a document opened
/// <c>access=read_only</c>, and never runs for a refusal that never reached COM — writing into a user file
/// without cause. History: docs/decisions/tests.md#control-copy</remarks>
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

    // -----------------------------------------------------------------------------------------
    // Decision (pure function)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ReadOnlyDocument_IsNeverRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.ReadOnly, ErrorCodes.GeometryFailed, partialEffects: true);

        Assert.False(decision.Restore);
        Assert.Contains("read_only", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanRefusalBeforeCom_IsNotRestored()
    {
        // REVISION_CONFLICT arrives before COM: the file did not change, so a restore would be a needless write.
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, ErrorCodes.RevisionConflict, partialEffects: false);

        Assert.False(decision.Restore);
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
        var decision = ControlCopyRestorePolicy.Decide(copyMade: true, DocumentAccess.Edit, code, partialEffects: false);

        Assert.False(decision.Restore);
    }

    [Fact]
    public void PartialEffectOnEditableDocument_IsRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, ErrorCodes.GeometryFailed, partialEffects: true);

        Assert.True(decision.Restore);
    }

    [Fact]
    public void UnknownOutcome_IsRestored()
    {
        // Unexpected exception: no code, outcome unknown — the file is returned.
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.Edit, errorCode: null, partialEffects: false);

        Assert.True(decision.Restore);
    }

    [Fact]
    public void NoCopy_IsNotRestored()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: false, DocumentAccess.Edit, ErrorCodes.GeometryFailed, partialEffects: true);

        Assert.False(decision.Restore);
    }

    /// <summary>Negative control: a partial effect does NOT override the ban on writing to a read_only
    /// document. LIMIT: the order of the checks is part of the contract — overwriting a file around the
    /// path policy is worse than no rollback, and the reason is spoken, not withheld.</summary>
    [Fact]
    public void PartialEffect_DoesNotOverrideTheReadOnlyAccess()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.ReadOnly, ErrorCodes.GeometryFailed, partialEffects: true);

        Assert.False(decision.Restore);
    }

    // -----------------------------------------------------------------------------------------
    // Behaviour on files
    // -----------------------------------------------------------------------------------------

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
