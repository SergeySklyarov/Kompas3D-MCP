using System.Text.Json;
using KompasMcp.Domain.Journaling;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Гонки за владение сеансом: два одновременных захвата не могут оба получить право работы.
/// </summary>
/// <remarks>
/// <para>
/// ЗАЧЕМ ОТДЕЛЬНЫЙ КЛАСС. Гонка — это утверждение о ПОРЯДКЕ, а не о состоянии: проверить его
/// последовательными вызовами нельзя, потому что последовательные вызовы по определению не
/// пересекаются. Здесь каждый участник — отдельный объект <see cref="HostOwnership"/> (своя
/// «личность» Хоста, свой захват имени блокировки), и все они стартуют ОДНОВРЕМЕННО с барьера.
/// </para>
/// <para>
/// ЧТО ЭТИМ НЕ ПРОВЕРЯЕТСЯ. Настоящая межпроцессная гонка меряется прибором двух независимых
/// MCP-клиентов на бинарях поставки: в одном процессе pid у всех участников общий, и правило
/// «тот же pid, но чужое поколение» здесь работает иначе, чем между процессами. Этот класс
/// проверяет атомарность ПЕРЕХОДА состояния, а не межпроцессную исключительность целиком.
/// </para>
/// </remarks>
public class SessionOwnershipRaceTests : IDisposable
{
    private readonly string _journal;
    private readonly string _directory;

    public SessionOwnershipRaceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "session-race-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
        _journal = Path.Combine(_directory, "operations.jsonl");
    }

    /// <summary>
    /// Запустить <paramref name="count"/> участников одновременно и собрать их исходы.
    /// </summary>
    private OwnershipOutcome[] RunInParallel(int count, Func<HostOwnership, OwnershipOutcome> action)
    {
        var participants = Enumerable.Range(0, count).Select(_ => HostOwnership.Open(_journal)).ToArray();
        try
        {
            // Барьер: без него «одновременно» означало бы «кто успел первым», и гонка не случилась бы.
            using var barrier = new Barrier(count);
            var outcomes = new OwnershipOutcome[count];

            var threads = participants.Select((ownership, index) => new Thread(() =>
            {
                barrier.SignalAndWait();
                outcomes[index] = action(ownership);
            })).ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            return outcomes;
        }
        finally
        {
            foreach (var participant in participants)
            {
                participant.Dispose();
            }
        }
    }

    [Fact]
    public void ConcurrentAcquire_ExactlyOneOwner()
    {
        var outcomes = RunInParallel(8, ownership => ownership.TryAcquire(explicitRequest: false).Outcome);

        Assert.Equal(1, outcomes.Count(o => o == OwnershipOutcome.Acquired));
        Assert.Equal(7, outcomes.Count(o => o == OwnershipOutcome.RefusedActiveOwner));
        Assert.DoesNotContain(OwnershipOutcome.RecordUnavailable, outcomes);
    }

    [Fact]
    public void ConcurrentBeginRelease_ExactlyOneTransitionsToReleasing()
    {
        using var owner = HostOwnership.Open(_journal);
        Assert.Equal(OwnershipOutcome.Acquired, owner.TryAcquire(explicitRequest: true).Outcome);

        // Участники — ДРУГИЕ «личности» с тем же pid: претендуют на освобождение чужого сеанса.
        var results = RunInParallel(8, ownership => ownership.BeginRelease() ? OwnershipOutcome.Acquired : OwnershipOutcome.RefusedActiveOwner);

        Assert.True(results.Count(o => o == OwnershipOutcome.Acquired) == 0,
            "освободить сеанс имеет право только владелец: чужое поколение не трогается");

        // Владелец при этом остаётся владельцем и может освободить сеанс сам.
        Assert.True(owner.BeginRelease());
        Assert.True(owner.CompleteRelease());
    }

    [Fact]
    public void ConcurrentCompleteRelease_ExactlyOnePublishes()
    {
        using var owner = HostOwnership.Open(_journal);
        owner.TryAcquire(explicitRequest: true);
        owner.BeginRelease();

        var published = RunInParallel(8, ownership => ownership.CompleteRelease() ? OwnershipOutcome.Acquired : OwnershipOutcome.RefusedActiveOwner);
        Assert.True(published.Count(o => o == OwnershipOutcome.Acquired) == 0);

        Assert.True(owner.CompleteRelease(), "владелец публикует released ровно один раз");
        Assert.False(owner.CompleteRelease(), "повторная публикация не выдаётся за успех");
    }

    /// <summary>
    /// Передача сеанса: владелец освобождает, следующий Хост занимает сеанс ЯВНО и получает НОВОЕ
    /// поколение; прежний владелец после этого не имеет права ни на работу, ни на освобождение.
    /// </summary>
    [Fact]
    public void HandOver_PreviousOwnerCannotActAfterNewOwnerAcquired()
    {
        using var first = HostOwnership.Open(_journal);
        using var second = HostOwnership.Open(_journal);

        var firstGeneration = first.TryAcquire(explicitRequest: true).Generation;
        Assert.True(first.BeginRelease());
        Assert.True(first.CompleteRelease());

        var secondAcquire = second.TryAcquire(explicitRequest: true);
        Assert.Equal(OwnershipOutcome.Acquired, secondAcquire.Outcome);
        Assert.NotEqual(firstGeneration, secondAcquire.Generation);

        // СТАРЫЙ ВЛАДЕЛЕЦ: ни работать, ни освобождать чужой сеанс.
        Assert.False(first.MarkServing());
        Assert.False(first.BeginRelease());
        Assert.False(first.StillOwned());
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
