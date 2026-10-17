using KompasMcp.Contracts;
using KompasMcp.Domain.Applications;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Which instance a kill-on-close job may guard, and how a refused guard is worded.</summary>
/// <remarks>TEST: both answers are a pure function of the ownership and the Win32 step, so they are held
/// without КОМПАС and without a job object.
/// History: docs/decisions/host.md#client-shutdown</remarks>
public class OwnedInstanceGuardTests
{
    /// <summary>The instance this server started is the only one a job may end.</summary>
    [Fact]
    public void ShouldGuard_IsTrueForTheLaunchedInstance()
    {
        Assert.True(OwnedInstanceGuard.ShouldGuard(ApplicationOwnership.Launched));
    }

    /// <summary>An attached instance is the user's process: our exit must never end it.</summary>
    [Theory]
    [InlineData(ApplicationOwnership.Attached)]
    [InlineData(ApplicationOwnership.Unknown)]
    public void ShouldGuard_IsFalseForEverythingElse(ApplicationOwnership ownership)
    {
        Assert.False(OwnedInstanceGuard.ShouldGuard(ownership));
    }

    /// <summary>A refusal names the Win32 step and the code, and says what it costs.</summary>
    [Fact]
    public void FailureText_NamesTheStepAndTheCode()
    {
        var text = OwnedInstanceGuard.FailureText("AssignProcessToJobObject", 5);

        Assert.Contains("AssignProcessToJobObject", text, StringComparison.Ordinal);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.Contains("жёсткое снятие", text, StringComparison.Ordinal);
    }

    /// <summary>Two different steps must not read the same: the message is the only diagnosis there is.</summary>
    [Fact]
    public void FailureText_DistinguishesTheSteps()
    {
        Assert.NotEqual(OwnedInstanceGuard.FailureText("CreateJobObject", 5),
            OwnedInstanceGuard.FailureText("OpenProcess", 5));
    }
}
