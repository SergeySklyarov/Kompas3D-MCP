using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>
/// Определённость эскиза: чтение агрегированного статуса системы ограничений
/// (<c>kompas_get_sketch_status</c>, docs/05 §2.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Маршрут.</b> Измерен 17.09.2026 пробой S (прогон <c>82880ed0b14a4e299bb0e93d7f8a7f2f</c>,
/// артефакты <c>docs/acceptance/api7/sketch-definition.{md,json}</c>, PASS 9 · FAIL 0 · UNKNOWN 6)
/// и подтверждён на build 24.0.0.2799:
/// </para>
/// <code>
/// TransferInterface(sketchEntity, ksAPITypeEnum.ksAPI7Dual, 0)  →  KompasAPI7.ISketch
/// ISketch.ConstraintsState                                       →  ksConstraintsStateEnum
/// </code>
/// <para>
/// Перенос идёт через существующий <see cref="Api7Bridge"/> того же STA-сеанса — второго экземпляра
/// КОМПАСа не запускается, и исследовательская проба в продукт не подключена (ADR-003).
/// </para>
/// <para>
/// <b>Почему это чтение, а не мутация.</b> S.7 измерил: пятикратное чтение статуса не изменило ни
/// объём (80000), ни число тел (1), ни граней (6), ни рёбер (12). Поэтому здесь нет ни
/// <c>BeginEdit</c>, ни <c>EndEdit</c>, ни <c>Update</c>, ни перестроения, и ревизия документа не
/// поднимается. Аннотация «чтение» подкреплена кодом, а не выдана за неё.
/// </para>
/// <para>
/// <b>Чего маршрут не отдаёт.</b> Числа степеней свободы. <c>ConstraintsState</c> возвращает
/// состояние, а не счётчик, поэтому в ответе <c>degrees_of_freedom</c> всегда <c>null</c>;
/// вычислять его из числа размеров запрещено.
/// </para>
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>
    /// Регистрируется ли <c>ISketch</c> под своим именем в ответе. Вынесено константой, потому что
    /// строка уходит в поле <c>transfer_route</c> пользовательского ответа и в доказательства пробы
    /// — расхождение этих двух записей сделало бы их несравнимыми.
    /// </summary>
    private const string SketchDirectRoute = "ISketch напрямую";

    private const string SketchViaModelObjectRoute = "IModelObject → ISketch";

    /// <summary>
    /// Прочитать определённость эскиза по явной ссылке.
    /// </summary>
    /// <remarks>
    /// Ошибки и «неизвестность» здесь разведены намеренно, и это не педантизм:
    /// <list type="bullet">
    /// <item>ссылка неизвестна/устарела/чужая → <c>StaleReference</c> из <see cref="RequireSketch"/>
    /// (штатная ошибка реестра, а не <c>unknown</c>);</item>
    /// <item>ссылка ведёт не на эскиз → <c>InvalidArgument</c>;</item>
    /// <item>мост API7 не построился или <c>ConstraintsState</c> отказал по COM →
    /// <c>CapabilityUnavailable</c> / <c>VerificationFailed</c> с диагностикой;</item>
    /// <item>КОМПАС ответил <c>ksStateUnknown</c> → успешный ответ со статусом <c>unknown</c>. Это
    /// ответ продукта, и превращать его в ошибку значило бы стереть измеренный факт (S.5b: пустой
    /// эскиз отвечает именно так).</item>
    /// </list>
    /// </remarks>
    public SketchStatusResult GetSketchStatus(GetSketchStatusCommand command)
    {
        var target = RequireSketch(command.SketchRef);
        var document = target.Document;

        var bridge = BridgeFor(document);
        if (bridge.Application() is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Маршрут API7 не построился: " + (bridge.BridgeFailure ?? "причина не известна") +
                ". Статус определённости читается только через ISketch, поэтому ответить достоверно нельзя; " +
                "«недоопределён» или «определён» без этого маршрута не выдаётся.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["bridge_failure"] = bridge.BridgeFailure });
        }

        var transferred = bridge.TransferTo7(target.Sketch);
        if (transferred is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                "Эскиз не перенесён в API7 (" + (bridge.BridgeFailure ?? "TransferInterface вернул null") +
                ") — ISketch недостижим, статус не читался.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["bridge_failure"] = bridge.BridgeFailure });
        }

        // QI делается явно и по двум ступеням, как в пробе: объект, приводимый к IModelObject, не
        // обязан отвечать на ISketch, и молчаливый null от `as` означал бы «статуса нет» вместо
        // «приведение не прошло». Обе ступени различимы в ответе полем transfer_route.
        var (sketch7, route) = transferred switch
        {
            KompasAPI7.ISketch direct => (direct, SketchDirectRoute),
            KompasAPI7.IModelObject model => (model as KompasAPI7.ISketch, SketchViaModelObjectRoute),
            _ => (null, transferred.GetType().Name),
        };

        if (sketch7 is null)
        {
            throw new KompasContractException(
                ErrorCodes.CapabilityUnavailable,
                $"Перенесённый объект не отвечает на ISketch (получено: {route}). Приведение не прошло — " +
                "это утверждение о маршруте, а не об эскизе, поэтому статус не выдаётся.",
                RetryPolicy.ReacquireContext,
                details: new Dictionary<string, object?> { ["transfer_route"] = route });
        }

        int? raw;
        try
        {
            raw = (int)sketch7.ConstraintsState;
        }
        catch (COMException ex)
        {
            // Отказ COM — не «недоопределён». Разведено отдельным исключением, чтобы клиент не
            // прочитал технический отказ как свойство своей модели.
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "Чтение ISketch.ConstraintsState отказало на уровне COM. Смысл состояния эскиза не " +
                "получен: это отказ вызова, а не недоопределённость.",
                RetryPolicy.AfterReconciliation,
                partialEffects: false,
                hresult: ComHResult.From(ex),
                details: new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["transfer_route"] = route,
                });
        }
        catch (InvalidCastException ex)
        {
            throw new KompasContractException(
                ErrorCodes.VerificationFailed,
                "ISketch.ConstraintsState вернул значение, не приводимое к объявленному перечислению. " +
                "Смысл состояния не установлен.",
                RetryPolicy.AfterReconciliation,
                details: new Dictionary<string, object?>
                {
                    ["exception"] = ex.GetType().Name,
                    ["transfer_route"] = route,
                });
        }

        var diagnostics = new List<string>
        {
            $"Эскиз: {target.Sketch.name}.",
            $"Перенос в ISketch: {route}.",
        };

        if (!string.Equals(route, SketchDirectRoute, StringComparison.Ordinal))
        {
            diagnostics.Add(
                "Перенос дал объект через IModelObject: интерфейс получен приведением, а не прямым " +
                "возвратом — маршрут на этом объекте отличается от измеренного в пробе S.");
        }

        var limitations = new List<string>
        {
            "degrees_of_freedom_not_available",
        };

        return SketchStatusResult.FromRawState(
            raw,
            RedundancyVerified,
            diagnostics,
            limitations,
            sketchName: target.Sketch.name,
            transferRoute: route);
    }

    /// <summary>
    /// Получен ли в подтверждённом прогоне живой контроль состояния
    /// <c>ksStateUnresolvedRedundancy</c> (3).
    /// </summary>
    /// <remarks>
    /// Сейчас <c>false</c>, и это измеренный факт, а не «ещё не дошли руки»: проба S прочитала 46
    /// эскизов поставки и получила три значения — 0/1/2; состояние 3 <b>не встретилось ни разу</b>
    /// и контролем не построено (маршрут записи ограничений не найден ни в одной из четырёх веток).
    /// Пока флаг false, значение 3 публикуется консервативно как <c>unknown</c> с причиной
    /// <c>unresolved_redundancy_not_verified</c> — «объявлено в перечислении» не выдаётся за
    /// «измерено на продукте». Мок-тест преобразования не является основанием поднять этот флаг.
    /// </remarks>
    private const bool RedundancyVerified = false;
}
