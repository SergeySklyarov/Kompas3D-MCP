using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>Decision "restore the document file from the control copy": yes/no plus a machine-readable
/// <see cref="Code"/> and a NAMED <see cref="Reason"/>.</summary>
public sealed record RestoreDecision(bool Restore, string Code, string Reason);

/// <summary>The restore decision — a PURE function of the failure signals, the document access and whether
/// the document is open in KOMPAS.</summary>
/// <remarks>INVARIANT: a document OPEN IN KOMPAS is not overwritten from the copy. MEASURED: the write
/// succeeds on some machines and fails on others, and even on success it returns only the FILE — the
/// in-memory model stays changed, so the copy is KEPT and the manual rollback is named instead.
/// INVARIANT: access is checked BEFORE the partial effect — read_only is never overwritten.
/// INVARIANT: a refusal that guaranteed no model change is not restored — that would be a pointless write.
/// History: docs/decisions/files.md#control-copies</remarks>
public static class ControlCopyRestorePolicy
{
    /// <summary>Decision codes carried to the client in <c>restore_decision</c>.</summary>
    public const string CopyNotMade = "copy_not_made";

    /// <summary>The document is open in KOMPAS: the copy is kept, the file is NOT overwritten.</summary>
    public const string CopyKeptDocumentOpen = "copy_kept_document_open";

    /// <summary>The document is open read-only: the file is not overwritten.</summary>
    public const string DocumentReadOnly = "document_read_only";

    /// <summary>The failure came before COM: the file is not overwritten.</summary>
    public const string FailureBeforeCom = "failure_before_com";

    /// <summary>The model may have changed and the document was NOT open in KOMPAS: the file is returned.</summary>
    public const string FileRestored = "file_restored";

    /// <summary>Failures that GUARANTEED no model change: they come BEFORE COM or from the contract layer,
    /// so restoring the file on them would be a pointless write to a user file.</summary>
    /// <remarks>The set is by the PROPERTY "failure before mutation": argument validation, path policy,
    /// revision check, session busyness. Codes arriving AFTER a COM call (<c>GEOMETRY_FAILED</c>,
    /// <c>VERIFICATION_FAILED</c>, <c>OUTCOME_UNKNOWN</c>) are NOT here.</remarks>
    private static readonly HashSet<string> NeverTouchedTheModel = new(StringComparer.Ordinal)
    {
        ErrorCodes.RevisionConflict,
        ErrorCodes.InvalidArgument,
        ErrorCodes.StaleReference,
        ErrorCodes.PathNotAllowed,
        ErrorCodes.DocumentNotFound,
        ErrorCodes.WrongDocumentKind,
        ErrorCodes.OperationIdConflict,
        ErrorCodes.JournalUnavailable,
        ErrorCodes.QueueFull,
        ErrorCodes.SessionOwnerActive,
        ErrorCodes.SessionNotAcquired,
        ErrorCodes.SessionReleaseBusy,
        ErrorCodes.SessionReleaseFailed,
        ErrorCodes.OwnershipStateUnknown,
        ErrorCodes.DocumentStateUnknown,
        ErrorCodes.CapabilityUnavailable,
        ErrorCodes.DocumentDirty,
        ErrorCodes.FileExists,
    };

    /// <summary>Whether to restore the document file.</summary>
    /// <param name="copyMade">Whether a control copy was taken (if not, there is nothing to restore).</param>
    /// <param name="access">The access mode the document is open in.</param>
    /// <param name="errorCode">The failure code; <c>null</c> — an unexpected exception (unknown outcome).</param>
    /// <param name="partialEffects">The failure carried a partial effect (the model may have changed).</param>
    /// <param name="openInKompas">Whether the server holds a LIVE CAD handle for the document — i.e. the
    /// document is open in KOMPAS right now and its file is therefore held by the CAD application.</param>
    public static RestoreDecision Decide(
        bool copyMade, DocumentAccess access, string? errorCode, bool partialEffects, bool openInKompas)
    {
        if (!copyMade)
        {
            return new RestoreDecision(false, CopyNotMade,
                "копия не снималась: восстанавливать нечего");
        }

        // A READ-ONLY DOCUMENT IS NOT OVERWRITTEN — even on a partial effect. Restoring is a write to the
        // document file, and such a write is not allowed for this access by the policy.
        if (access == DocumentAccess.ReadOnly)
        {
            return new RestoreDecision(false, DocumentReadOnly,
                "документ открыт access=read_only: файл документа НЕ перезаписывается (восстановление " +
                "из копии — это запись в файл, а этот доступ записи не разрешает). Модель в памяти " +
                "КОМПАСа при этом не откатывается");
        }

        // THE DOCUMENT IS OPEN IN KOMPAS — THE COPY IS KEPT AND THE FILE IS NOT TOUCHED. Named, not left
        // to the OS: the same call that overwrites the file here failed for the client, so the outcome is
        // machine-dependent and cannot be promised. And a successful overwrite returns only the FILE,
        // never the in-memory model, which the next save would silently overwrite anyway.
        if (openInKompas && partialEffects)
        {
            return new RestoreDecision(false, CopyKeptDocumentOpen,
                "файл открыт в КОМПАС: копия сохранена (путь в control_copy_path), файл документа НЕ " +
                "перезаписывается — перезапись открытого файла зависит от машины и заранее не " +
                "определяется, а удачная перезапись вернула бы только файл. Ничего не откачено; " +
                "модель в памяти КОМПАСа не откатывалась");
        }

        if (partialEffects)
        {
            return new RestoreDecision(true, FileRestored,
                "отказ нёс частичный эффект, документ НЕ открыт в КОМПАС: файл документа возвращается к " +
                "состоянию до мутации (модель в памяти КОМПАСа — нет)");
        }

        if (errorCode is not null && NeverTouchedTheModel.Contains(errorCode))
        {
            return new RestoreDecision(false, FailureBeforeCom,
                $"отказ «{errorCode}» приходит до обращения к COM и модель не менял: восстановление " +
                "файла было бы лишней записью в файл пользователя");
        }

        return new RestoreDecision(true, FileRestored, errorCode is null
            ? "неожиданное исключение: исход мутации неизвестен, файл возвращается к состоянию до неё"
            : $"отказ «{errorCode}» мог изменить модель: файл возвращается к состоянию до мутации");
    }
}
