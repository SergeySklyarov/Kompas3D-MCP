using KompasMcp.Contracts;

namespace KompasMcp.Domain.Documents;

/// <summary>
/// Состояние сохранённости документа в СОБСТВЕННОЙ модели MCP: чем подтверждено, что на диске
/// лежит именно та модель, которую держит КОМПАС.
/// </summary>
/// <remarks>
/// <para>
/// Почему учёт собственный, а не взятый у КОМПАСа. Документированного признака «документ изменён»
/// у целевой версии нет: в официальной справке v24 у <c>ksDocument3D</c> перечислены все методы и
/// свойства, и среди них нет ни «IsSaved», ни «Modified» (страницы <c>ksdocument3d_methods.html</c>,
/// <c>ksdocument3d_properties.html</c>), а в interop установленной версии такого члена тоже нет.
/// Следовательно, спрашивать у CAD «сохранён ли документ» нечем, и состояние обязано вести
/// приложение — это логика продукта, а не маршрут SDK.
/// </para>
/// <para>
/// Грубого геометрического отпечатка для этого недостаточно, и это измерено на самом коде:
/// отпечаток последней наблюдаемой ревизии сравнивался с текущим, а мутация записывала в него
/// свежее значение, поэтому «отпечатки равны» получалось ровно в тот момент, когда модель уже
/// разошлась с файлом. Отпечаток отвечает на другой вопрос — «менял ли модель кто-то помимо нас»
/// — и здесь он используется только для этого.
/// </para>
/// </remarks>
public enum DocumentSaveState
{
    /// <summary>Изменений, не подтверждённых записью в файл, нет.</summary>
    Clean = 0,

    /// <summary>
    /// Есть изменение, не подтверждённое записью в файл: мутация (в том числе частично
    /// выполненная), правка в UI, неуспешное сохранение или документ, который ещё не сохраняли.
    /// </summary>
    Dirty = 1,

    /// <summary>
    /// Достоверно определить не удалось: состояние документа не читается. Консервативный исход —
    /// считать изменённым, а не «подтверждённо чистым».
    /// </summary>
    Unknown = 2,
}

/// <summary>Что сервер делает с документом при закрытии — результат таблицы решений.</summary>
public enum CloseAction
{
    /// <summary>Закрыть как есть (сохранять нечего либо политика discard).</summary>
    Close,

    /// <summary>Отказать: изменения есть, а политика требует не закрывать без сохранения.</summary>
    Refuse,

    /// <summary>Сохранить, подтвердить запись чтением и только затем закрыть.</summary>
    SaveThenClose,
}

/// <summary>
/// Переходы состояния сохранённости и решение о закрытии. Отдельный COM-свободный модуль, потому
/// что это единственная часть правила, которую можно проверить без КОМПАС: сами переходы.
/// </summary>
public static class DocumentSaveTracking
{
    /// <summary>
    /// Отпечаток, который означает «прочитать не удалось». Отдельное значение, а не число:
    /// ноль тел у пустого документа и «коллекция не ответила» — разные факты, и первый из них
    /// не должен читаться как второй.
    /// </summary>
    public const string UnreadableFingerprint = "unavailable";

    /// <summary>
    /// Изменённым считается всё, кроме подтверждённо чистого: неизвестное состояние не выдаётся
    /// за <c>dirty=false</c>.
    /// </summary>
    public static bool IsDirty(DocumentSaveState state) => state != DocumentSaveState.Clean;

    /// <summary>
    /// Обычная мутация: модель разошлась с файлом. Ни чтение контекста, ни обновление ревизии
    /// сохранённости не подтверждают — подтверждает только запись в файл, проверенная чтением.
    /// </summary>
    public static DocumentSaveState AfterMutation() => DocumentSaveState.Dirty;

    /// <summary>Правка модели помимо MCP: наблюдаемый отпечаток разошёлся с прежним.</summary>
    public static DocumentSaveState AfterExternalChange() => DocumentSaveState.Dirty;

    /// <summary>Документ, который ещё ни разу не записывали (создан и не сохранён).</summary>
    public static DocumentSaveState AfterCreate() => DocumentSaveState.Dirty;

    /// <summary>Открытие файла: КОМПАС прочитал его с диска, модель совпадает с файлом.</summary>
    public static DocumentSaveState AfterOpen() => DocumentSaveState.Clean;

    /// <summary>Отпечаток не читается: неизменность не доказана, поэтому «неизвестно».</summary>
    public static DocumentSaveState AfterUnreadableObservation() => DocumentSaveState.Unknown;

    /// <summary>Сохранение подтверждено: операция вернула успех И файл перечитан с диска.</summary>
    public static DocumentSaveState AfterConfirmedSave() => DocumentSaveState.Clean;

    /// <summary>
    /// Отказ сохранения состояние не очищает: прежнее «чисто» переходит в «изменён» (модель
    /// разошлась с файлом), «изменён»/«неизвестно» остаются собой.
    /// </summary>
    public static DocumentSaveState AfterFailedSave(DocumentSaveState current) =>
        current == DocumentSaveState.Clean ? DocumentSaveState.Dirty : current;

    /// <summary>
    /// Таблица решений при закрытии. <c>discard</c> закрывает без сохранения независимо от
    /// состояния — это и означает «отказаться от изменений»; <c>refuse</c> отказывает на всём,
    /// что не подтверждено чистым; <c>save</c> сохраняет перед закрытием.
    /// </summary>
    public static CloseAction Decide(DocumentSaveState state, DirtyPolicy policy) => policy switch
    {
        DirtyPolicy.Discard => CloseAction.Close,
        DirtyPolicy.Refuse => IsDirty(state) ? CloseAction.Refuse : CloseAction.Close,
        DirtyPolicy.Save => IsDirty(state) ? CloseAction.SaveThenClose : CloseAction.Close,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "неизвестная политика закрытия"),
    };
}
