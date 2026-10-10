using System.Globalization;

namespace KompasMcp.Contracts;

/// <summary>How addressing a feature parameter by exact name came out.</summary>
/// <remarks>INVARIANT: the address is the exact NAME, never the value and never the position — two
/// parameters with an equal value are indistinguishable by value. Zero matches and several matches are both
/// refusals, so an ambiguous address can never resolve to "the first one".
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public enum ParameterAddressVerdict
{
    /// <summary>Exactly one parameter carries the name.</summary>
    Found,

    /// <summary>No parameter carries the name.</summary>
    NotFound,

    /// <summary>Several parameters carry the name: the address is ambiguous.</summary>
    Ambiguous,
}

/// <summary>The verdict of <see cref="VariableBindRules.SelectParameter"/> plus the names it saw.</summary>
/// <remarks><see cref="Names"/> is carried so a refusal can LIST what is available instead of saying
/// "not found" about a collection the caller cannot see.</remarks>
public readonly record struct ParameterAddressResult(
    ParameterAddressVerdict Verdict,
    int Index,
    IReadOnlyList<string?> Names);

/// <summary>The pure rules of the G3 block: parameter addressing by exact name and the constant expression
/// a created variable carries when no expression is given.</summary>
/// <remarks>TEST: kept pure and in Contracts so the addressing rule is testable WITHOUT KOMPAS.
/// History: docs/decisions/variables-material.md#g3-route</remarks>
public static class VariableBindRules
{
    /// <summary>Find the ONE parameter whose name equals <paramref name="wanted"/> exactly.</summary>
    /// <remarks>INVARIANT: an unread name (null) never matches; a match count other than one is never
    /// resolved by position.</remarks>
    public static ParameterAddressResult SelectParameter(IReadOnlyList<string?> names, string wanted)
    {
        var index = -1;
        var matches = 0;
        for (var i = 0; i < names.Count; i++)
        {
            if (!string.Equals(names[i], wanted, StringComparison.Ordinal))
            {
                continue;
            }

            matches++;
            index = i;
        }

        return new ParameterAddressResult(
            matches == 1 ? ParameterAddressVerdict.Found
            : matches == 0 ? ParameterAddressVerdict.NotFound
            : ParameterAddressVerdict.Ambiguous,
            matches == 1 ? index : -1,
            names);
    }

    /// <summary>The constant expression a created variable carries when the caller gave none.</summary>
    /// <remarks>INVARIANT: an empty expression is not a supported kernel state (MEASURED: the write reports
    /// success, the old expression stays, the variable leaves the collection), so a created variable always
    /// gets the documented «число или константа» form. The number is written round-trip ("R") so the
    /// expression reads back exactly what the caller asked for.</remarks>
    public static string ConstantExpression(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Whether two doubles are the same number within the profile's relative tolerance.</summary>
    /// <remarks>INVARIANT: the comparison is numeric, not textual — "10" and "10.0" are the same value.
    /// The relative floor keeps large values comparable without an absolute epsilon that would be wrong for
    /// a dimension of metres.</remarks>
    public static bool SameValue(double left, double right)
    {
        var scale = Math.Max(1d, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1e-9 * scale;
    }
}
