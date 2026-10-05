using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>Decision "restore the document file from the control copy": yes/no plus a NAMED reason.</summary>
public sealed record RestoreDecision(bool Restore, string Reason);

/// <summary>The restore decision — a PURE function of the failure signals and the document access.</summary>
/// <remarks>INVARIANT: access is checked BEFORE the partial effect — a read-only document is not
/// overwritten even when the mutation applied partially; overwriting around the path policy is worse than
/// no rollback, and the reason is stated in the response. INVARIANT: failures that GUARANTEED no model
/// change (<c>REVISION_CONFLICT</c>, <c>INVALID_ARGUMENT</c>, <c>STALE_REFERENCE</c> without partial
/// effects) are not restored — that would be a pointless write to a user file.
/// History: docs/decisions/files.md#restore-policy</remarks>
public static class ControlCopyRestorePolicy
{
    /// <summary>Failures that GUARANTEED no model change: they come BEFORE COM or from the contract layer,
    /// so restoring the file on them would be a pointless write to a user file.</summary>
    /// <remarks>The set is by the PROPERTY "failure before mutation": argument validation, path policy,
    /// revision check, session busyness. Codes arriving AFTER a COM call (<c>GEOMETRY_FAILED</c>,
    /// <c>VERIFICATION_FAILED</c>, <c>OUTCOME_UNKNOWN</c>) are NOT here — the model may have changed and the
    /// file is restored.</remarks>
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
    public static RestoreDecision Decide(
        bool copyMade, DocumentAccess access, string? errorCode, bool partialEffects)
    {
        if (!copyMade)
        {
            return new RestoreDecision(false, "копия не снималась: восстанавливать нечего");
        }

        // A READ-ONLY DOCUMENT IS NOT OVERWRITTEN — even on a partial effect. Restoring is a write to the
        // document file, and such a write is not allowed for this access by the policy.
        if (access == DocumentAccess.ReadOnly)
        {
            return new RestoreDecision(false,
                "документ открыт access=read_only: файл документа НЕ перезаписывается (восстановление " +
                "из копии — это запись в файл, а этот доступ записи не разрешает). Модель в памяти " +
                "КОМПАСа при этом не откатывается");
        }

        if (partialEffects)
        {
            return new RestoreDecision(true,
                "отказ нёс частичный эффект: файл документа возвращается к состоянию до мутации " +
                "(модель в памяти КОМПАСа — нет)");
        }

        if (errorCode is not null && NeverTouchedTheModel.Contains(errorCode))
        {
            return new RestoreDecision(false,
                $"отказ «{errorCode}» приходит до обращения к COM и модель не менял: восстановление " +
                "файла было бы лишней записью в файл пользователя");
        }

        return new RestoreDecision(true, errorCode is null
            ? "неожиданное исключение: исход мутации неизвестен, файл возвращается к состоянию до неё"
            : $"отказ «{errorCode}» мог изменить модель: файл возвращается к состоянию до мутации");
    }
}
