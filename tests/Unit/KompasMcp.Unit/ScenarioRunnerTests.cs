using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Scenarios;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The bounded scenario: plan reading, derived step ids and the stop rule.</summary>
/// <remarks>INVARIANT: a plan that names a forward reference, a forbidden tool, a repeated step id or
/// more than <see cref="ScenarioRunner.MaxSteps"/> steps is refused BEFORE the first call, so no effect
/// happens. INVARIANT: a step's operation id is a pure function of the scenario's operation id and the
/// step id, which is what makes a repeated scenario harmless.
/// History: docs/decisions/scenario.md#scenario-run</remarks>
public sealed class ScenarioRunnerTests
{
    private const string Parent = "35fc1d4a-58a4-441c-91dc-108426c0ce5a";
    private const string Document = "0123456789abcdef0123456789abcdef";

    private static ScenarioRunner Runner(List<string> calls, Func<string, JsonObject, ResultEnvelope<JsonNode?>?>? overrideAnswer = null) =>
        new(@"C:\temp\control", (tool, arguments, _, _) =>
        {
            calls.Add(tool);
            var answer = overrideAnswer?.Invoke(tool, arguments) ?? Succeeded(arguments, new JsonObject { ["sketch_ref"] = "sk-1" });
            return Task.FromResult(answer);
        });

    private static ResultEnvelope<JsonNode?> Succeeded(JsonObject arguments, JsonNode? result) => new()
    {
        Status = OperationStatus.Succeeded,
        OperationId = JsonScalars.ReadString(arguments["operation_id"]),
        DocumentId = JsonScalars.ReadString(arguments["document_id"]),
        RevisionBefore = 1,
        RevisionAfter = 2,
        Result = result,
    };

    private static JsonObject Plan(params JsonObject[] steps) => new()
    {
        ["scenario_version"] = 1,
        ["document_id"] = Document,
        ["operation_id"] = Parent,
        ["on_error"] = "stop",
        ["checkpoint_policy"] = "none",
        ["steps"] = new JsonArray(steps.Select(s => (JsonNode)s).ToArray()),
    };

    private static JsonObject Step(string id, string tool, JsonObject? arguments = null) => new()
    {
        ["step_id"] = id,
        ["tool"] = tool,
        ["arguments"] = arguments ?? new JsonObject(),
    };

    /// <summary>The derived id is stable, a v5 UUID, and differs per step.</summary>
    [Fact]
    public void ChildOperationIdIsDeterministicVersionFive()
    {
        var first = ScenarioRunner.ChildOperationId(Parent, "s1");
        var again = ScenarioRunner.ChildOperationId(Parent, "s1");
        var other = ScenarioRunner.ChildOperationId(Parent, "s2");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        var bytes = Guid.Parse(first).ToByteArray(bigEndian: true);
        Assert.Equal(5, bytes[6] >> 4);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    /// <summary>A reference to a later step is refused before any call: the plan would otherwise depend
    /// on an order the caller cannot see.</summary>
    [Fact]
    public async Task ForwardReferenceIsRefusedBeforeTheFirstCall()
    {
        var calls = new List<string>();
        var plan = Plan(
            Step("s1", "kompas_extrude", new JsonObject
            {
                ["sketch_ref"] = new JsonObject { ["from_step"] = "s2", ["field"] = "result.sketch_ref" },
                ["depth_mm"] = 10,
            }),
            Step("s2", "kompas_create_sketch"));

        var answer = await Runner(calls).RunAsync(plan, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, answer.Status);
        Assert.Equal("steps[0].arguments.sketch_ref.from_step", answer.Error?.Details?["field"]?.GetValue<string>());
        Assert.Empty(calls);
    }

    /// <summary>A step may not nest a scenario or write a file, and the plan is bounded.</summary>
    [Fact]
    public async Task ForbiddenToolAndStepBoundAreRefusedBeforeAnyCall()
    {
        var calls = new List<string>();

        var nested = await Runner(calls).RunAsync(
            Plan(Step("s1", "kompas_run_scenario")), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, nested.Status);

        var export = await Runner(calls).RunAsync(
            Plan(Step("s1", "kompas_export_step")), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, export.Status);

        var tooLong = await Runner(calls).RunAsync(
            Plan(Enumerable.Range(0, ScenarioRunner.MaxSteps + 1)
                .Select(i => Step($"s{i}", "kompas_rebuild")).ToArray()), CancellationToken.None);
        Assert.Equal(OperationStatus.Failed, tooLong.Status);

        Assert.Empty(calls);
    }

    /// <summary>A backward reference is replaced by the earlier step's value, and the step carries the
    /// derived operation id and the plan's document.</summary>
    [Fact]
    public async Task BackwardReferenceIsResolvedAndStepsRunInOrder()
    {
        var calls = new List<string>();
        JsonObject? extrudeArguments = null;
        var runner = new ScenarioRunner(@"C:\temp\control", (tool, arguments, _, _) =>
        {
            calls.Add(tool);
            if (tool == "kompas_extrude")
            {
                extrudeArguments = (JsonObject)arguments.DeepClone();
            }

            return Task.FromResult(Succeeded(arguments, new JsonObject { ["sketch_ref"] = "sk-1", ["volume_mm3"] = 80000 }));
        });

        var answer = await runner.RunAsync(Plan(
            Step("s1", "kompas_create_sketch"),
            Step("s2", "kompas_extrude", new JsonObject
            {
                ["sketch_ref"] = new JsonObject { ["from_step"] = "s1", ["field"] = "result.sketch_ref" },
                ["depth_mm"] = 10,
            })), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, answer.Status);
        Assert.Equal(new[] { "kompas_create_sketch", "kompas_extrude" }, calls);
        Assert.Equal("sk-1", extrudeArguments?["sketch_ref"]?.GetValue<string>());
        Assert.Equal(ScenarioRunner.ChildOperationId(Parent, "s2"), extrudeArguments?["operation_id"]?.GetValue<string>());

        // INVARIANT: only fields the step tool DECLARES are filled in. kompas_extrude is addressed by
        // its sketch reference and has no document_id, so the scenario must not add one.
        Assert.Null(extrudeArguments?["document_id"]);
    }

    /// <summary>A failed step stops the plan: later steps are skipped, the effects before it are named,
    /// and the answer is a failure rather than a partial success.</summary>
    [Fact]
    public async Task FailedStepStopsThePlanAndNamesPartialEffects()
    {
        var calls = new List<string>();
        var runner = new ScenarioRunner(@"C:\temp\control", (tool, arguments, _, _) =>
        {
            calls.Add(tool);
            return Task.FromResult(tool == "kompas_extrude"
                ? new ResultEnvelope<JsonNode?>
                {
                    Status = OperationStatus.Failed,
                    OperationId = JsonScalars.ReadString(arguments["operation_id"]),
                    Error = new ErrorDto("GEOMETRY_FAILED", "отказ ядра", RetryPolicy.Never, null, false, null),
                }
                : Succeeded(arguments, new JsonObject { ["sketch_ref"] = "sk-1" }));
        });

        var answer = await runner.RunAsync(Plan(
            Step("s1", "kompas_create_sketch"),
            Step("s2", "kompas_extrude", new JsonObject { ["sketch_ref"] = "sk-1", ["depth_mm"] = 10 }),
            Step("s3", "kompas_measure")), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, answer.Status);
        Assert.Equal("s2", answer.Result?["failed_step"]?.GetValue<string>());
        Assert.Equal(new[] { "s1" }, answer.Result?["partial_effects"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("skipped", answer.Result?["steps"]?[2]?["status"]?.GetValue<string>());
        Assert.Equal(2, calls.Count);
    }

    /// <summary>Nested argument objects and a nested step result survive the rewrite of references:
    /// a node taken out of the tree keeps its parent, and re-attaching it must not throw.</summary>
    [Fact]
    public async Task NestedArgumentsAndResultsAreResolved()
    {
        var runner = new ScenarioRunner(@"C:	emp\control", (tool, arguments, _, _) => Task.FromResult(
            Succeeded(arguments, new JsonObject
            {
                ["id"] = "sk-1",
                ["sketch"] = new JsonObject { ["name"] = "profile", ["points"] = new JsonArray(1, 2, 3) },
            })));

        var answer = await runner.RunAsync(Plan(
            Step("s1", "kompas_create_sketch", new JsonObject
            {
                ["expected_revision"] = 1,
                ["plane"] = new JsonObject { ["base"] = "xy", ["offset_mm"] = 0.0 },
            }),
            Step("s2", "kompas_extrude", new JsonObject
            {
                ["sketch_ref"] = new JsonObject { ["from_step"] = "s1", ["field"] = "result.id" },
                ["expected_revision"] = new JsonObject { ["from_step"] = "s1", ["field"] = "revision.after" },
                ["depth_mm"] = 10,
            })), CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, answer.Status);
        Assert.Equal("sk-1", answer.Result?["steps"]?[1]?["result"]?["id"]?.GetValue<string>());
    }

    /// <summary>Only the checkpoint's own save carries a service path, and it names the copy it writes;
    /// a step carries none, so no plan reaches outside the client's roots.</summary>
    /// <remarks>INVARIANT: the grant is computed by the runner from the copy path, never read from an
    /// argument. History: docs/decisions/scenario.md#checkpoint-service-path</remarks>
    [Fact]
    public async Task CheckpointSaveCarriesTheCopyAsItsServicePathOnly()
    {
        var grants = new List<(string Tool, string? Grant)>();
        var runner = new ScenarioRunner(@"C:\temp\control", (tool, arguments, grant, _) =>
        {
            grants.Add((tool, grant));
            return Task.FromResult(tool == "kompas_get_context"
                ? new ResultEnvelope<JsonNode?>
                {
                    Status = OperationStatus.Succeeded,
                    Result = new JsonObject { ["revision"] = 7, ["kind"] = "part" },
                }
                : Succeeded(arguments, new JsonObject { ["id"] = "sk-1" }));
        });

        var plan = Plan(Step("s1", "kompas_create_sketch"));
        plan["on_error"] = "restore_checkpoint";
        plan["checkpoint_policy"] = "document_copy";

        var answer = await runner.RunAsync(plan, CancellationToken.None);

        Assert.Equal(OperationStatus.Succeeded, answer.Status);
        var copy = Path.Combine(@"C:\temp\control", "control-copies", "scenario", Parent + "-r7.m3d");
        Assert.Equal(copy, grants.Single(row => row.Tool == "kompas_save_document").Grant);
        Assert.Null(grants.Single(row => row.Tool == "kompas_create_sketch").Grant);
    }
}
