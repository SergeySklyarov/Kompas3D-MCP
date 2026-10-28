using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The rules a face deletion is judged by when COM cannot be asked in a test.</summary>
/// <remarks>WHY. <c>Update()</c> answers both ways on the same route, so the mode is confirmed by the
/// numbers: the named faces went, and the volume says whether a body or a shell is left.
/// History: docs/decisions/direct-faces.md#delete-restore</remarks>
public class FaceRemovalEffectTests
{
    // The measured fixture: a 40 x 30 x 20 box - 6 faces, 24000 mm3, 5200 mm2.
    private const double BoxFaces = 6d;
    private const double BoxVolume = 24000d;
    private const double BoxArea = 5200d;

    [Fact]
    public void FacesRemoved_IsTheDifferenceOfTwoReads()
    {
        Assert.Equal(1, FaceRemovalEffect.FacesRemoved(BoxFaces, 5d));
        Assert.Equal(3, FaceRemovalEffect.FacesRemoved(BoxFaces, 3d));
        Assert.Equal(0, FaceRemovalEffect.FacesRemoved(BoxFaces, BoxFaces));
    }

    /// <summary>An unread collection must not look like «nothing disappeared»: the check would then pass
    /// for a body that was never measured.</summary>
    [Fact]
    public void FacesRemoved_IsNullWhenAReadFailed()
    {
        Assert.Null(FaceRemovalEffect.FacesRemoved(null, 5d));
        Assert.Null(FaceRemovalEffect.FacesRemoved(BoxFaces, null));
        Assert.Null(FaceRemovalEffect.FacesRemoved(double.NaN, 5d));
    }

    [Fact]
    public void MatchesRequest_ComparesAgainstTheNamedFaces()
    {
        Assert.True(FaceRemovalEffect.MatchesRequest(1, 1));
        Assert.False(FaceRemovalEffect.MatchesRequest(2, 1));
        Assert.False(FaceRemovalEffect.MatchesRequest(0, 1));
        Assert.False(FaceRemovalEffect.MatchesRequest(null, 1));
    }

    [Fact]
    public void IsSolid_IsDecidedByTheVolume()
    {
        Assert.True(FaceRemovalEffect.IsSolid(BoxVolume));
        Assert.False(FaceRemovalEffect.IsSolid(0d));
        Assert.Null(FaceRemovalEffect.IsSolid(null));
    }

    [Fact]
    public void AreaDecreased_IsFalseWhenTheAreaGrew()
    {
        Assert.True(FaceRemovalEffect.AreaDecreased(BoxArea, 4700d));
        Assert.False(FaceRemovalEffect.AreaDecreased(BoxArea, BoxArea));
        Assert.False(FaceRemovalEffect.AreaDecreased(BoxArea, 5900d));
        Assert.Null(FaceRemovalEffect.AreaDecreased(BoxArea, null));
    }
}
