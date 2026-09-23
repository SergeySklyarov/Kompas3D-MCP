using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Преобразование сырого <c>ksConstraintsStateEnum</c> в опубликованный статус определённости эскиза.
/// </summary>
/// <remarks>
/// <para>
/// Эти тесты проверяют <b>чистую функцию</b> <see cref="SketchStatusResult.FromRawState"/>, и это
/// единственное, что они проверяют. Живого КОМПАСа здесь нет, поэтому ни один прогон этого файла не
/// является подтверждением маршрута чтения: маршрут подтверждён пробой S
/// (<c>docs/acceptance/api7/sketch-definition.json</c>), а не мок-значением. Тест на значении 3
/// специально написан так, чтобы <b>не</b> считаться доказательством получения состояния «на живой
/// модели» — см. <see cref="Redundancy_WithoutLiveVerification_StaysUnknown"/>.
/// </para>
/// <para>
/// Проверяются три свойства, каждое из которых легко потерять при следующей правке:
/// <list type="number">
/// <item><c>is_fully_defined</c> — nullable: «недоопределён» (<c>false</c>) и «не установлено»
/// (<c>null</c>) это разные ответы;</item>
/// <item><c>degrees_of_freedom</c> — всегда <c>null</c>, и никогда не выводится из числа размеров;</item>
/// <item>неизвестное значение enum — не успех и не «недоопределён», а честный <c>unknown</c> с
/// причиной.</item>
/// </list>
/// </para>
/// </remarks>
public class SketchStatusConversionTests
{
    private static readonly string[] Diagnostics = { "Эскиз: Эскиз:1.", "Перенос в ISketch: ISketch напрямую." };
    private static readonly string[] Limitations = { "degrees_of_freedom_not_available" };

    private static SketchStatusResult Convert(int? raw, bool redundancyVerified = false) =>
        SketchStatusResult.FromRawState(raw, redundancyVerified, Diagnostics, Limitations);

    // ── Подтверждённые маршрутом значения 0/1/2 ───────────────────────────────────────────────

    [Fact]
    public void WellConstrained_IsFullyDefined()
    {
        var result = Convert(1);

        Assert.Equal(SketchDefinitionStatus.FullyDefined, result.DefinitionStatus);
        Assert.True(result.IsFullyDefined);
        Assert.Equal(1, result.RawState);
        Assert.Equal("ksStateWellConstrained", result.NativeStateName);
        Assert.DoesNotContain(result.Limitations, l => l.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderConstrained_IsUnderDefined()
    {
        // Минус, а не «неизвестно»: именно так ответил КОМПАС на свободную окружность в S.5b.
        var result = Convert(2);

        Assert.Equal(SketchDefinitionStatus.UnderDefined, result.DefinitionStatus);
        Assert.False(result.IsFullyDefined);
        Assert.NotEqual(true, result.IsFullyDefined);
    }

    [Fact]
    public void UnknownState_IsUnknownNotUnderDefined()
    {
        // 0 — это ответ КОМПАСа «не установил», и он не равен 2. Смешать их значило бы объявить
        // пустой эскиз (S.5b: ровно этот случай) недоопределённым без основания.
        var result = Convert(0);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Equal("ksStateUnknown", result.NativeStateName);
        Assert.Contains(result.Diagnostics, d => d.Contains("ksStateUnknown", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRead_IsUnknownAndKeepsNoRawState()
    {
        // raw == null означает, что вызов не дошёл (или дошёл, но значения нет). Это не ответ
        // продукта, и подставлять сюда 0 было бы выдумкой.
        var result = Convert(null);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Null(result.RawState);
        Assert.Null(result.NativeStateName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(97)]
    public void DegreesOfFreedom_IsNeverInvented(int raw)
    {
        // Маршрут (ISketch.ConstraintsState) отдаёт состояние, а не счётчик. Число степеней свободы
        // не складывается из числа размеров: ограничения связывают объекты между собой, и сумма
        // размеров не равна числу оставшихся свобод. Ноль здесь был бы утверждением, которого
        // измерение не делало.
        Assert.Null(Convert(raw, redundancyVerified: true).DegreesOfFreedom);
    }

    // ── Значение 3: объявлено, но живой контроль не получен ───────────────────────────────────

    [Fact]
    public void Redundancy_WithoutLiveVerification_StaysUnknown()
    {
        // Ключевой тест честности. Значение 3 объявлено в ksConstraintsStateEnum, но в
        // подтверждённом прогоне на живой модели (S.8: 46 эскизов поставки) не встретилось ни разу,
        // а ветка записи ограничений не найдена. Поэтому «у нас есть мок» не является основанием
        // публиковать состояние как измеренное.
        var result = Convert(3, redundancyVerified: false);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Equal(3, result.RawState);
        Assert.Contains("unresolved_redundancy_not_verified", result.Limitations);
        Assert.Contains(result.Diagnostics, d => d.Contains("ksStateUnresolvedRedundancy", StringComparison.Ordinal));

        // Сырое значение сохранено: «неизвестное значение enum» и «КОМПАС ответил 3» — разные вещи.
        Assert.Equal("ksStateUnresolvedRedundancy", result.NativeStateName);
    }

    [Fact]
    public void Redundancy_WithLiveVerification_IsNeedsAttention()
    {
        // Если живой контроль когда-нибудь появится, преобразование уже готово — и оно остаётся
        // needs_attention, а не fully_defined: «требует внимания» это не «+».
        var result = Convert(3, redundancyVerified: true);

        Assert.Equal(SketchDefinitionStatus.NeedsAttention, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.DoesNotContain("unresolved_redundancy_not_verified", result.Limitations);
    }

    [Fact]
    public void NeedsAttention_IsNotFullyDefinedAndNotUnderDefined()
    {
        // Утверждение про различимость состояний: «!» нельзя свести ни к «+», ни к «−», ни к
        // generic unknown по значению поля.
        var needsAttention = Convert(3, redundancyVerified: true);

        Assert.NotEqual(SketchDefinitionStatus.FullyDefined, needsAttention.DefinitionStatus);
        Assert.NotEqual(SketchDefinitionStatus.UnderDefined, needsAttention.DefinitionStatus);
        Assert.NotEqual(true, needsAttention.IsFullyDefined);
        Assert.NotEqual(false, needsAttention.IsFullyDefined);
    }

    // ── Значения вне объявленного перечисления ────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void UnknownEnumValue_IsNotTreatedAsSuccess(int raw)
    {
        // Строгое запрещение из постановки: любое неизвестное значение enum → unknown,
        // is_fully_defined=null, диагностическая причина. Не «недоопределён» и не «определён»:
        // продукт сказал что-то, чего сервер не понимает, и это надо назвать.
        var result = Convert(raw);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Contains("unknown_enum_value", result.Limitations);
        Assert.Equal(raw, result.RawState);
        Assert.Null(result.NativeStateName);
        Assert.Contains(result.Diagnostics, d => d.Contains(raw.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEnumValue_IsDistinguishableFromAnsweredUnknown()
    {
        // 0 и 42 дают одинаковый DefinitionStatus=Unknown, но разные Limitations и разные RawState.
        // Именно это различие и есть смысл хранить сырое значение отдельно от нормализованного.
        var answeredUnknown = Convert(0);
        var unknownValue = Convert(42);

        Assert.NotEqual(answeredUnknown.Limitations, unknownValue.Limitations);
        Assert.NotEqual(answeredUnknown.RawState, unknownValue.RawState);
        Assert.NotEqual(answeredUnknown.NativeStateName, unknownValue.NativeStateName);
        Assert.Equal(new[] { "degrees_of_freedom_not_available" }, answeredUnknown.Limitations);
        Assert.Equal(
            new[] { "degrees_of_freedom_not_available", "unknown_enum_value" },
            unknownValue.Limitations);
    }

    // ── Контекст, приложенный вызывающим, не теряется ─────────────────────────────────────────

    [Fact]
    public void CallerDiagnosticsAndLimitations_ArePreserved()
    {
        // Адаптер передаёт сюда диагностику переноса и ограничение по DOF. Функция не имеет права
        // их вытеснять: клиент читает ответ, а не внутренности адаптера.
        var result = SketchStatusResult.FromRawState(
            raw: 2,
            redundancyVerified: false,
            diagnostics: Diagnostics,
            limitations: Limitations,
            sketchName: "Эскиз:3",
            transferRoute: "IModelObject → ISketch");

        Assert.Equal(Diagnostics, result.Diagnostics);
        Assert.Equal(Limitations, result.Limitations);
        Assert.Equal("Эскиз:3", result.SketchName);
        Assert.Equal("IModelObject → ISketch", result.TransferRoute);
    }

    [Fact]
    public void CallerDiagnostics_ComeBeforeStatusSpecificOnes()
    {
        // Порядок важен для человека: сначала «какой эскиз и каким маршрутом прочитан», затем
        // «почему статус именно такой».
        var result = SketchStatusResult.FromRawState(0, false, Diagnostics, Limitations);

        Assert.Equal(Diagnostics.Length + 1, result.Diagnostics.Count);
        Assert.Equal(Diagnostics[0], result.Diagnostics[0]);
        Assert.Contains("ksStateUnknown", result.Diagnostics[^1], StringComparison.Ordinal);
    }

    // ── Ответ на главный вопрос инструмента ───────────────────────────────────────────────────

    [Fact]
    public void FullyDefined_IsTheOnlyStateAnsweringYes()
    {
        // «Этот эскиз сейчас полностью определён?» — на этот вопрос true отвечает ровно одно
        // состояние. Всё остальное, включая «не установлено», не даёт права сказать «да».
        var yes = new[] { 1 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == true);
        var everythingElse = new[] { 0, 2, 3, 4, 42 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == true);

        Assert.Equal(1, yes);
        Assert.Equal(0, everythingElse);
    }

    [Fact]
    public void UnderDefined_IsTheOnlyStateAnsweringNo()
    {
        // Зеркальное свойство, и оно не менее важно: ответ «нет» допустим только там, где продукт
        // прямо сказал «недоопределён». «Неизвестно» не превращается в «нет».
        var no = new[] { 2 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == false);
        var everythingElse = new[] { 0, 1, 3, 4, 42, 99 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == false);

        Assert.Equal(1, no);
        Assert.Equal(0, everythingElse);
    }
}
