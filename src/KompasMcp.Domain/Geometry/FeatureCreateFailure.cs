namespace KompasMcp.Domain.Geometry;

/// <summary>What was observed at the moment a solid feature's <c>Create()</c> answered false.</summary>
/// <remarks>WHY THIS EXISTS. A refusal that happens once in dozens of runs and cannot be reproduced
/// afterwards is unusable: by the time it is investigated the session is gone. This snapshot is written
/// at the refusal itself, so the NEXT refusal carries its own evidence.
/// INVARIANT: the SAME keys on every failure. A key that could not be read carries a string saying why,
/// never <c>null</c> and never a missing entry - an absent field is indistinguishable from "we forgot to
/// fill it". LIMIT: the installed v24 help documents no route for the REASON of a refusal, so this
/// reports the state around it. History: docs/decisions/adapter-features.md#create-false-snapshot</remarks>
public static class FeatureCreateFailure
{
    /// <summary>Key under which the snapshot travels inside an error's <c>details</c>.</summary>
    public const string DetailKey = "failure_snapshot";

    /// <summary>Everything the adapter read at the refusal. Nullable members are the ones a route could
    /// not answer; the reason travels next to the value in the snapshot, not in this record.</summary>
    public sealed record Observation(
        string Operation,
        int DirectionType,
        string EndCondition,
        double? DepthMm,
        double? DraftMm,
        string? TargetBodyRef,
        string? SketchPlane,
        int? FeatureCount,
        int? BodyCount,
        string? SketchState,
        string? SketchStateUnavailable,
        int? SketchProfileEntities,
        double? SketchProfileAreaMm2,
        string? SketchProfileAreaUnavailable,
        double SessionSeconds,
        int OperationOrdinal,
        int CreateFalseCount);

    /// <summary>The snapshot as the <c>details</c> dictionary of a refusal.</summary>
    public static Dictionary<string, object?> Snapshot(Observation observation) =>
        new(StringComparer.Ordinal)
        {
            ["operation"] = observation.Operation,
            // The three parameters below are the ones the adapter just WROTE into the definition; they are
            // reported as sent, not as read back - the definition of a feature that was never created has
            // no read-back route, and "what we asked for" and "what the model stored" are different claims.
            ["direction_type"] = observation.DirectionType,
            ["end_condition"] = observation.EndCondition,
            ["depth_mm"] = Number(observation.DepthMm, "не задано: режим без глубины"),
            ["draft_mm"] = Number(observation.DraftMm, "не задано: без уклона"),
            ["target_body_ref"] = Text(observation.TargetBodyRef, "не задано: тело не выбиралось"),
            ["sketch_plane"] = Text(observation.SketchPlane, "не прочитана: плоскость эскиза неизвестна"),
            ["feature_count"] = Number(observation.FeatureCount, "не прочитано: коллекция признаков не ответила"),
            ["body_count"] = Number(observation.BodyCount, "не прочитано: коллекция тел не ответила"),
            ["sketch_state"] = Text(
                observation.SketchState,
                observation.SketchStateUnavailable ?? "не прочитано: причина не названа"),
            ["sketch_profile_entities"] = Number(
                observation.SketchProfileEntities,
                "не прочитано: адаптер не рисовал этот эскиз"),
            ["sketch_profile_area_mm2"] = Number(
                observation.SketchProfileAreaMm2,
                observation.SketchProfileAreaUnavailable ?? "не прочитано: причина не названа"),
            // Session age and the ordinal of this operation are the two numbers that separate "this
            // configuration is refused" from "this session has gone stale": the same call at ordinal 5 of
            // a fresh session and at ordinal 400 of a long one is the discriminating comparison.
            ["session_seconds"] = observation.SessionSeconds,
            ["operation_ordinal"] = observation.OperationOrdinal,
            ["create_false_count"] = observation.CreateFalseCount,
        };

    /// <summary>The value as measured. INVARIANT: no rounding — a diagnostic figure that was rounded in
    /// the instrument cannot be compared with the same figure published unrounded elsewhere.</summary>
    private static object Number(double? value, string unavailable) =>
        value.HasValue ? value.Value : unavailable;

    private static object Number(int? value, string unavailable) =>
        value.HasValue ? value.Value : unavailable;

    private static object Text(string? value, string unavailable) =>
        string.IsNullOrWhiteSpace(value) ? unavailable : value;
}
