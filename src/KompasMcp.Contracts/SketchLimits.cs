namespace KompasMcp.Contracts;

/// <summary>SINGLE place where the sketch-bulk limits live: the published schema and the argument
/// validation read them from here, so a schema advertising one number cannot enforce another.</summary>
/// <remarks>
/// DOC: the kernel imposes neither limit — both are MCP-contract limits, and the descriptions say so.
/// MEASURED: one call above the limit is refused by the CONTRACT while the same work passes in several.
/// Each number is the largest load whose worst of three repeats fits half the synchronous budget.
/// Figures: docs/decisions/adapter-sketch.md#bulk-limits
/// </remarks>
public static class SketchLimits
{
    /// <summary>Primitives per <c>kompas_edit_sketch</c> call: 2000 / 5000 / 10000 segments measured,
    /// worst 1.7 / 3.5 / 9.2 s — the last one does not fit half the budget.</summary>
    public const int MaxEntitiesPerCall = 5_000;

    /// <summary>Vertices of one polyline or spline — the schema field <c>points_mm</c> is SHARED by both
    /// kinds, so one number bounds both. 512 / 2000 / 5000 / 10000 vertices measured, worst 1.2 / 5.3 /
    /// 12.4 / 15.1 s; at 10000 the call answered <c>running</c> instead of staying synchronous. The
    /// native polyline costs about two COM calls per vertex (a parameter block plus an array item), which
    /// is why its limit is below the segment one — a measured cost, not a kernel limit.</summary>
    public const int MaxPolylineVertices = 2_000;
}
