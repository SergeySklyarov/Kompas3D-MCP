namespace KompasMcp.Domain.References;

/// <summary>Whether the two documented routes to a component's mates agree — a PURE verdict.</summary>
public sealed record DependentMateAgreement(bool Agree, string Detail);

/// <summary>The dependent-mate agreement rule — a PURE function, no KOMPAS.</summary>
/// <remarks>INVARIANT: the two routes answer the SAME question by different means — walking
/// <c>MateConstraintCollection</c> and matching each mate's <c>BaseObject1/2</c> → <c>IModelObject.Part</c>
/// against the component, and asking the kernel <c>IMateConstraints3D.ObjectConstraints</c>. A DISAGREEMENT
/// means the walk UNDER-COUNTED (a re-obtained RCW does not compare equal), and deleting on the smaller set
/// would remove a component while a mate is still attached. The disagreement is therefore a refusal, never a
/// preference for the smaller number.
/// History: docs/decisions/assembly.md#delete-component</remarks>
public static class DependentMates
{
    /// <summary>Compare the two counts.</summary>
    /// <param name="byCollectionWalk">Mates found by walking the collection and matching the owning component.</param>
    /// <param name="byKernel">Mates the kernel reports via <c>ObjectConstraints</c>; <c>null</c> — that read
    /// failed, so nothing was cross-checked (which is NAMED, not treated as agreement).</param>
    public static DependentMateAgreement Decide(int byCollectionWalk, int? byKernel)
    {
        if (byKernel is null)
        {
            return new DependentMateAgreement(false,
                "перекрёстная проверка ядром НЕ выполнена (ObjectConstraints не прочитан): перечень "
                + $"построен только перебором коллекции ({byCollectionWalk})");
        }

        if (byKernel == byCollectionWalk)
        {
            return new DependentMateAgreement(true,
                $"оба маршрута сошлись: перебор коллекции {byCollectionWalk}, "
                + $"ObjectConstraints(ядро) {byKernel}");
        }

        return new DependentMateAgreement(false,
            $"маршруты РАСХОДЯТСЯ: перебор коллекции {byCollectionWalk}, "
            + $"ObjectConstraints(ядро) {byKernel}");
    }
}
