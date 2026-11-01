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

        public Task<ResultEnvelope<JsonNode?>> Invoke(
            string tool, JsonObject arguments, string? servicePath, CancellationToken cancellationToken)
        {
            Tools.Add(tool);
            var access = arguments["access"]?.GetValue<string>();
            if (tool == RefuseTool && (RefuseAccess is null || access == RefuseAccess))
            {
                return Task.FromResult(new ResultEnvelope<JsonNode?>
                {
                    Status = OperationStatus.Failed,
                    Error = new ErrorDto(
                        "PATH_NOT_ALLOWED", $"отказ вызова {tool}", RetryPolicy.Never, null, false, null),
                });
            }

            return Task.FromResult(Answer(tool, arguments));
        }

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
                    return new ResultEnvelope<JsonNode?>
                    {
                        Status = OperationStatus.Succeeded,
                        RevisionAfter = arguments["target_path"] is null ? 3 : 2,
                    };
                case "kompas_close_document":
                    return new ResultEnvelope<JsonNode?> { Status = OperationStatus.Succeeded };
                case "kompas_open_document":
                    var readOnly = (arguments["access"]?.GetValue<string>() ?? "edit") == "read_only";
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
}
