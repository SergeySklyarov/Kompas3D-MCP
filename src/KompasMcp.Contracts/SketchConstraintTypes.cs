namespace KompasMcp.Contracts;

/// <summary>The constraint-type vocabulary of <c>ksConstraintTypeEnum</c>, in ONE place, so the
/// published schema and the adapter cannot drift apart.</summary>
/// <remarks>WHY A NAME LIST AND NOT THE ENUM ITSELF: the numbers live in the vendor's constants
/// assembly, which the Host does not reference (the Host never touches COM). So the NAMES are declared
/// here — a schema has to enumerate them — and the NUMBERS are resolved by the adapter from the
/// constants assembly itself. The two are held together by an agreement check
/// (<c>SketchConstraintTypes.AgreementProblems</c>), not by memory: a name added to the enum and not
/// here, or the other way round, is a named failure rather than a silent hole.
/// DOC: <c>ksconstrainttypeenum.html</c> — «Типы параметрических ограничений».
/// History: docs/decisions/contracts.md#sketch-constraints
/// </remarks>
public static class SketchConstraintTypes
{
    /// <summary>The types this server can ask the kernel for, in the order the help lists them.
    /// <c>ksCUnknown</c> (0) is deliberately NOT offered: it is the absence of a type, not a type to
    /// write, and offering it would let a call ask for "some constraint".</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "ksCFixedPoint",
        "ksCPointOnCurve",
        "ksCHorizontal",
        "ksCVertical",
        "ksCParallel",
        "ksCPerpendicular",
        "ksCEqualLength",
        "ksCEqualRadius",
        "ksCHAlignPoints",
        "ksCVAlignPoints",
        "ksCMergePoints",
        "ksCAssociation",
        "ksCDimWithVariable",
        "ksCFixedDim",
        "ksCTangentTwoCurves",
        "ksCSymmetryTwoPoints",
        "ksCCollinear",
        "ksCFixedAngle",
        "ksCFixedLenght",
        "ksCPointOnCurveMiddle",
        "ksCBisector",
        "ksCConcentricity",
    };

    /// <summary>The one type whose <c>Value</c>, <c>Expression</c> and <c>Variable</c> are meaningful.
    /// DOC: all three pages say «Используется только для ограничения "Размер с переменной"
    /// (ksCDimWithVariable)».</summary>
    public const string DimensionWithVariable = "ksCDimWithVariable";

    /// <summary>The declared values this server deliberately does NOT offer, with the reason. They are
    /// excluded from the agreement check for the same reason: a value that is not a type to write cannot
    /// be missing from a list of types to write.</summary>
    /// <remarks>DOC: <c>ksconstrainttypeenum.html</c> — «ksCUnknown 0 — Неизвестный тип».</remarks>
    public static IReadOnlyDictionary<string, string> DeliberatelyNotOffered { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ksCUnknown"] = "нулевое значение перечисления — отсутствие типа, а не тип для записи",
        };

    public static bool IsDeclared(string name) =>
        All.Contains(name, StringComparer.Ordinal);

    /// <summary>Compares this list with the names actually declared in the constants assembly and
    /// returns the differences, each named. Empty means they agree.</summary>
    public static IReadOnlyList<string> AgreementProblems(IEnumerable<string> declaredInConstants)
    {
        var declared = declaredInConstants.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var name in All.Where(name => !declared.Contains(name)))
        {
            problems.Add($"'{name}' объявлен здесь и отсутствует в ksConstraintTypeEnum");
        }

        foreach (var name in declared.Where(name => !IsDeclared(name) && name != "value__"
                                                    && !DeliberatelyNotOffered.ContainsKey(name)))
        {
            problems.Add($"'{name}' объявлен в ksConstraintTypeEnum и отсутствует здесь");
        }

        return problems;
    }
}
