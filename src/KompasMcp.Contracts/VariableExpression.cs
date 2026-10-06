using System.Globalization;

namespace KompasMcp.Contracts;

/// <summary>Tells a constant assignment method of a variable apart from a formula.</summary>
/// <remarks>DOC: <c>1862_175_1_sozd_peremen.html</c> names the assignment methods a variable may carry — «число
/// или константу», «выражение для вычисления значения», «ссылку на другую переменную». A constant IS the
/// value, so overwriting it with a new number is exactly what the caller asked for; a formula or a reference
/// COMPUTES the value from something else, and overwriting it would destroy that computation.
/// MEASURED: the kernel gives every user variable a non-empty <c>Expression</c> — a freshly added one carries
/// the constant it was added with, and clearing it is not a supported state (the variable stops resolving by
/// name) — so a rule that refused EVERY non-empty expression would make the documented <c>value</c> mode
/// unusable on any real document.
/// LIMIT: only a string that parses as a whole number is treated as a constant; anything else (a formula, a
/// reference, an unparsable string) is treated as governing and refuses the value write.
/// TEST: <c>VariableMaterialDomainTests</c> holds the classification.
/// History: docs/decisions/variables-material.md#set-variable</remarks>
public static class VariableExpression
{
    /// <summary>True when the expression is a plain number and therefore the variable's value itself.</summary>
    public static bool IsPlainConstant(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            // "not read" and "empty" both arrive here as "no constant to overwrite", and the caller keeps its
            // own handling for an unread field: this helper only classifies a string it was given.
            return false;
        }

        return double.TryParse(
            expression.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out _);
    }
}
