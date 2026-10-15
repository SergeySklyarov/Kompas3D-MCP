using System.Globalization;

namespace KompasMcp.Domain.Geometry;

/// <summary>What a change of the face count after a fillet or a chamfer proves, and what it cannot.</summary>
/// <remarks>INVARIANT: the rule is GROWTH (<c>after &gt; before</c>), not "one new face per requested
/// edge". MEASURED: a cube with all 12 edges rounded at R3 answers 6 -&gt; 26 faces, not 6 -&gt; 18: the
/// fillet that meets at a vertex leaves a corner surface there, so the exact <c>+N</c> rule refused a
/// correct body. LIMIT: growth does not say the surface stands on the REQUESTED edge (placement decides
/// that) and cannot see faces that merge at a large radius; the numeric volume comparison stays the
/// geometry check of these tools.
/// History: docs/decisions/adapter-features.md#face-count-growth-rule</remarks>
public static class FaceCountGrowth
{
    /// <summary>Whether the face count grew at all. An unread side is never "grown".</summary>
    public static bool Grew(double? before, double? after) =>
        before is double was && after is double now && now > was;

    /// <summary>The change, or null when either side was not read.</summary>
    public static double? Delta(double? before, double? after) =>
        before is double was && after is double now ? now - was : null;

    /// <summary>The reading a client sees: both counts and the change that actually happened.</summary>
    public static string Observed(double? before, double? after) =>
        Count(before) + "→" + Count(after)
        + (Delta(before, after) is double delta
            ? " (" + (delta >= 0 ? "+" : "") + Number(delta) + ")"
            : string.Empty);

    /// <summary>The rule a client sees: a growth relative to the original count, not a fixed number.</summary>
    public static string Expected(double? before) =>
        before is double was ? "> " + Number(was) : "число граней выросло";

    private static string Count(double? value) =>
        value is double count ? Number(count) : "не читается";

    private static string Number(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}
