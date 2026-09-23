using KompasMcp.Contracts;

namespace KompasMcp.Domain.Geometry;

/// <summary>
/// The contours a sketch profile consists of, accumulated across the edits that built it, with the
/// analytic area of the region they enclose.
/// </summary>
/// <remarks>
/// <para>
/// The contours are kept, not the area: one scalar cannot express nesting. Measured 24.09.2026 — with
/// a stored sum, appending a circle inside another one produced exactly the figure of drawing both at
/// once (π·100 + π·25 = π·125 = 392.699081698724), so no later edit could ever turn the value into a
/// hole. Keeping the entities lets <see cref="ProfileArea"/> see the whole profile at once, in
/// whatever order the caller built it.
/// </para>
/// <para>
/// The area is computed on demand rather than cached: it is read once per extrusion, and a cached
/// figure is one more thing that can go stale against the contour list it describes.
/// </para>
/// </remarks>
public sealed class SketchProfile
{
    private readonly List<SketchEntityDto> _entities = new();

    /// <summary>Every primitive drawn into this sketch by this server, oldest first.</summary>
    public IReadOnlyList<SketchEntityDto> Entities => _entities;

    /// <summary>
    /// Area of the region the whole profile encloses, in mm², or null when it is not analytically
    /// determined (see <see cref="ProfileArea"/>).
    /// </summary>
    public double? AreaMm2 => ProfileArea.Of(_entities);

    /// <summary>Replaces the profile with the primitives of a clearing edit.</summary>
    public void Replace(IReadOnlyList<SketchEntityDto> entities)
    {
        _entities.Clear();
        _entities.AddRange(entities);
    }

    /// <summary>Adds the primitives of a drawing edit to what the profile already holds.</summary>
    public void Append(IReadOnlyList<SketchEntityDto> entities) => _entities.AddRange(entities);

    public void Clear() => _entities.Clear();
}
