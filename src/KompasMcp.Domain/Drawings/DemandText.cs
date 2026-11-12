namespace KompasMcp.Domain.Drawings;

/// <summary>How the requested technical-requirements text is compared with the one the document returns, and
/// which display state an empty text asks for.</summary>
/// <remarks>MEASURED: the kernel breaks a long line at its own width, so a byte comparison reports a lost text
/// where only the layout changed; the final drawing held the whole text. INVARIANT: whitespace is the only
/// difference forgiven — every other character still has to come back, otherwise it is a loss.
/// History: docs/decisions/drawings.md#technical-demand-text</remarks>
public static class DemandText
{
    /// <summary>The text with every run of whitespace collapsed to one space, so a wrap is not a loss.</summary>
    public static string Normalize(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether the document holds the requested text, ignoring how it was wrapped.</summary>
    public static bool Matches(string? requested, string? readBack) =>
        string.Equals(Normalize(requested), Normalize(readBack), StringComparison.Ordinal);

    /// <summary>Whether the two texts differ only in whitespace: the wrap is named, the text is not lost.</summary>
    public static bool FormattingOnly(string? requested, string? readBack) =>
        Matches(requested, readBack) && !string.Equals(requested, readBack, StringComparison.Ordinal);

    /// <summary>Whether the block is expected to be DISPLAYED: an empty text asks for a hidden block, so
    /// <c>IsCreated = false</c> is the state that text requested, not a failed check.</summary>
    public static bool ExpectCreated(string? requested) => Normalize(requested).Length > 0;
}
