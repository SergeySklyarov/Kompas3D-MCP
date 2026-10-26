namespace KompasMcp.Domain.Bom;

/// <summary>One component instance as the walk read it. A null flag means "not read", never "false".</summary>
/// <remarks>INVARIANT: a row is grouped by the FULL source path, never by the file name: two different
/// <c>part.m3d</c> in different folders are two parts, and merging them by name was measured to turn
/// 3 + 1 instances into one row of 4. History: docs/decisions/bom.md#bom-tool</remarks>
public sealed record BomInstance(
    string? File,
    string? Name,
    string? Marking,
    string? Material,
    bool? Hidden,
    bool? Excluded,
    bool? Standard);

/// <summary>What the BOM includes and how rows are grouped.</summary>
public sealed record BomOptions(bool IncludeHidden, bool IncludeExcluded, bool IncludeStandard, string GroupBy);

/// <summary>One BOM row: a group of instances of one source file.</summary>
public sealed record BomRow(string? File, string? Name, string? Marking, string? Material, int Instances);

/// <summary>Instances left out by the filters, by reason. The first matching reason wins.</summary>
public sealed record BomSkipped(int Hidden, int Excluded, int Standard)
{
    public int Total => Hidden + Excluded + Standard;
}

/// <summary>The summary and its totals.</summary>
public sealed record BomSummaryResult(
    IReadOnlyList<BomRow> Rows,
    int Instances,
    int UniqueFiles,
    BomSkipped Skipped);

/// <summary>Grouping and filtering of the component instances — the part of the BOM that does not touch
/// KOMPAS, so it is unit-tested without it.</summary>
/// <remarks>INVARIANT: <c>sum(row.instances) + skipped.total</c> equals the number of instances handed in.
/// The filters are the TOOL's work, not KOMPAS's: an excluded or hidden instance stays in
/// <c>PartCollection</c> and in the instance count (measured), so the skip is done here and counted.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public static class BomSummary
{
    /// <summary>Group by the full source path only.</summary>
    public const string GroupByFile = "file";

    /// <summary>Group by the full source path plus the designation.</summary>
    public const string GroupByFileDesignation = "file+designation";

    public static bool IsGroupBy(string? value) =>
        value is GroupByFile or GroupByFileDesignation;

    public static BomSummaryResult Build(IReadOnlyList<BomInstance> instances, BomOptions options)
    {
        var rows = new List<BomRow>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new BomSkipped(0, 0, 0);

        foreach (var instance in instances)
        {
            if (SkipReason(instance, options) is { } reason)
            {
                skipped = reason switch
                {
                    "hidden" => skipped with { Hidden = skipped.Hidden + 1 },
                    "excluded" => skipped with { Excluded = skipped.Excluded + 1 },
                    _ => skipped with { Standard = skipped.Standard + 1 },
                };
                continue;
            }

            var key = Key(instance, options.GroupBy);
            if (index.TryGetValue(key, out var at))
            {
                rows[at] = rows[at] with { Instances = rows[at].Instances + 1 };
            }
            else
            {
                index[key] = rows.Count;
                rows.Add(new BomRow(instance.File, instance.Name, instance.Marking, instance.Material, 1));
            }

            if (instance.File is { Length: > 0 } file)
            {
                files.Add(file);
            }
        }

        return new BomSummaryResult(rows, rows.Sum(r => r.Instances), files.Count, skipped);
    }

    /// <summary>Why the instance is left out, or null when it is included.</summary>
    /// <remarks>A flag that was NOT read does not filter the instance out: "not read" is not "excluded",
    /// and dropping a component on an unread flag would hide it from the answer.</remarks>
    private static string? SkipReason(BomInstance instance, BomOptions options)
    {
        if (instance.Hidden == true && !options.IncludeHidden)
        {
            return "hidden";
        }

        if (instance.Excluded == true && !options.IncludeExcluded)
        {
            return "excluded";
        }

        return instance.Standard == true && !options.IncludeStandard ? "standard" : null;
    }

    private static string Key(BomInstance instance, string groupBy) =>
        groupBy == GroupByFileDesignation
            ? (instance.File ?? string.Empty) + "\u0000" + (instance.Marking ?? string.Empty)
            : instance.File ?? string.Empty;
}
