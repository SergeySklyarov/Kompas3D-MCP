namespace KompasMcp.Domain.Imaging;

/// <summary>Outcome of computing <c>extScale</c> from a probe render: the scale to apply, or the NAMED
/// reason it cannot be computed.</summary>
public sealed record RasterSizingOutcome(double? Scale, string? RefusalReason, bool Clamped, string? Note);

/// <summary>Which route produced the applied size, as a machine-readable token.</summary>
public static class RasterSizing
{
    /// <summary>The caller passed <c>resolution</c> and/or <c>scale</c>: the server touches nothing.</summary>
    public const string Explicit = "explicit";

    /// <summary>The server took a probe render and computed the scale from it.</summary>
    public const string AutoTwoPass = "auto_two_pass";

    /// <summary>Scale computed from a probe render. PURE: no COM, no state.</summary>
    /// <remarks>INVARIANT: an unread or non-positive probe long side is a REFUSAL with a named reason,
    /// never a division. INVARIANT: the result is clamped to the MEASURED range the kernel accepts
    /// (<see cref="RasterLimits.MinAutoScale"/>..<see cref="RasterLimits.MaxAutoScale"/>) and a clamp is
    /// NAMED. MEASURED: the long side is strictly proportional to <c>(resolution / 120) * scale</c> on
    /// every size and both views, which is why one probe is enough.
    /// History: docs/decisions/contracts.md#raster-sizing</remarks>
    public static RasterSizingOutcome FromProbe(int targetLongSidePx, int? probeLongSidePx)
    {
        if (probeLongSidePx is not int probe || probe <= 0)
        {
            return new RasterSizingOutcome(
                null,
                "probe_long_side_unread — пробный снимок не дал длинной стороны (габарит из заголовка "
                + "не прочитан), поэтому масштаб вычислить не из чего; передайте resolution или scale явно",
                false,
                null);
        }

        var raw = (double)targetLongSidePx / probe;
        var clamped = Math.Clamp(raw, RasterLimits.MinAutoScale, RasterLimits.MaxAutoScale);
        var wasClamped = clamped != raw;
        var note = wasClamped
            ? $"scale_clamped_to_measured_limit — вычисленный масштаб {raw:0.######} вне пределов, "
              + $"которые ядро принимает ({RasterLimits.MinAutoScale}..{RasterLimits.MaxAutoScale}); "
              + $"применён {clamped:0.######}"
            : null;
        return new RasterSizingOutcome(clamped, null, wasClamped, note);
    }
}
