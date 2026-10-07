using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Session lifecycle from the Host side: acquire, release, refusal without ownership.</summary>
/// <remarks>LIMIT: there is deliberately no COM here — the tests check what is decided BEFORE COM (routing,
/// ownership, journal write, refusal shape). No test starts a Worker: the channel is created and the process
/// starts only on the first real command, of which there is none here. INVARIANT: a CAD call without ownership
/// is refused BEFORE the journal and BEFORE COM; release forbids an implicit acquire; diagnostics answer
/// without ownership too.
/// History: docs/decisions/tests.md#host-session-2</remarks>
public class HostSessionLifecycleTests : IDisposable
{
    private readonly string _directory;
    private readonly HostOptions _options;

    public HostSessionLifecycleTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "session-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _options = new HostOptions
        {
            JournalPath = Path.Combine(_directory, "operations.jsonl"),
            LogPath = Path.Combine(_directory, "host.jsonl"),
        };
    }

    private HostSession Session()
    {
        var log = HostLog.Open(Path.Combine(_directory, "host-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl"));
        return new HostSession(_options, HostOwnership.Open(_options.JournalPath), log);
    }

    private int JournalLines()
    {
        if (!File.Exists(_options.JournalPath))
        {
            return 0;
        }

        return File.ReadAllLines(_options.JournalPath).Count(line => line.Trim().Length > 0);
    }

    private static JsonObject Body(ResultEnvelope<JsonNode?> envelope) =>
        envelope.Result as JsonObject ?? throw new InvalidOperationException("ответ без результата");

    private static string State(ResultEnvelope<JsonNode?> envelope) =>
        Body(envelope)["session_state"]!.GetValue<string>();

    // Status.

    [Fact]
    public async Task Status_BeforeAcquire_ReportsFreeSessionAndCreatesNoWorker()
    {
        await using var session = Session();

        var status = session.Status();

        Assert.Equal("free", State(status));
        Assert.True(Body(status)["can_acquire"]!.GetValue<bool>());
        Assert.False(Body(status)["requires_explicit_acquire"]!.GetValue<bool>());
        Assert.Equal("not_created", Body(status)["this_host"]!["worker_channel"]!.GetValue<string>());
        Assert.False(File.Exists(HostOwnership.RecordPathFor(_options.JournalPath)),
            "статус не имеет права занимать сеанс");
    }

    [Fact]
    public async Task Status_AfterAcquire_ReportsSelfOwned()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);

        var status = session.Status();

        Assert.Equal("owned_by_self", State(status));
        Assert.True(Body(status)["this_host"]!["owns_session"]!.GetValue<bool>());
        Assert.NotNull(Body(status)["this_host"]!["generation"]!.GetValue<string>());
    }

    // Diagnostics without ownership.

    [Fact]
    public async Task Health_WithoutOwnership_AnswersLocallyAndDoesNotAcquire()
    {
        await using var session = Session();

        var envelope = await session.InvokeAsync("kompas_health", new JsonObject(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, envelope.Status);
        Assert.Equal("not_started", Body(envelope)["cad_channel"]!.GetValue<string>());
        Assert.False(File.Exists(HostOwnership.RecordPathFor(_options.JournalPath)),
            "диагностический health не занимает CAD-владение");
    }

    [Fact]
    public async Task Capabilities_WithoutOwnership_PublishesTheCatalog()
    {
        await using var session = Session();

        var envelope = await session.InvokeAsync("kompas_capabilities", new JsonObject(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, envelope.Status);
        var tools = (JsonArray)Body(envelope)["tools"]!;
        var names = tools.Select(node => node!.GetValue<string>()).ToArray();

        Assert.Equal(ToolCatalog.All.Count, names.Length);
        Assert.Contains(HostSession.StatusTool, names);
        Assert.Contains(HostSession.AcquireTool, names);
        Assert.Contains(HostSession.ReleaseTool, names);

        // INVARIANT: the same three fields are present in BOTH session states, and the count is the
        // count of the published names — not a second, independently maintained number.
        Assert.Equal(names.Length, Body(envelope)["tool_count"]!.GetValue<int>());
        Assert.Equal(
            ToolCatalog.All.Select(t => t.Name),
            names);
        Assert.Equal(Program.ServerVersion, Body(envelope)["server_version"]!.GetValue<string>());
    }

    /// <summary>INVARIANT: a <c>kompas_capabilities</c> answer produced by the WORKER (the environment
    /// block of <c>env.probe</c>, which knows nothing about the catalog) gets the same three fields,
    /// and its own environment fields are left alone.</summary>
    /// <remarks>MEASURED defect: the catalog appeared only while the Host owned no session, so one and
    /// the same tool answered a different contract depending on who replied. The test drives the merge
    /// function on the WORKER-answer shape: the test project does not reference KompasMcp.Worker, and the
    /// live run in the order's §6 covers the real Worker path.
    /// History: docs/decisions/host.md#capabilities-catalog</remarks>
    [Fact]
    public void Capabilities_WorkerAnswer_GetsTheSameCatalogAndKeepsItsEnvironment()
    {
        var workerAnswer = new ResultEnvelope<JsonNode?>
        {
            Status = OperationStatus.Succeeded,
            Result = JsonNode.Parse("""
                {"worker_runtime":"net10.0","running_instances":2,"rot_kompas_entries":1}
                """)!.AsObject(),
        };

        var merged = HostSession.WithCapabilitiesCatalog(workerAnswer);
        var body = (JsonObject)merged.Result!;

        Assert.Equal(ToolCatalog.All.Count, body["tool_count"]!.GetValue<int>());
        Assert.Equal(ToolCatalog.All.Select(t => t.Name),
            ((JsonArray)body["tools"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal(Program.ServerVersion, body["server_version"]!.GetValue<string>());

        // The Worker's own numbers are not overwritten: the merge adds fields, it does not replace the
        // environment block.
        Assert.Equal(2, body["running_instances"]!.GetValue<int>());
    }

    // Acquire and release.

    [Fact]
    public async Task Acquire_GrantsOwnershipAndRepeatedAcquireKeepsOneGeneration()
    {
        await using var session = Session();

        var first = await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        var generation = Body(first)["generation"]!.GetValue<string>();
        Assert.True(Body(first)["acquired"]!.GetValue<bool>());

        var second = await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        Assert.True(Body(second)["already_owner"]!.GetValue<bool>());
        Assert.Equal(generation, Body(second)["generation"]!.GetValue<string>());
    }

    [Fact]
    public async Task CadCall_AfterExplicitRelease_IsRefusedBeforeJournalAndCom()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        await session.InvokeAsync(HostSession.ReleaseTool, new JsonObject(), CancellationToken.None);

        var before = JournalLines();

        var envelope = await session.InvokeAsync("kompas_rebuild", new JsonObject
        {
            ["document_id"] = "0000000000000000000000000000000f",
        }, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, envelope.Status);
        Assert.Equal(ErrorCodes.SessionNotAcquired, envelope.Error!.Code);
        Assert.NotNull(envelope.Error.Details);
        Assert.True(envelope.Error.Details!.ContainsKey("remedy"), "отказ обязан нести инструкцию, а не только код");

        // REFUSAL BEFORE THE JOURNAL: lines are counted, not file existence — the journal is created already at
        // acquire, so "no file" would prove nothing here.
        Assert.True(before == JournalLines(),
            "отказ без владения обязан приходить до записи в журнал операций");
    }

    [Fact]
    public async Task Release_ThenAcquireAgain_CreatesNewGeneration()
    {
        await using var session = Session();

        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        var released = await session.InvokeAsync(HostSession.ReleaseTool, new JsonObject(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, released.Status);
        Assert.True(Body(released)["released_by_this_request"]!.GetValue<bool>());
        Assert.Equal("released", State(released));

        // INVARIANT: after an explicit RELEASE an implicit acquire is FORBIDDEN.
        var refused = await session.InvokeAsync("kompas_rebuild", new JsonObject
        {
            ["document_id"] = "0000000000000000000000000000000f",
        }, CancellationToken.None);
        Assert.Equal(ErrorCodes.SessionNotAcquired, refused.Error!.Code);

        var again = await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        Assert.True(Body(again)["new_generation"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Release_WithoutOwnership_IsSafeAndSaysItDidNotRelease()
    {
        await using var session = Session();

        var first = await session.InvokeAsync(HostSession.ReleaseTool, new JsonObject(), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.False(Body(first)["released_by_this_request"]!.GetValue<bool>());

        // A repeat is safe: a second release does not corrupt the state or pretend it worked.
        var second = await session.InvokeAsync(HostSession.ReleaseTool, new JsonObject(), CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.False(Body(second)["released_by_this_request"]!.GetValue<bool>());
    }

    // Release replay by operation_id (rule §2.1: the field is declared AND used).

    [Fact]
    public async Task Release_SameOperationId_ReplaysRecordedOutcomeAndDoesNotReleaseAgain()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);

        var id = Guid.NewGuid().ToString();
        var first = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id }, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, first.Status);
        Assert.Equal(id, first.OperationId);
        Assert.True(Body(first)["released_by_this_request"]!.GetValue<bool>());

        // INVARIANT: the SAME id replays the recorded outcome — "released BY THIS request" stays true even
        // though ownership is already gone; running the procedure again would answer "not by this request".
        var replay = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id }, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, replay.Status);
        Assert.Equal(id, replay.OperationId);
        Assert.True(Body(replay)["released_by_this_request"]!.GetValue<bool>());
        Assert.Contains(replay.Warnings, w => w.Contains("памяти процесса", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Release_NewOperationId_ReevaluatesInsteadOfReplaying()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);

        var first = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = Guid.NewGuid().ToString() }, CancellationToken.None);
        Assert.True(Body(first)["released_by_this_request"]!.GetValue<bool>());

        // A NEW id starts the release again: ownership is already gone, so "not by this request".
        var fresh = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = Guid.NewGuid().ToString() }, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, fresh.Status);
        Assert.False(Body(fresh)["released_by_this_request"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Release_SameOperationIdWithDifferentArguments_IsRefusedAsConflict()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);

        var id = Guid.NewGuid().ToString();
        var first = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id, ["timeout_ms"] = 5000 }, CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, first.Status);

        var conflict = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id, ["timeout_ms"] = 9000 }, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, conflict.Status);
        Assert.Equal(ErrorCodes.OperationIdConflict, conflict.Error!.Code);
    }

    [Fact]
    public async Task Release_ReplayHistoryIsDroppedWhenANewSessionIsAcquired()
    {
        await using var session = Session();
        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);

        var id = Guid.NewGuid().ToString();
        var first = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id }, CancellationToken.None);
        Assert.True(Body(first)["released_by_this_request"]!.GetValue<bool>());

        await session.InvokeAsync(HostSession.AcquireTool, new JsonObject(), CancellationToken.None);
        var again = await session.InvokeAsync(HostSession.ReleaseTool,
            new JsonObject { ["operation_id"] = id }, CancellationToken.None);

        // INVARIANT: the same id but a NEW generation does not replay the previous session's outcome — the
        // release runs again and is again "by this request".
        Assert.True(Body(again)["released_by_this_request"]!.GetValue<bool>());
    }

    /// <summary>INVARIANT: the session tools are in the published catalog — they cannot be added "for a
    /// list-changed notification", since the base catalog is published at once, including a waiting Host.</summary>
    [Fact]
    public void SessionTools_AreInThePublishedCatalog()
    {
        var names = ToolCatalog.All.Select(t => t.Name).ToArray();

        Assert.Contains(HostSession.StatusTool, names);
        Assert.Contains(HostSession.AcquireTool, names);
        Assert.Contains(HostSession.ReleaseTool, names);
        Assert.All(
            ToolCatalog.All.Where(t => t.Name is HostSession.StatusTool or HostSession.AcquireTool or HostSession.ReleaseTool),
            tool => Assert.False(tool.Behaviour.RequiresOperationId));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp directory cleanup is best effort.
        }
    }
}
