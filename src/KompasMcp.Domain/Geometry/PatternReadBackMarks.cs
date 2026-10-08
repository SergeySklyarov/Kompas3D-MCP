using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The rule that ties a FAILED parameter read-back to the mark the caller sees.</summary>
/// <remarks>INVARIANT: creation and editing of a pattern answer the same way. The edit path puts
/// <c>parameter_not_read_back</c> first in the unverified aspects when a <c>read_back_*</c> check fails;
/// creation carried the same check but no mark, so a substituted value looked like a plain success.
/// <para>INVARIANT: the mark is added FIRST and only on a failed read-back — a mark that is always
/// present names nothing. The decision is a pure function of the checks, so it is exercised with a
/// SUBSTITUTED read result rather than a live КОМПАС.</para>
/// History: docs/decisions/adapter-core.md#pattern-read-back-mark</remarks>
public static class PatternReadBackMarks
{
    /// <summary>The wording shared by both paths; one string, so the two cannot drift apart.</summary>
    public const string ParameterNotReadBack =
        "parameter_not_read_back — запись принята сеттером, но модель отдаёт другое значение";

    /// <summary>True when any <c>read_back_*</c> check failed.</summary>
    public static bool AnyReadBackFailed(IEnumerable<NamedCheck> checks) =>
        checks.Any(c => c.Name.StartsWith("read_back_", StringComparison.Ordinal) && !c.Passed);

    /// <summary>Put the mark FIRST in <paramref name="unverified"/> when a read-back failed.</summary>
    public static void MarkUnreadBack(IList<string> unverified, IEnumerable<NamedCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(unverified);
        ArgumentNullException.ThrowIfNull(checks);

        if (AnyReadBackFailed(checks))
        {
            unverified.Insert(0, ParameterNotReadBack);
        }
    }
}
