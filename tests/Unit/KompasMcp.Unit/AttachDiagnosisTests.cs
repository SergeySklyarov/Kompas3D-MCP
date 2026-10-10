using KompasMcp.Contracts;
using KompasMcp.Domain.Applications;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The attach refusal when no KOMPAS instance is available: the code and the wording as a table.</summary>
/// <remarks>INVARIANT: "no KOMPAS entry in the ROT" is NOT ambiguity — <c>AMBIGUOUS_APPLICATION</c> stays
/// for the case where instances really are more than one. INVARIANT: "the process is not running" and "it
/// runs but is not in the ROT" are different diagnoses, and both are named.
/// History: docs/decisions/adapter-core.md#attach-diagnosis</remarks>
public class AttachDiagnosisTests
{
    [Fact]
    public void NoKompasEntry_IsNotAmbiguous()
    {
        var refusal = AttachDiagnosis.NoInstance(kompasProcessCount: 0, rotTotalEntries: 2, rotKompasEntries: 0);

        Assert.NotEqual(ErrorCodes.AmbiguousApplication, refusal.Code);
        Assert.Equal(ErrorCodes.ApplicationDisconnected, refusal.Code);
        Assert.Equal(AttachDiagnosis.ProcessNotRunning, refusal.ReasonCode);
        Assert.Contains("не запущен", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KompasRunningButNotInRot_NamesThatReason()
    {
        var refusal = AttachDiagnosis.NoInstance(kompasProcessCount: 1, rotTotalEntries: 2, rotKompasEntries: 0);

        Assert.Equal(AttachDiagnosis.ProcessNotInRot, refusal.ReasonCode);
        Assert.Contains("не зарегистрирован", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("другой пользователь", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRot_NamesThatReason()
    {
        var refusal = AttachDiagnosis.NoInstance(kompasProcessCount: 0, rotTotalEntries: 0, rotKompasEntries: 0);

        Assert.Equal(AttachDiagnosis.RotEmpty, refusal.ReasonCode);
        Assert.Contains("ни одной записи", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    [InlineData(2, 5)]
    public void EveryBranch_SaysAttachWasNotPerformedAndPointsAtLaunch(int processes, int total)
    {
        var refusal = AttachDiagnosis.NoInstance(processes, total, rotKompasEntries: 0);

        Assert.Contains("mode=launch", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("attach не найден", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }
}
