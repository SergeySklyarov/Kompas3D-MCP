using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Journaling;

/// <summary>Состояние владельца сеанса, как его видит второй Хост.</summary>
public enum HostOwnerState
{
    /// <summary>Хост поднялся, но ни одного запроса клиента ещё не обслужил: сеанса нет.</summary>
    Starting,

    /// <summary>Хост обслужил хотя бы один запрос клиента — он ВЕДЁТ сеанс.</summary>
    Serving,

    /// <summary>Хост снимается: транспорт завершён, новых операций он не начнёт.</summary>
    Draining,
}

/// <summary>Запись владельца. Лежит рядом с журналом, рядом же и «именованная» блокировка.</summary>
public sealed record HostOwnerRecord(
    int Pid,
    HostOwnerState State,
    DateTimeOffset StartedUtc,
    int RequestsServed,
    int? TookOverFromPid);

/// <summary>Исход попытки взять владение журналом.</summary>
public enum OwnershipOutcome
{
    /// <summary>Владение взято.</summary>
    Acquired,

    /// <summary>Журнал принадлежит живому владельцу, который ВЕДЁТ сеанс. Отказ именованный.</summary>
    RefusedActiveOwner,

    /// <summary>Запись владельца не удалось ни прочитать, ни записать (права, занятый файл, диск).</summary>
    RecordUnavailable,
}

/// <summary>
/// Единственный активный владелец журнала операций.
/// </summary>
/// <remarks>
/// <para>
/// ЗАЧЕМ ЭТО ЗДЕСЬ. Два Хоста с ОДНИМ конфигом делят один журнал и один экземпляр КОМПАС. Пока
/// журнал держал ручку на запись всю жизнь процесса, второй Хост падал на старте необработанным
/// <c>IOException</c>, и клиент видел «Connection closed» — то есть терял ВСЕ инструменты молча
/// (дефект <c>JOURNAL-REPLAY-SHARING-VIOLATION</c>, измерен 21.09.2026). Совместность журнала
/// вылечена в <see cref="OperationJournal"/>; здесь решается второй вопрос — КОМУ можно работать.
/// </para>
/// <para>
/// ПРАВИЛО, КОТОРОЕ ЗДЕСЬ ВЫПОЛНЯЕТСЯ (R3 наряда):
/// <list type="bullet">
/// <item>владелец жив и <see cref="HostOwnerState.Serving"/> → второй Хост ОТКАЗЫВАЕТ именованно;</item>
/// <item>владелец жив, но сеанса не ведёт (<see cref="HostOwnerState.Starting"/> или
/// <see cref="HostOwnerState.Draining"/>) → владение берётся;</item>
/// <item>владелец мёртв → владение берётся безусловно.</item>
/// </list>
/// </para>
/// <para>
/// ЧТО ЗДЕСЬ НАЗВАНО ОГРАНИЧЕНИЕМ, А НЕ ЗАМОЛЧАНО. «Ведёт сеанс» = «обслужил хотя бы один запрос
/// клиента» (<c>tools/list</c> или <c>tools/call</c>). Событие <c>initialize</c> владельцем НЕ
/// отмечается: SDK обрабатывает его сам, перехватчика у этого события нет, а подмена транспорта
/// ради одной отметки меняла бы поведение выхода по концу stdin — то есть цену за отметку платил бы
/// весь сервер. Следствие названо прямо: Хост, обслуживший сеанс и оставшийся жить после ухода
/// клиента, до своего завершения считается АКТИВНЫМ. Это и есть поведение, требуемое нарядом:
/// отказ вместо тихой потери инструментов.
/// </para>
/// <para>
/// ЖИВОСТЬ. Владелец считается живым, если процесс с его pid существует И стартовал не позже
/// момента записи (иначе номер переиспользован другим процессом). Если определить это не удалось,
/// владелец считается ЖИВЫМ: цена ошибки несимметрична — лишний отказ виден и назван, а принятый за
/// мёртвого живой владелец дал бы два Хоста, ведущих операции одновременно.
/// </para>
/// </remarks>
public sealed class HostOwnership : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Сколько ждать именованную блокировку при разборе владения.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private readonly Mutex _gate;
    private readonly DateTimeOffset _startedUtc;
    private int _requestsServed;

    private HostOwnership(Mutex gate, string recordPath, DateTimeOffset startedUtc, OwnershipOutcome outcome)
    {
        _gate = gate;
        _startedUtc = startedUtc;
        RecordPath = recordPath;
        Outcome = outcome;
    }

    public OwnershipOutcome Outcome { get; }

    /// <summary>Путь записи владельца — рядом с журналом, а не в каталоге по умолчанию.</summary>
    public string RecordPath { get; }

    /// <summary>Именованный код отказа (<see cref="ErrorCodes"/>) либо null, если владение взято.</summary>
    public string? ErrorCode { get; private init; }

    /// <summary>Читаемый текст отказа: клиент обязан видеть ПРИЧИНУ, а не «Connection closed».</summary>
    public string? ErrorMessage { get; private init; }

    /// <summary>Pid владельца, из-за которого отказано.</summary>
    public int? OwnerPid { get; private init; }

    /// <summary>Состояние владельца, из-за которого отказано.</summary>
    public HostOwnerState? OwnerState { get; private init; }

    /// <summary>Сколько запросов обслужил владелец на момент отказа.</summary>
    public int? OwnerRequestsServed { get; private init; }

    /// <summary>У кого взято владение, если у живого, но не ведущего сеанс.</summary>
    public int? TookOverFromPid { get; private init; }

    /// <summary>Взято ли владение у ЖИВОГО владельца, который сеанса не вёл.</summary>
    public bool TookOverFromLiveOwner { get; private init; }

    /// <summary>Запись владельца была нечитаема. Названо, а не проглочено.</summary>
    public bool RecordWasUnreadable { get; private init; }

    public static string RecordPathFor(string journalPath) => journalPath + ".owner.json";

    /// <summary>
    /// Попытаться взять владение журналом. Возвращает объект в любом исходе: отказ — это тоже
    /// измеренное состояние, и он несёт текст для клиента.
    /// </summary>
    public static HostOwnership Claim(string journalPath)
    {
        var full = Path.GetFullPath(journalPath);
        var recordPath = RecordPathFor(full);
        var mutex = new Mutex(initiallyOwned: false, name: MutexNameFor(full));
        var startedUtc = DateTimeOffset.UtcNow;

        var taken = false;
        try
        {
            try
            {
                taken = mutex.WaitOne(GateTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Прежний владелец умер, не освободив блокировку. Это не ошибка: владение наше.
                taken = true;
            }

            if (!taken)
            {
                return new HostOwnership(mutex, recordPath, startedUtc, OwnershipOutcome.RecordUnavailable)
                {
                    ErrorCode = ErrorCodes.JournalUnavailable,
                    ErrorMessage = $"Не удалось получить именованную блокировку журнала '{full}' за "
                                   + $"{GateTimeout.TotalSeconds:0} с: состояние владельца не читается. "
                                   + "Работа без журнала безопасности не начинается.",
                };
            }

            var (previous, unreadable) = ReadRecord(recordPath);
            if (previous is not null && previous.Pid != Environment.ProcessId && IsAlive(previous, out var aliveReason))
            {
                if (previous.State == HostOwnerState.Serving)
                {
                    return new HostOwnership(mutex, recordPath, startedUtc, OwnershipOutcome.RefusedActiveOwner)
                    {
                        ErrorCode = ErrorCodes.SessionOwnerActive,
                        OwnerPid = previous.Pid,
                        OwnerState = previous.State,
                        OwnerRequestsServed = previous.RequestsServed,
                        RecordWasUnreadable = unreadable,
                        ErrorMessage =
                            $"Журнал операций '{full}' принадлежит АКТИВНОМУ сеансу другого процесса: "
                            + $"pid {previous.Pid}, состояние {previous.State.ToString().ToLowerInvariant()}, "
                            + $"обслужено запросов {previous.RequestsServed}. Два Хоста с одним конфигом "
                            + "не выполняют операции одновременно, поэтому этот Хост инструментов не даёт. "
                            + "Закройте предыдущий сеанс клиента (или дождитесь завершения его процесса "
                            + $"KompasMcp.Host.exe pid {previous.Pid}) и подключитесь снова. "
                            + $"Основание: {aliveReason}.",
                    };
                }

                // Жив, но сеанса не ведёт: забираем владение и НАЗЫВАЕМ, у кого именно.
                var owner = new HostOwnership(mutex, recordPath, startedUtc, OwnershipOutcome.Acquired)
                {
                    TookOverFromPid = previous.Pid,
                    TookOverFromLiveOwner = true,
                    RecordWasUnreadable = unreadable,
                };
                owner.WriteRecord(HostOwnerState.Starting);
                return owner;
            }

            var fresh = new HostOwnership(mutex, recordPath, startedUtc, OwnershipOutcome.Acquired)
            {
                TookOverFromPid = previous?.Pid,
                TookOverFromLiveOwner = false,
                RecordWasUnreadable = unreadable,
            };
            fresh.WriteRecord(HostOwnerState.Starting);
            return fresh;
        }
        finally
        {
            if (taken)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Отметить, что Хост ОБСЛУЖИЛ запрос клиента: с этого момента он ведёт сеанс, и второй Хост
    /// обязан отказать. Возвращает <c>false</c>, если владение уже перешло другому процессу, —
    /// тогда вызывающий обязан отказать ИМЕНОВАННО и до обращения к КОМПАС.
    /// </summary>
    /// <remarks>
    /// Запись владельца обновляется здесь же, и это не косметика: отказ второго Хоста называет
    /// число обслуженных запросов, а оно должно быть измеренным. Провал ЗАПИСИ владением не
    /// считается: он называется (<see cref="LastWriteProblem"/>), но не превращает живые вызовы в
    /// отказы. Перезапись чужой записи запрещена: иначе Хост, потерявший владение, вернул бы его
    /// себе одной строкой.
    /// </remarks>
    public bool MarkServing()
    {
        Interlocked.Increment(ref _requestsServed);
        return Outcome == OwnershipOutcome.Acquired && RefreshRecord(HostOwnerState.Serving);
    }

    /// <summary>
    /// Отметить, что Хост снимается: владение освобождается для следующего, не дожидаясь смерти
    /// процесса. Чужая запись не перезаписывается — по той же причине, что и в <see cref="MarkServing"/>.
    /// </summary>
    public bool MarkDraining() =>
        Outcome == OwnershipOutcome.Acquired && RefreshRecord(HostOwnerState.Draining);

    /// <summary>Почему не удалось обновить запись владельца. Пусто — удалось.</summary>
    public string? LastWriteProblem { get; private set; }

    /// <summary>
    /// Забрать проблему записи, чтобы назвать её РОВНО ОДИН раз. Без этого одна и та же беда
    /// печаталась бы на каждый вызов инструмента и перестала бы читаться.
    /// </summary>
    public string? TakeWriteProblem()
    {
        var problem = LastWriteProblem;
        LastWriteProblem = null;
        return problem;
    }

    /// <summary>
    /// Обновить запись владельца под именованной блокировкой. Возвращает «журнал всё ещё наш» —
    /// независимо от того, удалась ли запись.
    /// </summary>
    private bool RefreshRecord(HostOwnerState state)
    {
        var taken = false;
        try
        {
            try
            {
                taken = _gate.WaitOne(GateTimeout);
            }
            catch (AbandonedMutexException)
            {
                taken = true;
            }

            if (!taken)
            {
                LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
                return true;
            }

            var (current, _) = ReadRecord(RecordPath);
            if (current is not null && current.Pid != Environment.ProcessId)
            {
                // Владение перешло: запись принадлежит другому процессу, и перезаписывать её нельзя.
                return false;
            }

            LastWriteProblem = WriteRecord(state);
            return true;
        }
        finally
        {
            if (taken)
            {
                _gate.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Всё ещё наш ли журнал.
    /// </summary>
    /// <remarks>
    /// Нужно ровно для одного случая: владение взято у живого, но НЕ ведущего сеанс Хоста, а тот
    /// затем всё-таки получил клиента. Отказ обязан прийти ДО обращения к КОМПАС — иначе два Хоста
    /// вели бы операции одновременно, и «единственный владелец» остался бы словом.
    ///
    /// Отсутствие или нечитаемость записи потерей владения НЕ считается: потеря требует
    /// положительного доказательства — живой чужой pid в записи. Иначе сбой записи превращался бы
    /// в отказ всех вызовов.
    /// </remarks>
    public bool StillOwned()
    {
        if (Outcome != OwnershipOutcome.Acquired)
        {
            return false;
        }

        var (record, _) = ReadRecord(RecordPath);
        if (record is null || record.Pid == Environment.ProcessId)
        {
            return true;
        }

        return !IsAlive(record, out _);
    }

    /// <summary>Читаемый текст отказа «владение потеряно» для вызова, который уже начался.</summary>
    public string LostOwnershipMessage()
    {
        var (record, _) = ReadRecord(RecordPath);
        return record is null
            ? $"Владение журналом '{Path.GetFileName(RecordPath)}' перешло другому процессу."
            : $"Владение журналом перешло процессу pid {record.Pid}: он поднялся, пока этот Хост "
              + "ещё не обслужил ни одного запроса, и теперь ведёт сеанс. Два Хоста с одним "
              + "конфигом не выполняют операции одновременно.";
    }

    private string? WriteRecord(HostOwnerState state)
    {
        var record = new HostOwnerRecord(
            Environment.ProcessId,
            state,
            _startedUtc,
            Volatile.Read(ref _requestsServed),
            TookOverFromPid);

        var temp = RecordPath + "." + Environment.ProcessId + ".tmp";
        try
        {
            // Атомарность через переименование: запись владельца читают другие процессы, и
            // половина строки читалась бы как «владельца нет» — то есть как разрешение работать.
            File.WriteAllText(temp, JsonSerializer.Serialize(record, Json), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, RecordPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex.Message;
        }
    }

    private static (HostOwnerRecord? Record, bool Unreadable) ReadRecord(string recordPath)
    {
        if (!File.Exists(recordPath))
        {
            return (null, false);
        }

        try
        {
            using var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var record = JsonSerializer.Deserialize<HostOwnerRecord>(reader.ReadToEnd(), Json);
            return (record, false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, true);
        }
    }

    private static bool IsAlive(HostOwnerRecord record, out string reason)
    {
        if (record.Pid == Environment.ProcessId)
        {
            reason = "это наш собственный процесс";
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(record.Pid);
            if (process.HasExited)
            {
                reason = $"процесса pid {record.Pid} нет";
                return false;
            }

            var started = process.StartTime.ToUniversalTime();
            if (started > record.StartedUtc.UtcDateTime.AddSeconds(2))
            {
                // Номер переиспользован: процесс с этим pid стартовал ПОСЛЕ записи владельца.
                reason = $"pid {record.Pid} переиспользован процессом, стартовавшим позже записи";
                return false;
            }

            reason = $"процесс pid {record.Pid} жив";
            return true;
        }
        catch (ArgumentException)
        {
            reason = $"процесса pid {record.Pid} нет";
            return false;
        }
        catch (InvalidOperationException)
        {
            reason = $"процесс pid {record.Pid} завершился";
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            // Определить не удалось — считаем живым. Цена ошибки несимметрична (см. remarks класса).
            reason = $"живость pid {record.Pid} определить не удалось ({ex.GetType().Name}); "
                     + "владелец считается живым";
            return true;
        }
    }

    private static string MutexNameFor(string fullJournalPath)
    {
        var normalized = fullJournalPath.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        return "Local\\kompas-mcp-owner-" + hash;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
