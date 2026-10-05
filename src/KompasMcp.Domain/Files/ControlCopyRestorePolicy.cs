using KompasMcp.Contracts;

namespace KompasMcp.Domain.Files;

/// <summary>Решение «восстанавливать ли файл документа из контрольной копии»: да/нет и НАЗВАННАЯ причина.</summary>
public sealed record RestoreDecision(bool Restore, string Reason);

/// <summary>
/// Решение о восстановлении файла документа — ЧИСТАЯ функция от признаков отказа и доступа к документу.
/// </summary>
/// <remarks>
/// <para>
/// <b>Зачем отдельная функция.</b> Прежнее восстановление проверяло только файловую возможность записи
/// (<c>File.Open(..., ReadWrite)</c>), а не доступ документа и не класс отказа. Следствие (дефект H4
/// ревью 05.10.2026): документ, открытый <c>access=read_only</c> из корня «только чтение», после
/// отклонённой мутации получал ПЕРЕЗАПИСАННЫЙ файл — восстановление шло в обход политики путей.
/// Отдельно: восстановление выполнялось и на отказах, которые ГАРАНТИРОВАННО не меняли модель
/// (<c>REVISION_CONFLICT</c>, <c>INVALID_ARGUMENT</c>, <c>STALE_REFERENCE</c> без частичных эффектов) —
/// то есть сервер писал в пользовательский файл там, где писать было незачем.
/// </para>
/// <para>
/// <b>Порядок проверок.</b> Доступ проверяется ПЕРЕД частичным эффектом: документ, открытый «только
/// чтение», не перезаписывается даже тогда, когда мутация применилась частично. Это не «пропустили
/// откат» — это названная граница: перезапись файла в обход политики путей хуже, чем отсутствие
/// отката, а причина произносится в ответе.
/// </para>
/// </remarks>
public static class ControlCopyRestorePolicy
{
    /// <summary>
    /// Отказы, которые ГАРАНТИРОВАННО не меняли модель: они приходят ДО COM либо из слоя контракта.
    /// Восстановление файла на них — лишняя запись в пользовательский файл.
    /// </summary>
    /// <remarks>
    /// Перечень — по СВОЙСТВУ «отказ до мутации», а не по списку имён ради списка: сюда попадают коды,
    /// которые выставляются валидацией аргументов, политикой путей, проверкой ревизии и занятостью
    /// сеанса. Коды, приходящие ПОСЛЕ обращения к COM (<c>GEOMETRY_FAILED</c>,
    /// <c>VERIFICATION_FAILED</c>, <c>OUTCOME_UNKNOWN</c>), сюда НЕ входят: по ним модель могла
    /// измениться, и файл восстанавливается.
    /// </remarks>
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

    /// <summary>
    /// Восстанавливать ли файл документа.
    /// </summary>
    /// <param name="copyMade">Снята ли контрольная копия (если нет — восстанавливать нечего).</param>
    /// <param name="access">Режим доступа, в котором открыт документ.</param>
    /// <param name="errorCode">Код отказа; <c>null</c> — неожиданное исключение (исход неизвестен).</param>
    /// <param name="partialEffects">Отказ нёс частичный эффект (модель могла измениться).</param>
    public static RestoreDecision Decide(
        bool copyMade, DocumentAccess access, string? errorCode, bool partialEffects)
    {
        if (!copyMade)
        {
            return new RestoreDecision(false, "копия не снималась: восстанавливать нечего");
        }

        // ДОКУМЕНТ «ТОЛЬКО ЧТЕНИЕ» НЕ ПЕРЕЗАПИСЫВАЕТСЯ — ДАЖЕ ПРИ ЧАСТИЧНОМ ЭФФЕКТЕ. Восстановление —
        // это запись в файл документа, а такая запись для этого доступа не разрешена политикой.
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
