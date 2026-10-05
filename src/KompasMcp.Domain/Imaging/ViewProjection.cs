namespace KompasMcp.Domain.Imaging;

/// <summary>A standard view projection: the wire name the tool publishes and the vendor type code from
/// <c>ksViewProjectionType</c>.</summary>
/// <remarks>DOC: <c>ksviewprojectiontype.html</c> — <c>ksVPNone = -1 … ksVPIsometric = 7</c>,
/// <c>ksVPDimetric = 8</c>, <c>ksVPUnfold = 9</c>, <c>ksVPUser = 10</c>. The type is what the tool sends
/// to the kernel; the wire name is what the caller sends to the tool. They are kept apart because the
/// KERNEL name is localized ("#Спереди") and the type is not.
/// History: docs/decisions/contracts.md#view-projection</remarks>
public sealed record ViewProjectionSpec(string Wire, int Type, string Display);

/// <summary>The view projections the tool publishes.</summary>
/// <remarks>INVARIANT: the list is a SUBSET the probe measured live in the document's collection, not
/// every member of the enum. <c>dimetric</c> (8) IS published: MEASURED — it is the current projection of
/// a freshly created part, so leaving it out made the previous view of every fresh document
/// UNRESTORABLE and every first <c>view</c> refusal a constant. LIMIT: <c>ksVPUser</c> (10) is absent —
/// a user projection has no fixed type to address and was not measured. <c>vp_None</c> (-1) is never
/// current (DOC) and is not a view.
/// History: docs/decisions/contracts.md#view-projection</remarks>
public static class ViewProjections
{
    public static readonly ViewProjectionSpec Front = new("front", 1, "#Спереди");

    public static readonly ViewProjectionSpec Rear = new("rear", 2, "#Сзади");

    public static readonly ViewProjectionSpec Up = new("up", 3, "#Сверху");

    public static readonly ViewProjectionSpec Down = new("down", 4, "#Снизу");

    public static readonly ViewProjectionSpec Left = new("left", 5, "#Слева");

    public static readonly ViewProjectionSpec Right = new("right", 6, "#Справа");

    public static readonly ViewProjectionSpec Isometric = new("isometric", 7, "#Изометрия");

    public static readonly ViewProjectionSpec Dimetric = new("dimetric", 8, "#Диметрия");

    public static readonly IReadOnlyList<ViewProjectionSpec> All =
        new[] { Front, Rear, Up, Down, Left, Right, Isometric, Dimetric };

    /// <summary>Names published by the schema, in list order.</summary>
    public static readonly IReadOnlyList<string> WireNames =
        All.Select(spec => spec.Wire).ToArray();

    /// <summary>Resolve by published name. An unknown name is a refusal, not "the isometric by default":
    /// the caller would not be able to tell a substituted view from the one requested.</summary>
    public static bool TryResolve(string? wire, out ViewProjectionSpec spec)
    {
        spec = Isometric;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Wire, wire, StringComparison.OrdinalIgnoreCase))
            {
                spec = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Name of a type read back from the kernel. A type outside the published list is NAMED
    /// (<c>type:NN</c>) rather than mapped onto a neighbour: the read-back is evidence, and rounding it
    /// to the nearest published name would substitute a measurement for a guess.</summary>
    public static string Describe(int type)
    {
        foreach (var candidate in All)
        {
            if (candidate.Type == type)
            {
                return candidate.Wire;
            }
        }

        return $"type:{type}";
    }
}
