using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Schema;
using KompasMcp.Domain.Journaling;
using KompasMcp.Domain.Schema;
using KompasMcp.Host.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KompasMcp.Host;

/// <summary>KompasMcp.Host: the only process that speaks MCP. stdout carries protocol frames and nothing
/// else — diagnostics go to stderr and to the JSONL log, because a stray log line on stdout breaks
/// the transport for the client.</summary>
public static class Program
{
    public const string ServerName = "kompas-mcp";
    public const string ServerVersion = "0.1.0-preview";

    public static async Task<int> Main(string[] args)
    {
        var configPath = Argument(args, "--config");
        var showSchemas = args.Contains("--print-schemas");
        var schemaOnlyDirectory = Argument(args, "--emit-schemas");

        HostOptions options;
        try
        {
            options = HostOptions.Load(configPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            StderrWriter.WriteLine($"Не удалось прочитать конфигурацию: {ex.Message}");
            return 64;
        }

        if (schemaOnlyDirectory is not null)
        {
            return EmitSchemas(schemaOnlyDirectory);
        }

        // A schema that this build cannot fully interpret would silently accept input the client
        // believes is rejected, so it is a startup failure rather than a runtime surprise.
        foreach (var tool in ToolCatalog.All)
        {
            try
            {
                JsonSchemaValidator.AssertSchemaIsUnderstood(tool.InputSchema);
            }
            catch (InvalidOperationException ex)
            {
                StderrWriter.WriteLine($"Схема инструмента {tool.Name} непроверяема: {ex.Message}");
                return 65;
            }
        }

        if (showSchemas)
        {
            Console.WriteLine(JsonSerializer.Serialize(ToolCatalog.All.ToDictionary(t => t.Name, t => t.InputSchema), KompJson.Options));
            return 0;
        }

        foreach (var problem in options.Validate())
        {
            StderrWriter.WriteLine("конфигурация: " + problem);
        }

        using var log = HostLog.Open(options.LogPath);
        if (log.UnavailableReason is { } logProblem)
        {
            log.WriteStderr("предупреждение: " + logProblem);
        }

        log.Write("info", "host starting", new { version = ServerVersion, pid = Environment.ProcessId, roots = new { read_only = options.ReadOnlyRoots, writable = options.WritableRoots, export = options.ExportRoots } });

        // INVARIANT: ownership is NOT taken at start — a measured decision. MEASURED: an earlier Host
        // captured the journal before the transport came up and refused a second Host at `initialize`,
        // so the client lost ALL tools with no visible reason, and `tools/list` alone took the session.
        // Ownership comes only from an EXPLICIT `kompas_acquire_session` or a coordinated admission.
        // History: docs/decisions/host.md#ownership-model
        using var ownership = HostOwnership.Open(options.JournalPath);
        await using var session = new HostSession(options, ownership, log);

        var initialState = session.Status().Result as JsonObject;
        log.Write("info", "host waiting for session", new
        {
            journal = options.JournalPath,
            record = ownership.RecordPath,
            session_state = initialState?["session_state"]?.GetValue<string>(),
            can_acquire = initialState?["can_acquire"]?.GetValue<bool>(),
        });

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        // stdout carries MCP frames, so the framework's console logger cannot be used: SDK errors
        // are routed to the JSONL log (and warnings to stderr) instead of being silenced.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new HostLogProvider(log));
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddMcpServer(serverOptions =>
        {
            serverOptions.ServerInfo = new Implementation { Name = ServerName, Title = "КОМПАС-3D MCP", Version = ServerVersion };
            serverOptions.ServerInstructions = Instructions;

            // The schemas in ToolCatalog are the contract: published verbatim and the same document the
            // Host validates against, so tools/list and validation cannot drift. The SDK-generated
            // schema cannot express additionalProperties:false, hence this registration.
            //
            // Capabilities starts null on a bare McpServerOptions, so the whole object is assigned.
            serverOptions.Capabilities = new ServerCapabilities { Tools = new ToolsCapability() };
            serverOptions.Handlers.ListToolsHandler = (request, cancellationToken) => ListTools(request, cancellationToken, log);
            serverOptions.Handlers.CallToolHandler = async (request, cancellationToken) => await CallToolAsync(session, log, request, cancellationToken).ConfigureAwait(false);
        }).WithStdioServerTransport();

        var host = builder.Build();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
        };

        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            // INVARIANT: transport end hands the session back by the SAME mechanism as an explicit
            // release. An earlier revision wrote `draining` before cleanup finished, and a second Host
            // read it as "free to take" while the first Worker still held COM. Cleanup is now confirmed
            // before the state becomes `free`. History: docs/decisions/host.md#transport-end
            await session.OnTransportEndAsync().ConfigureAwait(false);
        }

        return 0;
    }

    private const string Instructions = """
        Сервер управляет одним экземпляром КОМПАС-3D v24 через COM. Порядок работы:
        kompas_health → kompas_connect → kompas_create_document/kompas_open_document →
        kompas_get_context (получить document_id и revision) → чтение (list/get/measure) →
        мутация (ожидает operation_id и expected_revision) → перечитывание контекста → сохранение.
        Ревизия отменяет выданные ранее ссылки: это не ошибка, а защита от правки устаревшей модели.
        Исход исходных моделей доступен только для чтения; запись разрешена в настроенные песочницы.
        Ответы содержат verification.level: он показывает, что реально проверено, а что нет.

        Одновременно моделью работает только один сеанс. Если CAD-вызов ответил SESSION_NOT_ACQUIRED
        или SESSION_OWNER_ACTIVE, сеанс занят или требует явного захвата: вызовите
        kompas_session_status, освободите сеанс у владельца (kompas_release_session) и займите его
        здесь (kompas_acquire_session). После захвата — новый контекст: kompas_connect и
        kompas_get_context, прежние document_id и revision недействительны.
        """;

    /// <summary>The tool list: the same catalog for the owner and for a waiting Host.</summary>
    /// <remarks>INVARIANT: the catalog does not depend on ownership. An earlier refusal arrived at
    /// <c>initialize</c>, leaving the second chat with ZERO tools. A refusal is not returned here either:
    /// an exception from this handler never reaches the client — the SDK replaces it with <c>-32603</c> —
    /// so refusals travel in the call envelope (<see cref="CallToolAsync"/>). Ownership is neither taken
    /// nor refreshed here.
    /// History: docs/decisions/host.md#listtools-no-ownership</remarks>
    private static ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken, HostLog log)
    {
        _ = request;
        _ = cancellationToken;
        log.Write("debug", "tools/list", new { tools = ToolCatalog.All.Count });

        var tools = ToolCatalog.All.Select(tool => new Tool
        {
            Name = tool.Name,
            Title = tool.Title,
            Description = tool.Description,
            InputSchema = JsonDocument.Parse(tool.InputSchema.ToJsonString()).RootElement,
            Annotations = new ToolAnnotations
            {
                // Honest annotations only: a mutation is not idempotent just because a journal makes
                // its replay safe.
                ReadOnlyHint = tool.Behaviour.ReadOnly,
                DestructiveHint = tool.Behaviour.Destructive,
                IdempotentHint = null,
                OpenWorldHint = false,
                Title = tool.Title,
            },
            Meta = null,
        }).ToList();

        return ValueTask.FromResult(new ListToolsResult { Tools = tools, NextCursor = null });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(HostSession session, HostLog log, RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name ?? string.Empty;
        var arguments = ToArgumentsObject(request.Params?.Arguments);
        var started = DateTimeOffset.UtcNow;

        ResultEnvelope<JsonNode?> envelope;
        try
        {
            // Routing, ownership and release are decided INSIDE HostSession: an ownership refusal must
            // arrive before any journal write and before COM, and only the place that creates the
            // journal and the Worker can guarantee that.
            envelope = await session.InvokeAsync(name, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client went away: report the state we know, do not invent a CAD outcome.
            envelope = new ResultEnvelope<JsonNode?>
            {
                Status = OperationStatus.Cancelled,
                Error = new ErrorDto(ErrorCodes.CancelNotConfirmed, "Запрос отменён клиентом; фактическое состояние модели не подтверждено.", RetryPolicy.AfterReconciliation, null, true, null),
            };
        }
        catch (Exception ex)
        {
            // Anything escaping the invoker must still come back as a contract envelope: a raw exception
            // would surface as a JSON-RPC error with no status, no operation_id and no error code — what
            // spec 2.2 forbids ("an exception is not the only error channel"). Type and message are
            // reported verbatim.
            log.Write("error", "tool call escaped the invoker", new { tool = name, type = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
            envelope = new ResultEnvelope<JsonNode?>
            {
                Status = OperationStatus.Failed,
                OperationId = ReadOperationId(arguments),
                Verification = new VerificationDto(VerificationLevel.None, Array.Empty<NamedCheck>(), new[] { "outcome_of_partial_effects_unknown" }),
                Error = new ErrorDto(
                    ErrorCodes.VerificationFailed,
                    $"Необработанная ошибка сервера: {ex.GetType().Name}: {ex.Message}",
                    RetryPolicy.AfterReconciliation,
                    null,
                    true,
                    new JsonObject
                    {
                        // The first frames are the actionable part; the full stack is in the log.
                        ["exception"] = ex.GetType().Name,
                        ["stack_head"] = string.Join("\n", (ex.StackTrace ?? string.Empty).Split('\n').Take(6)),
                    }),
            };
        }

        log.Write("info", "tool call", new
        {
            tool = name,
            operation_id = envelope.OperationId,
            status = envelope.Status.ToString().ToLowerInvariant(),
            error = envelope.Error?.Code,
            // Both sides of the revision chain, so a stale expectation is traceable to the Worker's
            // payload or the Host's envelope.
            //
            // Read through ResultField(), never Result["key"]: a list-valued result is a JsonArray, and
            // indexing one by property name throws "The node must be of type 'JsonObject'".
            payload_revision = ResultField(envelope, "revision"),
            envelope_revision_after = envelope.RevisionAfter,
            duration_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
        });

        var json = JsonSerializer.SerializeToNode(envelope, KompJson.Options) ?? JsonValue.Create("null");
        var summary = Summarize(name, envelope);

        // INVARIANT: the image goes as a separate MCP image block, never as base64 inside the result.
        // MEASURED: some clients build model-visible content from TEXT blocks only and park
        // structuredContent where the model never sees it, so base64 there would arrive as a huge wall
        // of text. The field is removed from the structure because the block already delivered it.
        // History: docs/decisions/host.md#image-block
        string? imageBase64 = null;
        var imageMimeType = "application/octet-stream";
        if (json is JsonObject envelopeNode && envelopeNode["result"] is JsonObject resultNode)
        {
            if (resultNode["image_base64"] is JsonNode base64Node)
            {
                imageBase64 = base64Node.GetValue<string>();
                resultNode.Remove("image_base64");
            }

            if (resultNode["mime_type"]?.GetValue<string>() is { Length: > 0 } declaredMime)
            {
                imageMimeType = declaredMime;
            }
        }

        var jsonText = json.ToJsonString();
        var content = new System.Collections.Generic.List<ModelContextProtocol.Protocol.ContentBlock>
        {
            // Both the human summary and the machine envelope travel in the TEXT block, not only in
            // structuredContent: MEASURED, a client that builds model-visible content from text blocks
            // ONLY would leave the agent with no document_id, no revision, no error code, no
            // measurement. MCP 2025-06-18 asks a tool returning structured content to also return the
            // serialized JSON in a text block. History: docs/decisions/host.md#text-block-envelope
            new ModelContextProtocol.Protocol.TextContentBlock { Text = summary + "\n" + jsonText },
        };

        if (imageBase64 is { Length: > 0 })
        {
            content.Add(ModelContextProtocol.Protocol.ImageContentBlock.FromBytes(
                Convert.FromBase64String(imageBase64), imageMimeType));
        }

        return new CallToolResult
        {
            Content = content,
            // The full envelope also goes out as structuredContent so a client never has to parse the
            // text summary to act on a revision or an error code.
            StructuredContent = JsonDocument.Parse(jsonText).RootElement,
            IsError = envelope.Status is OperationStatus.Failed or OperationStatus.OutcomeUnknown,
        };
    }

    private static string? ReadOperationId(JsonObject arguments) => JsonScalars.ReadString(arguments["operation_id"]);

    /// <summary>Read a field of the result only when the result actually is an object. Log lines must not be
    /// able to fail a call: results are objects for reads of one entity and arrays for listings.</summary>
    private static string? ResultField(ResultEnvelope<JsonNode?> envelope, string field) =>
        envelope.Result is JsonObject obj ? JsonScalars.ReadString(obj[field]) ?? JsonScalars.ReadLong(obj[field])?.ToString() : null;

    private static JsonObject ToArgumentsObject(IDictionary<string, JsonElement>? arguments)
    {
        var node = new JsonObject();
        if (arguments is null)
        {
            return node;
        }

        foreach (var pair in arguments)
        {
            node[pair.Key] = JsonNode.Parse(pair.Value.GetRawText());
        }

        return node;
    }

    private static string Summarize(string tool, ResultEnvelope<JsonNode?> envelope)
    {
        var parts = new List<string> { $"{tool}: {envelope.Status.ToString().ToLowerInvariant()}" };

        if (envelope.Error is { } error)
        {
            parts.Add($"{error.Code} — {error.Message} (повтор: {error.RetryPolicy.ToString().ToLowerInvariant()})");
        }

        if (envelope.RevisionAfter is long revision)
        {
            parts.Add($"rev {revision}");
        }

        if (envelope.Verification is { } verification)
        {
            parts.Add($"verified: {verification.Level.ToString().ToLowerInvariant()}");
        }

        if (envelope.Warnings.Count > 0)
        {
            parts.Add(string.Join("; ", envelope.Warnings));
        }

        return string.Join(" | ", parts);
    }

    private static int EmitSchemas(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var tool in ToolCatalog.All)
            {
                var schema = (JsonObject)tool.InputSchema.DeepClone();
                schema["$schema"] = Sch.Draft;
                File.WriteAllText(Path.Combine(directory, tool.Name + ".json"), schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            StderrWriter.WriteLine($"Записано схем: {ToolCatalog.All.Count} в {directory}");
            return 0;
        }
        catch (IOException ex)
        {
            StderrWriter.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
