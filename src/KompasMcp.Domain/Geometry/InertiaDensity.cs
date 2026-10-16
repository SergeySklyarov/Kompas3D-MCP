using System.Globalization;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>Compares the density the inertia block was computed with against the density the CALLER stated
/// for the mass block, and names the divergence instead of smoothing it.</summary>
/// <remarks>DOC: <c>ksmassinertiaparam_r.html</c> — <c>r</c> is «Плотность материала»: the moments come from
/// the MATERIAL of the part, while <c>mass_kg</c> is arithmetic on the density the caller passed. The help
/// never makes the caller's density govern the integral, so when the two differ the response must say so.
/// INVARIANT: the tolerance is the read-back allowance the material block already uses — the material
/// density reaches this comparison through the same documented route and the same rounding.
/// History: docs/decisions/geometry.md#measure-density</remarks>
public static class InertiaDensity
{
    /// <summary>Aspect naming the divergence, or <c>null</c> when the two densities agree or either is
    /// unknown (an unknown density is named elsewhere, not here).</summary>
    public static string? MismatchAspect(double? materialDensityKgPerM3, double? requestedDensityKgPerM3)
    {
        if (materialDensityKgPerM3 is not double material || requestedDensityKgPerM3 is not double requested)
        {
            return null;
        }

        if (Math.Abs(material - requested) <= DensityUnits.ReadBackToleranceKgPerM3)
        {
            return null;
        }

        return "inertia_density_mismatch — моменты инерции посчитаны по плотности МАТЕРИАЛА детали "
            + material.ToString("R", CultureInfo.InvariantCulture) + " кг/м³, а запрошена "
            + requested.ToString("R", CultureInfo.InvariantCulture) + " кг/м³; mass_kg считается по "
            + "запрошенной, моменты инерции — нет";
    }
}
