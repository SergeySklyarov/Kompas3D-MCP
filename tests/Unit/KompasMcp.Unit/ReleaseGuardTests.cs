using KompasMcp.Contracts;
using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Решение «можно ли освобождать сеанс» — таблицей, без КОМПАС и без Worker.
/// </summary>
/// <remarks>
/// <para>
/// Проверяется ИМЕННО чистая функция: дефект H3 ревью 05.10.2026 состоял в том, что правило жило
/// разложенным по ветвям <c>HostSession.ReleaseAsync</c> и один из обходов (промежуточный CAD-вызов,
/// поднявший новый Worker с пустой описью) через эти ветви проскакивал. Здесь каждая строка таблицы —
/// это состояние, в котором решение обязано быть принято ДО обращения к Worker.
/// </para>
/// <para>
/// ОБЯЗАТЕЛЬНАЯ СТРОКА: «Worker перезапущен после обрыва, опись пуста → отказ». Пустая опись нового
/// Worker — это не «правок нет», а «документы потеряны», и признак <c>DocumentStateUnknown</c> её
/// перекрывает.
/// </para>
/// </remarks>
public class ReleaseGuardTests
{
    /// <summary>Состояние по умолчанию: Worker работал, канал жив, опись прочитана, правок нет.</summary>
    private static ReleaseFacts Facts(
        bool workerStarted = true,
        bool canSendWithoutRestart = true,
        bool documentStateUnknown = false,
        bool acknowledge = false,
        bool inventoryRead = true,
        int dirtyCount = 0) =>
        new(workerStarted, canSendWithoutRestart, documentStateUnknown, acknowledge, inventoryRead, dirtyCount);

    [Fact]
    public void CleanSession_IsReleased()
    {
        var decision = ReleaseGuard.Decide(Facts());

        Assert.True(decision.Proceed);
        Assert.Null(decision.RefusalCode);
    }

    [Fact]
    public void WorkerNeverStarted_IsReleasedWithoutInventory()
    {
        // COM-сеанса не было вовсе: документов нет, и описи спрашивать не у кого.
        var decision = ReleaseGuard.Decide(Facts(workerStarted: false, inventoryRead: false));

        Assert.True(decision.Proceed);
    }

    /// <summary>ОБЯЗАТЕЛЬНАЯ СТРОКА ТАБЛИЦЫ (H3).</summary>
    [Fact]
    public void WorkerRestartedAfterBreakWithEmptyInventory_IsRefused()
    {
        // Промежуточный CAD-вызов поднял новый Worker: канал жив (canSend=true), опись прочитана и
        // ПУСТА (dirty=0) — то есть все «старые» проверки довольны. Но документы прежнего Worker
        // потеряны, и это состояние обязано перекрыть пустую опись.
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: true,
            documentStateUnknown: true,
            acknowledge: false,
            inventoryRead: true,
            dirtyCount: 0));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentStateUnknown, decision.RefusalCode);
        Assert.Contains("НЕИЗВЕСТНО", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownDocumentState_WithExplicitAcknowledgement_Proceeds()
    {
        var decision = ReleaseGuard.Decide(Facts(documentStateUnknown: true, acknowledge: true));

        Assert.True(decision.Proceed);
    }

    [Fact]
    public void UnknownDocumentState_StillRefusesOnDirtyDocumentsEvenWithAcknowledgement()
    {
        // Подтверждение неизвестного состояния НЕ отменяет отказа по известным несохранённым правкам:
        // «я не знаю» и «я знаю, что там правки» — разные состояния, и второе лечится сохранением.
        var decision = ReleaseGuard.Decide(Facts(
            documentStateUnknown: true, acknowledge: true, dirtyCount: 2));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentDirty, decision.RefusalCode);
    }

    [Fact]
    public void BrokenChannel_IsRefusedBecauseInventoryCannotBeRead()
    {
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: false, inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
        Assert.Contains("НЕИЗВЕСТНО", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// ОБЯЗАТЕЛЬНАЯ СТРОКА (§4): подтверждённое неизвестное состояние снимает отказ по СЛОМАННОМУ каналу.
    /// </summary>
    [Fact]
    public void UnknownDocumentState_Acknowledged_SkipsTheBrokenChannelRefusal()
    {
        // Сразу после обрыва канала клиент, который уже принял неизвестное состояние, обязан уметь
        // освободить сеанс. Прежде подтверждение снимало только шаг 1, а шаг 3 («канал сломан») всё
        // равно отказывал, и единственным выходом был обходной: сделать CAD-вызов, чтобы Worker
        // перезапустился, и повторить. Опись при подтверждении не нужна — она не добавляет сведений.
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false,
            documentStateUnknown: true,
            acknowledge: true,
            inventoryRead: false));

        Assert.True(decision.Proceed);
        Assert.Null(decision.RefusalCode);
    }

    /// <summary>ОБЯЗАТЕЛЬНАЯ СТРОКА (§4): подтверждение снимает и отказ «опись не прочитана».</summary>
    [Fact]
    public void UnknownDocumentState_Acknowledged_SkipsTheUnreadInventoryRefusal()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: true,
            documentStateUnknown: true,
            acknowledge: true,
            inventoryRead: false));

        Assert.True(decision.Proceed);
    }

    /// <summary>
    /// Отрицательный контроль: подтверждение НЕ снимает отказ по каналу, если неизвестное состояние не
    /// отмечено. Подтверждать нечего — признак не стоит, и «acknowledge» не должен быть универсальной
    /// отмычкой от любой проверки описи.
    /// </summary>
    [Fact]
    public void BrokenChannel_WithoutUnknownStateFlag_StillRefusesEvenWithAcknowledgement()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false,
            documentStateUnknown: false,
            acknowledge: true,
            inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
    }

    [Fact]
    public void BrokenChannelRefusal_NamesTheAcknowledgementAsTheWayOut()
    {
        // Отказ обязан НАЗЫВАТЬ выход, а не оставлять клиента в тупике: иначе единственным способом
        // остаётся обходной шаг, которого в тексте нет.
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: false, inventoryRead: false));

        Assert.Contains("acknowledge_unknown_document_state", decision.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveChannelButUnreadInventory_IsRefused()
    {
        var decision = ReleaseGuard.Decide(Facts(canSendWithoutRestart: true, inventoryRead: false));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, decision.RefusalCode);
    }

    [Fact]
    public void DirtyDocuments_AreRefused()
    {
        var decision = ReleaseGuard.Decide(Facts(dirtyCount: 1));

        Assert.False(decision.Proceed);
        Assert.Equal(ErrorCodes.DocumentDirty, decision.RefusalCode);
        Assert.Contains("1", decision.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Отрицательный контроль: неизвестное состояние без подтверждения отказывает РАНЬШЕ, чем
    /// проверяется сломанный канал. Иначе ответ называл бы причиной «канал сломан» там, где истинная
    /// причина — потеря документов, и клиент искал бы выход не там.
    /// </summary>
    [Fact]
    public void UnknownDocumentState_IsCheckedBeforeTheChannel()
    {
        var decision = ReleaseGuard.Decide(Facts(
            canSendWithoutRestart: false, documentStateUnknown: true, acknowledge: false));

        Assert.Equal(ErrorCodes.DocumentStateUnknown, decision.RefusalCode);
    }
}
