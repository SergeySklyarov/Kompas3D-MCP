using KompasMcp.Api5Adapter;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Input checks of the component-pattern mode and the excluded-instance edit: refusals BEFORE COM.</summary>
/// <remarks>INVARIANT: a request that cannot be a component pattern is answered INVALID_ARGUMENT, and the
/// excluded-instance set is CAPABILITY_UNAVAILABLE — both before anything is written to the model, so the
/// client sees its own error and not a kernel refusal. These are the rules the work order names explicitly;
/// they are tested here without КОМПАС, because a refusal after the mutation is a different (and worse) event.
/// History: docs/decisions/assembly-ext.md#component-arrays</remarks>
public class ComponentPatternValidationTests
{
    private static PatternGridCommand Grid(string[] sourceRefs, int count1, int? count2 = null,
        double step1 = 30d, double? step2 = null) => new()
    {
        DocumentId = "asm",
        CopyKind = PatternCopyKind.Components,
        SourceRefs = sourceRefs,
        Axis1Point1Mm = [0d, 0d, 0d],
        Axis1Point2Mm = [1d, 0d, 0d],
        Step1Mm = step1,
        Count1 = count1,
        Count2 = count2,
        Step2Mm = step2,
        ExpectedRevision = 1,
    };

    private static PatternCircularCommand Circular(string[] sourceRefs, int count2, double step2Deg = 90d) => new()
    {
        DocumentId = "asm",
        CopyKind = PatternCopyKind.Components,
        SourceRefs = sourceRefs,
        AxisPoint1Mm = [0d, 0d, 0d],
        AxisPoint2Mm = [0d, 0d, 1d],
        Count2 = count2,
        Step2Deg = step2Deg,
        ExpectedRevision = 1,
    };

    private static string CodeOf(Action action)
    {
        var refusal = Assert.Throws<KompasContractException>(action);
        return refusal.Code;
    }

    /// <summary>A pattern with no sources is refused by name, not built from an empty set.</summary>
    [Fact]
    public void Grid_NoSources_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, CodeOf(() => Api5Session.ValidateComponentGrid(Grid([], 2))));
    }

    /// <summary>The work order names <c>count1 &lt; 2</c> explicitly: one instance is not a pattern.</summary>
    [Fact]
    public void Grid_Count1BelowTwo_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateComponentGrid(Grid(["component:1"], 1))));
    }

    /// <summary>A second direction with more than one instance needs its step.</summary>
    [Fact]
    public void Grid_Count2WithoutStep2_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateComponentGrid(Grid(["component:1"], 2, count2: 2))));
    }

    /// <summary>A valid component grid passes the pre-COM checks and reaches no refusal.</summary>
    [Fact]
    public void Grid_Valid_DoesNotRefuse()
    {
        Api5Session.ValidateComponentGrid(Grid(["component:1"], 3));
        Api5Session.ValidateComponentGrid(
            Grid(["component:1"], 2, count2: 2, step1: 30d, step2: 20d) with
            {
                Axis2Point1Mm = [0d, 0d, 0d],
                Axis2Point2Mm = [0d, 1d, 0d],
            });
    }

    /// <summary>A ring of one instance is not a pattern either.</summary>
    [Fact]
    public void Circular_Count2BelowTwo_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateComponentCircular(Circular(["component:1"], 1))));
    }

    /// <summary>A zero angular step would stack every instance on one position.</summary>
    [Fact]
    public void Circular_ZeroStep_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidateComponentCircular(Circular(["component:1"], 4, step2Deg: 0d))));
    }

    /// <summary>A degenerate axis is refused before COM: no refusal on an axis that does not exist.</summary>
    [Fact]
    public void Circular_DegenerateAxis_IsInvalidArgument()
    {
        var command = Circular(["component:1"], 4) with { AxisPoint2Mm = [0d, 0d, 0d] };

        Assert.Equal(ErrorCodes.InvalidArgument, CodeOf(() => Api5Session.ValidateComponentCircular(command)));
    }

    /// <summary>INVARIANT: the excluded-instance set is documented as writable and measured nowhere, so it is
    /// refused CAPABILITY_UNAVAILABLE — including on a component array, whose count the mode is proven by.</summary>
    [Fact]
    public void PatternEdit_InstanceDeletedIndexes_IsCapabilityUnavailable()
    {
        var edit = new PatternEditDto { InstanceDeletedIndexes = [[1, 0]] };

        var refusal = Assert.Throws<KompasContractException>(() => Api5Session.ValidatePatternEdit(edit));
        Assert.Equal(ErrorCodes.CapabilityUnavailable, refusal.Code);
    }

    /// <summary>An edit with no member at all stays INVALID_ARGUMENT, not the capability refusal.</summary>
    [Fact]
    public void PatternEdit_NoMembers_IsInvalidArgument()
    {
        Assert.Equal(ErrorCodes.InvalidArgument,
            CodeOf(() => Api5Session.ValidatePatternEdit(new PatternEditDto())));
    }
}
