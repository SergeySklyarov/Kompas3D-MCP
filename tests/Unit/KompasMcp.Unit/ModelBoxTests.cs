using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>One rule decides whether the kernel's box is a MEASUREMENT or its "no box" service value, so the
/// published gabarit and the fingerprint cannot disagree about an empty model.</summary>
/// <remarks>WHY this test exists. MEASURED: a part with no bodies answered <c>GetGabarit</c> with service
/// corners around ±2e300; the fingerprint printed them in full (hundreds of digits) while the response said
/// <c>gabarit=null</c>. History: docs/decisions/adapter-core.md#gabarit-scope</remarks>
public class ModelBoxTests
{
    [Fact]
    public void ServiceCornersAreNotAMeasurement()
    {
        // The measured service value of an empty part: a magnitude no model at mm scale reaches.
        var service = 2e300;
        var accepted = ModelBox.TryNormalise(service, service, service, -service, -service, -service,
            out var min, out var max);

        Assert.False(accepted);
        Assert.Empty(min);
        Assert.Empty(max);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCornersAreNotAMeasurement(double value)
    {
        Assert.False(ModelBox.TryNormalise(value, 0, 0, 0, 0, 0, out _, out _));
    }

    [Fact]
    public void RealCornersAreNormalised()
    {
        // The kernel does not promise which corner is which: min/max must not depend on the order.
        var accepted = ModelBox.TryNormalise(10, 20, 30, -10, -20, -30, out var min, out var max);

        Assert.True(accepted);
        Assert.Equal(new[] { -10d, -20d, -30d }, min);
        Assert.Equal(new[] { 10d, 20d, 30d }, max);
    }

    [Fact]
    public void AZeroSizedBoxAtTheOrigin_IsStillAMeasurement()
    {
        // A flat part (or a degenerate body) is a real box: only the service magnitude is "no box".
        Assert.True(ModelBox.TryNormalise(0, 0, 0, 0, 0, 0, out var min, out var max));
        Assert.Equal(new[] { 0d, 0d, 0d }, min);
        Assert.Equal(new[] { 0d, 0d, 0d }, max);
    }
}
