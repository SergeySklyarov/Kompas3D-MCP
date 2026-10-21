using System.Globalization;

namespace KompasMcp.Domain.Geometry;

/// <summary>Whether a dynamic cross-section call may answer success.</summary>
/// <remarks>INVARIANT: a section exists only when the counter grew by exactly one AND the added row reads
/// back with the written step. MEASURED defect: <c>kompas_create_section</c> answered success while the
/// counter stayed 0 and the model held no section, so a caller took an absent cut for a created one.
/// A grown counter alone is not enough either: <c>AddDynamicCrossSection()</c> can leave a section without
/// the written step, and that row must be named, not reported as the requested cut.
/// History: docs/decisions/sections.md#assembly-section</remarks>
public static class SectionCreationRule
{
    /// <summary>Reason named when the counter did not move.</summary>
    public const string CountUnchanged = "sections_count_unchanged";

    /// <summary>Reason named when the counter moved by something other than one.</summary>
    public const string CountJumped = "sections_count_changed_by_more_than_one";

    /// <summary>Reason named when the added row does not carry the written step.</summary>
    public const string StepMissing = "written_step_not_read_back";

    /// <summary>The section counts before and after the write, as the model answered them.</summary>
    public static bool GrewByOne(int? before, int? after) =>
        before is not null && after is not null && after == before + 1;

    /// <summary>Decide from the counter and the read-back row. <paramref name="writtenOffsets"/> are the
    /// offsets the call wrote, <paramref name="readOffsets"/> the ones the new row returned; a written
    /// offset with no read value is not confirmed.</summary>
    /// <remarks>ASSUMPTION: offsets compare with the same 1e-6 mm tolerance the plane write is checked
    /// with, because the core stores millimetres as doubles.</remarks>
    public static string? RefusalReason(
        int? before,
        int? after,
        IReadOnlyList<double> writtenOffsets,
        IReadOnlyList<double?> readOffsets)
    {
        if (before is null || after is null)
        {
            return CountUnchanged;
        }

        if (after != before + 1)
        {
            return after == before ? CountUnchanged : CountJumped;
        }

        if (readOffsets.Count != writtenOffsets.Count)
        {
            return StepMissing;
        }

        for (var index = 0; index < writtenOffsets.Count; index++)
        {
            if (readOffsets[index] is not double read
                || Math.Abs(read - writtenOffsets[index]) > 1e-6)
            {
                return StepMissing;
            }
        }

        return null;
    }

    /// <summary>Reason as a detail field value: the reason plus the two counts, so a client can log it
    /// without parsing the message.</summary>
    public static string Detail(string reason, int? before, int? after) =>
        reason + " (before=" + (before?.ToString(CultureInfo.InvariantCulture) ?? "null") +
        ", after=" + (after?.ToString(CultureInfo.InvariantCulture) ?? "null") + ")";
}
