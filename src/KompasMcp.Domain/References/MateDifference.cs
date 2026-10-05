namespace KompasMcp.Domain.References;

/// <summary>One row of a mate-collection snapshot: the stable signature and the read validity.</summary>
public sealed record MateSnapshotRow(string Signature, bool? Valid);

/// <summary>Outcome of comparing two snapshots: the index of the single added row (in the AFTER snapshot),
/// how many rows were not matched, and how many matched rows went from valid to invalid.</summary>
public sealed record MateDifferenceResult(int? AddedIndex, int AddedCount, int NewlyInvalid);

/// <summary>Finds the mate a create call added, by multiset difference of stable signatures. Pure, no KOMPAS.</summary>
/// <remarks>INVARIANT: the signature holds only what the creating call fixes (type, fixed, parameter,
/// alignment, direction, base objects); validity is NOT part of it and never locates the new row.
/// MEASURED: a new mate can make an older one invalid (distance on faces already coincident), so a
/// signature with validity changes for an untouched row and the difference gives two rows.
/// TEST: MateDifferenceTests.
/// History: docs/decisions/mates.md#new-mate-signature</remarks>
public static class MateDifference
{
    public static MateDifferenceResult Find(IReadOnlyList<MateSnapshotRow> before, IReadOnlyList<MateSnapshotRow> after)
    {
        var remaining = before.ToList();
        int? added = null;
        var addedCount = 0;
        var newlyInvalid = 0;

        for (var i = 0; i < after.Count; i++)
        {
            var row = after[i];

            // Among equal signatures a row with the same validity is matched first, so duplicates do not
            // invent a validity change.
            var index = remaining.FindIndex(r => r.Signature == row.Signature && r.Valid == row.Valid);
            if (index < 0)
            {
                index = remaining.FindIndex(r => r.Signature == row.Signature);
            }

            if (index < 0)
            {
                addedCount++;
                added = i;
                continue;
            }

            if (remaining[index].Valid == true && row.Valid == false)
            {
                newlyInvalid++;
            }

            remaining.RemoveAt(index);
        }

        return new MateDifferenceResult(addedCount == 1 ? added : null, addedCount, newlyInvalid);
    }
}
