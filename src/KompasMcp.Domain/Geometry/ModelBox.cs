namespace KompasMcp.Domain.Geometry;

/// <summary>Decides whether the box KOMPAS returned is a MEASUREMENT or its "no box" service value.</summary>
/// <remarks>INVARIANT: one rule answers "the gabarit is not determined" for the published
/// <c>gabarit</c> field and for the <c>fingerprint</c>, so the two cannot disagree about a model with no
/// bodies. MEASURED: <c>ksPart.GetGabarit</c> on a fresh part answers true and returns service corners
/// around ±2e300; the fingerprint printed them in full (hundreds of digits) while <c>gabarit</c> said
/// null. The limit is a magnitude no model at millimetre scale reaches.
/// History: docs/decisions/adapter-core.md#gabarit-scope</remarks>
public static class ModelBox
{
    /// <summary>The largest coordinate a real model box can carry, in millimetres (a thousand
    /// kilometres). Anything at or beyond it is the kernel's service value, not a measurement.</summary>
    public const double ServiceMagnitude = 1e9;

    /// <summary>Corner values of the box, normalised so that min ≤ max on every axis (the kernel does not
    /// promise which corner is which). False means "no box": a service value the caller must not read as a
    /// measurement.</summary>
    public static bool TryNormalise(
        double x1, double y1, double z1, double x2, double y2, double z2,
        out double[] min, out double[] max)
    {
        min = Array.Empty<double>();
        max = Array.Empty<double>();
        var values = new[] { x1, y1, z1, x2, y2, z2 };
        foreach (var value in values)
        {
            if (!double.IsFinite(value) || Math.Abs(value) >= ServiceMagnitude)
            {
                return false;
            }
        }

        min = new[] { Math.Min(x1, x2), Math.Min(y1, y2), Math.Min(z1, z2) };
        max = new[] { Math.Max(x1, x2), Math.Max(y1, y2), Math.Max(z1, z2) };
        return true;
    }
}
