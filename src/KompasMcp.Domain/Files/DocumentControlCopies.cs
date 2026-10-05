using System.Text;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>Result of taking a control copy: the copy path, or the NAMED reason there is none.</summary>
/// <remarks>"No copy" and "copy taken" are different states: the reason is stated, not left to guess.</remarks>
public sealed record ControlCopyResult(bool Made, string? Path, string? Reason);

/// <summary>Control copies of a document file: taken BEFORE a mutation and restored on failure.</summary>
/// <remarks>INVARIANT: the copy lives in the SERVICE directory, never next to the document. INVARIANT:
/// the copy name carries document and revision, so copies at different revisions are different files.
/// LIMIT: the copy does NOT roll back the in-memory KOMPAS model — it restores the FILE. INVARIANT: the
/// directory grows by OUTCOME, not count — a copy is deleted after a SUCCESSFUL mutation, so only
/// FAILURE copies remain ("keep the last N" would restore the wrong state).
/// Policy: docs/operator-guide/control-copies.md.
/// History: docs/decisions/files.md#control-copies</remarks>
public sealed class DocumentControlCopies
{
    public const string FolderName = "control-copies";

    public string RootDirectory { get; }

    public DocumentControlCopies(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = System.IO.Path.GetFullPath(rootDirectory);
    }

    public string Folder => System.IO.Path.Combine(RootDirectory, FolderName);

    /// <summary>Take a copy of the document file before a mutation. Returns the copy or the reason there is
    /// none; a missing file on disk is a NAMED state, not an error — a just-created document has no file
    /// yet, and "no copy" is more honest than an empty path.</summary>
    public ControlCopyResult Before(string? documentPath, string documentId, long revision)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return new ControlCopyResult(false, null, "документ не имеет файла на диске: копировать нечего");
        }

        if (!File.Exists(documentPath))
        {
            return new ControlCopyResult(false, null,
                $"файла '{documentPath}' на диске нет: копировать нечего (документ ещё не сохранён)");
        }

        try
        {
            var folder = Folder;
            Directory.CreateDirectory(folder);

            // The document file name keeps the copy readable; documentId and revision make it unique
            // (two documents from different folders can share a name).
            var name = $"{documentId}-r{revision}-{System.IO.Path.GetFileName(documentPath)}";
            var target = System.IO.Path.Combine(folder, name);
            File.Copy(documentPath, target, overwrite: true);
            return new ControlCopyResult(true, target, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return new ControlCopyResult(false, null,
                $"копия не снята: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Restore the document file to its pre-mutation state. Returns <c>null</c> on success or a
    /// named reason: a failed restore must be visible, not swallowed.</summary>
    /// <remarks>INVARIANT: restoring is a WRITE to the document file, so a read-only document is not
    /// overwritten and the refusal is named. The access mode is checked HERE too, not only by the caller
    /// — the decision is made by <see cref="ControlCopyRestorePolicy"/>, and this is the second line of
    /// defence, so "do not restore read_only" cannot be bypassed by a forgotten flag.
    /// History: docs/decisions/files.md#control-copies</remarks>
    public string? Restore(string? documentPath, string? copyPath, DocumentAccess access)
    {
        if (string.IsNullOrWhiteSpace(documentPath) || string.IsNullOrWhiteSpace(copyPath))
        {
            return "восстановление не выполнено: нет пути документа или копии";
        }

        if (access == DocumentAccess.ReadOnly)
        {
            return "восстановление не выполнено: документ открыт access=read_only — файл документа не " +
                   "перезаписывается (восстановление есть запись в файл, а этот доступ записи не даёт)";
        }

        if (!File.Exists(copyPath))
        {
            return $"восстановление не выполнено: копии '{copyPath}' на диске нет";
        }

        if (!CanWrite(documentPath))
        {
            return $"восстановление не выполнено: файл документа '{documentPath}' недоступен для " +
                   "записи (документ вне записываемого корня или защищён) — возвращать файл к " +
                   "состоянию до мутации нельзя";
        }

        try
        {
            File.Copy(copyPath, documentPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"восстановление не выполнено: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Delete the control copy after a SUCCESSFUL mutation. Returns <c>null</c> on success or a
    /// named reason: an undeleted copy grows the service directory.</summary>
    /// <remarks>INVARIANT: growth is bounded by OUTCOME — only FAILURE copies remain.
    /// Policy: docs/operator-guide/control-copies.md.
    /// History: docs/decisions/files.md#control-copies</remarks>
    public string? DeleteAfterSuccess(string? copyPath)
    {
        if (string.IsNullOrWhiteSpace(copyPath))
        {
            return null;
        }

        try
        {
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return $"копия '{copyPath}' не удалена после успешной мутации: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Whether the file is writable. Checked by OPENING for write, not by a "read-only" flag:
    /// the flag ignores directory rights and server policy.</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // No file — File.Copy would create a new file in the document folder, i.e. write where
                // only a mutation was requested.
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return stream.CanWrite;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Copy description for the client response. A string, not silence: "no copy" is also a
    /// claim, else a missing field is indistinguishable from a forgotten write.</summary>
    public static string Describe(ControlCopyResult copy)
    {
        var text = new StringBuilder();
        text.Append(copy.Made ? "снята" : "не снята");
        if (copy.Path is not null)
        {
            text.Append(": ").Append(copy.Path);
        }

        if (copy.Reason is not null)
        {
            text.Append(" (").Append(copy.Reason).Append(')');
        }

        return text.ToString();
    }
}
