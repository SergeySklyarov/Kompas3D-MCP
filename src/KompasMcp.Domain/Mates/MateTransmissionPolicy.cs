using KompasMcp.Contracts;

namespace KompasMcp.Domain.Mates;

/// <summary>Which inputs a «Механическая передача» accepts, and how its read-back is judged.
/// Pure, no KOMPAS types.</summary>
/// <remarks>INVARIANT: a ratio is written only as two finite numbers greater than zero — the mate keeps
/// a ratio, and zero or NaN would be accepted by the call and mean nothing.
/// MEASURED: the mate is confirmed by <c>Valid</c> on cylindrical faces and refused (<c>Valid=false</c>)
/// on constructive axes, so a refusal needs no separate code path.
/// History: docs/decisions/mates.md#mechanical-transmission</remarks>
public static class MateTransmissionPolicy
{
    /// <summary>Motions the contract accepts. <c>linear</c> is out of this volume: its object is a
    /// trajectory edge, which the face addressing of the mate domain does not carry.</summary>
    public static readonly string[] Motions = { "rotation" };

    /// <summary>Whether the motion name is one this volume implements.</summary>
    public static bool IsKnownMotion(string? name) => name is "rotation";

    /// <summary>Whether a ratio side can be written: a finite number greater than zero.</summary>
    public static bool IsUsableScale(double? value) => value is { } scale && double.IsFinite(scale) && scale > 0d;

    /// <summary>Whether the ratio read back from the model equals the requested one.
    /// <c>null</c> — one of the sides was not read, which is "not confirmed", never "equal".</summary>
    public static bool? RatioMatches(double? scale1, double? scale2, double requested1, double requested2)
    {
        if (scale1 is null || scale2 is null)
        {
            return null;
        }

        var tolerance = 1e-6;
        return Math.Abs(scale1.Value - requested1) <= tolerance
            && Math.Abs(scale2.Value - requested2) <= tolerance;
    }

    /// <summary>Why an edit of an existing mate cannot be applied. <see cref="None"/> — it can.</summary>
    /// <remarks>INVARIANT: the field group follows the mate type read from the MODEL: a parameter is
    /// written only into a parametric mate, a ratio only into a transmission, and a ratio is a pair.
    /// History: docs/decisions/mates.md#transmission-edit</remarks>
    public enum EditRefusal
    {
        None,
        ParamValueOnTransmission,
        TransmissionFieldOnParamEdit,
        IncompleteRatio,
        RatioOutOfRule,
        NoEditFields,
    }

    /// <summary>The field rule of an edit, decided before anything is written.</summary>
    public static EditRefusal CheckEdit(
        bool isTransmission, double? paramValue,
        double? scale1, double? scale2, bool? direction1, bool? direction2)
    {
        var hasRatio = scale1 is not null || scale2 is not null;
        var hasDirection = direction1 is not null || direction2 is not null;

        if (!isTransmission)
        {
            if (hasRatio || hasDirection)
            {
                return EditRefusal.TransmissionFieldOnParamEdit;
            }

            return paramValue is null ? EditRefusal.NoEditFields : EditRefusal.None;
        }

        if (paramValue is not null)
        {
            return EditRefusal.ParamValueOnTransmission;
        }

        if (hasRatio && (scale1 is null || scale2 is null))
        {
            return EditRefusal.IncompleteRatio;
        }

        if (hasRatio && (!IsUsableScale(scale1) || !IsUsableScale(scale2)))
        {
            return EditRefusal.RatioOutOfRule;
        }

        return hasRatio || hasDirection ? EditRefusal.None : EditRefusal.NoEditFields;
    }

    /// <summary>The requested fields that did not read back as asked; empty — the edit is confirmed.
    /// A field that was not read counts as a mismatch: silence is never confirmation.</summary>
    public static IReadOnlyList<string> EditMismatches(
        MateTransmissionStateDto? read,
        double? scale1, double? scale2, bool? direction1, bool? direction2)
    {
        var mismatched = new List<string>();
        if ((scale1 is not null || scale2 is not null)
            && RatioMatches(read?.Scale1, read?.Scale2, scale1!.Value, scale2!.Value) != true)
        {
            mismatched.Add("scale");
        }

        if (direction1 is not null && read?.Direction1 != direction1)
        {
            mismatched.Add("direction1");
        }

        if (direction2 is not null && read?.Direction2 != direction2)
        {
            mismatched.Add("direction2");
        }

        return mismatched;
    }
}
