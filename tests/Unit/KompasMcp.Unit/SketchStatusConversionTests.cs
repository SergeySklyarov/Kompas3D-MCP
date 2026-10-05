using KompasMcp.Contracts.Ipc;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Converting the raw <c>ksConstraintsStateEnum</c> into the published sketch-definiteness status.</summary>
/// <remarks>TEST: these tests check the PURE function <see cref="SketchStatusResult.FromRawState"/> and nothing
/// else. LIMIT: there is no live KOMPAS here, so no run of this file confirms the read route — the route is
/// confirmed by probe S (<c>docs/acceptance/api7/sketch-definition.json</c>), not by a mock value; the test on
/// value 3 is deliberately written NOT to count as proof of reading the state on a live model (see
/// <see cref="Redundancy_WithoutLiveVerification_StaysUnknown"/>). INVARIANT: three properties, each easy to
/// lose on the next edit — <c>is_fully_defined</c> is nullable (false vs null are different answers);
/// <c>degrees_of_freedom</c> is always null and never derived from the dimension count; an unknown enum value
/// is not a success and not "under-defined" but an honest <c>unknown</c> with a reason.</remarks>
public class SketchStatusConversionTests
{
    private static readonly string[] Diagnostics = { "Эскиз: Эскиз:1.", "Перенос в ISketch: ISketch напрямую." };
    private static readonly string[] Limitations = { "degrees_of_freedom_not_available" };

    private static SketchStatusResult Convert(int? raw, bool redundancyVerified = false) =>
        SketchStatusResult.FromRawState(raw, redundancyVerified, Diagnostics, Limitations);

    // ── Values 0/1/2 confirmed by the route ─────────────────────────────────────────────────────

    [Fact]
    public void WellConstrained_IsFullyDefined()
    {
        var result = Convert(1);

        Assert.Equal(SketchDefinitionStatus.FullyDefined, result.DefinitionStatus);
        Assert.True(result.IsFullyDefined);
        Assert.Equal(1, result.RawState);
        Assert.Equal("ksStateWellConstrained", result.NativeStateName);
        Assert.DoesNotContain(result.Limitations, l => l.StartsWith("unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderConstrained_IsUnderDefined()
    {
        // Minus, not "unknown": this is how KOMPAS answered for a free circle in S.5b.
        var result = Convert(2);

        Assert.Equal(SketchDefinitionStatus.UnderDefined, result.DefinitionStatus);
        Assert.False(result.IsFullyDefined);
        Assert.NotEqual(true, result.IsFullyDefined);
    }

    [Fact]
    public void UnknownState_IsUnknownNotUnderDefined()
    {
        // 0 is KOMPAS's "did not set" answer and is not equal to 2. Mixing them would declare an empty sketch
        // (S.5b: exactly this case) under-defined without grounds.
        var result = Convert(0);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Equal("ksStateUnknown", result.NativeStateName);
        Assert.Contains(result.Diagnostics, d => d.Contains("ksStateUnknown", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRead_IsUnknownAndKeepsNoRawState()
    {
        // raw == null means the call did not arrive (or arrived but has no value). That is not the product's
        // answer, and substituting 0 here would be an invention.
        var result = Convert(null);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Null(result.RawState);
        Assert.Null(result.NativeStateName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(97)]
    public void DegreesOfFreedom_IsNeverInvented(int raw)
    {
        // The route (ISketch.ConstraintsState) returns a state, not a counter. The number of degrees of freedom
        // is not summed from the dimension count: constraints tie objects together, so the sum of dimensions is
        // not the number of remaining freedoms. A zero here would be a claim the measurement never made.
        Assert.Null(Convert(raw, redundancyVerified: true).DegreesOfFreedom);
    }

    // ── Value 3: declared, but no live check obtained ───────────────────────────────────────────

    [Fact]
    public void Redundancy_WithoutLiveVerification_StaysUnknown()
    {
        // The key honesty test. Value 3 is declared in ksConstraintsStateEnum but never appeared in a confirmed
        // run on a live model (S.8: 46 shipped sketches), and no constraint-writing branch was found. So "we
        // have a mock" is no ground to publish the state as measured.
        var result = Convert(3, redundancyVerified: false);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Equal(3, result.RawState);
        Assert.Contains("unresolved_redundancy_not_verified", result.Limitations);
        Assert.Contains(result.Diagnostics, d => d.Contains("ksStateUnresolvedRedundancy", StringComparison.Ordinal));

        // The raw value is kept: "an unknown enum value" and "KOMPAS answered 3" are different things.
        Assert.Equal("ksStateUnresolvedRedundancy", result.NativeStateName);
    }

    [Fact]
    public void Redundancy_WithLiveVerification_IsNeedsAttention()
    {
        // If a live check ever appears the conversion is already ready — and it stays needs_attention, not
        // fully_defined: "needs attention" is not "+".
        var result = Convert(3, redundancyVerified: true);

        Assert.Equal(SketchDefinitionStatus.NeedsAttention, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.DoesNotContain("unresolved_redundancy_not_verified", result.Limitations);
    }

    [Fact]
    public void NeedsAttention_IsNotFullyDefinedAndNotUnderDefined()
    {
        // A claim about state distinguishability: "!" cannot be reduced to "+", to "−", or to a generic unknown
        // by field value.
        var needsAttention = Convert(3, redundancyVerified: true);

        Assert.NotEqual(SketchDefinitionStatus.FullyDefined, needsAttention.DefinitionStatus);
        Assert.NotEqual(SketchDefinitionStatus.UnderDefined, needsAttention.DefinitionStatus);
        Assert.NotEqual(true, needsAttention.IsFullyDefined);
        Assert.NotEqual(false, needsAttention.IsFullyDefined);
    }

    // ── Values outside the declared enumeration ─────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void UnknownEnumValue_IsNotTreatedAsSuccess(int raw)
    {
        // Strict prohibition from the statement: any unknown enum value → unknown, is_fully_defined=null, a
        // diagnostic reason. Not "under-defined" and not "defined": the product said something the server does
        // not understand, and it must be named.
        var result = Convert(raw);

        Assert.Equal(SketchDefinitionStatus.Unknown, result.DefinitionStatus);
        Assert.Null(result.IsFullyDefined);
        Assert.Contains("unknown_enum_value", result.Limitations);
        Assert.Equal(raw, result.RawState);
        Assert.Null(result.NativeStateName);
        Assert.Contains(result.Diagnostics, d => d.Contains(raw.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEnumValue_IsDistinguishableFromAnsweredUnknown()
    {
        // 0 and 42 give the same DefinitionStatus=Unknown but different Limitations and RawState. That is the
        // point of keeping the raw value separate from the normalised one.
        var answeredUnknown = Convert(0);
        var unknownValue = Convert(42);

        Assert.NotEqual(answeredUnknown.Limitations, unknownValue.Limitations);
        Assert.NotEqual(answeredUnknown.RawState, unknownValue.RawState);
        Assert.NotEqual(answeredUnknown.NativeStateName, unknownValue.NativeStateName);
        Assert.Equal(new[] { "degrees_of_freedom_not_available" }, answeredUnknown.Limitations);
        Assert.Equal(
            new[] { "degrees_of_freedom_not_available", "unknown_enum_value" },
            unknownValue.Limitations);
    }

    // ── Context attached by the caller is not lost ─────────────────────────────────────────────

    [Fact]
    public void CallerDiagnosticsAndLimitations_ArePreserved()
    {
        // The adapter passes in the transfer diagnostics and the DOF limitation. The function must not displace
        // them: the client reads the answer, not the adapter's internals.
        var result = SketchStatusResult.FromRawState(
            raw: 2,
            redundancyVerified: false,
            diagnostics: Diagnostics,
            limitations: Limitations,
            sketchName: "Эскиз:3",
            transferRoute: "IModelObject → ISketch");

        Assert.Equal(Diagnostics, result.Diagnostics);
        Assert.Equal(Limitations, result.Limitations);
        Assert.Equal("Эскиз:3", result.SketchName);
        Assert.Equal("IModelObject → ISketch", result.TransferRoute);
    }

    [Fact]
    public void CallerDiagnostics_ComeBeforeStatusSpecificOnes()
    {
        // The order matters to a human: first "which sketch and by which route it was read", then "why the
        // status is what it is".
        var result = SketchStatusResult.FromRawState(0, false, Diagnostics, Limitations);

        Assert.Equal(Diagnostics.Length + 1, result.Diagnostics.Count);
        Assert.Equal(Diagnostics[0], result.Diagnostics[0]);
        Assert.Contains("ksStateUnknown", result.Diagnostics[^1], StringComparison.Ordinal);
    }

    // ── The answer to the tool's main question ──────────────────────────────────────────────────

    [Fact]
    public void FullyDefined_IsTheOnlyStateAnsweringYes()
    {
        // "Is this sketch fully defined right now?" — exactly one state answers true. Everything else, including
        // "not set", gives no right to say "yes".
        var yes = new[] { 1 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == true);
        var everythingElse = new[] { 0, 2, 3, 4, 42 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == true);

        Assert.Equal(1, yes);
        Assert.Equal(0, everythingElse);
    }

    [Fact]
    public void UnderDefined_IsTheOnlyStateAnsweringNo()
    {
        // The mirror property, no less important: a "no" answer is allowed only where the product explicitly
        // said "under-defined". "Unknown" does not turn into "no".
        var no = new[] { 2 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == false);
        var everythingElse = new[] { 0, 1, 3, 4, 42, 99 }.Select(r => Convert(r)).Count(r => r.IsFullyDefined == false);

        Assert.Equal(1, no);
        Assert.Equal(0, everythingElse);
    }
}
