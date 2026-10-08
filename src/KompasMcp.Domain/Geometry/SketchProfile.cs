using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>The contours a sketch profile consists of, accumulated across the edits that built it, with the
/// analytic area of the region they enclose.</summary>
/// <remarks>INVARIANT: a profile's area is not the sum of its primitives — a contour inside another is a
/// hole. The extent is therefore derived from the contour list, never accumulated next to it. The area is
/// computed on demand rather than cached: it is read once per extrusion, and a cached figure can go stale
/// against the contour list it describes.
/// History: docs/decisions/geometry.md#sketch-profiles-contour-list</remarks>
public sealed class SketchProfile
{
    private readonly List<SketchEntityDto> _entities = new();

    /// <summary>Every primitive drawn into this sketch by this server, oldest first.</summary>
    public IReadOnlyList<SketchEntityDto> Entities => _entities;

    /// <summary>Area of the region the whole profile encloses, in mm², or null when it is not analytically
    /// determined (see <see cref="ProfileArea"/>).</summary>
    public double? AreaMm2 => Outcome.AreaMm2;

    /// <summary>The area together with the reason it is unavailable, so an extrusion can name the gap
    /// instead of reporting a bare "not computable".</summary>
    public ProfileAreaOutcome Outcome => ProfileArea.Compute(_entities);

    public void Replace(IReadOnlyList<SketchEntityDto> entities)
    {
        _entities.Clear();
        _entities.AddRange(entities);
    }

    public void Append(IReadOnlyList<SketchEntityDto> entities) => _entities.AddRange(entities);

    public void Clear() => _entities.Clear();
}
