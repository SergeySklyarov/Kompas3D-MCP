using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Восстановление файла документа из контрольной копии: решение — таблицей, поведение — на файлах.
/// </summary>
/// <remarks>
/// Дефект H4 ревью 05.10.2026 состоял из двух частей, и обе проверяются здесь: (1) восстановление
/// шло в обход режима доступа и перезаписывало файл документа, открытого <c>access=read_only</c>;
/// (2) восстановление выполнялось и на отказах, которые до COM не доходили, — то есть сервер писал в
/// пользовательский файл без причины.
/// </remarks>
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
    // Решение (чистая функция)
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
        // REVISION_CONFLICT приходит до COM: файл не менялся, восстановление было бы лишней записью.
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
        // Неожиданное исключение: кода нет, исход неизвестен — файл возвращается.
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

    /// <summary>
    /// Отрицательный контроль: частичный эффект НЕ перебивает запрет записи в read_only-документ.
    /// Порядок проверок — часть контракта: перезапись файла в обход политики путей хуже, чем
    /// отсутствие отката, и причина произносится, а не умалчивается.
    /// </summary>
    [Fact]
    public void PartialEffect_DoesNotOverrideTheReadOnlyAccess()
    {
        var decision = ControlCopyRestorePolicy.Decide(
            copyMade: true, DocumentAccess.ReadOnly, ErrorCodes.GeometryFailed, partialEffects: true);

        Assert.False(decision.Restore);
    }

    // -----------------------------------------------------------------------------------------
    // Поведение на файлах
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Restore_RefusesToOverwriteAReadOnlyDocument()
    {
        var copies = Copies();
        var document = DocumentFile("original");
        var copy = copies.Before(document, "doc-1", 1);
        Assert.True(copy.Made);

        File.WriteAllText(document, "mutated");

        // Режим доступа read_only: восстановление обязано отказать и НЕ трогать файл документа.
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
            // Уборка временного каталога — best effort.
        }
    }
}
