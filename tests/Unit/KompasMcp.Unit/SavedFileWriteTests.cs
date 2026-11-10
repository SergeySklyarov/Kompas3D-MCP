using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Proof of a save is read from the FILE: what a stamp taken after the call must show, against the
/// stamp taken before it, temporary file included.</summary>
/// <remarks>INVARIANT: a file that existed before the call must prove THIS call wrote it - a changed hash or
/// a later write time; "the file exists and is not empty" is true of the previous content as well.
/// TEST: the rule is pure, so the failure the order was written for (the call answers TRUE, the content stays
/// in '&lt;имя&gt;.$$$', the target keeps its bytes) is held here without waiting for the rare case.
/// History: docs/decisions/files.md#save-file-write</remarks>
public class SavedFileWriteTests
{
    private static SavedFileStamp Stamp(long bytes, string sha, DateTime writtenUtc) =>
        new(true, bytes, sha, writtenUtc);

    private static readonly DateTime Moment = new(2026, 11, 8, 13, 21, 1, 380, DateTimeKind.Utc);

    /// <summary>The defect itself: the call answers TRUE, the target is byte-for-byte the old content and its
    /// write time did not move. Nothing is confirmed.</summary>
    /// <remarks>WHY the write time is compared with the PREVIOUS stamp, not with the call's clock: the defect
    /// was caught on a second save 1.26 s after the first, so "the write time is close to the call" would have
    /// accepted the old file.
    /// History: docs/decisions/files.md#save-file-write</remarks>
    [Fact]
    public void UnchangedTarget_IsNotConfirmed()
    {
        var before = Stamp(37472, "85ca12", Moment);
        var after = Stamp(37472, "85ca12", Moment);

        Assert.Equal("target_unchanged", SavedFileWrite.UnconfirmedReason(
            before, after, SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    [Fact]
    public void ChangedContent_IsConfirmedEvenWithTheSameWriteTime()
    {
        var before = Stamp(10, "aaaa", Moment);
        var after = Stamp(11, "bbbb", Moment);

        Assert.Null(SavedFileWrite.UnconfirmedReason(
            before, after, SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    /// <summary>A re-save of an unchanged model can write the SAME bytes; the later write time is then the
    /// only proof, and it is enough while no temporary file appeared.</summary>
    [Fact]
    public void LaterWriteTime_IsConfirmedEvenWithTheSameHash()
    {
        var before = Stamp(10, "aaaa", Moment);
        var after = Stamp(10, "aaaa", Moment.AddMilliseconds(1));

        Assert.Null(SavedFileWrite.UnconfirmedReason(
            before, after, SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    /// <summary>The measured defect: the target kept its bytes and moved only its write time, while the call
    /// created the temporary file that holds the model's content. The moved time is not proof.</summary>
    [Fact]
    public void TemporaryFileThatAppearedBesideAnUnchangedTarget_IsNotConfirmed()
    {
        var before = Stamp(42194, "1d483ccd", Moment);
        var after = Stamp(42194, "1d483ccd", Moment.AddSeconds(2));
        var temporaryAfter = Stamp(42253, "66c92249", Moment.AddSeconds(2));

        Assert.Equal(SavedFileWrite.ContentLeftInTemporary, SavedFileWrite.UnconfirmedReason(
            before, after, SavedFileStamp.Absent, temporaryAfter));
    }

    /// <summary>A temporary file that was already there and changed during the call is the same evidence: the
    /// content went into it and the target kept the old bytes.</summary>
    [Fact]
    public void TemporaryFileWrittenByTheCallBesideAnUnchangedTarget_IsNotConfirmed()
    {
        var before = Stamp(10, "aaaa", Moment);
        var after = Stamp(10, "aaaa", Moment.AddSeconds(1));
        var temporaryBefore = Stamp(20, "bbbb", Moment);
        var temporaryAfter = Stamp(21, "cccc", Moment.AddSeconds(1));

        Assert.Equal(SavedFileWrite.ContentLeftInTemporary, SavedFileWrite.UnconfirmedReason(
            before, after, temporaryBefore, temporaryAfter));
    }

    /// <summary>An old temporary file the call did not touch proves nothing about this call, so the later
    /// write time of the target stays the proof.</summary>
    [Fact]
    public void OldTemporaryFileTheCallDidNotTouch_IsConfirmed()
    {
        var before = Stamp(10, "aaaa", Moment);
        var after = Stamp(10, "aaaa", Moment.AddSeconds(1));
        var temporary = Stamp(20, "bbbb", Moment);

        Assert.Null(SavedFileWrite.UnconfirmedReason(before, after, temporary, temporary));
    }

    /// <summary>The target itself changed: this call wrote it, and the temporary file beside it says nothing
    /// against that.</summary>
    [Fact]
    public void TemporaryFileBesideAChangedTarget_IsConfirmed()
    {
        var before = Stamp(10, "aaaa", Moment);
        var after = Stamp(11, "bbbb", Moment.AddSeconds(1));
        var temporaryAfter = Stamp(12, "cccc", Moment.AddSeconds(1));

        Assert.Null(SavedFileWrite.UnconfirmedReason(
            before, after, SavedFileStamp.Absent, temporaryAfter));
    }

    [Fact]
    public void FileThatDidNotExistBefore_IsConfirmedByItsAppearance()
    {
        Assert.Null(SavedFileWrite.UnconfirmedReason(
            SavedFileStamp.Absent, Stamp(41579, "cccc", Moment), SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    [Fact]
    public void MissingTarget_IsNotConfirmed()
    {
        Assert.Equal("target_missing", SavedFileWrite.UnconfirmedReason(
            Stamp(10, "aaaa", Moment), SavedFileStamp.Absent, SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    [Fact]
    public void EmptyTarget_IsNotConfirmed()
    {
        Assert.Equal("target_empty", SavedFileWrite.UnconfirmedReason(
            Stamp(10, "aaaa", Moment), Stamp(0, "", Moment.AddSeconds(1)),
            SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    [Fact]
    public void UnreadableTarget_IsNotConfirmed()
    {
        Assert.Equal("target_unreadable", SavedFileWrite.UnconfirmedReason(
            Stamp(10, "aaaa", Moment), null, SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    /// <summary>An unreadable target BEFORE the call leaves nothing to compare against: the fact that a file
    /// is there now proves no write.</summary>
    [Fact]
    public void UnreadableTargetBefore_IsNotConfirmed()
    {
        Assert.Equal("target_before_unreadable", SavedFileWrite.UnconfirmedReason(
            null, Stamp(10, "aaaa", Moment), SavedFileStamp.Absent, SavedFileStamp.Absent));
    }

    /// <summary>Only the content-left-in-temporary case is worth repeating, and only once: the next save is
    /// the same documented call, and a kernel that keeps writing to the temporary file must not be looped on.
    /// </summary>
    [Fact]
    public void Repeat_IsAllowedOnlyForTheTemporaryFileReasonAndOnlyOnce()
    {
        Assert.True(SavedFileWrite.MayRepeat(SavedFileWrite.ContentLeftInTemporary, 1));
        Assert.False(SavedFileWrite.MayRepeat(SavedFileWrite.ContentLeftInTemporary, 2));
        Assert.False(SavedFileWrite.MayRepeat("target_unchanged", 1));
        Assert.False(SavedFileWrite.MayRepeat("target_missing", 1));
    }

    [Fact]
    public void Capture_OfAnAbsentFile_IsAbsentNotUnreadable()
    {
        var path = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "save-" + Guid.NewGuid().ToString("N")[..8]);

        var stamp = SavedFileWrite.Capture(path);

        Assert.NotNull(stamp);
        Assert.False(stamp.Exists);
    }

    [Fact]
    public void Capture_ReadsSizeAndHashOfTheFileOnDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "save-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "part.m3d");
        var bytes = System.Text.Encoding.UTF8.GetBytes("save-write");
        File.WriteAllBytes(path, bytes);
        try
        {
            var stamp = SavedFileWrite.Capture(path);

            Assert.NotNull(stamp);
            Assert.True(stamp.Exists);
            Assert.Equal(bytes.Length, stamp.ByteLength);
            Assert.Equal(FileHash.Sha256(bytes), stamp.Sha256);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Describe_CarriesTheSameKeysForEveryState()
    {
        var absent = SavedFileWrite.Describe(SavedFileStamp.Absent);
        var present = SavedFileWrite.Describe(Stamp(10, "aaaa", Moment));
        var unreadable = SavedFileWrite.Describe(null);

        Assert.Equal(absent.Keys.OrderBy(k => k, StringComparer.Ordinal),
            present.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(absent.Keys.OrderBy(k => k, StringComparer.Ordinal),
            unreadable.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("absent", absent["state"]);
        Assert.Equal("present", present["state"]);
        Assert.Equal("unreadable", unreadable["state"]);
    }
}
