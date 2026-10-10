using KompasMcp.Contracts;

namespace KompasMcp.Domain.Documents;

/// <summary>Whether a document kind HAS a model feature tree, and the wording to use when it does not.</summary>
/// <remarks>INVARIANT: the feature tree belongs to a 3D model, and a 3D model exists only for a part or an
/// assembly. A drawing (and a fragment) has no 3D descriptor BY CONSTRUCTION, so a feature count read on it
/// is not "unread" but NOT APPLICABLE — the two states are different claims and are named differently.
/// DOC: the 3D route is reached through <c>ksDocument3D</c>; a drawing is opened and created through the 2D
/// route (<c>ksdocument3d_open.html</c>, <c>ksdocument2d_kscreatedocument.html</c>), so no part handle exists.
/// History: docs/decisions/contracts.md#feature-left-in-tree</remarks>
public static class ModelFeatureTree
{
    /// <summary>Whether the model feature tree applies to this document kind.</summary>
    public static bool Applies(DocumentKind kind) =>
        kind is DocumentKind.Part or DocumentKind.Assembly;

    /// <summary>The note carried to the client when the tree does NOT apply, so that "no tree" is never
    /// read as "the count came back zero".</summary>
    public static string NotApplicableNote(DocumentKind kind) => kind switch
    {
        DocumentKind.Drawing => "неприменимо: у чертежа нет дерева признаков модели",
        DocumentKind.Fragment => "неприменимо: у фрагмента нет дерева признаков модели",
        _ => "неприменимо: у документа этого вида нет дерева признаков модели",
    };
}
