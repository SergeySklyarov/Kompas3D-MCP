namespace KompasMcp.Domain.Files;

/// <summary>What a document file held at ONE moment: existence, size, SHA-256 and write time.</summary>
/// <remarks>INVARIANT: a stamp is taken BEFORE and AFTER a save, never only after it — "the file exists and
/// is not empty" is true of the PREVIOUS content as well, so it proves nothing about this call.
/// History: docs/decisions/files.md#save-file-write</remarks>
public sealed record SavedFileStamp(bool Exists, long ByteLength, string? Sha256, DateTime WrittenUtcUtc)
{
    /// <summary>The file is not there. A separate value from "unreadable": one is a fact, the other is not.</summary>
    public static readonly SavedFileStamp Absent = new(false, 0, null, default);
}

/// <summary>Proof that a save wrote the file, read from the FILE rather than from the call's answer.</summary>
/// <remarks>DOC: v24 promises only that <c>ksDocument3D.SaveAs</c> answers TRUE «в случае успешного
/// завершения» (<c>ksdocument3d_saveas.html</c>) and that <c>IKompasDocument.SaveAs</c> answers «false -
/// неуспешное (например, нет прав на запись/перезапись файла)» (<c>ksapi_ikompasdocument_saveas.html</c>);
/// it says nothing about a temporary file or its replacement. MEASURED: a write can end in
/// <c>&lt;имя&gt;.$$$</c> next to an UNTOUCHED target while the call answers TRUE, so the answer alone is
/// not a success claim.
/// History: docs/decisions/files.md#save-file-write</remarks>
public static class SavedFileWrite
{
    /// <summary>Key under which the file evidence travels inside an error's <c>details</c>.</summary>
    public const string DetailKey = "failure_snapshot";

    /// <summary>The temporary file KOMPAS keeps next to the target while saving (MEASURED name).</summary>
    public const string TemporarySuffix = ".$$$";

    /// <summary>The file as it is now, or <c>null</c> when it cannot be read at all — a failed read is NOT
    /// "absent", because the two demand different answers from the caller.</summary>
    public static SavedFileStamp? Capture(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return SavedFileStamp.Absent;
            }

            return new SavedFileStamp(true, info.Length, FileHash.Sha256(path), info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary><c>null</c> when the file proves this call wrote it; otherwise the reason it does not.</summary>
    /// <remarks>INVARIANT: a file that existed before must show a changed hash OR a later write time - the
    /// time alone would accept a target rewritten by the PREVIOUS save (MEASURED: two saves 1.26 s apart),
    /// the hash alone would refuse a re-save with unchanged bytes; the OR holds in both directions.
    /// LIMIT: a re-save whose bytes AND write time both stay identical is refused (MEASURED: KOMPAS rewrites
    /// the bytes on every save, so this was not observed).
    /// History: docs/decisions/files.md#save-file-write</remarks>
    public static string? UnconfirmedReason(SavedFileStamp? before, SavedFileStamp? after)
    {
        if (after is null)
        {
            return "target_unreadable";
        }

        if (!after.Exists)
        {
            return "target_missing";
        }

        if (after.ByteLength <= 0)
        {
            return "target_empty";
        }

        if (before is null)
        {
            return "target_before_unreadable";
        }

        if (!before.Exists)
        {
            return null;
        }

        if (!string.Equals(after.Sha256, before.Sha256, StringComparison.Ordinal))
        {
            return null;
        }

        return after.WrittenUtcUtc > before.WrittenUtcUtc ? null : "target_unchanged";
    }

    /// <summary>One stamp as it travels inside a refusal. The SAME keys on every state, and <c>state</c>
    /// explains the nulls: an absent file and an unread one are different facts.</summary>
    public static Dictionary<string, object?> Describe(SavedFileStamp? stamp) => stamp is null
        ? new Dictionary<string, object?>
        {
            ["state"] = "unreadable",
            ["bytes"] = null,
            ["sha256"] = null,
            ["written_utc"] = null,
        }
        : new Dictionary<string, object?>
        {
            ["state"] = stamp.Exists ? "present" : "absent",
            ["bytes"] = stamp.Exists ? stamp.ByteLength : null,
            ["sha256"] = stamp.Exists ? stamp.Sha256 : null,
            ["written_utc"] = stamp.Exists
                ? stamp.WrittenUtcUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture)
                : null,
        };
}
