using System.Text;

namespace KompasMcp.Host.Catalog;

/// <summary>Renders the tool catalog as a Markdown list for the overview documents.</summary>
/// <remarks>INVARIANT: the listing is GENERATED from <see cref="ToolCatalog.All"/>, never written by
/// hand — a manual list drifts from the catalog silently (the documents named 50 tools while the
/// catalog held 63). The generator and the documents therefore share one source.
/// History: docs/decisions/host.md#tool-listing</remarks>
public static class ToolListing
{
    /// <summary>The markers a document must carry around the generated block. Editing between them by
    /// hand is forbidden: the next run overwrites it.</summary>
    public const string BeginMarker = "<!-- BEGIN TOOL LISTING -->";
    public const string EndMarker = "<!-- END TOOL LISTING -->";

    /// <summary>One line per tool: <c>- `name`: **Title**. First sentence.</c> No trailing blank line
    /// beyond the block itself, so the surrounding Markdown controls spacing.</summary>
    /// <remarks>INVARIANT: the published documents carry no em dash, so the listing prints a hyphen in
    /// its place. Only the rendered text changes: the catalog descriptions and schemas stay as they are.
    /// TEST: ToolListingTests.GeneratedBlock_HasNoEmDash.</remarks>
    public static string Markdown(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        foreach (var tool in tools)
        {
            sb.Append("- `").Append(tool.Name).Append("`: **").Append(NoEmDash(tool.Title)).Append("**. ");
            sb.Append(NoEmDash(Summary(tool.Description))).Append('\n');
        }

        return sb.ToString();
    }

    private static string NoEmDash(string text) => text.Replace('—', '-');

    /// <summary>The first sentence of a description, capped at <see cref="SummaryLimit"/> characters.
    /// MEASURED: some descriptions open with a 500-character enumeration; printing it verbatim would
    /// bury the tool under a paragraph. A cut sentence ends in an ellipsis so the reader knows more
    /// follows in the tool's own description.</summary>
    public static string Summary(string description)
    {
        var sentence = FirstSentence(description);
        if (sentence.Length <= SummaryLimit)
        {
            return sentence;
        }

        var cut = sentence.LastIndexOf(' ', SummaryLimit - 1);
        if (cut <= 0)
        {
            cut = SummaryLimit;
        }

        return sentence[..cut] + "…";
    }

    /// <summary>How many characters of the first sentence are shown before it is cut.</summary>
    public const int SummaryLimit = 240;

    /// <summary>The first sentence of a description: up to the first <c>. </c>, or the whole string when
    /// there is none. A period inside an abbreviation would cut early, so the sentence boundary is taken
    /// as a period followed by a space and an uppercase letter.</summary>
    public static string FirstSentence(string description)
    {
        var text = description.Replace('\n', ' ').Trim();
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '.')
            {
                continue;
            }

            var next = text[i + 1];
            if (next == ' ' && i + 2 < text.Length && char.IsUpper(text[i + 2]))
            {
                return text[..(i + 1)];
            }
        }

        return text;
    }
}
