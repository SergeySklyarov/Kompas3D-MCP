namespace KompasMcp.Domain.Geometry;

/// <summary>What deleting faces does to the numbers that can be read back: face count and volume.</summary>
/// <remarks>DOC: <c>ifaceremover_savebody.html</c> — <c>SaveBody</c> is a BOOL «Сохранять целостность
/// тела», so the mode names which numbers must hold, not which answer is expected.
/// INVARIANT: exactly the named faces must disappear; a different count means the kernel resolved other
/// faces, whatever <c>Update()</c> answered.
/// MEASURED: <c>SaveBody = false</c> leaves no material, so «оболочка» is decided by the volume, not by
/// the absent <c>IBody7.IsSolid</c>.
/// History: docs/decisions/direct-faces.md#delete-restore</remarks>
public static class FaceRemovalEffect
{
    /// <summary>Faces that disappeared between two reads. <c>null</c> when either read failed: "not read"
    /// differs from "none disappeared".</summary>
    public static int? FacesRemoved(double? facesBefore, double? facesAfter)
    {
        if (facesBefore is not double before || facesAfter is not double after
            || !double.IsFinite(before) || !double.IsFinite(after))
        {
            return null;
        }

        return (int)Math.Round(before - after);
    }

    /// <summary>The named faces are the ones that went: the count equals what was asked for.</summary>
    public static bool MatchesRequest(int? removed, int requested) => removed == requested;

    /// <summary>«Тело или оболочка» by the volume: a shell encloses no material. <c>null</c> when the
    /// volume was not read.</summary>
    public static bool? IsSolid(double? volumeMm3) =>
        volumeMm3 is double volume && double.IsFinite(volume) ? Math.Abs(volume) > 1e-6d : null;

    /// <summary>Surface area fell: a removed face takes its own area away and adds nothing back.</summary>
    public static bool? AreaDecreased(double? areaBefore, double? areaAfter) =>
        areaBefore is double before && areaAfter is double after
        && double.IsFinite(before) && double.IsFinite(after)
            ? after < before
            : null;
}
