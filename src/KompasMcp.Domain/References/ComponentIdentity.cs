namespace KompasMcp.Domain.References;

/// <summary>State of one component-identity signal: whether it was read and whether it matched.</summary>
/// <remarks>Three states, not <c>bool</c>: "not read" and "read and matched" are different claims, and
/// substituting the first for the second would confirm an address on COM silence.</remarks>
public enum ComponentIdentitySignal
{
    /// <summary>The signal was not read on at least one side — no information.</summary>
    NotRead,

    /// <summary>The signal was read on both sides and matched.</summary>
    Matches,

    /// <summary>The signal was read and DIFFERS.</summary>
    Differs,
}

/// <summary>Verdict on the component-address identity.</summary>
/// <param name="Matches">
/// <c>true</c> — identity confirmed; <c>false</c> — the address leads to a FOREIGN component;
/// <c>null</c> — nothing to compare.
/// </param>
/// <param name="Detail">Human-readable explanation: which signals were read and what decided the verdict.</param>
public sealed record ComponentIdentityVerdict(bool? Matches, string Detail);

/// <summary>The component-address identity rule — a PURE function of three signals, no KOMPAS.</summary>
/// <remarks>INVARIANT: ONLY THE SOURCE FILE decides the refusal. It is the only signal (a) MEASURED live
/// and (b) not changing by itself — a component's source changes only by an explicit replacement. The
/// component name (API5 <c>ksPart.name</c> vs API7 <c>IPart7.Name</c> was never compared live) and the
/// placement matrix (MUTABLE state: the server's own mutation and a mate both change it, and the
/// <c>GetSummMatrix</c> layout is neither documented nor measured, so per AGENTS.md it cannot drive
/// behaviour) are NOTES. A mismatch of name or matrix is NAMED in the note — silence is indistinguishable
/// from "we did not look". The rule is a table here so it can be tested without KOMPAS.
/// History: docs/decisions/assembly.md#identity</remarks>
public static class ComponentIdentity
{
    /// <summary>Why name and matrix do not decide the refusal. Printed in the note when one is needed.</summary>
    public const string SecondarySignalsAreInformative =
        "Отказ решает только ИСТОЧНИК: имя компонента и матрица размещения — примечания. Сравнение имён "
        + "API5/API7 живьём не измерялось, а матрица размещения — изменяемое состояние (её меняет и "
        + "собственная мутация, и сопряжение), раскладка IPart7.GetSummMatrix не измерена.";

    /// <summary>Reduce three signals to one verdict.</summary>
    /// <param name="source">Component source file: <c>ksPart.fileName</c> vs <c>IPart7.FileName</c>.</param>
    /// <param name="name">Component name in the tree: <c>ksPart.name</c> vs <c>IPart7.Name</c>.</param>
    /// <param name="placement">Placement matrix: API5 by ordinal vs API7 <c>GetSummMatrix</c>, taken at ONE
    /// point in time.</param>
    public static ComponentIdentityVerdict Decide(
        ComponentIdentitySignal source,
        ComponentIdentitySignal name,
        ComponentIdentitySignal placement)
    {
        var signals = string.Join("; ",
            DescribeSource(source),
            DescribeSecondary("имя компонента", name),
            DescribeSecondary("матрица размещения", placement));

        if (source == ComponentIdentitySignal.Differs)
        {
            return new ComponentIdentityVerdict(false,
                signals + ". Источник РАСХОДИТСЯ: по этому номеру лежит компонент с ДРУГИМ "
                + "файлом-источником, значит номер ведёт не туда, и мутация по нему не выполняется. "
                + SecondarySignalsAreInformative);
        }

        if (source == ComponentIdentitySignal.Matches)
        {
            // A source match confirms the address. A name or matrix mismatch does NOT drive the refusal,
            // but if present it is stated honestly, together with the reason.
            var secondaryDiffers = name == ComponentIdentitySignal.Differs
                || placement == ComponentIdentitySignal.Differs;
            return new ComponentIdentityVerdict(true,
                secondaryDiffers ? signals + ". " + SecondarySignalsAreInformative : signals);
        }

        return new ComponentIdentityVerdict(null,
            signals + ". Источник не прочитан с одной из сторон, а имя и матрица отказом не управляют, "
            + "поэтому подтвердить адрес НЕЧЕМ: мутация по неподтверждённому адресу не выполняется. "
            + SecondarySignalsAreInformative);
    }

    private static string DescribeSource(ComponentIdentitySignal signal) => signal switch
    {
        ComponentIdentitySignal.Matches => "источник (файл) совпал",
        ComponentIdentitySignal.Differs => "источник (файл) РАСХОДИТСЯ",
        _ => "источник (файл) не читается с одной из сторон",
    };

    private static string DescribeSecondary(string what, ComponentIdentitySignal signal) => signal switch
    {
        ComponentIdentitySignal.Matches => $"{what} совпал(а) (отказом не управляет)",
        ComponentIdentitySignal.Differs => $"{what} РАСХОДИТСЯ (отказом НЕ управляет)",
        _ => $"{what} не читается",
    };
}
