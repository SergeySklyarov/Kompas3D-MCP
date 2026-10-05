using KompasMcp.Contracts;

namespace KompasMcp.Host;

/// <summary>
/// Факты, от которых зависит решение «можно ли освобождать сеанс».
/// </summary>
/// <remarks>
/// Вынесены в отдельную запись, чтобы решение было ЧИСТОЙ функцией: тест на таблице значений не
/// требует ни КОМПАС, ни Worker, ни живого Хоста, а «правило» перестаёт быть разбросанным по ветвям
/// <see cref="HostSession.ReleaseAsync"/>.
/// </remarks>
/// <param name="WorkerStarted">Запускался ли Worker в этом сеансе (COM-сеанса могло не быть вовсе).</param>
/// <param name="CanSendWithoutRestart">
/// Жив ли канал настолько, чтобы запросить опись БЕЗ перезапуска Worker. Ложь здесь — не «правок
/// нет», а «опись получить нечем».
/// </param>
/// <param name="DocumentStateUnknown">
/// Липкий признак: Worker терялся или перезапускался, и что стало с его документами, неизвестно.
/// </param>
/// <param name="AcknowledgeUnknownDocumentState">
/// Клиент ЯВНО принял неизвестное состояние документов на себя.
/// </param>
/// <param name="InventoryRead">Прочитана ли опись документов.</param>
/// <param name="DirtyCount">Сколько документов с несохранёнными правками перечислено.</param>
public sealed record ReleaseFacts(
    bool WorkerStarted,
    bool CanSendWithoutRestart,
    bool DocumentStateUnknown,
    bool AcknowledgeUnknownDocumentState,
    bool InventoryRead,
    int DirtyCount);

/// <summary>Решение об освобождении: либо выполнять, либо отказ с названным кодом и причиной.</summary>
public sealed record ReleaseDecision(bool Proceed, string? RefusalCode, string? Reason);

/// <summary>
/// Решение «можно ли освобождать сеанс» — ЧИСТАЯ функция от <see cref="ReleaseFacts"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Порядок проверок — не украшение, а сам смысл.</b> Липкий признак неизвестного состояния стоит
/// ПЕРВЫМ: он отвечает на случай, который каналом не ловится. После обрыва канала любой CAD-вызов
/// поднимает НОВЫЙ Worker, у которого нет ни одного документа: его опись пуста и честна, но пуста
/// она ровно потому, что документы ПОТЕРЯНЫ, а не потому, что правок не было. Проверять после этого
/// «жив ли канал» и «пуста ли опись» — значит пропустить именно то состояние, ради которого признак
/// заведён (дефект H3 ревью 05.10.2026, обход через промежуточный вызов).
/// </para>
/// <para>
/// <b>Выход есть, и он явный.</b> Единственный способ освободить сеанс с неизвестным состоянием —
/// <c>acknowledge_unknown_document_state: true</c>: клиент берёт на себя, что правки прежнего Worker
/// могли остаться в КОМПАС несохранёнными. Молчаливого выхода нет — он был бы тем же обходом, только
/// с другого конца.
/// </para>
/// </remarks>
public static class ReleaseGuard
{
    public static ReleaseDecision Decide(ReleaseFacts facts)
    {
        // 1. ЛИПКИЙ ПРИЗНАК — ПЕРВЫМ. Опись после перезапуска пуста и «честна», поэтому проверять
        //    её до этого признака значило бы принимать потерю документов за отсутствие правок.
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

        // 2. WORKER НЕ ЗАПУСКАЛСЯ — COM-СЕАНСА И ДОКУМЕНТОВ НЕТ. Описи спрашивать не у кого.
        if (!facts.WorkerStarted)
        {
            return new ReleaseDecision(true, null, null);
        }

        // 3. ОПИСЬ НЕ ЗАПРАШИВАЕТСЯ ПЕРЕЗАПУСКОМ WORKER. Сломанный канал — это отсутствие описи, а
        //    не «правок нет»; перезапуск ради описи потерял бы документы и дал пустой список.
        if (!facts.CanSendWithoutRestart)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.SessionReleaseFailed,
                "Канал к Worker сломан: опись документов получить нельзя, состояние документов "
                + "НЕИЗВЕСТНО. Освобождение не может утверждать, что правок нет. Перезапуск Worker "
                + "ради описи запрещён: он потерял бы все документы сеанса и дал бы пустую опись.");
        }

        // 4. ОПИСЬ НЕ ПРОЧИТАНА — ТОЖЕ «НЕИЗВЕСТНО», а не «правок нет».
        if (!facts.InventoryRead)
        {
            return new ReleaseDecision(
                false,
                ErrorCodes.SessionReleaseFailed,
                "Опись документов не получена: состояние документов НЕИЗВЕСТНО. Без описи нельзя "
                + "утверждать, что правок нет.");
        }

        // 5. НЕСОХРАНЁННЫЕ ДОКУМЕНТЫ — ОТКАЗ ПО УМОЛЧАНИЮ.
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
