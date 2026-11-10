using KompasMcp.Api5Adapter;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Input checks of the component array ALONG A CURVE: refusals before COM.</summary>
/// <remarks>INVARIANT: every rule the work order names is answered BEFORE anything is written to the model, so the
/// client sees its own error rather than a kernel refusal that leaves an unbuilt pattern in the tree. The one
/// capability refusal (a chained trajectory) is a NAMED limit: several curves in one trajectory were never
/// measured, and writing them would pass an unmeasured mode off as supported.
/// History: docs/decisions/assembly-ext.md#path-pattern</remarks>
public class PatternPathValidationTests
{
    private static PatternPathCommand Path(string[] curveRefs, int count = 3, double? step = 30d,
        bool byStep = true) => new()
    {
        DocumentId = "asm",
        SourceRefs = ["component:1"],
        CurveRefs = curveRefs,
        Count = count,
        StepMm = step,
        ByStep = byStep,
        ExpectedRevision = 1,
    };

    private static string CodeOf(Action action)
    {
        var refusal = Assert.Throws<KompasContractException>(action);
        return refusal.Code;
    }

    /// <summary>The work order names an empty trajectory explicitly: nothing to copy along.</summary>
    [Fact]
    public void Path_NoCurves_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, CodeOf(() => Api5Session.ValidatePatternPath(Path([]))));
    }

    /// <summary>A trajectory is ONE curve; joining several was not measured and is not written.</summary>
    [Fact]
    public void Path_TwoCurves_IsCapabilityUnavailable()
    {
        Assert.Equal(ErrorCodes.CapabilityUnavailable,
            CodeOf(() => Api5Session.ValidatePatternPath(Path(["edge:1", "edge:2"]))));
    }

    /// <summary>One instance is not a pattern.</summary>
    [Fact]
    public void Path_CountBelowTwo_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidatePatternPath(Path(["edge:1"], count: 1))));
    }

    /// <summary>Step mode without a positive step would stack every instance on the trajectory's start.</summary>
    [Fact]
    public void Path_StepModeWithoutStep_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidatePatternPath(Path(["edge:1"], step: null))));
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidatePatternPath(Path(["edge:1"], step: 0d))));
    }

    /// <summary>Along the whole trajectory the step is the model's business, so it may be absent.</summary>
    [Fact]
    public void Path_FullLengthWithoutStep_DoesNotRefuse()
    {
        Api5Session.ValidatePatternPath(Path(["edge:1"], step: null, byStep: false));
    }

    /// <summary>A pattern needs its sources, exactly as the grid mode does.</summary>
    [Fact]
    public void Path_NoSources_IsInvalidArgument()
    {
        var command = Path(["edge:1"]) with { SourceRefs = [] };

        Assert.Equal(ErrorCodes.InvalidArgument, CodeOf(() => Api5Session.ValidatePatternPath(command)));
    }

    /// <summary>A conforming request passes the pre-COM checks and reaches no refusal.</summary>
    [Fact]
    public void Path_Valid_DoesNotRefuse()
    {
        Api5Session.ValidatePatternPath(Path(["edge:1"]));
    }
}
