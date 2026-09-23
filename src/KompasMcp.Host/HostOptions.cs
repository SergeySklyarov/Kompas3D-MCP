using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Domain.Journaling;

namespace KompasMcp.Host;

/// <summary>
/// Host configuration. Loaded from a JSON file and environment overrides; nothing is downloaded
/// and no default points at a user's model directory (spec 1.12).
/// </summary>
public sealed class HostOptions
{
    /// <summary>Model roots the server may read but never write.</summary>
    public IReadOnlyList<string> ReadOnlyRoots { get; init; } = Array.Empty<string>();

    /// <summary>Scratch roots the server may create files in.</summary>
    public IReadOnlyList<string> WritableRoots { get; init; } = Array.Empty<string>();

    /// <summary>Where exports and generated packages go.</summary>
    public IReadOnlyList<string> ExportRoots { get; init; } = Array.Empty<string>();

    public bool AllowUncPaths { get; init; }

    public string? WorkerPath { get; init; }

    public string? WorkerLogPath { get; init; }

    public string LogPath { get; init; } = Default("logs", "host.jsonl");

    public string JournalPath { get; init; } = Default("journal", "operations.jsonl");

    public string ArtifactDirectory { get; init; } = Default("artifacts");

    /// <summary>Bounded CAD queue depth (spec 1.13: 64).</summary>
    public int QueueCapacity { get; init; } = 64;

    /// <summary>Synchronous wait before a mutation is handed back as a poll-able operation.</summary>
    public int SyncBudgetMs { get; init; } = 10_000;

    /// <summary>Per-operation budget on the Worker side.</summary>
    public int OperationBudgetMs { get; init; } = 120_000;

    public int MaxResponseBytes { get; init; } = 16 * 1024;

    public int DefaultPageLimit { get; init; } = 100;

    public int MaxPageLimit { get; init; } = 500;

    /// <summary>True when the EH70 working tree must never be reachable for writing.</summary>
    public bool RequireReadOnlyModelRoots { get; init; } = true;

    private static string Default(params string[] parts)
    {
        var root = Path.Combine(Path.GetTempPath(), "kompas-mcp");
        var full = parts.Length == 1 ? Path.Combine(root, parts[0]) : Path.Combine(new[] { root }.Concat(parts).ToArray());
        return full;
    }

    public static HostOptions Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return FromEnvironment(new HostOptions());
        }

        var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("Файл конфигурации пуст или не является объектом.");

        var options = new HostOptions
        {
            ReadOnlyRoots = ReadList(node, "read_only_roots"),
            WritableRoots = ReadList(node, "writable_roots"),
            ExportRoots = ReadList(node, "export_roots"),
            AllowUncPaths = ReadBool(node, "allow_unc_paths") ?? false,
            WorkerPath = ReadString(node, "worker_path"),
            WorkerLogPath = ReadString(node, "worker_log_path"),
            LogPath = ReadString(node, "log_path") ?? Default("logs", "host.jsonl"),
            JournalPath = ReadString(node, "journal_path") ?? Default("journal", "operations.jsonl"),
            ArtifactDirectory = ReadString(node, "artifact_directory") ?? Default("artifacts"),
            QueueCapacity = ReadInt(node, "queue_capacity") ?? 64,
            SyncBudgetMs = ReadInt(node, "sync_budget_ms") ?? 10_000,
            OperationBudgetMs = ReadInt(node, "operation_budget_ms") ?? 120_000,
            MaxResponseBytes = ReadInt(node, "max_response_bytes") ?? 16 * 1024,
            DefaultPageLimit = ReadInt(node, "default_page_limit") ?? 100,
            MaxPageLimit = ReadInt(node, "max_page_limit") ?? 500,
            RequireReadOnlyModelRoots = ReadBool(node, "require_read_only_model_roots") ?? true,
        };

        return FromEnvironment(options);
    }

    private static HostOptions FromEnvironment(HostOptions options)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "KOMPAS_MCP_CONFIG", "KOMPAS_MCP_READONLY_ROOTS", "KOMPAS_MCP_WRITABLE_ROOTS", "KOMPAS_MCP_EXPORT_ROOTS", "KOMPAS_MCP_WORKER_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                overrides[name] = value;
            }
        }

        if (overrides.Count == 0)
        {
            return options;
        }

        string[] Split(string? value) => value is null ? Array.Empty<string>() : value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new HostOptions
        {
            ReadOnlyRoots = overrides.TryGetValue("KOMPAS_MCP_READONLY_ROOTS", out var ro) ? Split(ro) : options.ReadOnlyRoots,
            WritableRoots = overrides.TryGetValue("KOMPAS_MCP_WRITABLE_ROOTS", out var rw) ? Split(rw) : options.WritableRoots,
            ExportRoots = overrides.TryGetValue("KOMPAS_MCP_EXPORT_ROOTS", out var ex) ? Split(ex) : options.ExportRoots,
            WorkerPath = overrides.TryGetValue("KOMPAS_MCP_WORKER_PATH", out var wp) ? wp : options.WorkerPath,
            AllowUncPaths = options.AllowUncPaths,
            WorkerLogPath = options.WorkerLogPath,
            LogPath = options.LogPath,
            JournalPath = options.JournalPath,
            ArtifactDirectory = options.ArtifactDirectory,
            QueueCapacity = options.QueueCapacity,
            SyncBudgetMs = options.SyncBudgetMs,
            OperationBudgetMs = options.OperationBudgetMs,
            MaxResponseBytes = options.MaxResponseBytes,
            DefaultPageLimit = options.DefaultPageLimit,
            MaxPageLimit = options.MaxPageLimit,
            RequireReadOnlyModelRoots = options.RequireReadOnlyModelRoots,
        };
    }

    private static IReadOnlyList<string> ReadList(JsonObject node, string key) =>
        node[key] is JsonArray array
            ? array.Select(item => item?.GetValue<string>() ?? string.Empty).Where(s => s.Length > 0).ToArray()
            : Array.Empty<string>();

    private static string? ReadString(JsonObject node, string key) => node[key]?.GetValue<string>();

    private static int? ReadInt(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : null;

    private static bool? ReadBool(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<bool>(out var parsed) ? parsed : null;

    /// <summary>
    /// Refuse to start when the configuration would allow writing into a directory the operator
    /// declared read-only, or when no writable root exists at all.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (WritableRoots.Count == 0 && ExportRoots.Count == 0)
        {
            problems.Add("Не задан ни один корень для записи: экспорты и сохранение будут невозможны (paths.writable_roots).");
        }

        foreach (var writable in WritableRoots.Concat(ExportRoots))
        {
            var full = TryFull(writable);
            if (full is null)
            {
                problems.Add($"Корень записи не канонизируется: {writable}");
                continue;
            }

            foreach (var readOnly in ReadOnlyRoots)
            {
                var ro = TryFull(readOnly);
                if (ro is not null && (KompasMcp.Domain.Paths.PathPolicy.IsWithin(full, ro) || string.Equals(full, ro, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"Корень записи '{full}' лежит внутри только-для-чтения '{ro}': так рабочие модели не защищены.");
                }
            }
        }

        return problems;
    }

    private static string? TryFull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>
/// JSONL host log. stdout is reserved for MCP frames, so nothing here is ever written there.
/// </summary>
/// <remarks>
/// ЗАПИСЬ — ОДИН ВЫЗОВ НА СТРОКУ, И ВСЁ ЖЕ ПОД БЛОКИРОВКОЙ. Прежде здесь стоял
/// <c>StreamWriter</c>: он кодирует строку и пишет её в поток, а тот нарезает байты по своему
/// буферу, поэтому две строки от ДВУХ процессов в одном файле могли перемешаться. Измерено
/// 21.09.2026 на журнале клиентского сеанса: 35994 строки, из них ровно одна неразбираемая —
/// 14-байтовый хвост <c>st_pid":38072}</c>.
///
/// Одной записи байтов ОКАЗАЛОСЬ НЕДОСТАТОЧНО: проба <c>scratch/_append_probe</c> измерила, что
/// <c>FileMode.Append</c> с одним вызовом <c>Write</c> при двух писателях ТЕРЯЕТ записи (381 из
/// 400) — дескриптор запоминает конец файла в момент открытия. Поэтому запись идёт под той же
/// именованной межпроцессной блокировкой, что и журнал операций: один вызов <c>Write</c> внутри
/// блокировки, 400/400 на двух процессах и 1200/1200 на четырёх.
/// </remarks>
public sealed class HostLog : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream? _stream;
    private readonly NamedFileLock? _fileGate;

    private HostLog(FileStream? stream, NamedFileLock? fileGate, string? unavailableReason)
    {
        _stream = stream;
        _fileGate = fileGate;
        UnavailableReason = unavailableReason;
    }

    /// <summary>
    /// Почему журнал Хоста не пишется. Пусто — пишется. Названо, а не проглочено: журнал без
    /// строки «host starting» неотличим от Хоста, который не запускался.
    /// </summary>
    public string? UnavailableReason { get; }

    public static HostLog Open(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Simple size guard: rotation by truncation. Losing history beats filling a disk.
            if (File.Exists(path) && new FileInfo(path).Length > 16L * 1024 * 1024)
            {
                File.Delete(path);
            }

            // Readable while open: the operator guide tells the reader to tail this file.
            // bufferSize 1 = без буферизации: строка уходит одним системным вызовом.
            return new HostLog(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1, FileOptions.None),
                NamedFileLock.For(path, "hostlog"),
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Журнал Хоста необязателен для работы (журнал ОПЕРАЦИЙ — обязателен, см. Program), но
            // его отсутствие обязано быть названо вслух, а не превратиться в пустой файл.
            var reason = $"журнал Хоста '{path}' не открывается: {ex.Message}";
            StderrWriter.WriteLine(reason);
            return new HostLog(null, null, reason);
        }
    }

    public void Write(string level, string message, object? fields = null)
    {
        var line = Build(level, message, fields);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_gate)
        {
            if (_stream is null)
            {
                return;
            }

            // Блокировка НЕ удерживается дольше одной строки: строка журнала Хоста не имеет права
            // задерживать вызов инструмента.
            var locked = _fileGate?.Enter(WriteLockTimeout) ?? false;
            try
            {
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Best effort: диагностика не имеет права уронить вызов инструмента.
            }
            finally
            {
                if (locked)
                {
                    _fileGate!.Exit();
                }
            }
        }
    }

    private static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Diagnostics that must reach a human when the log file itself is unavailable.</summary>
    public void WriteStderr(string message) => StderrWriter.WriteLine(message);

    /// <summary>Best-effort flush; the provider may be logging while the host is shutting down.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Flush();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // Already disposed by Dispose(); a trailing line is not worth a crash.
            }
        }
    }

    private string Build(string level, string message, object? fields)
    {
        var node = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields, Contracts.KompJson.Options)?.AsObject() ?? new JsonObject();
        node["ts_utc"] = DateTimeOffset.UtcNow.ToString("O");
        node["level"] = level;
        node["message"] = message;
        node["host_pid"] = Environment.ProcessId;
        return node.ToJsonString(Contracts.KompJson.Options);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _stream?.Flush();
                _stream?.Dispose();
                _fileGate?.Dispose();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException)
            {
                // Уже закрыт.
            }
        }
    }
}
