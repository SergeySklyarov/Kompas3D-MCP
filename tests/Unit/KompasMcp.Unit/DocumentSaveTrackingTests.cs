using KompasMcp.Contracts;
using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Сохранённость документа: переходы состояния и таблица решений при закрытии.
/// </summary>
/// <remarks>
/// <para>
/// Это управляемый тестовый шов, а не измерение КОМПАС. Проверяются ровно те правила, которые
/// нельзя получить от CAD: документированного признака «документ изменён» у целевой версии нет
/// (у <c>ksDocument3D</c> в справке v24 такого члена нет), поэтому состояние ведёт продукт, и
/// единственное место, где его можно проверить без КОМПАС, — сами переходы. Поведение в CAD
/// доказывается прогоном строк <c>DL</c> по поставленным бинарям.
/// </para>
/// <para>
/// Прежний дефект: отпечаток последней наблюдаемой ревизии писался и мутацией, и сохранением,
/// поэтому «отпечатки равны» получалось сразу после изменения модели — и <c>close(refuse)</c>
/// закрывал изменённый документ, а <c>close(save)</c> не сохранял. Обе половины закреплены ниже:
/// мутация не объявляет документ сохранённым, сохранённым объявляет только подтверждённая запись.
/// </para>
/// </remarks>
public sealed class DocumentSaveTrackingTests
{
    [Fact]
    public void Mutation_NeverDeclaresTheDocumentSaved()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterMutation());
        Assert.True(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterMutation()));
    }

    [Fact]
    public void RevisionBumpIsNotASave()
    {
        // «обновление ревизии не должно объявлять документ сохранённым»: после сохранения
        // документ чист, но следующая же мутация снова делает его изменённым.
        var afterSave = DocumentSaveTracking.AfterConfirmedSave();
        Assert.False(DocumentSaveTracking.IsDirty(afterSave));
        Assert.True(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterMutation()));
    }

    [Fact]
    public void CreateAndExternalChangeAreDirty()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterCreate());
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterExternalChange());
    }

    [Fact]
    public void OpenIsClean()
    {
        // Открытие читает файл с диска: модель и файл совпадают.
        Assert.False(DocumentSaveTracking.IsDirty(DocumentSaveTracking.AfterOpen()));
    }

    [Fact]
    public void FailedSaveDoesNotClearTheState()
    {
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Dirty));
        Assert.Equal(DocumentSaveState.Unknown, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Unknown));
        // «чисто» после отказа не остаётся чистым: сохранение не подтвердилось, а модель уже
        // разошлась с файлом (иначе сохранять было бы нечего).
        Assert.Equal(DocumentSaveState.Dirty, DocumentSaveTracking.AfterFailedSave(DocumentSaveState.Clean));
    }

    [Fact]
    public void UnreadableStateIsNotReportedAsClean()
    {
        var unknown = DocumentSaveTracking.AfterUnreadableObservation();
        Assert.Equal(DocumentSaveState.Unknown, unknown);
        Assert.True(DocumentSaveTracking.IsDirty(unknown));
    }

    [Theory]
    // refuse: закрывает только подтверждённо чистый документ
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Refuse, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Refuse, CloseAction.Refuse)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Refuse, CloseAction.Refuse)]
    // save: сохраняет перед закрытием всё, что не подтверждено чистым
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Save, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Save, CloseAction.SaveThenClose)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Save, CloseAction.SaveThenClose)]
    // discard: отказ от изменений закрывает в любом состоянии, включая неизвестное
    [InlineData(DocumentSaveState.Clean, DirtyPolicy.Discard, CloseAction.Close)]
    [InlineData(DocumentSaveState.Dirty, DirtyPolicy.Discard, CloseAction.Close)]
    [InlineData(DocumentSaveState.Unknown, DirtyPolicy.Discard, CloseAction.Close)]
    public void CloseDecisionTable(DocumentSaveState state, DirtyPolicy policy, CloseAction expected)
    {
        Assert.Equal(expected, DocumentSaveTracking.Decide(state, policy));
    }

    [Fact]
    public void UnreadableFingerprintIsNotAValue()
    {
        // Отдельная строка, а не число: ноль тел пустого документа и «коллекция не ответила» —
        // разные факты, и первый не должен читаться как второй.
        Assert.False(string.IsNullOrWhiteSpace(DocumentSaveTracking.UnreadableFingerprint));
        Assert.DoesNotContain(":", DocumentSaveTracking.UnreadableFingerprint);
    }
}
