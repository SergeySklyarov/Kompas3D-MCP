using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Жизненный цикл сеанса со стороны Хоста: захват, освобождение, отказ без владения.
/// </summary>
/// <remarks>
/// <para>
/// КОМ ЗДЕСЬ НЕТ, И ЭТО ОСОЗНАННО. Проверяется то, что решается ДО COM: маршрутизация, владение,
/// запись в журнал и форма отказа. Worker не запускается ни в одном тесте — канал создаётся, а
/// процесс стартует только на первой реальной команде, которой здесь нет.
/// </para>
/// <para>
/// ЧТО ЗДЕСЬ ПРОВЕРЯЕТСЯ ПО СУЩЕСТВУ: обычный CAD-вызов без владения отказывает ДО записи в
/// журнал и ДО COM; освобождение создаёт состояние, в котором неявный захват запрещён;
/// диагностика отвечает и без владения.
/// </para>
/// </remarks>
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

    // -----------------------------------------------------------------------------------------
    // Статус
    // -----------------------------------------------------------------------------------------

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

    // -----------------------------------------------------------------------------------------
    // Диагностика без владения
    // -----------------------------------------------------------------------------------------

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
    }

    // -----------------------------------------------------------------------------------------
    // Захват и освобождение
    // -----------------------------------------------------------------------------------------

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

        // ОТКАЗ ДО ЖУРНАЛА. Считаются строки, а не существование файла: журнал создаётся уже при
        // захвате, поэтому «файла нет» здесь ничего не доказывало бы.
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

        // ПОСЛЕ ЯВНОГО RELEASE НЕЯВНЫЙ ЗАХВАТ ЗАПРЕЩЁН.
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

        // ПОВТОР БЕЗОПАСЕН: второе освобождение не портит состояние и не делает вид, что работало.
        var second = await session.InvokeAsync(HostSession.ReleaseTool, new JsonObject(), CancellationToken.None);
        Assert.Equal(OperationStatus.Succeeded, second.Status);
        Assert.False(Body(second)["released_by_this_request"]!.GetValue<bool>());
    }

    // -----------------------------------------------------------------------------------------
    // Повтор освобождения по operation_id (правило §2.1: поле объявлено И используется)
    // -----------------------------------------------------------------------------------------

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

        // ТОТ ЖЕ id — записанный исход: «освобождён ЭТИМ запросом» остаётся истиной, хотя владения
        // уже нет. Выполнись процедура заново — ответ сказал бы «не этим запросом», и различие
        // воспроизведения от повторного исполнения было бы невидимым.
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

        // НОВЫЙ id начинает освобождение заново: владения уже нет, поэтому «не этим запросом».
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

        // Тот же id, но НОВОЕ поколение: исход прежнего сеанса не воспроизводится — освобождение
        // выполнено заново и снова «этим запросом». Без очистки карты ответ был бы чужим исходом.
        Assert.True(Body(again)["released_by_this_request"]!.GetValue<bool>());
    }

    /// <summary>
    /// Инструменты сеанса присутствуют в опубликованном каталоге: их нельзя добавить «для
    /// уведомления об изменении списка» — базовый каталог публикуется сразу, включая ожидающий
    /// Хост. Проверка живёт здесь, потому что именно этим достигается доступность без владения.
    /// </summary>
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
            // Уборка временного каталога — best effort.
        }
    }
}
