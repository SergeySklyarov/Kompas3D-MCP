using System.Runtime.InteropServices;
using Kompas6API5;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;

namespace KompasMcp.Api5Adapter;

/// <summary>Sketch certainty: read the aggregate state of the constraint system
/// (<c>kompas_get_sketch_status</c>, docs/05 §2.2).</summary>
/// <remarks>
/// MEASURED: the route below was measured 17.09.2026 by probe S (run
/// <c>82880ed0b14a4e299bb0e93d7f8a7f2f</c>, artifacts
/// <c>docs/acceptance/api7/sketch-definition.{md,json}</c>, PASS 9 · FAIL 0 · UNKNOWN 6) and confirmed
/// on build 24.0.0.2799. The transfer goes through the existing <see cref="Api7Bridge"/> of the same
/// STA session — no second KOMPAS instance is started and the research probe is not wired into the
/// product (ADR-003).
/// <code>
/// TransferInterface(sketchEntity, ksAPITypeEnum.ksAPI7Dual, 0)  →  KompasAPI7.ISketch
/// ISketch.ConstraintsState                                       →  ksConstraintsStateEnum
/// </code>
/// INVARIANT: this is a READ, not a mutation. MEASURED (S.7): reading the state five times changed
/// neither the volume (80000) nor the body count (1) nor faces (6) nor edges (12). Hence no
/// <c>BeginEdit</c>, <c>EndEdit</c>, <c>Update</c> or rebuild here, and the document revision is not
/// bumped — the "read" annotation is backed by code, not merely declared.
/// LIMIT: the route does NOT yield degrees of freedom. <c>ConstraintsState</c> returns a state, not a
/// count, so <c>degrees_of_freedom</c> is always <c>null</c> in the response; deriving it from the
/// dimension count is forbidden.
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
    /// <item>reference unknown/stale/foreign → <c>StaleReference</c> from <see cref="RequireSketch"/>
    /// (a normal registry error, not <c>unknown</c>);</item>
    /// <item>reference does not lead to a sketch → <c>InvalidArgument</c>;</item>
    /// <item>the API7 bridge was not built or <c>ConstraintsState</c> failed at the COM level →
    /// <c>CapabilityUnavailable</c> / <c>VerificationFailed</c> with diagnostics;</item>
    /// <item>KOMPAS answered <c>ksStateUnknown</c> → a successful response with status <c>unknown</c>.
    /// That is a product answer, and turning it into an error would erase a MEASURED fact (S.5b: an
    /// empty sketch answers exactly so).</item>
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
    /// <remarks>MEASURED: currently <c>false</c>, and that is a measured fact, not "not done yet" —
    /// probe S read 46 shipped sketches and got three values (0/1/2); state 3 was NEVER encountered and
    /// no control was built for it (no constraint-write route was found in any of the four branches).
    /// While the flag is false, value 3 is published conservatively as <c>unknown</c> with reason
    /// <c>unresolved_redundancy_not_verified</c> — "declared in the enum" is not passed off as
    /// "measured on the product". A mock test of the conversion is not grounds to raise this flag.</remarks>
    private const bool RedundancyVerified = false;
}
