using KompasMcp.Domain.Imaging;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The published view projections — what the <c>view</c> argument of <c>kompas_export_image</c>
/// rests on.</summary>
/// <remarks>INVARIANT: the caller names a projection by an ASCII wire name; the kernel is addressed by the
/// <c>ksViewProjectionType</c> code. The two are kept apart because the kernel's own names ("#Спереди") are
/// localized, and a localized name in a contract breaks the moment the product language changes.
/// LIMIT: accepting a name outside the list would make the tool fail at APPLY time — inside COM, after the
/// document was already touched — instead of at the argument, where the refusal costs nothing.</remarks>
public class ViewProjectionTests
{
    [Theory]
    [InlineData("front", 1)]
    [InlineData("rear", 2)]
    [InlineData("up", 3)]
    [InlineData("down", 4)]
    [InlineData("left", 5)]
    [InlineData("right", 6)]
    [InlineData("isometric", 7)]
    public void Published_name_resolves_to_the_documented_type(string wire, int type)
    {
        Assert.True(ViewProjections.TryResolve(wire, out var spec));
        Assert.Equal(type, spec.Type);
        Assert.Equal(wire, spec.Wire);
    }

    [Fact]
    public void Name_is_matched_without_regard_to_case()
    {
        Assert.True(ViewProjections.TryResolve("Isometric", out var spec));
        Assert.Equal(7, spec.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_name_is_not_resolved(string? wire)
    {
        // An absent name means "do not touch the view", not "pick a default": resolving it to a
        // projection would silently rotate the user's window.
        Assert.False(ViewProjections.TryResolve(wire, out _));
    }

    [Theory]
    [InlineData("Спереди")]
    [InlineData("#Спереди")]
    [InlineData("vp_Front")]
    [InlineData("axonometric")]
    [InlineData("8")]
    public void Unknown_name_is_refused_not_substituted(string wire)
    {
        // A substituted projection is indistinguishable to the caller from the one requested.
        Assert.False(ViewProjections.TryResolve(wire, out _));
    }

    [Theory]
    [InlineData(1, "front")]
    [InlineData(7, "isometric")]
    [InlineData(10, "type:10")]
    [InlineData(-1, "type:-1")]
    public void Read_back_type_is_described_or_named_by_its_number(int type, string expected)
    {
        // A type outside the published list is NAMED by its number rather than mapped onto a neighbour:
        // the read-back is evidence, and rounding it would substitute a measurement for a guess.
        Assert.Equal(expected, ViewProjections.Describe(type));
    }

    [Fact]
    public void Published_list_matches_the_wire_names()
    {
        Assert.Equal(ViewProjections.All.Select(spec => spec.Wire).ToArray(), ViewProjections.WireNames);
        Assert.Equal(7, ViewProjections.WireNames.Count);
        Assert.DoesNotContain("vpNone", ViewProjections.WireNames);
    }

    [Fact]
    public void Wire_names_are_ascii()
    {
        // The contract carries ASCII because the product's own names are localized; a Cyrillic wire name
        // would break the moment the product language changes.
        foreach (var name in ViewProjections.WireNames)
        {
            Assert.All(name, ch => Assert.True(ch < 128, $"'{name}' содержит не-ASCII символ '{ch}'"));
        }
    }
}
