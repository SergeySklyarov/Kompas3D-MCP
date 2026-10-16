using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>What a sketch edit must leave in the sketch, compared with what the model actually holds.</summary>
/// <remarks>INVARIANT: the verdict is taken from the composition READ BACK out of the model, never from
/// the answers of the delete and draw calls. MEASURED: every call answered success while two old arcs
/// stayed in the sketch and the next extrusion failed with KOMPAS code 54.
/// History: docs/decisions/adapter-sketch.md#sketch-replace</remarks>
public static class SketchComposition
{
    /// <summary>The read-back kinds that carry sketch GEOMETRY — the objects a replace or a clear owns.
    /// DOC: the names are <c>DrawingObjectTypeEnum</c> members. MEASURED: a rectangle drawn by
    /// <c>DrawRectangle</c> reads back as FOUR <c>ksDrLineSeg</c>, a spline as ONE <c>ksDrBezier</c>, and a
    /// full-turn arc stays <c>ksDrArc</c> rather than becoming a circle. An object of another kind (a
    /// dimension, a symbol) is not geometry and is left alone.</summary>
    public static IReadOnlySet<string> GeometryKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ksDrLineSeg",
        "ksDrArc",
        "ksDrCircle",
        "ksDrRectangle",
        "ksDrPolyline",
        "ksDrBezier",
        "ksDrNurbs",
    };

    /// <summary>The read-back kinds ONE requested primitive produces, in no particular order.</summary>
    public static IReadOnlyList<string> ReadBackKinds(SketchEntityKind kind) => kind switch
    {
        SketchEntityKind.Line => new[] { "ksDrLineSeg" },
        SketchEntityKind.Arc => new[] { "ksDrArc" },
        SketchEntityKind.Circle => new[] { "ksDrCircle" },
        // MEASURED: the rectangle route is four ksLineSeg calls, so the sketch holds four objects.
        SketchEntityKind.Rectangle => new[] { "ksDrLineSeg", "ksDrLineSeg", "ksDrLineSeg", "ksDrLineSeg" },
        SketchEntityKind.Polyline => new[] { "ksDrPolyline" },
        SketchEntityKind.Spline => new[] { "ksDrBezier" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The geometry the batch is expected to leave, as a multiset of read-back kinds.</summary>
    public static IReadOnlyList<string> ExpectedReadBackKinds(IEnumerable<SketchEntityKind> requested) =>
        requested.SelectMany(ReadBackKinds).ToList();

    /// <summary>Compare the expected geometry with the kinds read back from the model.</summary>
    /// <param name="expected">Read-back kinds of the batch just written.</param>
    /// <param name="observed">Kinds of every object the sketch holds after the edit; an entry is
    /// <c>null</c> when the kind could not be read — "not read" is not "absent".</param>
    public static SketchCompositionVerdict Verify(
        IReadOnlyList<string> expected,
        IReadOnlyList<string?> observed)
    {
        var unreadable = observed.Count(kind => kind is null);
        var geometry = observed.Where(kind => kind is not null && GeometryKinds.Contains(kind)).ToList();
        var ignored = observed.Count - geometry.Count - unreadable;

        var pending = new List<string>(expected);
        var leftover = new List<string>();
        foreach (var kind in geometry)
        {
            if (!pending.Remove(kind!))
            {
                leftover.Add(kind!);
            }
        }

        // A kind that did not answer makes the comparison impossible: an unreadable object could be
        // either a leftover or a wanted one, and silence is not evidence that the sketch is clean.
        if (unreadable > 0)
        {
            return new SketchCompositionVerdict(false, "kind_unreadable", leftover, pending, ignored, unreadable);
        }

        if (leftover.Count > 0)
        {
            return new SketchCompositionVerdict(false, "extra_objects", leftover, pending, ignored, 0);
        }

        if (pending.Count > 0)
        {
            return new SketchCompositionVerdict(false, "missing_objects", leftover, pending, ignored, 0);
        }

        return new SketchCompositionVerdict(true, null, leftover, pending, ignored, 0);
    }
}

/// <summary>Outcome of comparing the read-back sketch composition with the requested one.
/// <see cref="Leftover"/> — geometry the sketch holds that the batch did not ask for (the defect);
/// <see cref="Missing"/> — geometry the batch asked for and the sketch does not hold.</summary>
public sealed record SketchCompositionVerdict(
    bool Matches,
    string? ReasonCode,
    IReadOnlyList<string> Leftover,
    IReadOnlyList<string> Missing,
    int IgnoredNonGeometry,
    int UnreadableKinds);
