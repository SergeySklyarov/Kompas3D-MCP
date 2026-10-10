namespace KompasMcp.Domain.Files;

/// <summary>The parameters an STL write needs, derived from the caller's request.</summary>
/// <remarks>DOC: <c>d3formatconvtype.html</c> — «format_STL 6 формат STL»;
/// <c>ksadditionformatparam_formatbinary.html</c> — «formatBinary — признак, определяющий тип файла
/// (двоичный или текстовый)»; <c>ksadditionformatparam_length.html</c> and
/// <c>ksadditionformatparam_angle.html</c> give the tessellation accuracy.
/// INVARIANT: <c>binary</c> maps onto the FILE KIND, never onto the format code — the format is STL in
/// both cases, and the two are separate members of the parameter object.
/// History: docs/decisions/geometry.md#stl</remarks>
public readonly record struct StlExportPlan(int FormatCode, bool FormatBinary, double LengthMm, double AngleDeg)
{
    /// <summary>D3FormatConvType.format_STL.</summary>
    public const int FormatStl = 6;

    /// <summary>How the triangle count is obtained for this file kind.</summary>
    public const string BinaryCountSource = "binary_header";

    /// <summary>Text-STL count source.</summary>
    public const string TextCountSource = "text_facet_lines";

    /// <summary>Name of the format on the wire.</summary>
    public const string FormatName = "stl";

    /// <summary>Builds the write parameters, refusing a non-positive or non-finite accuracy.</summary>
    /// <remarks>INVARIANT: the accuracy is an ARGUMENT of the call; the server never substitutes a
    /// default. A value the kernel would silently clamp or ignore is refused BEFORE COM.
    /// History: docs/decisions/geometry.md#stl</remarks>
    public static StlExportPlan For(bool binary, double maxEdgeLengthMm, double normalAngleDeg)
    {
        if (!double.IsFinite(maxEdgeLengthMm) || maxEdgeLengthMm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEdgeLengthMm), maxEdgeLengthMm,
                "max_edge_length_mm должен быть конечным положительным числом.");
        }

        if (!double.IsFinite(normalAngleDeg) || normalAngleDeg <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalAngleDeg), normalAngleDeg,
                "normal_angle_deg должен быть конечным положительным числом.");
        }

        return new StlExportPlan(FormatStl, binary, maxEdgeLengthMm, normalAngleDeg);
    }

    public string CountSource => FormatBinary ? BinaryCountSource : TextCountSource;
}
