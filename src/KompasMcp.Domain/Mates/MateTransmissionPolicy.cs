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
}
