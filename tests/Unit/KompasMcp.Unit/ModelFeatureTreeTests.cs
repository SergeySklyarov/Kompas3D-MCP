using KompasMcp.Contracts;
using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Which document kinds have a model feature tree, and how "not applicable" is worded.</summary>
/// <remarks>INVARIANT: the feature tree belongs to a 3D model, so a drawing (and a fragment) has none by
/// construction. The count read for a drawing must be reported as NOT APPLICABLE, never as "unread" and
/// never as zero — a mutation of a drawing is not refused because its feature count could not be measured.
/// History: docs/decisions/contracts.md#feature-left-in-tree</remarks>
public class ModelFeatureTreeTests
{
    [Theory]
    [InlineData(DocumentKind.Part)]
    [InlineData(DocumentKind.Assembly)]
    public void ThreeDimensionalKinds_HaveAFeatureTree(DocumentKind kind)
    {
        Assert.True(ModelFeatureTree.Applies(kind));
    }

    [Theory]
    [InlineData(DocumentKind.Drawing)]
    [InlineData(DocumentKind.Fragment)]
    public void TwoDimensionalKinds_HaveNoFeatureTree(DocumentKind kind)
    {
        Assert.False(ModelFeatureTree.Applies(kind));
    }

    [Fact]
    public void Drawing_NoteNamesTheDrawingAndSaysNotApplicable()
    {
        var note = ModelFeatureTree.NotApplicableNote(DocumentKind.Drawing);

        Assert.Contains("неприменимо", note, StringComparison.Ordinal);
        Assert.Contains("черт", note, StringComparison.Ordinal);
        Assert.Contains("нет дерева признаков", note, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKind_HasANonEmptyNote()
    {
        foreach (var kind in Enum.GetValues<DocumentKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ModelFeatureTree.NotApplicableNote(kind)));
        }
    }
}
