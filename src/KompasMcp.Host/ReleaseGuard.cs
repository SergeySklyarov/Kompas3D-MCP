using KompasMcp.Contracts;

namespace KompasMcp.Host;

/// <summary>The facts the "may this session be released" decision depends on.</summary>
/// <remarks>A record so the decision is a PURE function: a table-driven test needs neither KOMPAS, nor a Worker, nor a live Host, and the rule is not scattered across <see cref="HostSession.ReleaseAsync"/>.</remarks>
/// <param name="WorkerStarted">Whether the Worker ran in this session (there may have been no COM session at all).</param>
/// <param name="CanSendWithoutRestart">Whether the channel is alive enough to request the inventory WITHOUT restarting the Worker. False here is not "no edits" but "the inventory cannot be obtained".</param>
/// <param name="DocumentStateUnknown">Sticky flag: the Worker was lost or restarted, and what became of its documents is unknown.</param>
/// <param name="AcknowledgeUnknownDocumentState">The client EXPLICITLY accepted the unknown document state.</param>
/// <param name="InventoryRead">Whether the document inventory was read.</param>
/// <param name="DirtyCount">How many documents with unsaved edits were listed.</param>
public sealed record ReleaseFacts(
    bool WorkerStarted,
    bool CanSendWithoutRestart,
    bool DocumentStateUnknown,
    bool AcknowledgeUnknownDocumentState,
    bool InventoryRead,
    int DirtyCount);

/// <summary>The release decision: either proceed, or refuse with a named code and reason.</summary>
public sealed record ReleaseDecision(bool Proceed, string? RefusalCode, string? Reason);

/// <summary>The "may this session be released" decision — a PURE function of <see cref="ReleaseFacts"/>.</summary>
/// <remarks>The order of the checks is the meaning. The sticky unknown-state flag comes FIRST: after a
/// channel break any CAD call raises a NEW Worker with no documents, whose inventory is empty and honest
/// — empty precisely because the documents were LOST, not because there were no edits; checking "is the
/// channel alive" and "is the inventory empty" first would skip exactly the state the flag exists for
/// (defect H3, review 05.10.2026). The only way to release is an explicit
/// <c>acknowledge_unknown_document_state: true</c>; acknowledgement also lifts the CHANNEL refusals (steps 3 and 4).
/// History: docs/decisions/host.md#release-guard</remarks>
public static class ReleaseGuard
{
    public static ReleaseDecision Decide(ReleaseFacts facts)
    {
        // Acknowledgement of the unknown state also lifts the channel refusal: the inventory exists to
        // LEARN about unsaved edits, but if the client has already accepted that the state is UNKNOWN,
        // the inventory adds nothing (an empty inventory of a new Worker and no inventory are the same
        // to it). Steps 3 and 4 are skipped when acknowledged; step 5 is NOT — "I do not know" and "I
        // know there are edits" are different states, and known unsaved documents are cured by saving.
        // History: docs/decisions/host.md#release-guard
        var unknownAcknowledged = facts.DocumentStateUnknown && facts.AcknowledgeUnknownDocumentState;

        // 1. The sticky flag comes first. The inventory after a restart is empty and "honest", so
        //    checking it before this flag would mistake document loss for an absence of edits.
        if (facts.DocumentStateUnknown && !facts.AcknowledgeUnknownDocumentState)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.DocumentStateUnknown,
                "Состояние документов сеанса НЕИЗВЕСТНО: Worker терялся или перезапускался, и что "
                + "стало с его документами, знает только модель. Опись нового Worker пуста не потому, "
                + "что правок нет, а потому, что документы прежнего Worker ему не известны. "
                + "Правки могли остаться в КОМПАС несохранёнными. Освобождение отказывает, пока клиент "
                + "явно не подтвердит неизвестное состояние параметром "
                + "acknowledge_unknown_document_state=true.");
        }

        // 2. The Worker never ran: no COM session and no documents. There is nobody to ask for the inventory.
        if (!facts.WorkerStarted)
        {
            return new ReleaseDecision(true, null, null);
        }

        // 3. The inventory is NOT obtained by restarting the Worker. A broken channel means the
        //    inventory is absent, not that there are no edits; a restart for the inventory would lose
        //    the documents and return an empty list. Acknowledged unknown state skips this step.
        if (!facts.CanSendWithoutRestart && !unknownAcknowledged)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.SessionReleaseFailed,
                "Канал к Worker сломан: опись документов получить нельзя, состояние документов "
                + "НЕИЗВЕСТНО. Освобождение не может утверждать, что правок нет. Перезапуск Worker "
                + "ради описи запрещён: он потерял бы все документы сеанса и дал бы пустую опись. "
                + "Если потеря правок допустима, повторите release с "
                + "acknowledge_unknown_document_state=true — тогда опись не потребуется.");
        }

        // 4. The inventory was not read — also UNKNOWN, not "no edits". Acknowledged unknown state
        //    skips this step.
        if (!facts.InventoryRead && !unknownAcknowledged)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.SessionReleaseFailed,
                "Опись документов не получена: состояние документов НЕИЗВЕСТНО. Без описи нельзя "
                + "утверждать, что правок нет. Если потеря правок допустима, повторите release с "
                + "acknowledge_unknown_document_state=true — тогда опись не потребуется.");
        }

        // 5. Unsaved documents are refused by default.
        if (facts.DirtyCount > 0)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.DocumentDirty,
                $"Освобождение отклонено: в сеансе {facts.DirtyCount} документов с несохранёнными "
                + "изменениями. Сервер не сохраняет и не отказывается от правок молча.");
        }

        return new ReleaseDecision(true, null, null);
    }
}
