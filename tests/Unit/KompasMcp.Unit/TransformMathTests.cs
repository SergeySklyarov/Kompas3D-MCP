using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Transform rules from spec 1.10/2.3: the basis must be right-handed and orthonormal, Z is
/// derived, and the composition order is fixed — checked on a rotate-then-translate pair that does
/// not commute, so an accidental swap cannot pass.
/// </summary>
public class TransformMathTests
{
    private static TransformDto Frame(double[] origin, double[] x, double[] y) => new()
    {
        OriginMm = origin,
        XAxis = x,
        YAxis = y,
    };

    [Fact]
    public void Identity_IsAccepted()
    {
        var frame = TransformMath.FromDto(TransformDto.Identity);
        Assert.Equal(new[] { 0d, 0, 1 }, frame.ZMm);
        Assert.Equal(new[] { 0d, 0, 0 }, frame.OriginMm);
    }

    [Fact]
    public void Z_IsDerivedAndNeverTakenFromClient()
    {
        // A 90° rotation about Z: X→Y, Y→−X, so Z must stay +Z.
        var frame = TransformMath.FromDto(Frame(new[] { 5d, 6, 7 }, new[] { 0d, 1, 0 }, new[] { -1d, 0, 0 }));
        Assert.Equal(1d, frame.ZMm[2], 9);
        Assert.Equal(5d, frame.OriginMm[0]);
    }

    [Fact]
    public void NonUnitAxis_IsRejectedNotNormalised()
    {
        var ex = Assert.Throws<KompasContractException>(() =>
            TransformMath.FromDto(Frame(new[] { 0d, 0, 0 }, new[] { 2d, 0, 0 }, new[] { 0d, 1, 0 })));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("x_axis", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonOrthogonalAxes_AreRejected()
    {
        var ex = Assert.Throws<KompasContractException>(() =>
            TransformMath.FromDto(Frame(new[] { 0d, 0, 0 }, new[] { 1d, 0, 0 }, new[] { 1d / Math.Sqrt(2), 1d / Math.Sqrt(2), 0 })));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void CollinearAxes_AreRejectedAsDegenerate()
    {
        // X·Y = 1 here: the silent normalisation this guards against would produce garbage Z.
        var ex = Assert.Throws<KompasContractException>(() =>
            TransformMath.FromDto(Frame(new[] { 0d, 0, 0 }, new[] { 1d, 0, 0 }, new[] { 1d, 0, 0 })));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void NaNAndInfinity_AreRejected()
    {
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var ex = Assert.Throws<KompasContractException>(() =>
                TransformMath.FromDto(Frame(new[] { bad, 0, 0 }, new[] { 1d, 0, 0 }, new[] { 0d, 1, 0 })));
            Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        }
    }

    [Fact]
    public void Apply_IsOriginPlusAxisCombination()
    {
        var frame = TransformMath.FromDto(Frame(new[] { 10d, 20, 30 }, new[] { 0d, 1, 0 }, new[] { 0d, 0, 1 }));
        // Z = X×Y = (1,0,0)·... → derived; local (1,2,3) maps to origin + X*1 + Y*2 + Z*3.
        var parent = frame.ToParent(1, 2, 3);
        Assert.Equal(10d + 3, parent[0], 9);
        Assert.Equal(20d + 1, parent[1], 9);
        Assert.Equal(30d + 2, parent[2], 9);
    }

    [Fact]
    public void Compose_RotateThenTranslate_IsNotTheSameAsTranslateThenRotate()
    {
        // Outer: translate by (100,0,0). Inner: rotate 90° about Z.
        var translate = TransformMath.FromDto(Frame(new[] { 100d, 0, 0 }, new[] { 1d, 0, 0 }, new[] { 0d, 1, 0 }));
        var rotate = TransformMath.FromDto(Frame(new[] { 0d, 0, 0 }, new[] { 0d, 1, 0 }, new[] { -1d, 0, 0 }));

        var rotateInsideTranslate = TransformMath.Compose(translate, rotate);
        var translateInsideRotate = TransformMath.Compose(rotate, translate);

        // Same local point, different world answer: proves the order is honoured, not averaged.
        var a = rotateInsideTranslate.ToParent(1, 0, 0);
        var b = translateInsideRotate.ToParent(1, 0, 0);
        Assert.True(Math.Abs(a[0] - b[0]) > 1e-6, "композиция не должна быть коммутативной");
        Assert.Equal(100d, a[0], 6);
        Assert.Equal(0d, b[0], 6);
    }

    [Fact]
    public void InverseCompose_IsExactInverseOfCompose()
    {
        var parent = TransformMath.FromDto(Frame(new[] { 12d, -3, 4.5 }, new[] { 0d, 1, 0 }, new[] { 0d, 0, 1 }));
        var desiredWorld = TransformMath.FromDto(Frame(new[] { 50d, 60, 70 }, new[] { -1d, 0, 0 }, new[] { 0d, 0, 1 }));

        var inside = TransformMath.InverseCompose(parent, desiredWorld);
        var roundTrip = TransformMath.Compose(parent, inside);

        Assert.Equal(desiredWorld.OriginMm[0], roundTrip.OriginMm[0], 6);
        Assert.Equal(desiredWorld.OriginMm[1], roundTrip.OriginMm[1], 6);
        Assert.Equal(desiredWorld.OriginMm[2], roundTrip.OriginMm[2], 6);
        Assert.Equal(desiredWorld.XMm[0], roundTrip.XMm[0], 6);
        Assert.Equal(desiredWorld.YMm[1], roundTrip.YMm[1], 6);
    }

    [Fact]
    public void RotateAbout_UsesRightHandRule()
    {
        var frame = TransformMath.RotateAbout(RigidFrame.Identity, new[] { 0d, 0, 1 }, 90);
        Assert.Equal(0d, frame.XMm[0], 9);
        Assert.Equal(1d, frame.XMm[1], 9);
        Assert.Equal(-1d, frame.YMm[0], 9);
    }

    [Fact]
    public void RotateAbout_NonUnitAxis_IsRejected()
    {
        var ex = Assert.Throws<KompasContractException>(() =>
            TransformMath.RotateAbout(RigidFrame.Identity, new[] { 0d, 0, 2 }, 90));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public void OversizedCoordinate_IsRejected()
    {
        var ex = Assert.Throws<KompasContractException>(() =>
            TransformMath.FromDto(Frame(new[] { 1e12, 0, 0 }, new[] { 1d, 0, 0 }, new[] { 0d, 1, 0 })));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }
}
