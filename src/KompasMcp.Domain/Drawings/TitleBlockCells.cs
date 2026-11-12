namespace KompasMcp.Domain.Drawings;

/// <summary>One title-block cell whose value the document changed on its own.</summary>
public sealed record TitleBlockCellChange(int CellId, string? Before, string? After);

/// <summary>Which title-block cells an operation refilled, and which cell ids follow a MODEL property.</summary>
/// <remarks>DOC: <c>istamp_text.html</c> — <c>IStamp.Text(Id)</c> is a read-only property returning the
/// cell's <c>IText</c>, so reading a cell changes nothing; <c>istamp_getnextcolumnid.html</c> —
/// <c>GetNextColumnId(Id)</c> «идентификатор следующей непустой ячейки штампа», the documented way to walk
/// the non-empty cells. MEASURED: on the GOST layout a drawing with an associative view refills cell 1 from
/// <c>ksPart.name</c> and cell 2 (<c>ksPart.marking</c>) is cleared when the model has no marking; the help
/// does NOT document the numeric ids. LIMIT: the mapping below is measured on that layout and is named as
/// measured, never as documented. History: docs/decisions/drawings.md#stamp-rebuild-changes</remarks>
public static class TitleBlockCells
{
    /// <summary>Model property the cell id follows, or null when no link is measured for it.</summary>
    public static string? ModelPropertyOf(int cellId) => cellId switch
    {
        1 => "ksPart.name",
        2 => "ksPart.marking",
        _ => null,
    };

    /// <summary>Cells whose value differs between two reads, ordered by id; a cell present in one read
    /// only is a change too (an empty value is an empty string, not "absent").</summary>
    public static IReadOnlyList<TitleBlockCellChange> Changed(
        IReadOnlyDictionary<int, string?> before, IReadOnlyDictionary<int, string?> after)
    {
        var ids = before.Keys.Union(after.Keys).OrderBy(id => id);
        var changes = new List<TitleBlockCellChange>();
        foreach (var id in ids)
        {
            before.TryGetValue(id, out var was);
            after.TryGetValue(id, out var now);
            if (!string.Equals(was, now, StringComparison.Ordinal))
            {
                changes.Add(new TitleBlockCellChange(id, was, now));
            }
        }

        return changes;
    }
}
