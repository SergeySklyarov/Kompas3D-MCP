namespace KompasMcp.Contracts;

/// <summary>The four kinds of driving sketch dimension <c>kompas_create_sketch_dimension</c> can place,
/// in ONE place, so the published schema and the adapter cannot drift apart.</summary>
/// <remarks>WHY A NAME LIST: a schema has to enumerate the accepted strings and the adapter has to switch
/// on the same set; held together here, a name added on one side only is a test-time disagreement.
/// DOC: each kind has its OWN documented collection on <c>ISymbols2DContainer</c> — <c>LineDimensions</c>,
/// <c>DiametralDimensions</c>, <c>RadialDimensions</c>, <c>AngleDimensions</c>
/// (<c>isymbols2dcontainer_linedimensions.html</c> and its siblings).
/// History: docs/decisions/adapter-sketch.md#sketch-dimension</remarks>
public static class SketchDimensionKinds
{
    /// <summary>A linear dimension on one segment. The DEFAULT: omitting <c>kind</c> reproduces the
    /// behaviour the tool had before the field existed.</summary>
    public const string Linear = "linear";

    /// <summary>A diametral dimension on a circle or an arc; its value is the DIAMETER.</summary>
    public const string Diametral = "diametral";

    /// <summary>A radial dimension on an arc or a circle; its value is the RADIUS.</summary>
    public const string Radial = "radial";

    /// <summary>An angular dimension between two segments; its value is the ANGLE in degrees.</summary>
    public const string Angular = "angular";

    /// <summary>The kind used when the field is omitted.</summary>
    public const string Default = Linear;

    /// <summary>The kinds this server can place, in the order the tool description names them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Linear, Diametral, Radial, Angular };

    public static bool IsDeclared(string name) => All.Contains(name, StringComparer.Ordinal);

    /// <summary>The unit of the value for each kind. The help says what the value MEANS per kind:
    /// <c>NominalValue</c> returns the diameter for a diametral dimension, the radius for a radial one
    /// and the angle for an angular one (measured, not assumed — see the API notes).</summary>
    public static string ValueUnit(string kind) =>
        string.Equals(kind, Angular, StringComparison.Ordinal) ? "deg" : "mm";
}
