namespace KompasMcp.Domain.Geometry;

/// <summary>The one rule that turns a tree FEATURE's own kernel error into a refusal.</summary>
/// <remarks>INVARIANT: a write call that returned true is not an application. KOMPAS can leave a feature
/// in the tree carrying a non-zero <c>objectError</c> while the call reported success, so the feature's
/// own state is the observable that decides.
/// INVARIANT: only the FEATURE-error half of the hole rule is shared here. A measured no-op is a
/// refusal for a hole alone (a hole always removes material); for an edit a no-op call is legitimate,
/// so that half stays in <see cref="HoleEffectPolicy"/>.
/// History: docs/decisions/adapter-features.md#feature-error-refusal</remarks>
public static class FeatureErrorRule
{
    /// <summary>The reason inside <c>details.code</c> for a created or edited feature that carries a
    /// kernel error. The hole keeps its own, more specific code.</summary>
    public const string DetailCode = "feature_error";

    /// <summary>Whether the feature's own error makes the call a refusal. <c>null</c> — the state was not
    /// read, which is a named gap and never a refusal.</summary>
    public static bool IsRefusal(int? objectError) => objectError is int code && code != 0;
}
