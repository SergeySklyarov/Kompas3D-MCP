using System.Globalization;

namespace KompasMcp.Domain.Drawings;

/// <summary>The arithmetic between a view's scale and a dimension's numbers: the radius the kernel
/// stores in SHEET mm, the number the dimension shows, and the comparison of a re-read dimension
/// with the request.</summary>
/// <remarks>MEASURED: a view dimension's anchor points are in VIEW mm and the kernel scales them, but
/// <c>Radius</c> is taken AS IS in SHEET mm, so the model radius must be multiplied by the view scale
/// before it is written. INVARIANT: only numbers go in, so a substituted reading exercises every
/// branch. History: docs/decisions/drawings.md#drawing-dimensions-scale</remarks>
public static class ViewDimensionScale
{
    /// <summary>A view scale counts as "one to one" when it differs from 1 by less than this.</summary>
    public const double ScaleTolerance = 1e-9d;

    /// <summary>How far a re-read value may sit from the requested one and still be that value.</summary>
    public const double ReadTolerance = 1e-6d;

    /// <summary>The radius to WRITE: the model radius in sheet mm, the way the kernel takes it.</summary>
    public static double SheetRadius(double modelRadiusMm, double viewScale) => modelRadiusMm * viewScale;

    /// <summary>The model radius a re-read sheet radius stands for; the sheet radius itself when the
    /// scale is not a positive number, because then there is nothing to divide by.</summary>
    public static double ModelRadius(double sheetRadiusMm, double viewScale) =>
        viewScale > 0d ? sheetRadiusMm / viewScale : sheetRadiusMm;

    /// <summary>The number the dimension shows: the radius for a radial one, its double for a
    /// diametral one — the kernel derives the number from the SHEET radius.</summary>
    public static double Nominal(double modelRadiusMm, bool isRadial) =>
        isRadial ? modelRadiusMm : modelRadiusMm * 2d;

    /// <summary>Whether the shown number has to be written explicitly: at 1:1 the kernel's own number
    /// is already right, at any other scale it would show the sheet radius instead.</summary>
    public static bool NominalMustBeWritten(double viewScale) => Math.Abs(viewScale - 1d) > ScaleTolerance;

    /// <summary>A dimension number parsed from its caption text, which the kernel writes with the
    /// locale's decimal comma; null when the text is absent or is not a plain number.</summary>
    public static double? ParseNumber(string? text) =>
        text is not null
        && double.TryParse(text.Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    /// <summary>Whether a re-read value agrees with the requested one. The band is relative to the
    /// requested value, with an absolute floor for values at or near zero.</summary>
    public static bool Near(double read, double requested) =>
        Math.Abs(read - requested) <= Math.Max(ReadTolerance, Math.Abs(requested) * ReadTolerance);

    /// <summary>Whether a re-read point agrees with the requested one; false when nothing was read.</summary>
    public static bool NearPair(IReadOnlyList<double>? read, IReadOnlyList<double> want) =>
        read is { Count: >= 2 } && Near(read[0], want[0]) && Near(read[1], want[1]);

    /// <summary>Every way a re-read radial/diametral dimension disagrees with the request, in the
    /// client's words; an empty list means the dimension is what was asked for.</summary>
    /// <remarks>INVARIANT: a re-read centre, radius or number that disagrees is a FAILED creation,
    /// never a success — the caller would otherwise build a part at the wrong size. An unread value
    /// (null) is a mismatch too: "not read" is not evidence that it matched. The number is compared
    /// only when it was written.</remarks>
    public static IReadOnlyList<string> Mismatches(
        double centreX,
        double centreY,
        double sheetRadiusMm,
        double nominalMm,
        bool nominalWritten,
        double? readCentreX,
        double? readCentreY,
        double? readSheetRadiusMm,
        double? readNumber)
    {
        var mismatches = new List<string>();
        if (readCentreX is null || !Near(readCentreX.Value, centreX))
        {
            mismatches.Add($"Xc: задано {Num(centreX)}, прочитано {Num(readCentreX)}");
        }

        if (readCentreY is null || !Near(readCentreY.Value, centreY))
        {
            mismatches.Add($"Yc: задано {Num(centreY)}, прочитано {Num(readCentreY)}");
        }

        if (readSheetRadiusMm is null || !Near(readSheetRadiusMm.Value, sheetRadiusMm))
        {
            mismatches.Add($"радиус: задано {Num(sheetRadiusMm)}, прочитано {Num(readSheetRadiusMm)}");
        }

        if (nominalWritten && (readNumber is null || !Near(readNumber.Value, nominalMm)))
        {
            mismatches.Add($"число размера: задано {Num(nominalMm)}, показано {Num(readNumber)}");
        }

        return mismatches;
    }

    private static string Num(double? value) =>
        value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "не прочитано";
}
