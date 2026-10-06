namespace KompasMcp.Contracts;

/// <summary>Density arithmetic for the material of a part — block VM.</summary>
/// <remarks>DOC: <c>kspart_setmaterial.html</c> takes the density in <b>g/cm3</b> («плотность материала
/// (г/куб.см)»). DOC: the READ side has a documented route with the unit as an ARGUMENT —
/// <c>kspart_calcmassinertiaproperties.html</c> plus <c>ksmassinertiaparam.html</c> note 3: the length and
/// mass dimensions of everything the interface returns are set by <c>bitVector</c>, so <c>r</c>
/// («Плотность материала») at <c>ST_MIX_M|ST_MIX_KG</c> is kg/m3. MEASURED: the reading equals the written
/// kg/m3 at M|KG, scales with the length unit as its cube, and needs no rescaling by the server.
/// LIMIT: only the documented write-side conversion lives here; there is deliberately no g/cm3 → kg/m3
/// conversion, because that would be our own reinterpretation of an undocumented reading.
/// TEST: <c>VariableMaterialDomainTests</c> holds the write-side conversion and the read-back tolerance.
/// History: docs/decisions/variables-material.md#units-mci</remarks>
public static class DensityUnits
{
    private const double GramsPerCm3ToKgPerM3Factor = 1000.0;

    /// <summary>Unit the documented MCI read route publishes the density in, at <c>ST_MIX_M|ST_MIX_KG</c>.</summary>
    public const string DocumentedReadUnit = "kg/m3";

    /// <summary>Unit <c>kspart_getdensity.html</c> NAMES for the legacy <c>GetDensity()</c> diagnostic.</summary>
    public const string GetDensityPageUnit = "g/mm3";

    /// <summary>Tolerance for confirming a written density against the re-read one, in kg/m3.</summary>
    /// <remarks>INVARIANT: the re-read density comes from the documented M|KG route, so it is in the SAME
    /// unit as the request and the comparison is like-for-like. The value is a read-back allowance for the
    /// write through g/cm3, not a measurement tolerance.</remarks>
    public const double ReadBackToleranceKgPerM3 = 0.5;

    /// <summary>kg/m3 → g/cm3, the unit <c>SetMaterial</c> documents for its density argument.</summary>
    public static double KgPerM3ToGramsPerCm3(double densityKgPerM3) =>
        densityKgPerM3 / GramsPerCm3ToKgPerM3Factor;

    /// <summary>Mass of a body from a volume and a density the CALLER states.</summary>
    /// <remarks>INVARIANT: this is arithmetic on the caller's own density, NOT a reading of the document and
    /// NOT a substitute for one. The formula is <c>kg = mm3 * 1e-9 * kg/m3</c>.
    /// TEST: <c>VariableMaterialDomainTests</c> holds the mass arithmetic on the caller's density.</remarks>
    public static double MassKg(double volumeMm3, double densityKgPerM3) =>
        volumeMm3 * 1e-9 * densityKgPerM3;
}
