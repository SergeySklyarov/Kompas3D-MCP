namespace KompasMcp.Domain.References;

/// <summary>The address of a component NESTED in a subassembly: a path of indices, one per level, from the
/// assembly down to the component — carried by the reference id itself.</summary>
/// <remarks>INVARIANT: a top-level component's id carries NO path (<c>component:&lt;uuid&gt;</c>), so every
/// reference issued before the path existed stays valid and resolves the same way; a nested one appends
/// the path (<c>component:&lt;uuid&gt;/1/0</c>), which is why the format grew ADDITIVELY.
/// INVARIANT: the path is not a flat API5 index — MEASURED: <c>ksDocument3D.PartCollection(true)</c>
/// lists two rows for an assembly of three components, so a nested component has no ordinal at all.
/// DOC: <c>ipart7_parts.html</c>, <c>iparts7_part.html</c> («VT_I4 - индекс компонента»).
/// History: docs/decisions/assembly-ext.md#nested-implemented</remarks>
public static class ComponentPath
{
    /// <summary>Separator between the reference id and its first index, and between the indices.</summary>
    public const char Separator = '/';

    /// <summary>The path as the tail of a reference id — <c>1/0</c> for the first child of the first
    /// child.</summary>
    public static string Format(IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Join(Separator, path);
    }

    /// <summary>The path a reference id carries, or <c>false</c> when it carries none (a TOP-LEVEL
    /// component) or the tail is not a path.</summary>
    /// <remarks>INVARIANT: a tail that does not parse is NOT silently treated as "no path": the difference
    /// between "top-level" and "a broken address" decides whether a mutation runs at all, so a malformed
    /// tail is answered <c>false</c> and no reference is minted with one.</remarks>
    public static bool TryParse(string? referenceId, out IReadOnlyList<int> path)
    {
        path = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(referenceId))
        {
            return false;
        }

        var colon = referenceId.LastIndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var slash = referenceId.IndexOf(Separator, colon + 1);
        if (slash < 0)
        {
            return false;
        }

        var tail = referenceId[(slash + 1)..];
        if (tail.Length == 0)
        {
            return false;
        }

        var indices = new List<int>();
        foreach (var part in tail.Split(Separator))
        {
            if (part.Length == 0
                || !int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var index))
            {
                return false;
            }

            indices.Add(index);
        }

        path = indices;
        return true;
    }
}
