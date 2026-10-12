namespace KompasMcp.Domain.Geometry;

/// <summary>The side of an extrusion asked for in PART coordinates (<c>material_toward</c>), translated to the
/// <c>positive</c>/<c>negative</c> KOMPAS expects.</summary>
/// <remarks>WHY the REVERSE rule is needed. The v24 help fixes the sign of <c>directionType</c> against the
/// sketch normal, and the two families point OPPOSITE ways: «Прямое направление совпадает с нормалью,
/// проведенной к плоскости эскиза» for a base/boss, «Для вырезаемого элемента выдавливания направление
/// противоположно нормали» for a cut. A client asking for a side in part coordinates therefore cannot guess
/// the word, and the wrong word does not fail loudly — KOMPAS creates the feature and changes nothing. This is
/// the exact inverse of <see cref="MaterialDirectionRule.Toward"/>, and the inverse is tested against that
/// forward rule so the two cannot drift apart. Pure, so it is tested without COM.
/// DOC: kscutextrusiondefinition_directiontype.html, ksbossextrusiondefinition_directiontype.html.
/// History: docs/decisions/adapter-core.md#material-toward-input</remarks>
public static class ExtrusionSide
{
    /// <summary>The six published words. The hyphen is the ASCII one: it is what a client types and what the
    /// schema enum lists.</summary>
    public static readonly IReadOnlyList<string> Words = ["+x", "-x", "+y", "-y", "+z", "-z"];

    /// <summary>Deviation of |cos| from 1 within which the requested axis counts as the sketch normal. It is
    /// the SAME constant the axis naming uses, so "the normal is +z" and "material_toward=+z lies along the
    /// normal" cannot disagree about the same vector.</summary>
    public const double AxisTolerance = MaterialDirectionRule.AxisTolerance;

    /// <summary>The angle <see cref="AxisTolerance"/> corresponds to, in degrees. Named in the refusal so the
    /// bound is auditable instead of implied.</summary>
    public static double AngleToleranceDeg => Math.Acos(1d - AxisTolerance) * 180d / Math.PI;

    /// <summary>Parses a side word into a unit axis vector, or null when the word is not one of
    /// <see cref="Words"/>. The MINUS SIGN of the answer (U+2212, «−z») is folded to the ASCII hyphen first:
    /// the answer spells a side that way, and copying it back is the same request, not a different one.</summary>
    public static double[]? Parse(string? word)
    {
        if (word is null)
        {
            return null;
        }

        return word.Trim().Replace('\u2212', '-') switch
        {
            "+x" => [1d, 0d, 0d],
            "-x" => [-1d, 0d, 0d],
            "+y" => [0d, 1d, 0d],
            "-y" => [0d, -1d, 0d],
            "+z" => [0d, 0d, 1d],
            "-z" => [0d, 0d, -1d],
            _ => null,
        };
    }

    /// <summary>The word KOMPAS needs for the requested side, or null when the requested axis is not the
    /// sketch normal. <paramref name="angleDeg"/> always carries the MEASURED angle between them, so a refusal
    /// can name the number it refused on rather than only the rule.</summary>
    /// <remarks>LIMIT: only an axis-parallel normal is expressible — <c>material_toward</c> names an axis of
    /// the part, so a tilted sketch plane has no word for its side and the caller must use
    /// <c>direction</c>.</remarks>
    public static string? DirectionFor(bool removing, double[] normal, double[] toward, out double angleDeg)
    {
        var dot = (normal[0] * toward[0]) + (normal[1] * toward[1]) + (normal[2] * toward[2]);
        var cos = Math.Clamp(dot, -1d, 1d);
        angleDeg = Math.Acos(cos) * 180d / Math.PI;

        if (1d - Math.Abs(cos) > AxisTolerance)
        {
            return null;
        }

        // base/boss add material ALONG the normal, a cut removes AGAINST it; `negative` reverses the request.
        var along = cos > 0d;
        return removing
            ? (along ? "negative" : "positive")
            : (along ? "positive" : "negative");
    }
}
