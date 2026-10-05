using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Sketch certainty: aggregate constraint state (<c>kompas_get_sketch_status</c>, docs/05 §2.2).</summary>
/// <remarks>
/// MEASURED: the transfer goes through the existing <see cref="Api7Bridge"/> of the same STA session.
/// <code>TransferInterface(..., ksAPI7Dual) → ISketch; ISketch.ConstraintsState → ksConstraintsStateEnum</code>
/// INVARIANT: this is a READ, not a mutation — no <c>BeginEdit</c>, <c>EndEdit</c>, <c>Update</c> or rebuild.
/// LIMIT: <c>ConstraintsState</c> returns a state, not a count, so <c>degrees_of_freedom</c> is always <c>null</c>.
/// History: docs/decisions/adapter-sketch.md#sketch-status
/// </remarks>
public sealed partial class Api5Session
{
    /// <summary>Whether <c>ISketch</c> is registered under its own name in the response. Kept as a
    /// constant because the string goes both into the user response field <c>transfer_route</c> and
    /// into the probe evidence — a divergence between the two records would make them
    /// incomparable.</summary>
    private const string SketchDirectRoute = "ISketch напрямую";

    private const string SketchViaModelObjectRoute = "IModelObject → ISketch";

    /// <summary>Read the sketch certainty by an explicit reference.</summary>
    /// <remarks>INVARIANT: errors and "unknown" are kept apart deliberately:
    /// <list type="bullet">
    /// <item>unknown/stale/foreign reference → <c>StaleReference</c> from <see cref="RequireSketch"/>;</item>
    /// <item>reference does not lead to a sketch → <c>InvalidArgument</c>;</item>
    /// <item>no API7 bridge or a COM-level failure → <c>CapabilityUnavailable</c> / <c>VerificationFailed</c>;</item>
    /// <item>KOMPAS answered <c>ksStateUnknown</c> → success with status <c>unknown</c>, not an error.</item>
    /// </list></remarks>
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

        // QI is explicit and two-stage, as in the probe: an object castable to IModelObject is not
        // obliged to answer ISketch, and a silent null from `as` would mean "no status" instead of
        // "the cast failed". Both stages are distinguishable in the response via transfer_route.
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
            // A COM failure is NOT "under-constrained". Kept as a separate exception so the client
            // does not read a technical failure as a property of its own model.
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

    /// <summary>Whether a live control of the state <c>ksStateUnresolvedRedundancy</c> (3) was obtained
    /// in a confirmed run.</summary>
    /// <remarks>MEASURED: currently <c>false</c> — value 3 was never encountered, so no control was built
    /// for it, and while the flag is false value 3 is published conservatively as <c>unknown</c> with
    /// reason <c>unresolved_redundancy_not_verified</c>. "Declared in the enum" is not passed off as
    /// "measured on the product"; a mock test is not grounds to raise this flag.
    /// History: docs/decisions/adapter-sketch.md#sketch-status-redundancy</remarks>
    private const bool RedundancyVerified = false;
}
