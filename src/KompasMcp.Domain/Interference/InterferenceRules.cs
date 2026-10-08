namespace KompasMcp.Domain.Interference;

/// <summary>What a pair-wise interference check concluded, and on what basis.</summary>
/// <param name="Intersecting">True when an intersection was found; FALSE when every kernel call
/// answered "none"; <c>null</c> when at least one call failed, because then the pair is UNKNOWN and
/// <c>false</c> would claim an answer nobody gave.</param>
/// <param name="Volumetric">True when some intersection is a body; <c>null</c> on the same UNKNOWN.</param>
/// <param name="Basis">The named ground of the verdict.</param>
public sealed record InterferenceOutcome(bool? Intersecting, bool? Volumetric, string Basis);

/// <summary>Pure rules of the interference block: which pairs exist, how a kernel number is named, and
/// what an outcome means. No KOMPAS types — so they are unit-testable without the product.</summary>
/// <remarks>INVARIANT: "the kernel said there is no intersection" and "nobody answered" are different
/// outcomes and never collapse into one <c>false</c>.
/// TEST: InterferenceRulesTests. History: docs/decisions/assembly.md#interference-contracts</remarks>
public static class InterferenceRules
{
    /// <summary>The ground of a verdict read from a NULL result of the documented call.</summary>
    public const string NoIntersectionBasis = "kernel_null_documented_as_no_intersection";

    /// <summary>The ground of a verdict read from a returned intersection result.</summary>
    public const string IntersectionBasis = "kernel_intersection_result";

    /// <summary>The ground of a pair whose kernel call did not answer.</summary>
    public const string FailedBasis = "kernel_call_failed";

    /// <summary>Intersection type names by the numbers of <c>Intersection_Type</c>.</summary>
    /// <remarks>DOC: <c>intersection_type.html</c> — «itTangentPoint 1 Пересечение точкой»,
    /// «itTangentCurve 2 Пересечение вдоль касательной линии», «itTangentSurface 3 Пересечение
    /// касательной областью поверхности», «itBody 4 Пересечение образует тело».</remarks>
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [1] = "itTangentPoint",
        [2] = "itTangentCurve",
        [3] = "itTangentSurface",
        [4] = "itBody",
    };

    /// <summary>All unordered pairs of <paramref name="count"/> items: <c>count·(count−1)/2</c> rows,
    /// each unordered pair exactly once, no pair with itself.</summary>
    public static IReadOnlyList<(int A, int B)> Pairs(int count)
    {
        var pairs = new List<(int A, int B)>();
        for (var a = 0; a < count; a++)
        {
            for (var b = a + 1; b < count; b++)
            {
                pairs.Add((a, b));
            }
        }

        return pairs;
    }

    /// <summary>Name of an <c>Intersection_Type</c> value. An unknown number stays a NUMBER with the
    /// word "unread" next to it, rather than being mapped to a plausible name.</summary>
    public static string TypeName(int value) =>
        Names.TryGetValue(value, out var name) ? name : $"unread_type_{value}";

    /// <summary>What the found intersection types and the failed call count mean for one pair.</summary>
    public static InterferenceOutcome Decide(IReadOnlyList<string> types, int failures)
    {
        if (types.Count > 0)
        {
            return new InterferenceOutcome(true, types.Contains("itBody"), IntersectionBasis);
        }

        return failures > 0
            ? new InterferenceOutcome(null, null, FailedBasis)
            : new InterferenceOutcome(false, false, NoIntersectionBasis);
    }

    /// <summary>Whether the same reference is listed twice: a pair of a component with itself is an
    /// ARGUMENT defect, not a pair to check.</summary>
    public static bool HasRepeats(IReadOnlyList<string> references)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return references.Any(reference => !seen.Add(reference));
    }

    /// <summary>Whether the two measurement requests address the same component AND the same face.</summary>
    public static bool SameObject(string aRef, int? aFace, string bRef, int? bFace) =>
        string.Equals(aRef, bRef, StringComparison.Ordinal) && aFace == bFace;

    /// <summary>The minimum-distance segment, or <c>null</c> when either end was not defined.</summary>
    /// <remarks>A HALF-READ SEGMENT IS NOT PUBLISHED: one point alone is not the segment the help
    /// promises, and a substituted zero would claim a measured coordinate.</remarks>
    public static IReadOnlyList<IReadOnlyList<double>>? Segment(
        bool firstRead, IReadOnlyList<double> first, bool secondRead, IReadOnlyList<double> second) =>
        firstRead && secondRead ? new[] { first, second } : null;
}
