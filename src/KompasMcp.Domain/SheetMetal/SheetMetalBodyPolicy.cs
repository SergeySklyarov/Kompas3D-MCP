using KompasMcp.Contracts;

namespace KompasMcp.Domain.SheetMetal;

/// <summary>Which inputs a sheet-metal body accepts, and how its read-back is judged. Pure, no KOMPAS
/// types.</summary>
/// <remarks>INVARIANT: thickness and bend radius are refused before <c>Add()</c> when not positive —
/// MEASURED: the kernel accepts 0 and -1.5, answers <c>Update() = TRUE</c> and keeps a body with thickness 0.
/// INVARIANT: the unfold method is one of three values and carries exactly one matching field;
/// <c>bend_table</c> is not a value (no table, no type; the help does not describe the format).
/// INVARIANT: success is the read-back matching the request, never <c>Update() = true</c>.
/// History: docs/decisions/sheet-metal.md#body-tools</remarks>
public static class SheetMetalBodyPolicy
{
    /// <summary>Numeric tolerance of every read-back comparison.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>Unfold-length methods this volume implements, by name, with the kernel's number.</summary>
    public static readonly IReadOnlyList<(string Name, int Value)> UnfoldTypes = new[]
    {
        ("coefficient", 0),
        ("bend_value", 1),
        ("bend_reduction", 2),
    };

    /// <summary>Direction values <c>ksdirectiontypeenum.html</c> names, by name, with the kernel's
    /// number. <c>LIMIT:</c> only <c>normal</c> was measured on a body; the other three are accepted
    /// because the page the body points at names them, not because a run confirmed them.</summary>
    public static readonly IReadOnlyList<(string Name, int Value)> Directions = new[]
    {
        ("normal", 0),
        ("reverse", 1),
        ("both", 2),
        ("middle_plane", 3),
    };

    /// <summary>Why an input is refused. <see cref="None"/> — it is accepted.</summary>
    public enum Refusal
    {
        None,
        EmptySketch,
        ThicknessNotPositive,
        RadiusNotPositive,
        UnknownDirection,
        UnknownUnfoldType,
        MissingUnfoldValue,
        TwoUnfoldValues,
        NoEditFields,
        FieldOfAnotherUnfoldType,
        ValueNotFinite,
    }

    /// <summary>The kernel number of an unfold method, or <c>null</c> when the name is not one of the
    /// three. <c>bend_table</c> lands here on purpose: it is not a value of this contract.</summary>
    public static int? UnfoldTypeValue(string? name)
    {
        foreach (var entry in UnfoldTypes)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>The public name of an unfold method by the kernel's number; an unknown number is
    /// reported as the number, not invented.</summary>
    public static string UnfoldTypeName(int? value)
    {
        if (value is not { } number)
        {
            return string.Empty;
        }

        foreach (var entry in UnfoldTypes)
        {
            if (entry.Value == number)
            {
                return entry.Name;
            }
        }

        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The kernel number of a direction, or <c>null</c> for an unknown name.</summary>
    public static int? DirectionValue(string? name)
    {
        foreach (var entry in Directions)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>The field the method selects: 1 — coefficient, 2 — bend value, 3 — bend reduction,
    /// 0 — none set. Counted, not compared by name, so a request with two fields is visible.</summary>
    public static int UnfoldValueFields(double? coefficient, double? bendValue, double? reduction) =>
        (coefficient is null ? 0 : 1) + (bendValue is null ? 0 : 1) + (reduction is null ? 0 : 1);

    /// <summary>The field a method requires: <c>coefficient</c>→1, <c>bend_value</c>→2,
    /// <c>bend_reduction</c>→3. The names are matched positionally with <see cref="UnfoldTypes"/>.</summary>
    public static double? SelectedField(string? unfoldType, double? coefficient, double? bendValue,
        double? reduction) => unfoldType switch
    {
        "coefficient" => coefficient,
        "bend_value" => bendValue,
        "bend_reduction" => reduction,
        _ => null,
    };

    /// <summary>Whether the sketch has nothing to build a body from.
    /// <c>MEASURED:</c> a body created from an empty sketch answers with the counter +1 and readable
    /// properties but never reaches the saved file, and the kernel keeps no record to clean up — so the
    /// refusal has to happen before <c>Add()</c>. <c>null</c> — the server did not record this sketch's
    /// primitives (drawn by another client or reopened), which is "not checked", never "empty".</summary>
    public static bool SketchIsEmpty(int? recordedPrimitives) => recordedPrimitives == 0;

    /// <summary>The rule of a create request, decided before anything is written.</summary>
    public static Refusal CheckCreate(
        double thicknessMm, double radiusMm, string? direction, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction)
    {
        if (!double.IsFinite(thicknessMm) || thicknessMm <= 0d)
        {
            return Refusal.ThicknessNotPositive;
        }

        if (!double.IsFinite(radiusMm) || radiusMm <= 0d)
        {
            return Refusal.RadiusNotPositive;
        }

        if (DirectionValue(direction) is null)
        {
            return Refusal.UnknownDirection;
        }

        if (UnfoldTypeValue(unfoldType) is null)
        {
            return Refusal.UnknownUnfoldType;
        }

        var fields = UnfoldValueFields(coefficient, bendValue, reduction);
        if (fields == 0)
        {
            return Refusal.MissingUnfoldValue;
        }

        if (fields > 1)
        {
            return Refusal.TwoUnfoldValues;
        }

        // The one supplied field must be the one the method selects: a coefficient sent with
        // bend_value would be recorded by the kernel and never used.
        var selected = SelectedField(unfoldType, coefficient, bendValue, reduction);
        if (selected is null)
        {
            return Refusal.FieldOfAnotherUnfoldType;
        }

        return double.IsFinite(selected.Value) ? Refusal.None : Refusal.ValueNotFinite;
    }

    /// <summary>The rule of an edit request, decided before anything is written. The unfold method is
    /// read from the MODEL: a field of another method is refused by name.</summary>
    /// <param name="currentUnfoldType">Method the body holds now, by name.</param>
    public static Refusal CheckEdit(
        string? currentUnfoldType,
        double? thicknessMm, double? radiusMm, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction)
    {
        if (thicknessMm is not null && (!double.IsFinite(thicknessMm.Value) || thicknessMm.Value <= 0d))
        {
            return Refusal.ThicknessNotPositive;
        }

        if (radiusMm is not null && (!double.IsFinite(radiusMm.Value) || radiusMm.Value <= 0d))
        {
            return Refusal.RadiusNotPositive;
        }

        var hasValue = UnfoldValueFields(coefficient, bendValue, reduction) > 0;
        if (thicknessMm is null && radiusMm is null && unfoldType is null && !hasValue)
        {
            return Refusal.NoEditFields;
        }

        if (unfoldType is not null && UnfoldTypeValue(unfoldType) is null)
        {
            return Refusal.UnknownUnfoldType;
        }

        if (!hasValue)
        {
            return Refusal.None;
        }

        if (UnfoldValueFields(coefficient, bendValue, reduction) > 1)
        {
            return Refusal.TwoUnfoldValues;
        }

        // The method that will hold after the edit: the requested one, or the one already in the body.
        var target = unfoldType ?? currentUnfoldType;
        return SelectedField(target, coefficient, bendValue, reduction) is null
            ? Refusal.FieldOfAnotherUnfoldType
            : Refusal.None;
    }

    /// <summary>The field name a refusal points at, so the caller knows what to fix.</summary>
    public static string RefusalField(Refusal refusal) => refusal switch
    {
        Refusal.EmptySketch => "sketch_ref",
        Refusal.ThicknessNotPositive => "thickness_mm",
        Refusal.RadiusNotPositive => "bend_radius_mm",
        Refusal.UnknownDirection => "direction",
        Refusal.UnknownUnfoldType => "unfold_type",
        Refusal.MissingUnfoldValue => "bend_coefficient|bend_value|bend_reduction",
        Refusal.TwoUnfoldValues => "bend_coefficient|bend_value|bend_reduction",
        Refusal.NoEditFields => "thickness_mm|bend_radius_mm|unfold_type",
        Refusal.FieldOfAnotherUnfoldType => "bend_coefficient|bend_value|bend_reduction",
        _ => "value",
    };

    /// <summary>The requested fields that did not read back as asked; empty — the values are confirmed.
    /// A field that was not read counts as a mismatch: silence is never confirmation.</summary>
    public static IReadOnlyList<string> CreateMismatches(
        SheetMetalBodyDto? read, double thicknessMm, double radiusMm, string unfoldType,
        double? coefficient, double? bendValue, double? reduction)
    {
        var mismatched = new List<string>();
        if (read?.ThicknessMm is not { } thickness || Math.Abs(thickness - thicknessMm) > Tolerance)
        {
            mismatched.Add("thickness_mm");
        }

        if (read?.BendRadiusMm is not { } radius || Math.Abs(radius - radiusMm) > Tolerance)
        {
            mismatched.Add("bend_radius_mm");
        }

        if (!string.Equals(read?.UnfoldType, unfoldType, StringComparison.Ordinal))
        {
            mismatched.Add("unfold_type");
        }

        var requested = SelectedField(unfoldType, coefficient, bendValue, reduction);
        if (requested is { } value
            && (read?.UnfoldValue is not { } stored || Math.Abs(stored - value) > Tolerance))
        {
            mismatched.Add("unfold_value");
        }

        return mismatched;
    }

    /// <summary>The requested fields of an edit that did not read back as asked.</summary>
    public static IReadOnlyList<string> EditMismatches(
        SheetMetalBodyDto? read,
        double? thicknessMm, double? radiusMm, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction)
    {
        var mismatched = new List<string>();
        if (thicknessMm is { } thickness
            && (read?.ThicknessMm is not { } storedThickness
                || Math.Abs(storedThickness - thickness) > Tolerance))
        {
            mismatched.Add("thickness_mm");
        }

        if (radiusMm is { } radius
            && (read?.BendRadiusMm is not { } storedRadius || Math.Abs(storedRadius - radius) > Tolerance))
        {
            mismatched.Add("bend_radius_mm");
        }

        if (unfoldType is not null && !string.Equals(read?.UnfoldType, unfoldType, StringComparison.Ordinal))
        {
            mismatched.Add("unfold_type");
        }

        var requested = SelectedField(unfoldType ?? read?.UnfoldType, coefficient, bendValue, reduction);
        if (requested is { } value
            && (read?.UnfoldValue is not { } stored || Math.Abs(stored - value) > Tolerance))
        {
            mismatched.Add("unfold_value");
        }

        return mismatched;
    }

    /// <summary>Whether the edit asked for exactly the values the body already holds — the idempotent
    /// repeat the contract makes visible in the answer.</summary>
    public static bool IsRepeat(
        SheetMetalBodyDto? before,
        double? thicknessMm, double? radiusMm, string? unfoldType,
        double? coefficient, double? bendValue, double? reduction) =>
        before is not null && EditMismatches(before, thicknessMm, radiusMm, unfoldType,
            coefficient, bendValue, reduction).Count == 0;
}
