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

/// <summary>
/// KompasMcp.Host: the only process that speaks MCP. stdout carries protocol frames and nothing
/// else — diagnostics go to stderr and to the JSONL log, because a stray log line on stdout breaks
/// the transport for the client.
/// </summary>
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

        // ВЛАДЕНИЕ ЖУРНАЛОМ — ПРЕЖДЕ ЖУРНАЛА, И ЭТО ПОРЯДОК, А НЕ ВКУС.
        //
        // Два Хоста с одним конфигом делят один журнал и один экземпляр КОМПАС. Отказ обязан быть
        // ИМЕНОВАННЫМ и прийти раньше, чем кто-либо начнёт работать: измерено 21.09.2026, что
        // необработанный IOException на старте второго Хоста оставлял клиента с нулём инструментов
        // и без единого слова причины («MCP error -32000: Connection closed»).
        using var ownership = HostOwnership.Claim(options.JournalPath);
        if (ownership.Outcome != OwnershipOutcome.Acquired)
        {
            var refusalCode = ownership.ErrorCode ?? ErrorCodes.JournalUnavailable;
            var refusalMessage = ownership.ErrorMessage ?? "Журнал операций недоступен.";
            log.Write("error", "host refused to start", new
            {
                code = refusalCode,
                message = refusalMessage,
                owner_pid = ownership.OwnerPid,
                owner_state = ownership.OwnerState?.ToString().ToLowerInvariant(),
                owner_requests_served = ownership.OwnerRequestsServed,
                record = ownership.RecordPath,
            });
            return OwnershipRefusal.Serve(
                refusalCode,
                refusalMessage,
                "Закройте предыдущий сеанс клиента (или дождитесь завершения процесса KompasMcp.Host.exe "
                + "с названным pid) и подключитесь снова.");
        }

        if (ownership.TookOverFromPid is int previousOwnerPid)
        {
            log.Write("warn", "ownership taken over", new
            {
                previous_pid = previousOwnerPid,
                previous_was_alive = ownership.TookOverFromLiveOwner,
                record = ownership.RecordPath,
            });
        }

        if (ownership.RecordWasUnreadable)
        {
            log.Write("warn", "ownership record was unreadable; treated as no owner", new { record = ownership.RecordPath });
        }

        OperationJournal? journalOrNull = null;
        try
        {
            journalOrNull = new OperationJournal(options.JournalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Журнал операций — механизм безопасности, а не удобство: без него перезапуск не может
            // отличить «не выполнено» от «выполнено, но ответ потерян». Поэтому работа не
            // начинается, а причина называется и в stderr, и в журнале Хоста.
            var message = $"Журнал операций '{options.JournalPath}' недоступен для записи: {ex.Message}. "
                          + "Работа без журнала безопасности не начинается.";
            log.Write("error", "host refused to start", new { code = ErrorCodes.JournalUnavailable, message });
            log.WriteStderr($"{ErrorCodes.JournalUnavailable}: {message}");
            ownership.MarkDraining();
            return 70;
        }

        using var journal = journalOrNull;
        if (journal.RecoveredInFlight > 0)
        {
            log.Write("warn", "journal recovered unfinished operations as outcome_unknown", new { count = journal.RecoveredInFlight });
        }

        // ПРОПУСК В ЖУРНАЛЕ НАЗЫВАЕТСЯ ЧИСЛОМ. Рваный хвост после жёсткого убийства процесса —
        // ожидаемое состояние, но «журнал прочитан» и «журнал прочитан не весь» — разные
        // утверждения, и разница между ними должна быть видна, а не выведена из молчания.
        if (journal.SkippedLines > 0)
        {
            log.Write("warn", "journal replay skipped unreadable lines", new
            {
                skipped = journal.SkippedLines,
                torn_tail = journal.TornTail,
                path = options.JournalPath,
            });
            log.WriteStderr(
                $"журнал операций: пропущено неразобранных строк {journal.SkippedLines}"
                + (journal.TornTail ? " (последняя строка оборвана)" : " (обрыв хвоста не подтверждён)"));
        }

        await using var supervisor = new WorkerSupervisor(options, log);
        await using var invoker = new ToolInvoker(options, supervisor, journal, log);

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

            // The schemas in ToolCatalog are the contract: they are published verbatim and are the
            // same document the Host validates against, so tools/list and the validation cannot
            // drift apart. The SDK-generated schema from a method signature cannot express
            // additionalProperties:false, which is why the tools are registered this way.
            //
            // Capabilities starts null on a bare McpServerOptions, so the whole object is assigned
            // rather than a member of it.
            serverOptions.Capabilities = new ServerCapabilities { Tools = new ToolsCapability() };
            serverOptions.Handlers.ListToolsHandler = (request, cancellationToken) => ListTools(request, cancellationToken, ownership, log);
            serverOptions.Handlers.CallToolHandler = async (request, cancellationToken) => await CallToolAsync(invoker, log, ownership, request, cancellationToken).ConfigureAwait(false);
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
            await supervisor.StopAsync().ConfigureAwait(false);

            // ТРАНСПОРТ ЗАВЕРШЁН — СЕАНСА БОЛЬШЕ НЕТ. Владение объявляется свободным СРАЗУ, не
            // дожидаясь смерти процесса: иначе следующий Хост отказал бы живому, но уже ничем не
            // занятому предшественнику, и «владелец жив, но сеансов не ведёт» осталось бы словом.
            if (ownership.MarkDraining() is false)
            {
                log.Write("warn", "ownership was already taken over; draining not recorded",
                    new { record = ownership.RecordPath });
            }
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
        """;

    /// <summary>
    /// Список инструментов. Владение проверяется и здесь, но ОТКАЗ ЗДЕСЬ НЕ ВОЗВРАЩАЕТСЯ.
    /// </summary>
    /// <remarks>
    /// ИЗМЕРЕНО 21.09.2026 пробой P2: исключение, брошенное из этого обработчика, до клиента НЕ
    /// доходит — SDK заменяет его на <c>-32603 «An error occurred.»</c>, и читаемый текст причины
    /// теряется. Поэтому отказ доставляется там, где протокол его несёт: конвертом вызова
    /// (<see cref="CallToolAsync"/>). Здесь остаётся НАЗВАННАЯ запись в журнале Хоста, а список
    /// публикуется: клиент, у которого журнал забрал другой процесс, узнаёт причину на первом же
    /// вызове инструмента, а не получает пустой список без объяснения.
    /// </remarks>
    private static ValueTask<ListToolsResult> ListTools(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken, HostOwnership ownership, HostLog log)
    {
        if (!ownership.MarkServing())
        {
            log.Write("error", "tools/list served while ownership is held by another process", new
            {
                code = ErrorCodes.SessionOwnerActive,
                message = ownership.LostOwnershipMessage(),
                record = ownership.RecordPath,
                delivery = "причина доставляется конвертом tools/call: SDK не пропускает текст "
                           + "исключения обработчика tools/list (измерено 21.09.2026)",
            });
        }

        LogOwnershipProblem(log, ownership);

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

    private static async ValueTask<CallToolResult> CallToolAsync(ToolInvoker invoker, HostLog log, HostOwnership ownership, RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name ?? string.Empty;
        var arguments = ToArgumentsObject(request.Params?.Arguments);
        var started = DateTimeOffset.UtcNow;

        ResultEnvelope<JsonNode?> envelope;
        if (!ownership.MarkServing())
        {
            // Владение журналом перешло другому процессу. Отказ приходит ДО обращения к КОМПАС:
            // иначе два Хоста вели бы операции одновременно, и «единственный владелец» осталось бы
            // словом. Текст отказа — в конверте, а не только в журнале Хоста: клиент обязан видеть
            // причину там, где он её читает.
            var message = ownership.LostOwnershipMessage();
            log.Write("error", "tool call refused: ownership lost", new { tool = name, code = ErrorCodes.SessionOwnerActive, message, record = ownership.RecordPath });
            var refusal = new ResultEnvelope<JsonNode?>
            {
                Status = OperationStatus.Failed,
                OperationId = ReadOperationId(arguments),
                Verification = new VerificationDto(VerificationLevel.None, Array.Empty<NamedCheck>(), new[] { "no_effect_on_model" }),
                Error = new ErrorDto(ErrorCodes.SessionOwnerActive, message, RetryPolicy.Never, null, false, null),
            };
            var refusalJson = JsonSerializer.SerializeToNode(refusal, KompJson.Options)!.ToJsonString();
            return new CallToolResult
            {
                // Тот же формат, что у обычного ответа: текстовая строка плюс сериализованный
                // конверт. Клиент, читающий только текстовый блок, тоже видит код и причину.
                Content = new System.Collections.Generic.List<ModelContextProtocol.Protocol.ContentBlock>
                {
                    new ModelContextProtocol.Protocol.TextContentBlock { Text = $"{name}: failed | {ErrorCodes.SessionOwnerActive} — {message}\n{refusalJson}" },
                },
                StructuredContent = JsonDocument.Parse(refusalJson).RootElement,
                IsError = true,
            };
        }

        LogOwnershipProblem(log, ownership);

        try
        {
            envelope = await invoker.InvokeAsync(name, arguments, cancellationToken).ConfigureAwait(false);
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
            // Anything escaping the invoker must still come back as a contract envelope. A raw
            // exception would surface as a JSON-RPC error with no status, no operation_id and no
            // error code — the one response shape a client cannot act on, and precisely what
            // spec 2.2 forbids ("исключение не единственный канал ошибки"). The exception type
            // and message are reported verbatim so this path is diagnosable from the answer alone.
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
            // Both sides of the revision chain, so a stale expectation is traceable to whichever
            // party produced it: the Worker's payload or the Host's envelope.
            //
            // Read through ResultField(), never Result["key"]: a list-valued result is a JsonArray,
            // and indexing one by property name throws "The node must be of type 'JsonObject'".
            // That is exactly what happened here — a diagnostic line added to investigate an empty
            // list turned the call into an error, and the investigation produced the symptom it was
            // measuring.
            payload_revision = ResultField(envelope, "revision"),
            envelope_revision_after = envelope.RevisionAfter,
            duration_ms = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
        });

        var json = JsonSerializer.SerializeToNode(envelope, KompJson.Options) ?? JsonValue.Create("null");
        var summary = Summarize(name, envelope);

        // КАРТИНКА ЕДЕТ ОТДЕЛЬНЫМ image-БЛОКОМ, А НЕ СТРОКОЙ base64 В СТРУКТУРЕ.
        //
        // Причина измерена на живом клиенте (запись от 18.09.2026, WorkBuddy AI 5.5.2, build
        // 910352f0): клиент строит видимый модели контент из ТЕКСТОВЫХ блоков, а structuredContent
        // убирает в mcpMeta, куда модель не смотрит. Строка base64 в структуре была бы для модели
        // текстом на десятки тысяч символов — то есть «построил → увидел → сверил → поправил»
        // превратилось бы в «прочитал простыню», и ради чего инструмент и делался, не случилось бы.
        //
        // Поле ИЗ СТРУКТУРЫ УБИРАЕТСЯ ровно потому, что оно уже доставлено блоком: оставить его
        // значило бы платить за одну и ту же картинку дважды — и в контексте, и в блоке.
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
            // The summary line is for a human reading a log; the envelope underneath is what a
            // client actually acts on. Both must travel in the TEXT block, not only in
            // structuredContent.
            //
            // Measured 18.09.2026 on the live client (WorkBuddy AI 5.5.2, build 910352f0):
            // its `convertMcpResult` builds the model-visible content from text blocks ONLY and
            // parks structuredContent in `mcpMeta`, which reaches the UI but never the agent.
            // With a summary-only text block the agent saw
            //   "kompas_health: succeeded | verified: argumentvalidated"
            // and no document_id, no revision, no error code and no measurement — so §5 of the
            // client-acceptance scenario could not be executed at all, while all 671 acceptance
            // rows stayed green because mcp-smoke.py reads structuredContent directly.
            // The same blindness as the transport defect: the instrument reads what the client
            // does not. MCP 2025-06-18 asks a tool that returns structured content to also
            // return the serialized JSON in a text block for exactly this reason.
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

    /// <summary>
    /// Назвать проблему обновления записи владельца — ОДИН раз на случай, а не в каждой строке:
    /// строка журнала на каждый вызов инструмента утопила бы в себе то единственное, что нужно
    /// прочитать.
    /// </summary>
    private static void LogOwnershipProblem(HostLog log, HostOwnership ownership)
    {
        if (ownership.TakeWriteProblem() is { } problem)
        {
            log.Write("warn", "ownership record not refreshed", new { record = ownership.RecordPath, problem });
        }
    }

    /// <summary>
    /// Read a field of the result only when the result actually is an object. Log lines must not be
    /// able to fail a call: results are objects for reads of one entity and arrays for listings.
    /// </summary>
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
