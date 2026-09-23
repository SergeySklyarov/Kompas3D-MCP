using System.Diagnostics;
using System.Text.Json;
using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Единственный активный владелец журнала (R3 наряда).
/// </summary>
/// <remarks>
/// Проверяется ПРАВИЛО, а не удобный его случай. «Владелец мёртв» и «владелец жив, но сеанса не
/// ведёт» проверяются на НАСТОЯЩЕМ чужом процессе: подставленный pid, которого нет, доказывал бы
/// только то, что несуществующий процесс не мешает. Полный межпроцессный случай (два Хоста на
/// бинарях поставки) меряется пробой P1/P2 наряда — юнит-тест его не заменяет и не подменяет.
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

    private void WriteRecord(int pid, HostOwnerState state) =>
        File.WriteAllText(
            RecordPath,
            JsonSerializer.Serialize(new HostOwnerRecord(pid, state, DateTimeOffset.UtcNow, 1, null), RecordJson));

    [Fact]
    public void FirstClaim_AcquiresAndWritesItsOwnPid()
    {
        using var ownership = HostOwnership.Claim(_journal);

        Assert.Equal(OwnershipOutcome.Acquired, ownership.Outcome);
        Assert.Null(ownership.ErrorCode);

        var record = ReadRecord();
        Assert.Equal(Environment.ProcessId, record.Pid);
        Assert.Equal(HostOwnerState.Starting, record.State);
    }

    [Fact]
    public void MarkServing_RecordsThatTheOwnerIsConductingASession()
    {
        using var ownership = HostOwnership.Claim(_journal);

        Assert.True(ownership.MarkServing());

        var record = ReadRecord();
        Assert.Equal(HostOwnerState.Serving, record.State);
        Assert.Equal(1, record.RequestsServed);
    }

    [Fact]
    public void MarkDraining_ReleasesTheOwnershipWithoutWaitingForProcessExit()
    {
        using var ownership = HostOwnership.Claim(_journal);
        ownership.MarkServing();

        Assert.True(ownership.MarkDraining());

        Assert.Equal(HostOwnerState.Draining, ReadRecord().State);
    }

    /// <summary>
    /// Владелец жив и ВЕДЁТ сеанс → второй отказывает ИМЕНОВАННО, и отказ называет pid владельца.
    /// </summary>
    [Fact]
    public void LiveOwnerServing_IsRefusedByNameAndPid()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Serving);

        using var ownership = HostOwnership.Claim(_journal);

        Assert.Equal(OwnershipOutcome.RefusedActiveOwner, ownership.Outcome);
        Assert.Equal(ErrorCodes.SessionOwnerActive, ownership.ErrorCode);
        Assert.Equal(_foreign.Id, ownership.OwnerPid);
        Assert.Equal(HostOwnerState.Serving, ownership.OwnerState);
        Assert.Contains(_foreign.Id.ToString(), ownership.ErrorMessage!, StringComparison.Ordinal);

        // Отказ НЕ переписал чужую запись: иначе отказ был бы способом захватить владение.
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    /// <summary>
    /// Владелец жив, но сеанса не ведёт (starting) → владение берётся, и взятие названо.
    /// </summary>
    [Fact]
    public void LiveOwnerNotServing_IsTakenOverAndNamed()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Starting);

        using var ownership = HostOwnership.Claim(_journal);

        Assert.Equal(OwnershipOutcome.Acquired, ownership.Outcome);
        Assert.True(ownership.TookOverFromLiveOwner);
        Assert.Equal(_foreign.Id, ownership.TookOverFromPid);
        Assert.Equal(Environment.ProcessId, ReadRecord().Pid);
    }

    /// <summary>
    /// Владелец снимается (draining) → владение берётся: сеанса он больше не ведёт.
    /// </summary>
    [Fact]
    public void DrainingOwner_IsTakenOver()
    {
        WriteRecord(_foreign.Id, HostOwnerState.Draining);

        using var ownership = HostOwnership.Claim(_journal);

        Assert.Equal(OwnershipOutcome.Acquired, ownership.Outcome);
        Assert.True(ownership.TookOverFromLiveOwner);
    }

    /// <summary>
    /// Владелец мёртв → владение берётся безусловно. Проверяется на НАСТОЯЩЕМ завершённом процессе:
    /// «pid, которого наверное нет» измерял бы не правило, а удачу.
    /// </summary>
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

        using var ownership = HostOwnership.Claim(_journal);

        Assert.Equal(OwnershipOutcome.Acquired, ownership.Outcome);
        Assert.False(ownership.TookOverFromLiveOwner);
        Assert.Equal(dead.Id, ownership.TookOverFromPid);
    }

    /// <summary>
    /// Потерянный владелец ОБЯЗАН узнать о потере и НЕ перезаписать чужую запись.
    /// </summary>
    /// <remarks>
    /// Это то, что делает взятие владения у «starting» безопасным: Хост, у которого журнал забрали,
    /// не может вернуть его себе одной строкой в собственном журнале. Без этой проверки правило
    /// «единственный владелец» держалось бы только на порядке запуска.
    /// </remarks>
    [Fact]
    public void OwnerThatLostOwnership_CannotTakeItBackByWritingItsOwnRecord()
    {
        using var ownership = HostOwnership.Claim(_journal);
        WriteRecord(_foreign.Id, HostOwnerState.Serving);

        Assert.False(ownership.StillOwned());
        Assert.False(ownership.MarkServing(), "потерявший владение не имеет права отметиться как ведущий сеанс");
        Assert.Equal(_foreign.Id, ReadRecord().Pid);
    }

    /// <summary>
    /// Сбой ЗАПИСИ — не потеря владения: живые вызовы не превращаются в отказы, но беда называется.
    /// </summary>
    [Fact]
    public void UnwritableRecord_DoesNotCountAsLostOwnership()
    {
        using var ownership = HostOwnership.Claim(_journal);

        // На месте записи владельца — КАТАЛОГ: запись невозможна, а прочитать нечего.
        File.Delete(RecordPath);
        Directory.CreateDirectory(RecordPath);

        Assert.True(ownership.StillOwned(), "нечитаемая запись — не доказательство потери владения");
        Assert.True(ownership.MarkServing(), "сбой записи не имеет права превратить живой вызов в отказ");
        Assert.NotNull(ownership.TakeWriteProblem());
        Assert.Null(ownership.TakeWriteProblem());
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
