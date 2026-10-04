using System.Diagnostics;
using System.Text.Json;
using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Единственный владелец CAD-сеанса: захват, освобождение и поколения.
/// </summary>
/// <remarks>
/// <para>
/// Проверяется ПРАВИЛО, а не удобный его случай. «Владелец мёртв» и «владелец жив» проверяются на
/// НАСТОЯЩЕМ чужом процессе: подставленный pid, которого нет, доказывал бы только то, что
/// несуществующий процесс не мешает.
/// </para>
/// <para>
/// ПОЧЕМУ ПРЕЖНИЕ ПРОВЕРКИ ПЕРЕПИСАНЫ, А НЕ ДОПОЛНЕНЫ. Старая модель захватывала владение на
/// старте транспорта и считала <c>draining</c> разрешением взять сеанс. Оба утверждения измеренно
/// ложны (04.10.2026): <c>tools/list</c> вспомогательного обнаружения занимал сеанс, а владение,
/// помеченное <c>draining</c> до подтверждённой очистки, отдавалось при живом Worker. Здесь
/// проверяется новая модель: старт владения не берёт, «освобождается» не равно «можно взять»,
/// явный release запрещает неявный захват. Полный межпроцессный случай меряется прибором двух
/// независимых MCP-клиентов, юнит-тест его не заменяет.
/// </para>
/// </remarks>
public class HostOwnershipTests : IDisposable
{
    private readonly string _journal;
    private readonly string _directory;
    private readonly Process _foreign;

    public HostOwnershipTests()
    {
        // Свой каталог на класс: уборка одного класса не имеет права сносить файлы другого.
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "ownership-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _journal = Path.Combine(_directory, "operations.jsonl");

        // Живой чужой процесс: без него «владелец жив» нечем измерить.
        _foreign = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 120 127.0.0.1 > nul",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
    }

    private string RecordPath => HostOwnership.RecordPathFor(_journal);

    /// <summary>Те же правила разбора, что у самой записи: иначе тест мерил бы свой формат.</summary>
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private HostOwnerRecord ReadRecord()
    {
        using var stream = File.Open(RecordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<HostOwnerRecord>(reader.ReadToEnd(), RecordJson)!;
    }

    private void WriteRecord(int pid, HostOwnerState state, string? generation = null) =>
        File.WriteAllText(
            RecordPath,
            JsonSerializer.Serialize(
                new HostOwnerRecord(pid, generation ?? Guid.NewGuid().ToString("N"), state, DateTimeOffset.UtcNow, 1, null, null),
                RecordJson));

    // -----------------------------------------------------------------------------------------
    // Старт владения не берёт
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Open_DoesNotClaimOwnershipAndLeavesNoRecord()
    {
        using var ownership = HostOwnership.Open(_journal);

        Assert.False(ownership.IsOwner);
        Assert.Null(ownership.Generation);
        Assert.False(File.Exists(RecordPath), "старт транспорта не имеет права занимать сеанс");

        var probe = ownership.Probe();
        Assert.True(probe.CanAcquire);
        Assert.False(probe.RequiresExplicitAcquire);
    }

    // -----------------------------------------------------------------------------------------
    // Захват
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TryAcquire_WritesOwnerWithNewGeneration()
    {
        using var ownership = HostOwnership.Open(_journal);

        var result = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.Acquired, result.Outcome);
        Assert.True(result.NewGeneration);
        Assert.True(ownership.IsOwner);

        var record = ReadRecord();
        Assert.Equal(Environment.ProcessId, record.Pid);
        Assert.Equal(HostOwnerState.Serving, record.State);
        Assert.Equal(result.Generation, record.Generation);
    }

    [Fact]
    public void TryAcquire_TwiceBySameHost_DoesNotCreateSecondGeneration()
    {
        using var ownership = HostOwnership.Open(_journal);

        var first = ownership.TryAcquire(explicitRequest: true);
        var second = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.Acquired, first.Outcome);
        Assert.Equal(OwnershipOutcome.AlreadyOwned, second.Outcome);
        Assert.Equal(first.Generation, second.Generation);
        Assert.False(second.NewGeneration, "повторный захват тем же владельцем не создаёт поколения");
    }

    [Fact]
    public void LiveOwnerServing_IsRefusedByNameAndPidAndDoesNotOverwriteRecord()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Serving);

        using var ownership = HostOwnership.Open(_journal);
        var result = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.RefusedActiveOwner, result.Outcome);
        Assert.Equal(ErrorCodes.SessionOwnerActive, result.ErrorCode);
        Assert.Equal(_foreign.Id, result.Refusal!.OwnerPid);
        Assert.Equal(HostOwnerState.Serving, result.Refusal.State);
        Assert.Contains(_foreign.Id.ToString(), result.ErrorMessage!, StringComparison.Ordinal);

        // Отказ НЕ переписал чужую запись: иначе отказ был бы способом захватить владение.
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    /// <summary>
    /// «Освобождается» НЕ равно «можно взять»: живой владелец в <c>releasing</c> сохраняет
    /// исключительное право. Это ровно то различие, которого не было в прежней модели.
    /// </summary>
    [Fact]
    public void LiveOwnerReleasing_IsRefusedAndNotTakenOver()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Releasing);

        using var ownership = HostOwnership.Open(_journal);
        var result = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.RefusedActiveOwner, result.Outcome);
        Assert.Equal(ErrorCodes.SessionOwnerActive, result.ErrorCode);
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    /// <summary>
    /// Транспорт завершён, очистка не подтверждена (<c>draining</c>) — владение не отдаётся живому
    /// владельцу. Прежняя модель здесь отдавала его, и это было измеренным дефектом.
    /// </summary>
    [Fact]
    public void LiveOwnerDraining_IsNotTakenOver()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Draining);

        using var ownership = HostOwnership.Open(_journal);
        var result = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.RefusedActiveOwner, result.Outcome);
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    [Fact]
    public void DeadOwner_IsTakenOverUnconditionally()
    {
        using var dead = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        dead.WaitForExit();
        WriteRecord(dead.Id, HostOwnerState.Serving);

        using var ownership = HostOwnership.Open(_journal);
        var result = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.Acquired, result.Outcome);
        Assert.Equal(dead.Id, result.TookOverFromPid);
        Assert.False(result.TookOverFromLiveOwner);
    }

    // -----------------------------------------------------------------------------------------
    // Освобождение и поколения
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Release_GoesThroughReleasingAndPublishesReleased()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);

        Assert.True(ownership.BeginRelease());
        Assert.Equal(HostOwnerState.Releasing, ReadRecord().State);

        Assert.True(ownership.CompleteRelease());
        Assert.Equal(HostOwnerState.Released, ReadRecord().State);
        Assert.False(ownership.IsOwner, "после подтверждённого освобождения Хост не владелец");
    }

    [Fact]
    public void AfterExplicitRelease_ImplicitAcquireIsRefusedButExplicitWorks()
    {
        using var ownership = HostOwnership.Open(_journal);
        var first = ownership.TryAcquire(explicitRequest: true);
        ownership.BeginRelease();
        ownership.CompleteRelease();

        var implicitAttempt = ownership.TryAcquire(explicitRequest: false);
        Assert.Equal(OwnershipOutcome.RefusedExplicitAcquireRequired, implicitAttempt.Outcome);
        Assert.Equal(ErrorCodes.SessionNotAcquired, implicitAttempt.ErrorCode);

        var explicitAttempt = ownership.TryAcquire(explicitRequest: true);
        Assert.Equal(OwnershipOutcome.Acquired, explicitAttempt.Outcome);
        Assert.NotEqual(first.Generation, explicitAttempt.Generation);
        Assert.True(explicitAttempt.NewGeneration, "захват после release создаёт НОВОЕ поколение");
    }

    [Fact]
    public void AfterDrain_ImplicitAcquireIsAllowedAndCreatesNewGeneration()
    {
        using var ownership = HostOwnership.Open(_journal);
        var first = ownership.TryAcquire(explicitRequest: true);

        Assert.True(ownership.BeginDrain());
        Assert.Equal(HostOwnerState.Draining, ReadRecord().State);
        Assert.True(ownership.CompleteDrain());
        Assert.Equal(HostOwnerState.Free, ReadRecord().State);

        var second = ownership.TryAcquire(explicitRequest: false);
        Assert.Equal(OwnershipOutcome.Acquired, second.Outcome);
        Assert.NotEqual(first.Generation, second.Generation);
    }

    [Fact]
    public void AbortRelease_ReturnsOwnerToServing()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);
        Assert.True(ownership.BeginRelease());

        Assert.True(ownership.AbortRelease());
        Assert.Equal(HostOwnerState.Serving, ReadRecord().State);
        Assert.True(ownership.IsOwner, "отказ до очистки обязан вернуть owned, а не полусвободное состояние");
    }

    /// <summary>
    /// Незавершённое освобождение — не повод начать заново: новый захват создал бы второе поколение
    /// и второго Worker'а поверх, возможно, ещё живого прежнего.
    /// </summary>
    [Fact]
    public void AcquireWhileReleasing_IsRefusedAndDoesNotCreateASecondGeneration()
    {
        using var ownership = HostOwnership.Open(_journal);
        var acquired = ownership.TryAcquire(explicitRequest: true);
        Assert.True(ownership.BeginRelease());

        var attempt = ownership.TryAcquire(explicitRequest: true);

        Assert.Equal(OwnershipOutcome.RefusedActiveOwner, attempt.Outcome);
        Assert.Equal(ErrorCodes.SessionReleaseFailed, attempt.ErrorCode);
        Assert.Equal(acquired.Generation, ReadRecord().Generation);
        Assert.Equal(HostOwnerState.Releasing, ReadRecord().State);
    }

    [Fact]
    public void CompleteRelease_WithoutBeginRelease_IsRefused()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);

        Assert.False(ownership.CompleteRelease());
        Assert.Equal(HostOwnerState.Serving, ReadRecord().State);
    }

    /// <summary>
    /// Запоздалый callback СТАРОГО поколения не обновляет состояние нового владельца: запись с
    /// нашим pid, но чужим поколением — не наша.
    /// </summary>
    [Fact]
    public void StaleGeneration_CannotMarkServingOrRelease()
    {
        using var ownership = HostOwnership.Open(_journal);
        var acquired = ownership.TryAcquire(explicitRequest: true);

        // Тот же pid, но уже не то поколение: симуляция переданного сеанса.
        WriteRecord(Environment.ProcessId, HostOwnerState.Serving, generation: Guid.NewGuid().ToString("N"));

        Assert.NotEqual(acquired.Generation, ReadRecord().Generation);
        Assert.False(ownership.MarkServing(), "чужое поколение не имеет права отметиться владельцем");
        Assert.False(ownership.BeginRelease());
        Assert.False(ownership.CompleteRelease());
        Assert.False(ownership.StillOwned());
    }

    [Fact]
    public void AfterRelease_StaleMarkServingDoesNotResurrectOwnership()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);
        ownership.BeginRelease();
        ownership.CompleteRelease();

        Assert.False(ownership.MarkServing(), "MarkServing после release не восстанавливает владение");
        Assert.False(ownership.IsOwner);
        Assert.Equal(HostOwnerState.Released, ReadRecord().State);
    }

    [Fact]
    public void LostOwner_CannotTakeOwnershipBackByWritingItsOwnRecord()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);
        WriteRecord(_foreign.Id, HostOwnerState.Serving);

        Assert.False(ownership.StillOwned());
        Assert.False(ownership.MarkServing(), "потерявший владение не имеет права отметиться как ведущий сеанс");
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    // -----------------------------------------------------------------------------------------
    // Нечитаемая запись — не свобода
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void UnreadableRecord_IsNotTreatedAsFreeSession()
    {
        using var ownership = HostOwnership.Open(_journal);

        // Запись владельца есть, но не разбирается. «Не прочиталось» и «владельца нет» — разные
        // состояния: приравнять их значило бы разрешить работу при неизвестном сеансе.
        File.WriteAllText(RecordPath, "{ это не json ");

        var probe = ownership.Probe();
        Assert.True(probe.RecordUnreadable);
        Assert.False(probe.CanAcquire, "ошибка чтения записи не равна свободному сеансу");
        Assert.Equal(ErrorCodes.OwnershipStateUnknown, probe.RefusalCode);

        var result = ownership.TryAcquire(explicitRequest: true);
        Assert.Equal(OwnershipOutcome.RefusedStateUnknown, result.Outcome);
        Assert.Equal(ErrorCodes.OwnershipStateUnknown, result.ErrorCode);
    }

    [Fact]
    public void UnwritableRecord_DoesNotCountAsLostOwnership()
    {
        using var ownership = HostOwnership.Open(_journal);
        ownership.TryAcquire(explicitRequest: true);

        File.Delete(RecordPath);
        Directory.CreateDirectory(RecordPath);

        Assert.True(ownership.StillOwned(), "нечитаемая запись — не доказательство потери владения");

        // СБОЙ ЗАПИСИ НЕ ПРЕВРАЩАЕТ ЖИВОЙ ВЫЗОВ В ОТКАЗ: хост остаётся владельцем, но беда названа.
        Assert.True(ownership.MarkServing());
        Assert.NotNull(ownership.TakeWriteProblem());
        Assert.True(ownership.TakeWriteProblem() is null, "одна и та же беда называется ровно один раз");
    }

    public void Dispose()
    {
        try
        {
            if (!_foreign.HasExited)
            {
                _foreign.Kill(entireProcessTree: true);
            }

            _foreign.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Процесс уже ушёл.
        }

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
