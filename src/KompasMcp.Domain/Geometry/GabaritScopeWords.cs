namespace KompasMcp.Domain.Geometry;

/// <summary>What each published gabarit box covers, in words, including the one thing the route does NOT
/// promise: an exact bound of a TRIMMED analytic surface.</summary>
/// <remarks>DOC: <c>kspart_getgabarit.html</c> names the modes («full — TRUE — полный, FALSE — только тела;
/// customizable — TRUE — с учетом настроек видимости, FALSE — без учета; при full == false
/// игнорируется»), not the accuracy of the box.
/// MEASURED: a torus cut at 0° and 180° reported a body box of 23,0000 mm on X while the exported mesh
/// spans 22,98238 mm, i.e. the box is CONSERVATIVE for the trimmed surface; its help page does not say
/// otherwise. The box is therefore published with this limit attached: it names the extent of the method,
/// and a matching box is not proof that the geometry matched.
/// History: docs/decisions/adapter-core.md#gabarit-scope</remarks>
public static class GabaritScopeWords
{
    /// <summary>Whole document-model: bodies, sketches and auxiliary geometry.</summary>
    public const string Full =
        "весь документ-модель: сплошные тела, эскизы и вспомогательная геометрия "
        + "(ksPart.GetGabarit(full=true)); у обрезанных аналитических поверхностей габарит может быть больше "
        + "точной оболочки — это габарит метода, а не доказательство неизменности";

    /// <summary>Solid bodies only — the box to compare with <c>list_bodies</c> and <c>measure</c>.</summary>
    public const string Bodies =
        "только сплошные тела, без эскизов и вспомогательной геометрии (ksPart.GetGabarit(full=false)); "
        + "у обрезанных аналитических поверхностей габарит может быть БОЛЬШЕ точной оболочки (измерено: "
        + "23,0000 мм против сетки 22,98238 мм), поэтому совпадение габаритов не доказывает совпадения "
        + "геометрии";

    /// <summary>The limit itself, for the answers that USE a box as evidence of a change or its absence.</summary>
    public const string ConservationNote =
        "gabarit_conservative — габарит читается ksPart.GetGabarit и у обрезанных аналитических поверхностей "
        + "может быть больше точной оболочки (измерено: тело 23,0000 мм против сетки 22,98238 мм), поэтому "
        + "неподвижный или совпавший габарит не доказывает, что геометрия не изменилась";
}
