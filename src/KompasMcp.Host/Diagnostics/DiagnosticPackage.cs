using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Files;
using KompasMcp.Domain.Paths;

namespace KompasMcp.Host.Diagnostics;

/// <summary>One file of the package, as the manifest describes it.</summary>
public sealed record DiagnosticFile(
    string Name,
    long SizeBytes,
    string Sha256,
    int RecordsRead,
    int RecordsKept,
    int RecordsDropped,
    int FieldsDropped,
    bool Truncated);

/// <summary>What the caller receives: the file and its numbers, never its content.</summary>
public sealed record DiagnosticPackageResult(
    string Path,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CapturedAt,
    IReadOnlyList<DiagnosticFile> Files,
    IReadOnlyList<string> Notes)
{
    public JsonObject ToJson()
    {
        var files = new JsonArray();
        foreach (var file in Files)
        {
            files.Add(new JsonObject
            {
                ["name"] = file.Name,
                ["size_bytes"] = file.SizeBytes,
                ["sha256"] = file.Sha256,
                ["records_read"] = file.RecordsRead,
                ["records_kept"] = file.RecordsKept,
                ["records_dropped"] = file.RecordsDropped,
                ["fields_dropped"] = file.FieldsDropped,
                ["truncated"] = file.Truncated,
            });
        }

        var notes = new JsonArray();
        foreach (var note in Notes)
        {
            notes.Add(note);
        }

        return new JsonObject
        {
            ["path"] = Path,
            ["size_bytes"] = SizeBytes,
            ["sha256"] = Sha256,
            ["captured_at"] = CapturedAt.ToString("O"),
            ["files"] = files,
            ["notes"] = notes,
        };
    }
}

/// <summary>Builds the diagnostic package: a zip of text files that describes the server without any
/// model content (spec 1.4 item 10, 1.12).</summary>
/// <remarks>INVARIANT: the package is built by an ALLOW list — a field is written only when it is named
/// here, and everything else is dropped and counted. A deny list would let a new journal field travel
/// silently. INVARIANT: no COM and no Worker: the package must be buildable exactly when the CAD lane
/// is dead. History: docs/decisions/diagnostics.md#package</remarks>
public static class DiagnosticPackage
{
    public const string Command = "diagnostic.package";

    /// <summary>Format marker of the zip layout, not the product release number.</summary>
    public const string FormatVersion = "diag-1";

    /// <summary>Tail bound per text file (spec 1.12).</summary>
    public const int MaxRecordsPerFile = 2000;

    /// <summary>Whole-package bound (spec 1.12).</summary>
    public const long MaxPackageBytes = 8L * 1024 * 1024;

    /// <summary>Room kept for <c>manifest.json</c>, which is written after the files it describes.</summary>
    private const long ManifestReserveBytes = 64L * 1024;

    /// <summary>Fields <c>operations.jsonl</c> may carry (spec 1.12). The list is the contract: the
    /// projection below writes nothing else.</summary>
    public static readonly IReadOnlyList<string> JournalFields = new[]
    {
        "timestamp", "operation_id", "tool", "document_id", "base_revision", "revision_after",
        "hresult", "elapsed_ms", "outcome", "verification_status",
    };

    /// <summary>Fields <c>host-log.jsonl</c> may carry (spec 1.12). The free-text message is not one of
    /// them: it holds paths and user strings.</summary>
    public static readonly IReadOnlyList<string> LogFields = new[]
    {
        "timestamp", "level", "category", "event_code", "exception_type", "hresult",
    };

    /// <summary>Configuration keys the environment file may carry. Every path-valued key is absent by
    /// construction, so no root of the operator reaches the package.</summary>
    public static readonly IReadOnlyList<string> ConfigKeys = new[]
    {
        "allow_unc_paths", "queue_capacity", "sync_budget_ms", "operation_budget_ms", "update_check",
    };

    /// <summary>Configuration keys of the file that are NOT copied: all of them are paths.</summary>
    private static readonly string[] DroppedConfigKeys =
    {
        "read_only_roots", "writable_roots", "export_roots", "worker_path", "worker_log_path",
        "log_path", "journal_path", "control_copy_directory",
    };

    public static DiagnosticPackageResult Build(HostOptions options, string destination, bool allowOverwrite)
    {
        var policy = new PathPolicy(options.ReadOnlyRoots, options.WritableRoots.Concat(options.ExportRoots));
        var decision = policy.Evaluate(destination, intendToWrite: true);
        if (decision.Access != PathAccess.Writable || decision.CanonicalPath is null)
        {
            throw new KompasContractException(
                ErrorCodes.PathNotAllowed,
                $"Путь пакета отклонён политикой файлов: {decision.DeniedReason}",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "output_path" });
        }

        var target = decision.CanonicalPath;
        if (!string.Equals(Path.GetExtension(target), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                "Пакет диагностики пишется только в файл .zip.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["field"] = "output_path" });
        }

        AtomicPublish.RequireWritableTarget(target, allowOverwrite);

        var capturedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        var files = new List<DiagnosticFile>();
        var salt = RandomNumberGenerator.GetBytes(16);

        var environment = EnvironmentNode(options, notes);
        var processes = ProcessesNode(notes);
        var environmentBytes = Utf8(environment.ToJsonString(JsonOptions));
        var processBytes = Utf8(processes.ToJsonString(JsonOptions));
        var reserved = environmentBytes.Length + processBytes.Length + ManifestReserveBytes;
        if (reserved > MaxPackageBytes)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                "Пакет диагностики не помещается в предел 8 МиБ.",
                RetryPolicy.Never);
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = AtomicPublish.TemporarySibling(target);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddDocument(zip, "environment.json", environmentBytes, files);
                AddDocument(zip, "processes.json", processBytes, files);

                var budget = MaxPackageBytes - reserved;
                budget = AddLines(zip, "operations.jsonl",
                    ProjectJournal(options.JournalPath, salt, notes), budget, files);
                AddLines(zip, "host-log.jsonl", ProjectLog(options.LogPath, salt, notes), budget, files);

                var manifest = ManifestNode(capturedAt, files, notes);
                AddDocument(zip, "manifest.json", Utf8(manifest.ToJsonString(JsonOptions)), files);
            }

            AtomicPublish.WriteFrom(temporary, target, allowOverwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        var info = new FileInfo(target);
        return new DiagnosticPackageResult(target, info.Length, FileHash.Sha256(target), capturedAt, files, notes);
    }

    /// <summary>Reads the tail of a JSONL file and keeps only the allowed fields of each record.</summary>
    /// <remarks>INVARIANT: a torn or unparsable last line is dropped and COUNTED; it never fails the
    /// build, because the moment the package is needed is the moment the journal was cut.
    /// History: docs/decisions/diagnostics.md#package</remarks>
    internal static (List<JsonObject> Records, int Read, int Dropped, int Fields, bool Truncated) ProjectJournal(
        string path, byte[] salt, List<string> notes) =>
        Project(path, salt, notes, ProjectJournalRecord);

    internal static (List<JsonObject> Records, int Read, int Dropped, int Fields, bool Truncated) ProjectLog(
        string path, byte[] salt, List<string> notes) =>
        Project(path, salt, notes, ProjectLogRecord);

    private static (List<JsonObject> Records, int Read, int Dropped, int Fields, bool Truncated) Project(
        string path, byte[] salt, List<string> notes, Func<JsonObject, byte[], (JsonObject? Record, int Fields)> project)
    {
        var records = new List<JsonObject>();
        if (!File.Exists(path))
        {
            notes.Add($"файла '{MaskPath(path, salt)}' нет: запись в манифесте без содержимого");
            return (records, 0, 0, 0, false);
        }

        var (lines, truncated) = TailLines(path, MaxRecordsPerFile);
        var dropped = 0;
        var fields = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is null)
            {
                dropped++;
                continue;
            }

            var (record, droppedFields) = project(parsed, salt);
            fields += droppedFields;
            if (record is null)
            {
                dropped++;
                continue;
            }

            records.Add(record);
        }

        return (records, lines.Count, dropped, fields, truncated);
    }

    private static (JsonObject? Record, int Fields) ProjectJournalRecord(JsonObject record, byte[] salt)
    {
        var output = new JsonObject();
        var dropped = 0;
        long? startedMs = null;
        long? finishedMs = null;

        foreach (var pair in record)
        {
            switch (pair.Key)
            {
                case "operation_id":
                    output["operation_id"] = Mask(pair.Value, salt);
                    break;
                case "tool":
                    output["tool"] = Mask(pair.Value, salt);
                    break;
                case "document_id":
                    output["document_id"] = Mask(pair.Value, salt);
                    break;
                case "base_revision":
                    output["base_revision"] = pair.Value?.DeepClone();
                    break;
                case "outcome":
                    output["outcome"] = pair.Value?.DeepClone();
                    break;
                case "started_utc":
                    output["timestamp"] = pair.Value?.DeepClone();
                    startedMs = TimestampMs(pair.Value);
                    break;
                case "finished_utc":
                    finishedMs = TimestampMs(pair.Value);
                    break;
                case "error":
                    // Only the numeric HRESULT: code and message are user-facing text.
                    if (pair.Value is JsonObject error)
                    {
                        foreach (var inner in error)
                        {
                            if (inner.Key == "hresult")
                            {
                                output["hresult"] = inner.Value?.DeepClone();
                            }
                            else
                            {
                                dropped++;
                            }
                        }
                    }
                    else
                    {
                        dropped++;
                    }

                    break;
                case "result_json":
                    dropped += ReadResultFields(pair.Value, output);
                    break;
                default:
                    dropped++;
                    break;
            }
        }

        if (startedMs is { } start && finishedMs is { } finish)
        {
            output["elapsed_ms"] = finish - start;
        }

        return (output.Count == 0 ? null : output, dropped);
    }

    /// <summary>Takes the revision and the verification STATUS out of the recorded result; every other
    /// field of it is user data and is counted as dropped.</summary>
    private static int ReadResultFields(JsonNode? value, JsonObject output)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
        {
            return 1;
        }

        JsonObject? result;
        try
        {
            result = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return 1;
        }

        if (result is null)
        {
            return 1;
        }

        var dropped = 0;
        foreach (var pair in result)
        {
            if (pair.Key == "revision")
            {
                output["revision_after"] = pair.Value?.DeepClone();
                continue;
            }

            if (pair.Key == "verification" && pair.Value is JsonObject verification)
            {
                foreach (var inner in verification)
                {
                    if (inner.Key == "level")
                    {
                        output["verification_status"] = inner.Value?.DeepClone();
                    }
                    else
                    {
                        dropped++;
                    }
                }

                continue;
            }

            dropped++;
        }

        return dropped;
    }

    private static (JsonObject? Record, int Fields) ProjectLogRecord(JsonObject record, byte[] salt)
    {
        var output = new JsonObject();
        var dropped = 0;

        foreach (var pair in record)
        {
            if (pair.Key is "ts_utc")
            {
                output["timestamp"] = pair.Value?.DeepClone();
                continue;
            }

            if (pair.Key is "level" or "category" or "exception_type")
            {
                output[pair.Key] = Mask(pair.Value, salt);
                continue;
            }

            // `event_code` and `hresult` are allowed names, but the Host log writes neither: the
            // framework event id and the HRESULT of a framework exception are not recorded. The names
            // stay in the allow list so that adding them to the log later needs no change here.
            dropped++;
        }

        return (output.Count == 0 ? null : output, dropped);
    }

    private static JsonObject EnvironmentNode(HostOptions options, List<string> notes)
    {
        notes.Add("версия КОМПАС в пакет не попадает: Хост её не знает, её читает Worker "
            + "(kompas_capabilities), а отдельный запуск КОМПАС ради пакета запрещён");

        return new JsonObject
        {
            ["captured_from"] = "host",
            ["os"] = new JsonObject
            {
                ["description"] = RuntimeInformation.OSDescription,
                ["os_architecture"] = RuntimeInformation.OSArchitecture.ToString(),
                ["process_architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["64_bit_process"] = Environment.Is64BitProcess,
                ["64_bit_os"] = Environment.Is64BitOperatingSystem,
            },
            ["dotnet"] = Environment.Version.ToString(),
            ["kompas_version"] = null,
            ["kompas_version_reason"] = "Хост версию КОМПАС не знает: её называет Worker "
                + "(kompas_capabilities); отдельный запуск КОМПАС ради пакета запрещён.",
            ["config"] = new JsonObject
            {
                ["allow_unc_paths"] = options.AllowUncPaths,
                ["queue_capacity"] = options.QueueCapacity,
                ["sync_budget_ms"] = options.SyncBudgetMs,
                ["operation_budget_ms"] = options.OperationBudgetMs,
                ["update_check"] = options.UpdateCheckEnabled,
            },
            ["config_keys_dropped"] = DroppedConfigKeys.Length,
            ["config_keys_dropped_reason"] = "ключи-пути конфигурации не копируются: они называют "
                + "каталоги оператора",
        };
    }

    private static JsonObject ProcessesNode(List<string> notes)
    {
        var processes = new JsonArray();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!IsReported(process.ProcessName))
                {
                    continue;
                }

                DateTimeOffset? startedAt = null;
                try
                {
                    startedAt = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                    // A process that exited between enumeration and the read: named, not guessed.
                }

                processes.Add(new JsonObject
                {
                    ["name"] = process.ProcessName + ".exe",
                    ["pid"] = process.Id,
                    ["started_at_utc"] = startedAt?.ToString("O"),
                });
            }
            finally
            {
                process.Dispose();
            }
        }

        notes.Add("в processes.json только имена, pid и время запуска: командная строка, окна, "
            + "заголовки и модули не читаются");
        return new JsonObject { ["processes"] = processes };
    }

    /// <summary>Only the two families the package is about: the CAD application and this server.</summary>
    private static bool IsReported(string name) =>
        string.Equals(name, "KOMPAS", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("KompasMcp.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "KompasMcp", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ManifestNode(DateTimeOffset capturedAt, IReadOnlyList<DiagnosticFile> files, List<string> notes)
    {
        var entries = new JsonArray();
        foreach (var file in files)
        {
            entries.Add(new JsonObject
            {
                ["name"] = file.Name,
                ["size_bytes"] = file.SizeBytes,
                ["sha256"] = file.Sha256,
                ["records_read"] = file.RecordsRead,
                ["records_kept"] = file.RecordsKept,
                ["records_dropped"] = file.RecordsDropped,
                ["fields_dropped"] = file.FieldsDropped,
                ["truncated"] = file.Truncated,
            });
        }

        return new JsonObject
        {
            ["format_version"] = FormatVersion,
            ["build"] = BuildStamp(),
            ["captured_at"] = capturedAt.ToString("O"),
            ["limits"] = new JsonObject
            {
                ["max_records_per_file"] = MaxRecordsPerFile,
                ["max_package_bytes"] = MaxPackageBytes,
            },
            ["allowed_journal_fields"] = new JsonArray(JournalFields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["allowed_log_fields"] = new JsonArray(LogFields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["files"] = entries,
            ["notes"] = new JsonArray(notes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
        };
    }

    private static string BuildStamp() =>
        typeof(DiagnosticPackage).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is { Length: > 0 } attributes
            && attributes[0] is System.Reflection.AssemblyInformationalVersionAttribute stamp
            ? stamp.InformationalVersion
            : "0.0.0";

    private static void AddDocument(ZipArchive zip, string name, byte[] bytes, List<DiagnosticFile> files)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        files.Add(new DiagnosticFile(name, bytes.Length, Sha256(bytes), 0, 0, 0, 0, false));
    }

    /// <summary>Writes a JSONL file line by line while the byte budget lasts, and names the cut.</summary>
    private static long AddLines(
        ZipArchive zip,
        string name,
        (List<JsonObject> Records, int Read, int Dropped, int Fields, bool Truncated) projected,
        long budget,
        List<DiagnosticFile> files)
    {
        var builder = new StringBuilder();
        var kept = 0;
        var truncated = projected.Truncated;
        foreach (var record in projected.Records)
        {
            var line = record.ToJsonString(JsonOptions) + "\n";
            var size = Encoding.UTF8.GetByteCount(line);
            if (size > budget)
            {
                truncated = true;
                break;
            }

            budget -= size;
            builder.Append(line);
            kept++;
        }

        var bytes = Utf8(builder.ToString());
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using (var stream = entry.Open())
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        files.Add(new DiagnosticFile(
            name, bytes.Length, Sha256(bytes), projected.Read, kept, projected.Dropped, projected.Fields, truncated));
        return budget;
    }

    /// <summary>Last <paramref name="maxLines"/> non-empty lines, read from the end so a journal of
    /// hundreds of thousands of records is not loaded whole.</summary>
    /// <returns>The lines and whether the file held more than were taken.</returns>
    internal static (List<string> Lines, bool Truncated) TailLines(string path, int maxLines)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return (lines, false);
        }

        // A file without a trailing newline ends in a torn line: it is taken like any other and the
        // caller's parse decides, so the tear is COUNTED rather than silently glued to its neighbour.
        var buffer = new List<byte>();
        var position = stream.Length;
        const int ChunkSize = 64 * 1024;
        while (position > 0 && lines.Count < maxLines)
        {
            var size = (int)Math.Min(ChunkSize, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            var chunk = new byte[size];
            var read = 0;
            while (read < size)
            {
                var got = stream.Read(chunk, read, size - read);
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            buffer.InsertRange(0, chunk.AsSpan(0, read).ToArray());
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            var parts = text.Split('\n');
            if (position > 0)
            {
                // The first part may be half a line: keep it for the next round.
                buffer = new List<byte>(Utf8(parts[0]));
                parts = parts.Skip(1).ToArray();
            }
            else
            {
                buffer = new List<byte>();
            }

            for (var i = parts.Length - 1; i >= 0; i--)
            {
                if (string.IsNullOrWhiteSpace(parts[i]))
                {
                    continue;
                }

                lines.Insert(0, parts[i].TrimEnd('\r'));
                if (lines.Count == maxLines)
                {
                    return (lines, position > 0 || i > 0);
                }
            }
        }

        return (lines, position > 0);
    }

    /// <summary>Replaces a path with <c>path:&lt;12 hex of salt+path&gt;</c> and its extension: one file
    /// is still recognisable inside the package, and no dictionary reproduces the name.</summary>
    /// <remarks>INVARIANT: the salt is random per package, so the same label in two packages does not
    /// mean the same file. The salt itself never enters the package.
    /// History: docs/decisions/diagnostics.md#paths</remarks>
    internal static string MaskPath(string path, byte[] salt)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(salt.Concat(Encoding.UTF8.GetBytes(normalized)).ToArray());
        var extension = Path.GetExtension(path);
        return "path:" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant()
            + (string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant());
    }

    private static JsonNode? Mask(JsonNode? value, byte[] salt)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && LooksLikePath(text))
        {
            return JsonValue.Create(MaskPath(text, salt));
        }

        return value?.DeepClone();
    }

    private static bool LooksLikePath(string value) =>
        value.Contains(@":\", StringComparison.Ordinal)
        || value.Contains(":/", StringComparison.Ordinal)
        || value.StartsWith(@"\\", StringComparison.Ordinal);

    private static long? TimestampMs(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        return null;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    internal static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is noise, not data loss.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
