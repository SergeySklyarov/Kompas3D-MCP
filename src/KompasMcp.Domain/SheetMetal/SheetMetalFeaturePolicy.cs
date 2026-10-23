using KompasMcp.Contracts;

namespace KompasMcp.Domain.SheetMetal;

/// <summary>Which inputs a bend and a hole accept, and how their read-back is judged. Pure, no KOMPAS
/// types.</summary>
/// <remarks>INVARIANT: a value is refused before <c>Add()</c> only when the help page or a measurement says
/// the kernel would keep a record this product cannot delete; the refusal names the field.
/// <c>LIMIT:</c> the angle, radius and length pages name the type and nothing else, so only positivity and
/// finiteness are checked — a bound nobody documented is not invented here.
/// History: docs/decisions/sheet-metal.md#feature-tools</remarks>
public static class SheetMetalFeaturePolicy
{
    /// <summary>Numeric tolerance of every read-back comparison.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>Straight-part length methods, by name, with the kernel's number
    /// (<c>ksbendlengthtypeenum.html</c>).</summary>
    public static readonly IReadOnlyList<(string Name, int Value)> LengthTypes = new[]
    {
        ("continue", 0),
        ("contour", 1),
        ("touch", 2),
        ("contour_internal", 3),
        ("tangent_internal", 4),
    };

    /// <summary>Hole build types this volume implements (<c>ksholecuttypeenum.html</c>).
    /// <c>LIMIT:</c> <c>up_to_surface</c> (2) is left out: the page makes <c>Depth</c> available only with
    /// «на глубину» and the depth of that type is set by <c>DepthObject</c>, a face the caller would have to
    /// name.</summary>
    public static readonly IReadOnlyList<(string Name, int Value)> CutTypes = new[]
    {
        ("by_width", 0),
        ("by_depth", 1),
    };

    /// <summary>Why an input is refused. <see cref="None"/> — it is accepted.</summary>
    public enum Refusal
    {
        None,
        AngleNotPositive,
        RadiusNotPositive,
        UnknownLengthType,
        LengthNotPositive,
        DiameterNotPositive,
        UnknownCutType,
        MissingDepth,
        DepthWithoutItsCutType,
        DepthNotPositive,
        ValueNotFinite,
    }

    /// <summary>The kernel number of a length method, or <c>null</c> when the name is not one of the
    /// five.</summary>
    public static int? LengthTypeValue(string? name)
    {
        foreach (var entry in LengthTypes)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>The public name of a length method by the kernel's number.</summary>
    public static string? LengthTypeName(int? value)
    {
        foreach (var entry in LengthTypes)
        {
            if (entry.Value == value)
            {
                return entry.Name;
            }
        }

        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The kernel number of a hole build type, or <c>null</c> for an unknown name.</summary>
    public static int? CutTypeValue(string? name)
    {
        foreach (var entry in CutTypes)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>The public name of a hole build type by the kernel's number.</summary>
    public static string? CutTypeName(int? value)
    {
        foreach (var entry in CutTypes)
        {
            if (entry.Value == value)
            {
                return entry.Name;
            }
        }

        return value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The rule of a bend request, decided before anything is written.</summary>
    public static Refusal CheckBend(double angleDeg, double? radiusMm, string? lengthType, double? lengthMm)
    {
        if (!double.IsFinite(angleDeg))
        {
            return Refusal.ValueNotFinite;
        }

        if (angleDeg <= 0d)
        {
            return Refusal.AngleNotPositive;
        }

        if (radiusMm is { } radius && (!double.IsFinite(radius) || radius <= 0d))
        {
            return radius <= 0d ? Refusal.RadiusNotPositive : Refusal.ValueNotFinite;
        }

        if (lengthType is not null && LengthTypeValue(lengthType) is null)
        {
            return Refusal.UnknownLengthType;
        }

        if (lengthMm is { } length && (!double.IsFinite(length) || length <= 0d))
        {
            return length <= 0d ? Refusal.LengthNotPositive : Refusal.ValueNotFinite;
        }

        return Refusal.None;
    }

    /// <summary>The rule of a hole request, decided before anything is written. The depth belongs to the
    /// «на глубину» type only, so it is refused with the other type by name.</summary>
    public static Refusal CheckHole(double diameterMm, string? cutType, double? depthMm)
    {
        if (!double.IsFinite(diameterMm))
        {
            return Refusal.ValueNotFinite;
        }

        if (diameterMm <= 0d)
        {
            return Refusal.DiameterNotPositive;
        }

        if (cutType is not null && CutTypeValue(cutType) is null)
        {
            return Refusal.UnknownCutType;
        }

        var byDepth = string.Equals(cutType, "by_depth", StringComparison.Ordinal);
        if (byDepth && depthMm is null)
        {
            return Refusal.MissingDepth;
        }

        if (!byDepth && depthMm is not null)
        {
            return Refusal.DepthWithoutItsCutType;
        }

        if (depthMm is { } depth && (!double.IsFinite(depth) || depth <= 0d))
        {
            return depth <= 0d ? Refusal.DepthNotPositive : Refusal.ValueNotFinite;
        }

        return Refusal.None;
    }

    /// <summary>The field name a refusal points at.</summary>
    public static string RefusalField(Refusal refusal) => refusal switch
    {
        Refusal.AngleNotPositive => "angle_deg",
        Refusal.RadiusNotPositive => "radius_mm",
        Refusal.UnknownLengthType => "length_type",
        Refusal.LengthNotPositive => "length_mm",
        Refusal.DiameterNotPositive => "diameter_mm",
        Refusal.UnknownCutType => "cut_type",
        Refusal.MissingDepth => "depth_mm",
        Refusal.DepthWithoutItsCutType => "depth_mm",
        Refusal.DepthNotPositive => "depth_mm",
        _ => "value",
    };

    /// <summary>The requested bend fields that did not read back as asked; empty — confirmed. A field that
    /// was not read counts as a mismatch: silence is never confirmation.</summary>
    public static IReadOnlyList<string> BendMismatches(
        SheetMetalBendDto? read, double angleDeg, bool? direction, bool? internalRadius, double? radiusMm,
        string? lengthType, double? lengthMm)
    {
        var mismatched = new List<string>();
        if (read?.AngleDeg is not { } angle || Math.Abs(angle - angleDeg) > Tolerance)
        {
            mismatched.Add("angle_deg");
        }

        if (direction is { } wantedDirection && read?.Direction != wantedDirection)
        {
            mismatched.Add("direction");
        }

        if (internalRadius is { } wantedRadius && read?.InternalRadius != wantedRadius)
        {
            mismatched.Add("internal_radius");
        }

        if (radiusMm is { } wantedValue
            && (read?.RadiusMm is not { } stored || Math.Abs(stored - wantedValue) > Tolerance))
        {
            mismatched.Add("radius_mm");
        }

        if (lengthType is not null && !string.Equals(read?.LengthType, lengthType, StringComparison.Ordinal))
        {
            mismatched.Add("length_type");
        }

        if (lengthMm is { } wantedLength
            && (read?.LengthMm is not { } storedLength || Math.Abs(storedLength - wantedLength) > Tolerance))
        {
            mismatched.Add("length_mm");
        }

        return mismatched;
    }

    /// <summary>The requested hole fields that did not read back as asked.</summary>
    public static IReadOnlyList<string> HoleMismatches(
        SheetMetalHoleDto? read, double xMm, double yMm, double diameterMm, string? cutType, double? depthMm)
    {
        var mismatched = new List<string>();
        if (read?.XMm is not { } x || Math.Abs(x - xMm) > Tolerance)
        {
            mismatched.Add("x_mm");
        }

        if (read?.YMm is not { } y || Math.Abs(y - yMm) > Tolerance)
        {
            mismatched.Add("y_mm");
        }

        if (read?.DiameterMm is not { } diameter || Math.Abs(diameter - diameterMm) > Tolerance)
        {
            mismatched.Add("diameter_mm");
        }

        if (cutType is not null && !string.Equals(read?.CutType, cutType, StringComparison.Ordinal))
        {
            mismatched.Add("cut_type");
        }

        if (depthMm is { } wantedDepth
            && (read?.DepthMm is not { } storedDepth || Math.Abs(storedDepth - wantedDepth) > Tolerance))
        {
            mismatched.Add("depth_mm");
        }

        return mismatched;
    }
}
