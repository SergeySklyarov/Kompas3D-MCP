using KompasMcp.Domain.Drawings;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The client's word for a section view's picture, and the rule that an edit cannot switch it.</summary>
/// <remarks>TEST: the choice is made from a word alone, so a wrong word is refused without КОМПАС, and the
/// edit rule is checked without a built view. History: docs/decisions/drawings.md#section-kind</remarks>
public class SectionKindsTests
{
    [Fact]
    public void NoKindMeansTheCut()
    {
        Assert.False(SectionKinds.IsSection(null));
        Assert.True(SectionKinds.Published(null));
    }

    [Fact]
    public void CutAndSectionWordsMapToTheSectionValue()
    {
        Assert.False(SectionKinds.IsSection("cut"));
        Assert.True(SectionKinds.IsSection("section"));
    }

    [Fact]
    public void UnknownWordIsNotPublishedAndHasNoValue()
    {
        Assert.False(SectionKinds.Published("local"));
        Assert.Null(SectionKinds.IsSection("local"));
    }

    [Fact]
    public void ReadValueIsNamedInTheClientsWords()
    {
        Assert.Equal("cut", SectionKinds.Name(false));
        Assert.Equal("section", SectionKinds.Name(true));
        Assert.Null(SectionKinds.Name(null));
    }

    [Fact]
    public void EditMayNotSwitchTheKindOfABuiltView()
    {
        Assert.True(SectionKinds.SameKind(false, null));
        Assert.True(SectionKinds.SameKind(false, "cut"));
        Assert.False(SectionKinds.SameKind(false, "section"));
        Assert.True(SectionKinds.SameKind(true, "section"));
        Assert.False(SectionKinds.SameKind(true, "cut"));
        Assert.False(SectionKinds.SameKind(null, "cut"));
    }
}
