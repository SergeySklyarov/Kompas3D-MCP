using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The floor below which a mutation is called "nothing changed".</summary>
/// <remarks>WHY. A client saw `failed / NO_GEOMETRY_CHANGE` on a boss whose own response reported
/// ΔV = +9.08e-4 mm³ and a gabarit that had grown from Z 10.6898 to 10.774. The old floor was 0.01 mm³ —
/// a "meaningful change" threshold used as a "did anything happen" test. The rule now separates the two:
/// the floor is the measured NOISE of a reading, and a moved gabarit or a changed face count counts even
/// when the volume is within noise.
/// History: docs/decisions/adapter-core.md#volume-change-floor</remarks>
public class BodyChangePolicyTests
{
    /// <summary>The exact case the client reported: a legitimate feature far below the old 0.01 floor.</summary>
    [Fact]
    public void TinyButRealChange_IsNotANoOp()
    {
        var tinyBoss = 0.000908358315;   // pi * 0.0586^2 * 0.0842, measured on the client's boss

        Assert.True(BodyChangePolicy.VolumeMoved(tinyBoss, 309.96216732));
        Assert.True(BodyChangePolicy.Changed(tinyBoss, 309.96216732, boxChanged: true, topologyChanged: true));
    }

    [Fact]
    public void ReadingNoise_IsNotAChange()
    {
        // MEASURED repeatability: the same body re-measured moves by ~1e-13 mm3, relative ~1e-15.
        Assert.False(BodyChangePolicy.VolumeMoved(3e-14, 133.95714910138435));
        Assert.False(BodyChangePolicy.VolumeMoved(-2.1e-14, 170.28301512935678));
        Assert.False(BodyChangePolicy.VolumeMoved(0d, 80000d));
    }

    [Fact]
    public void NullDelta_Alone_IsNotANoOp()
    {
        // An UNREAD volume is not "unchanged": nothing was measured, so nothing was proven.
        Assert.False(BodyChangePolicy.VolumeMoved(null, 100d));
        Assert.False(BodyChangePolicy.Changed(null, 100d, boxChanged: false, topologyChanged: false));
        // ...but a moved gabarit still proves the body changed even with the volume unread.
        Assert.True(BodyChangePolicy.Changed(null, 100d, boxChanged: true, topologyChanged: false));
    }

    [Fact]
    public void GabaritOrTopologyAlone_IsAChange()
    {
        // A boolean `intersect` changes no volume; a feature can alter the face count with volume and box intact.
        Assert.True(BodyChangePolicy.Changed(0d, 100d, boxChanged: true, topologyChanged: false));
        Assert.True(BodyChangePolicy.Changed(0d, 100d, boxChanged: false, topologyChanged: true));
    }

    [Theory]
    [InlineData(1e-6)]     // exactly the absolute floor: NOT above it
    [InlineData(-1e-6)]
    [InlineData(5e-7)]
    public void AtOrBelowTheAbsoluteFloor_IsNotAChange(double delta)
    {
        Assert.False(BodyChangePolicy.VolumeMoved(delta, 0d));
    }

    [Fact]
    public void JustAboveTheAbsoluteFloor_IsAChange()
    {
        Assert.True(BodyChangePolicy.VolumeMoved(1.1e-6, 0d));
    }

    /// <summary>The relative part scales with the body: 1e-7 mm³ is noise on a 1e5 mm³ body, and the absolute
    /// part dominates on a small one. The two together are the floor, not either alone.</summary>
    [Fact]
    public void TheFloorIsRelativeToTheBodysOwnVolume()
    {
        // A large body carries a larger floor: 1e-6 + 1e5 * 1e-9 = 1.01e-4 mm3.
        Assert.False(BodyChangePolicy.VolumeMoved(1e-7, 1e5));
        Assert.False(BodyChangePolicy.VolumeMoved(9e-5, 1e5));
        Assert.True(BodyChangePolicy.VolumeMoved(2e-4, 1e5));

        // A small body's floor is the absolute part alone, so the same absolute delta is a real change.
        Assert.False(BodyChangePolicy.VolumeMoved(1e-7, 1d));
        Assert.True(BodyChangePolicy.VolumeMoved(1e-5, 1d));
    }
}
