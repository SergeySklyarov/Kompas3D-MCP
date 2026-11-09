using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Scenarios;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The reassignment loop and the refusals that follow the copy's SaveAs.</summary>
/// <remarks>INVARIANT: a replace is addressed by the row's position, so a reordered list must refuse before
/// the first replace; a refusal after the close names the document that is open, not the closed one.
/// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
public class AssemblyCheckpointTests : IDisposable
{
    private const string OpId = "3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private readonly string _directory;
    private readonly string _source;
    private readonly string _topCopy;

    public AssemblyCheckpointTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "asm-cp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _source = Path.Combine(_directory, "top.m3d");
        File.WriteAllText(_source, "top");
        _topCopy = Path.Combine(
            _directory, "control-copies", "scenario", OpId + "-r1", "top.m3d");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Part(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    private string CopyOf(string part) => Path.Combine(
        _directory, "control-copies", "scenario", OpId + "-r1", "files", "001-" + Path.GetFileName(part));

    private static JsonObject Row(string sourcePath, string name = "part", bool isDetail = true) => new()
    {
        ["component_ref"] = "ref-" + name,
        ["source_path"] = sourcePath,
        ["name"] = name,
        ["is_detail"] = isDetail,
        ["depth"] = 0,
    };

    /// <summary>A session that answers the checkpoint's calls from queues, and records what it was asked.</summary>
    private sealed class Session
    {
        public readonly List<string> Replaced = new();

        public readonly List<string> Tools = new();

        public Queue<JsonArray> Graphs { get; } = new();

        public Queue<JsonArray> Rows { get; } = new();

        public string CopyDocumentId { get; init; } = "copy-1";

        public string ReopenedDocumentId { get; init; } = "copy-2";

        public string CopyPath { get; init; } = string.Empty;

        public string SourcePath { get; init; } = string.Empty;

        /// <summary>A tool this session refuses, optionally only for one <c>access</c> value.</summary>
        public string? RefuseTool { get; init; }

        public string? RefuseAccess { get; init; }

        /// <summary>How many recursive component reads to answer before refusing the rest (the graph).</summary>
        public int RefuseGraphsAfter { get; init; } = -1;

        /// <summary>How many plain component reads to answer before refusing the rest (the copy's rows).</summary>
        public int RefuseRowsAfter { get; init; } = -1;

        /// <summary>A save WITHOUT a target path is refused; the top SaveAs (with a target) still succeeds.</summary>
        /// <remarks>The resave after the reassignment is the only save without a target path: it is the
        /// call the client failed on, so a test must be able to refuse it alone.</remarks>
        public bool RefuseSaveWithoutTarget { get; init; }

        /// <summary>What each save does to the file system: <c>true</c> leaves the model in
        /// <c>&lt;file&gt;.$$$</c> instead of replacing the file (MEASURED КОМПАС behaviour).</summary>
        public Queue<bool> LeaveTemporary { get; } = new();

        /// <summary>Every save leaves the temporary file: the file is never replaced.</summary>
        public bool LeaveTemporaryAlways { get; init; }

        /// <summary>The save number (1-based) from which every save leaves the temporary file.</summary>
        public int LeaveTemporaryFrom { get; init; } = -1;

        /// <summary>Read-only opens: the verification reads the file once per read.</summary>
        public int VerifyOpens { get; private set; }

        private int _graphsAnswered;

        private int _rowsAnswered;

        private int _saves;

        private bool LeavesTemporary()
        {
            if (LeaveTemporaryAlways)
            {
                return true;
            }

            if (LeaveTemporaryFrom >= 0 && _saves >= LeaveTemporaryFrom)
            {
                return true;
            }

            return LeaveTemporary.Count > 0 && LeaveTemporary.Dequeue();
        }

        public Task<ResultEnvelope<JsonNode?>> Invoke(
            string tool, JsonObject arguments, string? servicePath, CancellationToken cancellationToken)
        {
            Tools.Add(tool);
            var access = arguments["access"]?.GetValue<string>();
            if (tool == RefuseTool && (RefuseAccess is null || access == RefuseAccess))
            {
                return Task.FromResult(Refused(tool, "PATH_NOT_ALLOWED"));
            }

            if (tool == "kompas_list_components")
            {
                var recursive = arguments["recursive"]?.GetValue<bool>() == true;
                var answered = recursive ? _graphsAnswered++ : _rowsAnswered++;
                var limit = recursive ? RefuseGraphsAfter : RefuseRowsAfter;
                if (limit >= 0 && answered >= limit)
                {
                    return Task.FromResult(Refused(tool, "CAPABILITY_UNAVAILABLE"));
                }
            }

            return Task.FromResult(Answer(tool, arguments));
        }

        private static ResultEnvelope<JsonNode?> Refused(string tool, string code) => new()
        {
            Status = OperationStatus.Failed,
            Error = new ErrorDto(code, $"отказ вызова {tool}", RetryPolicy.Never, null, false, null),
        };

        private ResultEnvelope<JsonNode?> Answer(string tool, JsonObject arguments)
        {
            switch (tool)
            {
                case "kompas_list_documents":
                    var rows = new JsonArray();
                    if (arguments["path"] is null)
                    {
                        rows.Add(new JsonObject { ["id"] = "src-1", ["path"] = SourcePath });
                        rows.Add(new JsonObject { ["id"] = CopyDocumentId, ["path"] = CopyPath });
                    }

                    return Ok(rows);
                case "kompas_get_context":
                    return Ok(new JsonObject { ["dirty"] = false });
                case "kompas_list_components":
                    var queue = arguments["recursive"]?.GetValue<bool>() == true ? Graphs : Rows;
                    return Ok(new JsonObject
                    {
                        ["components"] = queue.Count > 0 ? queue.Dequeue() : new JsonArray(),
                    });
                case "kompas_save_document":
                    if (RefuseSaveWithoutTarget && arguments["target_path"] is null)
                    {
                        return Refused(tool, "SAVE_FAILED");
                    }

                    _saves++;
                    SimulateTemporary(arguments);
                    return new ResultEnvelope<JsonNode?>
                    {
                        Status = OperationStatus.Succeeded,
                        RevisionAfter = arguments["target_path"] is null ? 3 : 2,
                    };
                case "kompas_close_document":
                    return new ResultEnvelope<JsonNode?> { Status = OperationStatus.Succeeded };
                case "kompas_open_document":
                    var readOnly = (arguments["access"]?.GetValue<string>() ?? "edit") == "read_only";
                    if (readOnly)
                    {
                        VerifyOpens++;
                    }

                    return Ok(new JsonObject
                    {
                        ["document_id"] = readOnly ? "verify-1" : ReopenedDocumentId,
                    });
                case "kompas_replace_component":
                    Replaced.Add(arguments["component_ref"]!.GetValue<string>());
                    return new ResultEnvelope<JsonNode?>
                    {
                        Status = OperationStatus.Succeeded,
                        RevisionAfter = 3,
                        Result = new JsonObject
                        {
                            ["source_path_before"] = SourcePath,
                            ["source_path_after"] = arguments["source_path"]?.GetValue<string>(),
                        },
                    };
                default:
                    return new ResultEnvelope<JsonNode?>
                    {
                        Status = OperationStatus.Failed,
                        Error = new ErrorDto("unexpected_tool", tool, RetryPolicy.Never, null, false, null),
                    };
            }
        }

        private static ResultEnvelope<JsonNode?> Ok(JsonNode? result) =>
            new() { Status = OperationStatus.Succeeded, Result = result };

        /// <summary>Do to the files what КОМПАС does: either replace the file, or leave the model beside it.</summary>
        /// <remarks>MEASURED: a save can answer success and leave the model's content in
        /// <c>&lt;file&gt;.$$$</c> while the file keeps its previous bytes; the next save then replaces it.
        /// History: docs/decisions/scenario.md#checkpoint-settle</remarks>
        private void SimulateTemporary(JsonObject arguments)
        {
            var target = arguments["target_path"]?.GetValue<string>() ?? CopyPath;
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            var temporary = target + ".$$$";
            if (LeavesTemporary())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(temporary, "model");
                return;
            }

            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<AssemblyCheckpointOutcome> TakeAsync(Session session)
    {
        var checkpoint = new AssemblyCheckpoint(_directory, session.Invoke);
        var context = new ResultEnvelope<JsonNode?>
        {
            Status = OperationStatus.Succeeded,
            ApplicationId = "app-1",
            Result = new JsonObject
            {
                ["revision"] = 1,
                ["path"] = _source,
                ["kind"] = "assembly",
            },
        };

        return await checkpoint.TakeAsync(OpId, "src-1", context, AssemblyCheckpoint.ExternalPartsCopy,
            CancellationToken.None);
    }

    [Fact]
    public async Task ReorderedRows_RefuseBeforeTheFirstReplace()
    {
        var left = Part("left.m3d");
        var right = Part("right.m3d");
        var session = new Session { CopyPath = _topCopy, SourcePath = _source };
        session.Graphs.Enqueue(new JsonArray(Row(left, "left"), Row(right, "right")));
        session.Rows.Enqueue(new JsonArray(Row(left, "left"), Row(right, "right")));
        // The same NUMBER of rows, swapped: only the source of the row tells the two apart.
        session.Rows.Enqueue(new JsonArray(Row(right, "right"), Row(left, "left")));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Empty(session.Replaced);
        var details = outcome.Error!["details"]!.AsObject();
        Assert.Equal("top_refs_not_reassigned", details["cause"]!.GetValue<string>());
        Assert.Contains("порядок строк", outcome.Error!["message"]!.GetValue<string>());
        Assert.False(outcome.Report["taken"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RowThatAlreadyNamesItsCopy_IsNotARefusal()
    {
        var part = Part("part.m3d");
        var session = new Session { CopyPath = _topCopy, SourcePath = _source };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        // MEASURED: the list keeps the PREVIOUS source_path of a replaced row, so both values are legal.
        session.Rows.Enqueue(new JsonArray(Row(CopyOf(part))));
        session.Graphs.Enqueue(new JsonArray(Row(CopyOf(part))));

        var outcome = await TakeAsync(session);

        Assert.True(outcome.Ok);
        Assert.Single(session.Replaced);
        Assert.True(outcome.Report["taken"]!.GetValue<bool>());
    }

    [Fact]
    public async Task VerificationMismatch_NamesTheReopenedDocument()
    {
        var part = Part("part.m3d");
        var session = new Session { CopyPath = _topCopy, SourcePath = _source };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        // The copy still points at the client's file: the verification must refuse, and the refusal must
        // name the document that is open after the reopen — not the one closed before the check.
        session.Graphs.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_verified",
            outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("copy-2", outcome.Report["document_id_after"]!.GetValue<string>());
        Assert.NotEqual("copy-1", outcome.Report["document_id_after"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusedTopSave_CarriesThatCallsStepError()
    {
        var part = Part("part.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseTool = "kompas_save_document",
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_written", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("PATH_NOT_ALLOWED",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
        Assert.False(outcome.Report["taken"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RefusedReopen_CarriesThatCallsStepError()
    {
        var part = Part("part.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseTool = "kompas_open_document",
            RefuseAccess = "edit",
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Graphs.Enqueue(new JsonArray(Row(CopyOf(part))));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("reopen_failed", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("PATH_NOT_ALLOWED",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
        // The copy is closed at that point, so the answer must not offer its id as a live document.
        Assert.Null(outcome.Report["document_id_after"]);
    }

    [Fact]
    public async Task RefusedNestedCopyOpen_CarriesThatCallsStepError()
    {
        var sub = Part("sub.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseTool = "kompas_open_document",
            RefuseAccess = "edit",
        };
        // A nested ASSEMBLY is the only entry that is reopened for reassignment, so only it is refused.
        session.Graphs.Enqueue(new JsonArray(Row(sub, "sub", isDetail: false)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_written", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("PATH_NOT_ALLOWED",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
        Assert.Contains("вложенных сборок", outcome.Error!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusedVerificationOpen_CarriesThatCallsStepError()
    {
        var part = Part("part.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseTool = "kompas_open_document",
            RefuseAccess = "read_only",
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_verified", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("PATH_NOT_ALLOWED",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusedVerificationGraph_CarriesThatCallsStepError()
    {
        var part = Part("part.m3d");
        // The first recursive read builds the plan; the second one is the verification's.
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseGraphsAfter = 1,
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_verified", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("CAPABILITY_UNAVAILABLE",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task RefusedCopyRows_CarriesThatCallsStepError()
    {
        var part = Part("part.m3d");
        // No plain component read is answered: the reassignment cannot learn what the copy holds.
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseRowsAfter = 0,
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("top_refs_not_reassigned", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        Assert.Equal("CAPABILITY_UNAVAILABLE",
            outcome.Error!["step_error"]!["code"]!.GetValue<string>());
    }

    /// <summary>The refusal AFTER the top SaveAs: the copy is open, and the answer must name it.</summary>
    /// <remarks>INVARIANT: the client's own failure was the resave AFTER the SaveAs, so the answer on that
    /// path must name the open copy and say that the client's file is unchanged — otherwise the client
    /// saves into the copy and does not know where its file went.
    /// History: docs/decisions/scenario.md#checkpoint-save-refusal</remarks>
    [Fact]
    public async Task RefusedResave_KeepsTheCopyOpenAndNamesIt()
    {
        var part = Part("part.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            RefuseSaveWithoutTarget = true,
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        Assert.Equal("copy_not_saved", outcome.Error!["details"]!["cause"]!.GetValue<string>());
        // The refused call's own code reaches the client: this is the code the client saw.
        Assert.Equal("SAVE_FAILED", outcome.Error!["step_error"]!["code"]!.GetValue<string>());
        // The copy's document is NOT closed: the client's next save goes there, and it must be told.
        Assert.DoesNotContain("kompas_close_document", session.Tools);
        Assert.Equal("copy-1", outcome.Report["document_id_after"]!.GetValue<string>());
        Assert.Equal(_topCopy, outcome.Report["path"]!.GetValue<string>());
        Assert.Contains("файл клиента не изменён", outcome.Report["notes"]!.GetValue<string>());
        Assert.False(outcome.Report["taken"]!.GetValue<bool>());
    }

    /// <summary>A resave that leaves the model in <c>&lt;file&gt;.$$$</c> is repeated until the file is
    /// replaced, so the verdict reads the reassigned file and not the one before the reassignment.</summary>
    /// <remarks>MEASURED: the save after the reassignment can answer success while the copy file keeps its
    /// previous bytes and the model's new content stays in the temporary file. INVARIANT: the copy is closed
    /// right after that save, so accepting the first answer would verify the OLD file and report a copy that
    /// was never written. History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    [Fact]
    public async Task SaveThatLeavesATemporaryFile_IsRepeatedUntilTheFileIsReplaced()
    {
        var part = Part("part.m3d");
        var session = new Session { CopyPath = _topCopy, SourcePath = _source };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Graphs.Enqueue(new JsonArray(Row(CopyOf(part))));
        session.LeaveTemporary.Enqueue(false); // the SaveAs: the file is created
        session.LeaveTemporary.Enqueue(true); // the resave: the model stays beside an untouched file

        var outcome = await TakeAsync(session);

        Assert.True(outcome.Ok);
        Assert.True(outcome.Report["taken"]!.GetValue<bool>());
        // SaveAs, the resave, and the repeat of the resave that replaced the file.
        Assert.Equal(3, session.Tools.Count(tool => tool == "kompas_save_document"));
        Assert.False(File.Exists(_topCopy + ".$$$"));
    }

    /// <summary>A copy whose file is never replaced is refused with EVERY attempt's evidence.</summary>
    /// <remarks>INVARIANT: the refusal says where the model's content stayed, and the copy's folder is not
    /// deleted under the document that is open from it. History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    [Fact]
    public async Task SaveThatNeverReplacesItsFile_IsRefusedWithEveryAttempt()
    {
        var part = Part("part.m3d");
        var session = new Session
        {
            CopyPath = _topCopy,
            SourcePath = _source,
            LeaveTemporaryFrom = 2,
        };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        var details = outcome.Error!["details"]!.AsObject();
        Assert.Equal("copy_not_saved", details["cause"]!.GetValue<string>());
        var attempts = details["save_attempts"]!.AsArray();
        Assert.Equal(AssemblyCheckpoint.SaveAttempts, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal("present",
            attempt!["temporary"]!["state"]!.GetValue<string>()));
        Assert.False(outcome.Report["taken"]!.GetValue<bool>());
        Assert.True(outcome.Report[AssemblyCheckpoint.DocumentMoved]!.GetValue<bool>());
        // The folder stays: the live document lives in it.
        Assert.True(File.Exists(_topCopy + ".$$$"));
    }

    /// <summary>The mismatch names what every replace answered and every reading of the file.</summary>
    /// <remarks>INVARIANT: the answer separates "the replacement never reached the model" from "it did, and
    /// the save lost it"; the verdict rests on two readings, not on the first one.
    /// History: docs/decisions/scenario.md#checkpoint-settle</remarks>
    [Fact]
    public async Task VerificationMismatch_NamesTheReplacedRowsAndBothReadings()
    {
        var part = Part("part.m3d");
        var session = new Session { CopyPath = _topCopy, SourcePath = _source };
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        session.Rows.Enqueue(new JsonArray(Row(part)));
        // The file kept the client's link: the reading disagrees twice, because the copy is closed and
        // reopened between them.
        session.Graphs.Enqueue(new JsonArray(Row(part)));
        session.Graphs.Enqueue(new JsonArray(Row(part)));

        var outcome = await TakeAsync(session);

        Assert.False(outcome.Ok);
        var details = outcome.Error!["details"]!.AsObject();
        Assert.Equal("copy_not_verified", details["cause"]!.GetValue<string>());
        var rows = details["reassignments"]!.AsArray();
        Assert.Single(rows);
        Assert.Equal(CopyOf(part), rows[0]!["source_after"]!.GetValue<string>());
        var reads = details["verification_reads"]!.AsArray();
        Assert.Equal(AssemblyCheckpoint.VerifyReads, reads.Count);
        Assert.All(reads, count => Assert.True(count!.GetValue<int>() > 0));
        Assert.Equal(AssemblyCheckpoint.VerifyReads, session.VerifyOpens);
    }
}
