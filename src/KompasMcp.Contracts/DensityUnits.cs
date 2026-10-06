namespace KompasMcp.Contracts;

/// <summary>Density arithmetic for the material of a part — block VM.</summary>
/// <remarks>DOC: <c>kspart_setmaterial.html</c> takes the density in <b>g/cm3</b> («плотность материала
/// (г/куб.см)»). MEASURED: <c>ksPart.GetDensity()</c> is documented as <b>g/mm3</b> but the installed build
/// returns the value in <b>g/cm3</b>, and <c>IMassInertiaParam7.Density</c> (documented g/mm3 too) returns
/// the same. No v24 source establishes g/cm3 for either getter, so the READ side has no documented
/// unit and no normalized density is published from it. LIMIT: only the documented write-side conversion
/// lives here; there is deliberately no g/cm3 → kg/m3 conversion, because that would be our own
/// reinterpretation of an undocumented reading.
/// TEST: <c>VariableMaterialDomainTests</c> holds the write-side conversion.
/// History: docs/decisions/variables-material.md#units</remarks>
public static class DensityUnits
{
    private const double GramsPerCm3ToKgPerM3Factor = 1000.0;

    /// <summary>kg/m3 → g/cm3, the unit <c>SetMaterial</c> documents for its density argument.</summary>
    public static double KgPerM3ToGramsPerCm3(double densityKgPerM3) =>
        densityKgPerM3 / GramsPerCm3ToKgPerM3Factor;

    /// <summary>Mass of a body from a volume and a density the CALLER states.</summary>
    /// <remarks>INVARIANT: this is arithmetic on the caller's own density, NOT a reading of the document and
    /// NOT a substitute for one. The formula is <c>kg = mm3 * 1e-9 * kg/m3</c>.
    /// TEST: <c>VariableMaterialDomainTests</c> holds 0.628 kg for 80000 mm3 at 7850 kg/m3.</remarks>
    public static double MassKg(double volumeMm3, double densityKgPerM3) =>
        volumeMm3 * 1e-9 * densityKgPerM3;
}
