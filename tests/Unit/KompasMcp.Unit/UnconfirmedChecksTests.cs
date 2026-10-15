using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>How a lowered verification level names the check that lowered it.</summary>
/// <remarks>The selection is a pure function of the checks a result already publishes, so a substituted
/// list exercises it without a CAD session.
/// History: docs/decisions/adapter-features.md#face-count-growth-rule</remarks>
public class UnconfirmedChecksTests
{
    private static NamedCheck Check(string name, bool passed, string? observed = null, string? expected = null) =>
        new(name, passed, Observed: observed, Expected: expected);

    [Fact]
    public void FailedConfirmingCheck_IsNamedByItsOwnName()
    {
        var checks = new List<NamedCheck>
        {
            Check("radius_read_back", true, "3", "3"),
            Check("face_count_grew", false, "6→6 (+0)", "> 6"),
            Check("volume_delta", true, "2281.54051", "2281.54051"),
        };

        var reasons = UnconfirmedChecks.Of(checks, "radius_read_back", "face_count_grew", "volume_delta");

        Assert.Single(reasons);
        Assert.StartsWith("face_count_grew_not_confirmed", reasons[0]);
        Assert.Contains("6→6 (+0)", reasons[0]);
        Assert.Contains("> 6", reasons[0]);
    }

    [Fact]
    public void PassingVolumeDelta_IsNeverNamed_EvenWhenAnotherCheckFailed()
    {
        var checks = new List<NamedCheck>
        {
            Check("face_count_grew", false, "6→6 (+0)", "> 6"),
            Check("volume_delta", true, "2281.54051", "2281.54051"),
        };

        var reasons = UnconfirmedChecks.Of(checks, "face_count_grew", "volume_delta");

        Assert.DoesNotContain(reasons, r => r.Contains("volume_delta_not_confirmed"));
        Assert.All(reasons, r => Assert.Contains("face_count_grew", r));
    }

    [Fact]
    public void FailedVolumeDelta_KeepsItsExistingCode()
    {
        var checks = new List<NamedCheck> { Check("volume_delta", false, "5256", "5238") };

        var reasons = UnconfirmedChecks.Of(checks, "face_count_grew", "volume_delta");

        Assert.Single(reasons);
        Assert.StartsWith("volume_delta_not_confirmed", reasons[0]);
    }

    [Fact]
    public void FailedCheckOutsideTheConfirmingSet_IsNotAReason()
    {
        var checks = new List<NamedCheck>
        {
            Check("face_count_grew", true, "6→26 (+20)", "> 6"),
            Check("body_count_unchanged", false, "2", null),
        };

        var reasons = UnconfirmedChecks.Of(checks, "face_count_grew", "volume_delta");

        Assert.Single(reasons);
        Assert.Equal(UnconfirmedChecks.UnnamedReason, reasons[0]);
    }

    [Fact]
    public void NoFailedCheckAtAll_StillNamesSomething()
    {
        var checks = new List<NamedCheck> { Check("face_count_grew", true, "6→26 (+20)", "> 6") };

        var reasons = UnconfirmedChecks.Of(checks, "face_count_grew");

        Assert.Single(reasons);
        Assert.Equal(UnconfirmedChecks.UnnamedReason, reasons[0]);
    }

    [Fact]
    public void ReasonWithoutExpectedField_DoesNotInventOne()
    {
        var reason = UnconfirmedChecks.Reason(Check("body_count_unchanged", false, "1"));

        Assert.StartsWith("body_count_unchanged_not_confirmed", reason);
        Assert.Contains("наблюдалось 1", reason);
        Assert.DoesNotContain("ожидалось", reason);
    }
}
