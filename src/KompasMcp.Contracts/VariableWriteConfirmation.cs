namespace KompasMcp.Contracts;

/// <summary>Whether one mandatory field of a variable was actually READ, kept apart from its value.</summary>
/// <remarks>INVARIANT: "the field was read and came back empty" and "the field was not read at all" are two
/// different facts and must not collapse into one nullable value. A null that stands for both is how an
/// unread Expression passes for "no expression" and lets a numeric write destroy a formula.
/// History: docs/decisions/variables-material.md#set-variable</remarks>
public enum VariableFieldState
{
    NotRead,
    Read,
}

/// <summary>The measured facts a write confirmation is decided from — no KOMPAS object, only readings.</summary>
/// <remarks>INVARIANT: every field is taken from a FRESHLY FETCHED collection after the write, and the states
/// say whether each mandatory field was read at all. <see cref="ApplyResult"/> is the outcode of the
/// documented apply call (<c>ksDocument3D.RebuildDocument</c> — MEASURED to be the call that carries the
/// variable into the geometry; <c>ksPart.RebuildModel</c> does not), reported as such and never as proof on
/// its own. History: docs/decisions/variables-material.md#set-variable</remarks>
public sealed record VariableWriteFacts
{
    /// <summary><see cref="VariableWriteConfirmation.ModeValue"/> or
    /// <see cref="VariableWriteConfirmation.ModeExpression"/>.</summary>
    public required string Mode { get; init; }

    public double? RequestedValue { get; init; }

    public string? RequestedExpression { get; init; }

    public required VariableFieldState ExpressionAfterState { get; init; }

    public string? ExpressionAfter { get; init; }

    public required VariableFieldState ValueAfterState { get; init; }

    public double? ValueAfter { get; init; }

    /// <summary>Outcode of the documented apply call; null when it was not returned.</summary>
    public bool? ApplyResult { get; init; }

    /// <summary>Tolerance on the value comparison, in the variable's own dimension.</summary>
    public double ValueTolerance { get; init; } = VariableWriteConfirmation.DefaultValueTolerance;
}

/// <summary>Result of the confirmation rule: a verdict plus the named reasons it did not hold.</summary>
public sealed record VariableWriteDecision(bool Confirmed, IReadOnlyList<string> Reasons);

/// <summary>Decides whether a variable write is CONFIRMED by what was actually read back afterwards.</summary>
/// <remarks>INVARIANT: existence of the object is not confirmation. For <c>value</c> the re-read number must
/// match the requested one within tolerance; for <c>expression</c> the expression must have been read back
/// unchanged AND the computed value must have been read. The apply outcode must not be false.
/// LIMIT: the computed value is only confirmed as READ — the server never evaluates an expression itself.
/// TEST: <c>VariableMaterialDomainTests</c> holds the rule table (stale value, unread field, failed apply).
/// History: docs/decisions/variables-material.md#set-variable</remarks>
public static class VariableWriteConfirmation
{
    public const string ModeValue = "value";
    public const string ModeExpression = "expression";

    /// <summary>Tolerance in the variable's own dimension: absolute, small enough to catch a stale value and
    /// wide enough to survive a round trip through a double the kernel stores.</summary>
    public const double DefaultValueTolerance = 1e-6;

    public static VariableWriteDecision Decide(VariableWriteFacts facts)
    {
        var reasons = new List<string>();

        // A false apply outcode says the model did not take the change; a null says it was not reported.
        // Neither can be turned into a confirmation, so both are named here rather than smoothed over.
        if (facts.ApplyResult != true)
        {
            reasons.Add(facts.ApplyResult is null ? "apply_not_reported" : "apply_returned_false");
        }

        if (facts.Mode == ModeValue)
        {
            if (facts.ValueAfterState != VariableFieldState.Read || facts.ValueAfter is null)
            {
                reasons.Add("value_not_read");
            }
            else if (facts.RequestedValue is null
                     || Math.Abs(facts.ValueAfter.Value - facts.RequestedValue.Value) > facts.ValueTolerance)
            {
                reasons.Add("value_mismatch");
            }
        }
        else
        {
            if (facts.ExpressionAfterState != VariableFieldState.Read)
            {
                reasons.Add("expression_not_read");
            }
            else if (!string.Equals(facts.ExpressionAfter, facts.RequestedExpression, StringComparison.Ordinal))
            {
                reasons.Add("expression_mismatch");
            }

            if (facts.ValueAfterState != VariableFieldState.Read || facts.ValueAfter is null)
            {
                reasons.Add("computed_value_not_read");
            }
        }

        return new VariableWriteDecision(reasons.Count == 0, reasons);
    }
}
