using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KompasMcp.Contracts;

namespace KompasMcp.Domain.Journaling;

/// <summary>Состояние владельца сеанса, как его видит ЛЮБОЙ Хост с этим <c>journal_path</c>.</summary>
/// <remarks>
/// <para>
/// ДВА ВОПРОСА, КОТОРЫЕ РАЗДЕЛЕНЫ НАМЕРЕННО. Прежняя модель отвечала одним состоянием на оба:
/// «владелец есть, но уже снимается» (<c>draining</c>) читалось вторым Хостом как «можно брать».
/// Измерено 04.10.2026: владение, помеченное <c>draining</c> до подтверждённого конца очистки,
/// означало, что второй Хост начинает работать, пока первый ещё держит Worker, журнал и открытые
/// документы. Здесь это два разных состояния, и разница названа:
/// <list type="bullet">
/// <item><see cref="Releasing"/> и <see cref="Draining"/> — «не готов принимать работу, но ещё
/// очищает ресурсы»;</item>
/// <item><see cref="Released"/> и <see cref="Free"/> — «можно захватить».</item>
/// </list>
/// </para>
/// </remarks>
public enum HostOwnerState
{
    /// <summary>Владение взято, но владелец ещё не обслужил ни одного запроса клиента.</summary>
    Starting,

    /// <summary>Владелец ведёт сеанс: держит Worker, журнал и право на CAD-вызовы.</summary>
    Serving,

    /// <summary>
    /// Владелец освобождает сеанс: новых CAD-вызовов он не принимает, но ресурсы ещё не отпущены.
    /// Исключительное право сохраняется за ним: другой Хост захватить сеанс НЕ может.
    /// </summary>
    Releasing,

    /// <summary>
    /// Транспорт завершён либо Хост снимается аварийно: очистка НЕ подтверждена. Отличается от
    /// <see cref="Releasing"/> только инициатором, а не правами: захват тоже запрещён.
    /// </summary>
    Draining,

    /// <summary>
    /// Владение снято ЯВНЫМ <c>kompas_release_session</c>, очистка подтверждена. Сеанс свободно занять,
    /// но обычный CAD-вызов владение НЕ берёт — нужен явный <c>kompas_acquire_session</c>.
    /// </summary>
    Released,

    /// <summary>
    /// Владения нет, очистка подтверждена (транспорт завершён штатно). Обычный CAD-вызов владение
    /// берёт: это согласованный допуск реальной операции, а не обнаружение каталога.
    /// </summary>
    Free,
}

/// <summary>Запись владельца. Лежит рядом с журналом; читается и пишется под именованной блокировкой.</summary>
public sealed record HostOwnerRecord(
    int Pid,
    string Generation,
    HostOwnerState State,
    DateTimeOffset StartedUtc,
    int RequestsServed,
    int? TookOverFromPid,
    DateTimeOffset? ReleasedUtc);

/// <summary>Исход попытки ЗАХВАТА владения.</summary>
public enum OwnershipOutcome
{
    /// <summary>Владение взято этим вызовом; создано новое поколение.</summary>
    Acquired,

    /// <summary>Этот Хост уже владелец: второго Worker и второго поколения нет.</summary>
    AlreadyOwned,

    /// <summary>Сеансом владеет другой ЖИВОЙ Хост (serving/releasing/draining/starting).</summary>
    RefusedActiveOwner,

    /// <summary>
    /// Сеанс свободен, но захвачен обычным CAD-вызовом быть не может: прежний владелец снял
    /// владение ЯВНО, и требуется явный <c>kompas_acquire_session</c>.
    /// </summary>
    RefusedExplicitAcquireRequired,

    /// <summary>Состояние владельца определить не удалось (запись не читается). Свободой это не считается.</summary>
    RefusedStateUnknown,

    /// <summary>Именованную блокировку или запись получить не удалось.</summary>
    RecordUnavailable,
}

/// <summary>Исход попытки ОСВОБОЖДЕНИЯ владения.</summary>
public enum ReleaseOutcome
{
    /// <summary>Владение снято этим вызовом, состояние <see cref="HostOwnerState.Released"/> записано.</summary>
    Released,

    /// <summary>Владения у этого Хоста не было: повторный release безопасен и чужую запись не трогает.</summary>
    AlreadyReleased,

    /// <summary>Мы владелец, но не в состоянии <see cref="HostOwnerState.Serving"/>: освобождать нечего.</summary>
    NotOwner,

    /// <summary>Запись владельца не удалось обновить. Успехом это НЕ считается.</summary>
    RecordUnavailable,
}

/// <summary>
/// Наблюдение владения без его захвата: то, что отвечает <c>kompas_session_status</c> и на что
/// опирается решение об отказе.
/// </summary>
public sealed record SessionProbe(
    bool RecordExists,
    bool RecordUnreadable,
    bool LockUnavailable,
    HostOwnerState? State,
    int? OwnerPid,
    string? OwnerGeneration,
    int? OwnerRequestsServed,
    bool OwnerAlive,
    string? AliveReason,
    bool SelfOwned,
    bool CanAcquire,
    bool RequiresExplicitAcquire,
    string Reason,
    string? RefusalCode)
{
    /// <summary>
    /// Чем владелец, мешающий захвату, отличается от отсутствующего: «освобождается» называется
    /// отдельно от «ведёт сеанс», потому что remedies у них разные.
    /// </summary>
    public string Describe() => State switch
    {
        HostOwnerState.Releasing => "releasing",
        HostOwnerState.Draining => "draining",
        HostOwnerState.Serving => "serving",
        HostOwnerState.Starting => "starting",
        _ => "none",
    };
}

/// <summary>Результат попытки захвата.</summary>
public sealed record Acquisition(
    OwnershipOutcome Outcome,
    string? Generation,
    bool NewGeneration,
    SessionProbe? Refusal,
    string? ErrorCode,
    string? ErrorMessage,
    string? Remedy,
    int? TookOverFromPid,
    bool TookOverFromLiveOwner);

/// <summary>Результат попытки освобождения.</summary>
public sealed record Release(
    ReleaseOutcome Outcome,
    string? ErrorMessage,
    SessionProbe? Probe);

/// <summary>
/// Единственный владелец CAD-сеанса на один <c>journal_path</c>.
/// </summary>
/// <remarks>
/// <para>
/// ЗАЧЕМ ЭТО ЗДЕСЬ. Два Хоста с ОДНИМ конфигом делят один экземпляр КОМПАС и один журнал.
/// Пока журнал держал ручку на запись всю жизнь процесса, второй Хост падал на старте
/// необработанным <c>IOException</c>, и клиент видел «Connection closed» — то есть терял ВСЕ
/// инструменты молча (дефект <c>JOURNAL-REPLAY-SHARING-VIOLATION</c>, измерен 21.09.2026).
/// </para>
/// <para>
/// ЧТО ИЗМЕНИЛОСЬ 04.10.2026. Прежняя модель захватывала владение на СТАРТЕ транспорта и
/// отмечала владельцем всякий <c>tools/list</c>. Измерено 04.10.2026: вспомогательное
/// обнаружение инструментов клиентом заняло владение одним вызовом <c>tools/list</c>, и основной
/// чат получил <c>SESSION_OWNER_ACTIVE</c>. Отсюда три правила, которые здесь выполнены:
/// <list type="bullet">
/// <item>старт транспорта владения НЕ берёт — только явный <c>acquire</c> либо допуск РЕАЛЬНОЙ
/// CAD-операции;</item>
/// <item><c>tools/list</c>, <c>initialize</c> и диагностический <c>health</c> владения НЕ берут;
/// </item>
/// <item>владелец опознаётся pid И поколением: запоздалый callback старого поколения не обновляет
/// состояние нового.</item>
/// </list>
/// </para>
/// <para>
/// ПОКОЛЕНИЕ. Каждый захват создаёт новый <see cref="Guid"/>. Все обновления записи сверяют pid И
/// поколение; несовпадение — отказ обновить, а не «вернуть себе владение одной строкой». Это и
/// есть механизм исключительности, общий для освобождения, завершения транспорта и аварийного
/// восстановления: старый <c>finally</c> не вправе освободить чужое поколение.
/// </para>
/// <para>
/// ЖИВОСТЬ. Владелец считается живым, если процесс с его pid существует И стартовал не позже
/// момента записи (иначе номер переиспользован). Если определить не удалось — считается ЖИВЫМ:
/// цена ошибки несимметрична — лишний отказ виден и назван, а принятый за мёртвого живой владелец
/// дал бы два Хоста, ведущих операции одновременно.
/// </para>
/// <para>
/// СРОК БЕЗДЕЙСТВИЯ НЕ ОТБИРАЕТ ВЛАДЕНИЕ у живого владельца: автоматический таймаут передачи и
/// удалённое принудительное освобождение в это задание не входят.
/// </para>
/// <para>
/// БЛОКИРОВКА НЕ ДЕРЖИТСЯ ВО ВРЕМЯ COM. Именованная межпроцессная блокировка берётся только на
/// чтение и запись записи владельца; ни один CAD-вызов под ней не выполняется.
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

    private HostOwnership(Mutex gate, string recordPath, string journalPath)
    {
        _gate = gate;
        _startedUtc = DateTimeOffset.UtcNow;
        RecordPath = recordPath;
        JournalPath = journalPath;
    }

    /// <summary>Путь журнала, владение которым разбирается. Нужен для читаемых текстов отказа.</summary>
    public string JournalPath { get; }

    /// <summary>Путь записи владельца — рядом с журналом, а не в каталоге по умолчанию.</summary>
    public string RecordPath { get; }

    /// <summary>
    /// Поколение текущего захвата. <c>null</c> — этот Хост владения не держит.
    /// </summary>
    public string? Generation => Volatile.Read(ref _generation);

    private string? _generation;

    /// <summary>Держит ли этот Хост владение прямо сейчас.</summary>
    public bool IsOwner => Generation is not null;

    /// <summary>Сколько запросов клиента обслужил этот Хост в текущем поколении.</summary>
    public int RequestsServed => Volatile.Read(ref _requestsServed);

    public static string RecordPathFor(string journalPath) => journalPath + ".owner.json";

    /// <summary>
    /// Открыть владение БЕЗ захвата. Старт транспорта владения не берёт — это отдельное решение,
    /// принятое 04.10.2026 (см. remarks класса).
    /// </summary>
    public static HostOwnership Open(string journalPath)
    {
        var full = Path.GetFullPath(journalPath);
        var recordPath = RecordPathFor(full);
        var mutex = new Mutex(initiallyOwned: false, name: MutexNameFor(full));
        return new HostOwnership(mutex, recordPath, full);
    }

    /// <summary>
    /// Прочитать состояние владения, ничего не захватывая и не освобождая.
    /// </summary>
    public SessionProbe Probe()
    {
        var taken = EnterGate(out var lockProblem);
        if (!taken)
        {
            return new SessionProbe(
                RecordExists: false, RecordUnreadable: false, LockUnavailable: true,
                State: null, OwnerPid: null, OwnerGeneration: null, OwnerRequestsServed: null,
                OwnerAlive: false, AliveReason: lockProblem, SelfOwned: false,
                CanAcquire: false, RequiresExplicitAcquire: false,
                Reason: "состояние владения определить не удалось: " + lockProblem,
                RefusalCode: ErrorCodes.OwnershipStateUnknown);
        }

        try
        {
            return Classify(ReadRecord(RecordPath));
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Попытаться взять владение. Атомарно под именованной блокировкой: чтение записи, решение и
    /// запись нового состояния происходят внутри одного захвата, поэтому два одновременных запроса
    /// (даже из одного процесса) не могут оба получить право работы.
    /// </summary>
    /// <param name="explicitRequest">
    /// <c>true</c> — явный <c>kompas_acquire_session</c>; <c>false</c> — согласованный допуск
    /// реальной CAD-операции. Допуск после ЯВНОГО release запрещён: это и есть отличие
    /// <see cref="HostOwnerState.Released"/> от <see cref="HostOwnerState.Free"/>.
    /// </param>
    public Acquisition TryAcquire(bool explicitRequest)
    {
        var taken = EnterGate(out var lockProblem);
        if (!taken)
        {
            return Failure(OwnershipOutcome.RecordUnavailable, ErrorCodes.JournalUnavailable,
                $"Не удалось получить именованную блокировку журнала '{JournalPath}' за "
                + $"{GateTimeout.TotalSeconds:0} с: состояние владения не читается. "
                + "Работа без журнала безопасности не начинается.",
                null, null);
        }

        try
        {
            var (previous, unreadable) = ReadRecord(RecordPath);
            var probe = Classify((previous, unreadable));

            // УЖЕ ВЛАДЕЛЕЦ: второго Worker и второго поколения не создаём. Проверка по поколению,
            // а не только по pid: pid мог быть переиспользован.
            if (previous is not null && previous.Pid == Environment.ProcessId && Generation is { } mine
                && string.Equals(previous.Generation, mine, StringComparison.Ordinal)
                && previous.State is HostOwnerState.Serving)
            {
                return new Acquisition(OwnershipOutcome.AlreadyOwned, mine, false, null, null, null, null,
                    TookOverFromPid: null, TookOverFromLiveOwner: false);
            }

            // НЕЗАВЕРШЁННОЕ ОСВОБОЖДЕНИЕ — НЕ ПОВОД НАЧАТЬ ЗАНОВО. Если очистка не подтверждена
            // (состояние `releasing`/`draining` НАШЕГО поколения), новый захват создал бы второе
            // поколение и второго Worker'а поверх, возможно, ещё живого прежнего — то есть ровно то,
            // от чего защищает единственный владелец. Захват обязан упереться и назвать причину.
            if (previous is not null && IsOurGeneration(previous)
                && previous.State is HostOwnerState.Releasing or HostOwnerState.Draining)
            {
                return Failure(OwnershipOutcome.RefusedActiveOwner, ErrorCodes.SessionReleaseFailed,
                    $"Освобождение сеанса не подтверждено: состояние "
                    + $"{previous.State.ToString().ToLowerInvariant()}, поколение {previous.Generation}. "
                    + "Повторный захват невозможен, пока очистка не завершена: второй Worker поверх "
                    + "возможно живого прежнего — это два Хоста на одном КОМПАС.",
                    probe,
                    "Повторите kompas_release_session: повтор продолжает очистку, а не повторяет "
                    + "разрушительное действие.");
            }

            if (unreadable)
            {
                return Failure(OwnershipOutcome.RefusedStateUnknown, ErrorCodes.OwnershipStateUnknown,
                    $"Запись владельца '{RecordPath}' не читается. Состояние сеанса не определено, "
                    + "и «не прочиталось» не означает «свободно».",
                    probe,
                    "Убедитесь, что запись владельца доступна для чтения и записи этому пользователю; "
                    + "повторите kompas_session_status и захват.");
            }

            // ЧУЖОЙ ВЛАДЕЛЕЦ — ЭТО НЕ ТОЛЬКО ЧУЖОЙ PID. Запись с НАШИМ pid, но другим поколением,
            // принадлежит другому владельцу: тот же процесс может держать несколько «личностей»
            // Хоста (именно так ведут себя два чата в одном процессе в тестах), а в промышленном
            // случае — переиспользованный pid. Проверка по одному pid пропустила бы второго
            // владельца, и оба получили бы право работы.
            if (previous is not null && !IsOurGeneration(previous)
                && previous.State is not HostOwnerState.Released and not HostOwnerState.Free
                && (previous.Pid == Environment.ProcessId || probe.OwnerAlive))
            {
                var verb = previous.State == HostOwnerState.Releasing
                    ? "освобождает сеанс"
                    : previous.State == HostOwnerState.Draining
                        ? "завершает транспорт и ещё очищает ресурсы"
                        : "ведёт сеанс";

                return Failure(OwnershipOutcome.RefusedActiveOwner, ErrorCodes.SessionOwnerActive,
                    $"Сеанс КОМПАС принадлежит другому процессу: pid {previous.Pid}, состояние "
                    + $"{previous.State.ToString().ToLowerInvariant()} ({verb}), поколение "
                    + $"{previous.Generation}, обслужено запросов {previous.RequestsServed}. "
                    + $"Основание: {probe.AliveReason}. Два Хоста с одним конфигом не выполняют "
                    + "операции одновременно.",
                    probe,
                    previous.State == HostOwnerState.Releasing
                        ? "Дождитесь окончания освобождения (повторите kompas_session_status) и "
                          + "захватите сеанс снова."
                        : "Освободите сеанс у владельца: вызовите kompas_release_session в том чате "
                          + $"(pid {previous.Pid}), затем kompas_acquire_session здесь.");
            }

            if (previous is not null && previous.State == HostOwnerState.Released && !explicitRequest)
            {
                return Failure(OwnershipOutcome.RefusedExplicitAcquireRequired, ErrorCodes.SessionNotAcquired,
                    "Сеанс освобождён владельцем явным kompas_release_session. После явного "
                    + "освобождения обычный CAD-вызов владение не берёт: новый сеанс — это новый "
                    + "контекст, а старые ссылки намеренно не оживают.",
                    probe,
                    "Вызовите kompas_acquire_session явно, затем kompas_connect и kompas_get_context: "
                    + "document_id и revision прежнего сеанса недействительны.");
            }

            var generation = Guid.NewGuid().ToString("N");
            var problem = WriteRecord(new HostOwnerRecord(
                Environment.ProcessId,
                generation,
                HostOwnerState.Serving,
                _startedUtc,
                0,
                previous?.Pid,
                null));

            if (problem is not null)
            {
                return Failure(OwnershipOutcome.RecordUnavailable, ErrorCodes.JournalUnavailable,
                    $"Запись владельца '{RecordPath}' не записана: {problem}. Владение не подтверждено, "
                    + "работа не начинается.",
                    probe,
                    "Проверьте права на каталог журнала и повторите захват.");
            }

            _generation = generation;
            return new Acquisition(
                OwnershipOutcome.Acquired,
                generation,
                NewGeneration: true,
                Refusal: null,
                ErrorCode: null,
                ErrorMessage: null,
                Remedy: null,
                TookOverFromPid: previous?.Pid,
                TookOverFromLiveOwner: probe.OwnerAlive);
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Начать освобождение: <see cref="HostOwnerState.Serving"/> → <see cref="HostOwnerState.Releasing"/>.
    /// Исключительное право сохраняется за владельцем: другой Хост сеанс захватить не может.
    /// </summary>
    public bool BeginRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current))
            {
                return false;
            }

            if (current!.State != HostOwnerState.Serving)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Releasing });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Подтвердить освобождение: <see cref="HostOwnerState.Releasing"/> →
    /// <see cref="HostOwnerState.Released"/>. Вызывается ПОСЛЕ подтверждённой остановки Worker и
    /// освобождения очереди, журнала и Invoker — раньше владение свободным не объявляется.
    /// </summary>
    public bool CompleteRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Releasing)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with
            {
                State = HostOwnerState.Released,
                ReleasedUtc = DateTimeOffset.UtcNow,
            });

            if (LastWriteProblem is null)
            {
                _generation = null;
            }

            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Отменить освобождение: <see cref="HostOwnerState.Releasing"/> →
    /// <see cref="HostOwnerState.Serving"/>. Вызывается, когда проверка до очистки отказала:
    /// владелец остаётся владельцем, а не «почти свободным».
    /// </summary>
    public bool AbortRelease()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Releasing)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Serving });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Транспорт завершён: <see cref="HostOwnerState.Serving"/> → <see cref="HostOwnerState.Draining"/>.
    /// Освобождением это НЕ считается: ресурсы ещё не отпущены.
    /// </summary>
    public bool BeginDrain()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Serving)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with { State = HostOwnerState.Draining });
            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Подтвердить конец очистки после завершения транспорта: <see cref="HostOwnerState.Draining"/>
    /// → <see cref="HostOwnerState.Free"/>. Обычный CAD-вызов владение после этого берёт.
    /// </summary>
    public bool CompleteDrain()
    {
        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }

        try
        {
            var (current, _) = ReadRecord(RecordPath);
            if (!IsOurGeneration(current) || current!.State != HostOwnerState.Draining)
            {
                return false;
            }

            LastWriteProblem = WriteRecord(current with
            {
                State = HostOwnerState.Free,
                ReleasedUtc = DateTimeOffset.UtcNow,
            });

            if (LastWriteProblem is null)
            {
                _generation = null;
            }

            return LastWriteProblem is null;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>
    /// Отметить, что Хост ОБСЛУЖИЛ запрос клиента. Возвращает <c>false</c>, если владение уже не
    /// наше: тогда вызывающий обязан отказать ИМЕНОВАННО и до обращения к КОМПАС.
    /// </summary>
    /// <remarks>
    /// Запоздалый вызов СТАРОГО поколения (pid совпал, поколение — нет) обновления не делает: это
    /// и есть защита от «поздний callback старого захвата не обновляет состояние нового».
    /// </remarks>
    public bool MarkServing()
    {
        Interlocked.Increment(ref _requestsServed);

        var taken = EnterGate(out _);
        if (!taken)
        {
            LastWriteProblem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return Generation is not null;
        }

        try
        {
            if (Generation is not { } mine)
            {
                return false;
            }

            var (current, _) = ReadRecord(RecordPath);
            if (current is not null && !IsOurGeneration(current))
            {
                return false;
            }

            // ОТСУТСТВИЕ ЗАПИСИ ПОТЕРЕЙ НЕ СЧИТАЕТСЯ: цена несимметрична — принять сбой записи за
            // потерю владения значило бы превратить живые вызовы в отказы из-за одной ошибки диска.
            // Запись переписывается заново, а проблема называется (<see cref="LastWriteProblem"/>).
            LastWriteProblem = WriteRecord((current ?? new HostOwnerRecord(
                Environment.ProcessId, mine, HostOwnerState.Serving, _startedUtc, 0, null, null)) with
            {
                Pid = Environment.ProcessId,
                Generation = mine,
                State = HostOwnerState.Serving,
                RequestsServed = Volatile.Read(ref _requestsServed),
            });

            return true;
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Почему не удалось обновить запись владельца. Пусто — удалось.</summary>
    public string? LastWriteProblem { get; private set; }

    /// <summary>
    /// Забрать проблему записи, чтобы назвать её РОВНО ОДИН раз: одна и та же беда на каждый вызов
    /// инструмента перестала бы читаться.
    /// </summary>
    public string? TakeWriteProblem()
    {
        var problem = LastWriteProblem;
        LastWriteProblem = null;
        return problem;
    }

    /// <summary>
    /// Всё ещё наш ли сеанс. Положительное доказательство потери — чужой pid ИЛИ чужое поколение
    /// в записи; отсутствие или нечитаемость записи потерей НЕ считается, иначе сбой записи
    /// превращался бы в отказ всех вызовов.
    /// </summary>
    public bool StillOwned()
    {
        if (!IsOwner)
        {
            return false;
        }

        var (record, _) = ReadRecord(RecordPath);
        if (record is null)
        {
            return true;
        }

        return IsOurGeneration(record);
    }

    /// <summary>Читаемый текст отказа «владение потеряно» для вызова, который уже начался.</summary>
    public string LostOwnershipMessage()
    {
        var (record, _) = ReadRecord(RecordPath);
        return record is null
            ? $"Владение журналом '{Path.GetFileName(RecordPath)}' перешло другому процессу."
            : $"Владение сеансом перешло процессу pid {record.Pid} поколения {record.Generation}: "
              + "этот Хост больше не владелец. Повторно работать с моделью можно только после "
              + "kompas_acquire_session и нового kompas_get_context.";
    }

    /// <summary>
    /// Разобрать запись в наблюдение. Решения «можно ли захватить» и «нужен ли явный захват»
    /// принимаются ЗДЕСЬ и нигде больше: два места, отвечающих на один вопрос, разошлись бы.
    /// </summary>
    private SessionProbe Classify((HostOwnerRecord? Record, bool Unreadable) read)
    {
        var (record, unreadable) = read;

        if (record is null)
        {
            return new SessionProbe(
                RecordExists: false, RecordUnreadable: unreadable, LockUnavailable: false,
                State: null, OwnerPid: null, OwnerGeneration: null, OwnerRequestsServed: null,
                OwnerAlive: false,
                AliveReason: unreadable ? "запись владельца не читается" : "записи владельца нет",
                SelfOwned: false,
                CanAcquire: !unreadable,
                RequiresExplicitAcquire: false,
                Reason: unreadable
                    ? "запись владельца не читается: состояние сеанса определить не удалось"
                    : "сеанс свободен: записи владельца нет, владение берётся первым CAD-вызовом",
                RefusalCode: unreadable ? ErrorCodes.OwnershipStateUnknown : null);
        }

        var self = IsOurGeneration(record);
        string aliveReason = self ? "это наш собственный процесс и наше поколение" : string.Empty;
        var alive = self;
        if (!self)
        {
            alive = IsAlive(record, out aliveReason);
        }

        if (self)
        {
            var owned = record.State is HostOwnerState.Serving or HostOwnerState.Starting
                or HostOwnerState.Releasing or HostOwnerState.Draining;

            return new SessionProbe(
                RecordExists: true, RecordUnreadable: false, LockUnavailable: false,
                State: record.State, OwnerPid: record.Pid, OwnerGeneration: record.Generation,
                OwnerRequestsServed: record.RequestsServed,
                OwnerAlive: true, AliveReason: "это наш собственный процесс и наше поколение",
                SelfOwned: owned,
                CanAcquire: !owned,
                RequiresExplicitAcquire: record.State == HostOwnerState.Released,
                Reason: owned
                    ? $"этот Хост владеет сеансом (поколение {record.Generation}, состояние "
                      + $"{record.State.ToString().ToLowerInvariant()})"
                    : record.State == HostOwnerState.Released
                        ? "этот Хост освободил сеанс явным release; для работы нужен явный acquire"
                        : "владения нет",
                RefusalCode: null);
        }

        // Free и Released означают «владения нет» независимо от живости прежнего pid: состояние
        // записывается только после подтверждённой очистки.
        var free = record.State is HostOwnerState.Released or HostOwnerState.Free;

        return new SessionProbe(
            RecordExists: true, RecordUnreadable: false, LockUnavailable: false,
            State: record.State, OwnerPid: record.Pid, OwnerGeneration: record.Generation,
            OwnerRequestsServed: record.RequestsServed,
            OwnerAlive: alive, AliveReason: aliveReason,
            SelfOwned: false,
            CanAcquire: free || !alive,
            RequiresExplicitAcquire: record.State == HostOwnerState.Released,
            Reason: free
                ? record.State == HostOwnerState.Released
                    ? $"сеанс свободен: владелец pid {record.Pid} снял владение явным release; "
                      + "обычный CAD-вызов владение не берёт"
                    : $"сеанс свободен: прежний владелец pid {record.Pid} завершил работу, очистка подтверждена"
                : alive
                    ? $"сеанс занят процессом pid {record.Pid} (состояние "
                      + $"{record.State.ToString().ToLowerInvariant()}): {aliveReason}"
                    : $"прежний владелец pid {record.Pid} не жив ({aliveReason}): владение можно взять",
            RefusalCode: free || !alive ? null : ErrorCodes.SessionOwnerActive);
    }

    private bool IsOurGeneration(HostOwnerRecord? record) =>
        record is not null
        && record.Pid == Environment.ProcessId
        && Generation is { } mine
        && string.Equals(record.Generation, mine, StringComparison.Ordinal);

    private Acquisition Failure(OwnershipOutcome outcome, string code, string message, SessionProbe? probe, string? remedy) =>
        new(outcome, Generation, false, probe, code, message, remedy, TookOverFromPid: probe?.OwnerPid, TookOverFromLiveOwner: probe?.OwnerAlive ?? false);

    private bool EnterGate(out string? problem)
    {
        try
        {
            if (_gate.WaitOne(GateTimeout))
            {
                problem = null;
                return true;
            }

            problem = $"именованная блокировка не получена за {GateTimeout.TotalSeconds:0} с";
            return false;
        }
        catch (AbandonedMutexException)
        {
            // Прежний владелец умер, не освободив блокировку: владение разбираем заново.
            problem = null;
            return true;
        }
    }

    private void ExitGate()
    {
        try
        {
            _gate.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Блокировка не наша (снята по AbandonedMutexException):_release не требуется.
        }
    }

    private string? WriteRecord(HostOwnerRecord record)
    {
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
