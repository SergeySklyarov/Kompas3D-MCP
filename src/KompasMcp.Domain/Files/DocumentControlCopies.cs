using System.Text;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>Result of taking a control copy: the copy path, or the NAMED reason there is none.</summary>
/// <remarks>"No copy" and "copy taken" are different states and an empty field does not tell them apart:
/// the reason is stated, not left to the caller to guess.</remarks>
public sealed record ControlCopyResult(bool Made, string? Path, string? Reason);

/// <summary>Control copies of a document file: taken BEFORE a mutation and restored on failure
/// (<c>dep.foundation</c>, action <c>negative_tests</c>).</summary>
/// <remarks>INVARIANT: the copy lives in the SERVICE directory, never next to the document — writing next
/// to a read-only model put a full-file copy beside it and restoration OVERWROTE the document file
/// (defect H4, review 05.10.2026). INVARIANT: the copy name carries document and revision — a copy at
/// revision 7 and one at 8 are different files. LIMIT: the copy does NOT roll back the in-memory KOMPAS
/// model — it restores the FILE, and the caller must say so. INVARIANT: the directory grows by OUTCOME,
/// not by count — a copy is deleted after a SUCCESSFUL mutation, so only FAILURE copies remain ("keep the
/// last N" would restore the wrong state). Policy: docs/operator-guide/control-copies.md.
/// History: docs/decisions/files.md#control-copies</remarks>
public sealed class DocumentControlCopies
{
    /// <summary>Subfolder name inside the service directory. One for all copies of a run.</summary>
    public const string FolderName = "control-copies";

    /// <summary>Service directory the copies may be written to.</summary>
    public string RootDirectory { get; }

    /// <param name="rootDirectory">A writable service directory (next to the operation journal). Required:
    /// the "next to the document" default is the very defect fixed here.</param>
    public DocumentControlCopies(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = System.IO.Path.GetFullPath(rootDirectory);
    }

    /// <summary>The copy directory: the service root plus <see cref="FolderName"/>.</summary>
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

            // The document file name in the copy name keeps it readable; document and revision make it
            // unique. The document is NOT copied "next to the original": two documents from different
            // folders can share a name, hence documentId is added.
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
    /// <remarks>Restoring is a WRITE to the document file, so it happens only when the file is writable
    /// AND the document is open for writing; a read-only document is not overwritten and the refusal is
    /// named. INVARIANT: the access mode is checked HERE, not only by the caller — a document opened
    /// <c>access=read_only</c> previously got its file overwritten (defect H4, review 05.10.2026). The
    /// decision is made by <see cref="ControlCopyRestorePolicy"/>; this is the second line of defence, so
    /// "do not restore read_only" cannot be bypassed by forgetting to pass the flag.
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
    /// named reason: an undeleted copy grows the service directory, and that is named, not unnoticed.</summary>
    /// <remarks>INVARIANT: growth is bounded by OUTCOME — the copy is deleted on success, so only FAILURE
    /// copies remain ("keep the last N" would restore the wrong state). Policy:
    /// docs/operator-guide/control-copies.md. History: docs/decisions/files.md#control-copies</remarks>
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

    /// <summary>Whether the file is writable. Checked by OPENING for write, not by a "read-only" flag: the
    /// flag ignores directory rights and server policy, while opening is exactly what the restore would
    /// do.</summary>
    private static bool CanWrite(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // No file — nothing to restore into: File.Copy would create a new file in the document
                // folder, i.e. write where only a mutation was requested.
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

    /// <summary>Copy description for the client response. A string, not silence: "no copy" is also a claim
    /// and must be spoken, else a missing field is indistinguishable from a forgotten write.</summary>
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
