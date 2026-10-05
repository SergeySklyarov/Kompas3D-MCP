using KompasMcp.Domain.References;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Тождество адреса компонента — таблицей, без КОМПАС.
/// </summary>
/// <remarks>
/// <para>
/// Проверяется ИМЕННО чистая функция. До 05.10.2026 правило сверки жило разложенным по ветвям
/// адаптера (<c>Api5Session.IdentityMatches</c>), где каждая правка признака ломала соседний случай,
/// а регрессия была невидима модульным тестам: сборка проходила, тесты — тоже, а живая приёмка
/// отвергла бы вторую мутацию по той же ссылке.
/// </para>
/// <para>
/// ОБЯЗАТЕЛЬНЫЕ СТРОКИ (находки §1 и §3 задания 05.10.2026):
/// <list type="bullet">
/// <item><description>
/// «источник совпал, имя совпало, матрица СМЕНИЛАСЬ после собственной мутации» → тождество НЕ ложно.
/// Матрица — изменяемое состояние: её меняет и собственная мутация, и сопряжение.
/// </description></item>
/// <item><description>
/// «имя РАСХОДИТСЯ, источник и матрица совпали» → НЕ отказ. Сравнение имён API5/API7 живьём не
/// измерялось, и систематическое расхождение форматов отвергло бы все мутации сборки.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public class ComponentIdentityTests
{
    private const ComponentIdentitySignal NotRead = ComponentIdentitySignal.NotRead;
    private const ComponentIdentitySignal Match = ComponentIdentitySignal.Matches;
    private const ComponentIdentitySignal Differ = ComponentIdentitySignal.Differs;

    /// <summary>ОБЯЗАТЕЛЬНАЯ СТРОКА (п. 1): смена матрицы после собственной мутации не ломает тождество.</summary>
    [Fact]
    public void SourceAndNameMatch_ButMatrixChangedAfterOwnMutation_IsNotRefused()
    {
        var verdict = ComponentIdentity.Decide(Match, Match, Differ);

        Assert.True(verdict.Matches);
    }

    /// <summary>ОБЯЗАТЕЛЬНАЯ СТРОКА (п. 3): расхождение имени само по себе мутацию не отвергает.</summary>
    [Fact]
    public void NameDiffers_WhileSourceAndMatrixMatch_IsNotRefused()
    {
        var verdict = ComponentIdentity.Decide(Match, Differ, Match);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void SourceMatches_EvenWhenBothSecondarySignalsDiffer_IsNotRefused()
    {
        // Имя и матрица расходятся ОБА — и всё равно не отказ: ни один из них не является измеренным
        // и неизменяемым признаком. Отвергать здесь значило бы поставить работу на непроверенное.
        var verdict = ComponentIdentity.Decide(Match, Differ, Differ);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void SourceDiffers_IsRefusedEvenWhenSecondarySignalsMatch()
    {
        // Источник — единственный признак, решающий отказ: по номеру лежит компонент с другим файлом.
        // Отрицательный контроль: совпадение имени и матрицы НЕ перебивает расхождение источника —
        // иначе номер, ведущий в чужой компонент с той же матрицей, был бы подтверждён.
        var verdict = ComponentIdentity.Decide(Differ, Match, Match);

        Assert.False(verdict.Matches);
        Assert.Contains("РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingRead_IsUnconfirmed()
    {
        var verdict = ComponentIdentity.Decide(NotRead, NotRead, NotRead);

        Assert.Null(verdict.Matches);
    }

    [Fact]
    public void SourceUnread_IsUnconfirmedEvenWhenNameMatches()
    {
        // Совпадение имени НЕ подтверждает адрес: подтверждать непроверенным признаком значило бы
        // разрешить мутацию по адресу, который не сверен.
        var verdict = ComponentIdentity.Decide(NotRead, Match, NotRead);

        Assert.Null(verdict.Matches);
    }

    [Fact]
    public void SourceMatches_WithUnreadSecondarySignals_IsConfirmed()
    {
        var verdict = ComponentIdentity.Decide(Match, NotRead, NotRead);

        Assert.True(verdict.Matches);
    }

    [Fact]
    public void MatrixMismatch_IsNamedAsNonDecidingInTheDetail()
    {
        // Расхождение матрицы НАЗЫВАЕТСЯ, а не проглатывается: молчание неотличимо от «не смотрели».
        var verdict = ComponentIdentity.Decide(Match, Match, Differ);

        Assert.Contains("матрица размещения РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("отказом НЕ управляет", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NameMismatch_IsNamedAsNonDecidingInTheDetail()
    {
        var verdict = ComponentIdentity.Decide(Match, Differ, Match);

        Assert.Contains("имя компонента РАСХОДИТСЯ", verdict.Detail, StringComparison.Ordinal);
        Assert.Contains("отказом НЕ управляет", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanConfirmation_DoesNotCarryTheRuleExplanation()
    {
        // При чистом совпадении примечание не засоряется: правило печатается там, где оно объясняет
        // расхождение или непрочитанность.
        var verdict = ComponentIdentity.Decide(Match, Match, Match);

        Assert.True(verdict.Matches);
        Assert.DoesNotContain(ComponentIdentity.SecondarySignalsAreInformative, verdict.Detail,
            StringComparison.Ordinal);
    }
}
