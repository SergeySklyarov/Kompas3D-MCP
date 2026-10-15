using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The named gaps of a result whose geometry was NOT confirmed: one per FAILED check.</summary>
/// <remarks>INVARIANT: a lowered level names the check that lowered it. MEASURED: the fillet and the
/// chamfer published <c>volume_delta_not_confirmed</c> while <c>volume_delta</c> PASSED and a different
/// check had failed, so the reason contradicted the checks in the same response.
/// LIMIT: only the checks the confirmation actually depends on are named - a failed check outside that
/// set is a finding of its own and is not turned into a reason for the lowered level.
/// History: docs/decisions/adapter-features.md#face-count-growth-rule</remarks>
public static class UnconfirmedChecks
{
    /// <summary>The reason published when no failed check can be named - never a lowered level with no
    /// reason at all.</summary>
    public const string UnnamedReason =
        "geometry_not_confirmed — КОМПАС сообщил об успехе, но ни одна проверка не объясняет "
        + "понижение уровня: причина не названа";

    /// <summary>The reasons for the failed checks among <paramref name="confirming"/>, in the order the
    /// checks were declared. <paramref name="confirming"/> names exactly the checks the confirmation
    /// depends on, so a passing check never becomes a reason.</summary>
    public static IReadOnlyList<string> Of(IReadOnlyList<NamedCheck> checks, params string[] confirming)
    {
        var reasons = new List<string>();
        foreach (var name in confirming)
        {
            var check = checks.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
            if (check is not null && !check.Passed)
            {
                reasons.Add(Reason(check));
            }
        }

        if (reasons.Count == 0)
        {
            reasons.Add(UnnamedReason);
        }

        return reasons;
    }

    /// <summary>The one reason of a single failed check, carrying the check's own name and reading.</summary>
    public static string Reason(NamedCheck check) =>
        check.Name + "_not_confirmed — КОМПАС сообщил об успехе, но проверка «" + check.Name
        + "» не прошла: наблюдалось " + (check.Observed ?? "не читается")
        + (check.Expected is null ? string.Empty : ", ожидалось " + check.Expected) + ".";
}
