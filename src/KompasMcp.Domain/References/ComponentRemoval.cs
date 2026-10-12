namespace KompasMcp.Domain.References;

/// <summary>What the re-read assembly structure says about a component deletion — a PURE verdict.</summary>
/// <param name="CountFell">The number of components fell by exactly one.</param>
/// <param name="AddressedRemoved">The entry that left is EXACTLY the addressed one, by identity.</param>
/// <param name="Ambiguous">The addressed identity occurred more than once before the deletion, so "the
/// addressed one left" cannot be told apart from "its twin left".</param>
/// <param name="Left">The entries that were present before and are gone after, in enumeration order.</param>
public sealed record ComponentRemovalVerdict(
    bool CountFell,
    bool AddressedRemoved,
    bool Ambiguous,
    IReadOnlyList<string> Left);

/// <summary>The component-deletion identity rule — a PURE function over opaque keys, no KOMPAS.</summary>
/// <remarks>INVARIANT: the component that left is decided by IDENTITY (source file and placement), NOT by the
/// ordinal — the remaining components' ordinals SHIFT, so "index 0 is gone" would be a claim about the
/// numbering rather than about the component. The keys are opaque strings built by the adapter; keeping the
/// comparison here means it is exercised without COM.
/// History: docs/decisions/assembly.md#delete-component</remarks>
public static class ComponentRemoval
{
    /// <summary>Reduce the before/after key lists to one verdict.</summary>
    /// <param name="before">Identity keys of every component before the deletion, in enumeration order.</param>
    /// <param name="after">Identity keys of every component after the deletion, in enumeration order.</param>
    /// <param name="addressedKey">Identity key of the component the caller addressed, or <c>null</c> when the
    /// ordinal was outside the structure (then nothing can be confirmed).</param>
    public static ComponentRemovalVerdict Decide(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        string? addressedKey)
    {
        // The removed entries are the MULTISET difference before minus after: two insertions of one file at one
        // placement are indistinguishable, and that limit is reported rather than smoothed over.
        var left = new List<string>(before);
        foreach (var key in after)
        {
            left.Remove(key);
        }

        var addressedOccurrences = addressedKey is null ? 0 : before.Count(key => key == addressedKey);
        var countFell = after.Count == before.Count - 1;
        var addressedRemoved = left.Count == 1 && addressedKey is not null && left[0] == addressedKey;

        return new ComponentRemovalVerdict(countFell, addressedRemoved, addressedOccurrences > 1, left);
    }
}
