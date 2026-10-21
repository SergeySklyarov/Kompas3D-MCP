using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;

namespace KompasMcp.Host;

/// <summary>Whether a newer stable release exists — learned in the background, never in the way of a call.</summary>
/// <remarks>INVARIANT: nothing here may delay <c>initialize</c>, <c>tools/list</c> or a tool call. The
/// network answer lands in a file next to the Host log and is reused for a day; a call reads the
/// in-memory snapshot only. The Host downloads and installs nothing.
/// History: docs/decisions/host.md#update-notice</remarks>
public sealed class UpdateCheck
{
    public const string Repo = "SergeySklyarov/Kompas3D-MCP";

    public const string FeedUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Local JSON file in the shape of a <c>releases/latest</c> answer. A path, not an address:
    /// it cannot redirect the Host to another server. It exists so the check is testable without a network.
    /// </summary>
    public const string FeedVariable = "KOMPAS_MCP_UPDATE_FEED";

    public const string UpToDate = "up_to_date";
    public const string Available = "available";
    public const string Unknown = "unknown";
    public const string Disabled = "disabled";
    public const string Pending = "pending";

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>A tag is compared as numbers, so only this shape is comparable at all.</summary>
    private static readonly Regex TagPattern = new(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    private readonly HostLog? _log;
    private readonly bool _enabled;
    private readonly string _path;

    /// <summary>The last known outcome. Written by the background task, read by tool calls: a plain
    /// reference swap, so a call never waits for a lock the network holds.</summary>
    private volatile UpdateRecord? _record;

    private UpdateCheck(bool enabled, string path, HostLog? log)
    {
        _enabled = enabled;
        _path = path;
        _log = log;
    }

    /// <summary>Read the cached outcome, then decide whether a fresh check is due. Never touches the network.</summary>
    public static UpdateCheck Open(HostOptions options, HostLog log) =>
        Open(HostOptions.Default("update-check.json"), options.UpdateCheckEnabled, log);

    /// <summary>Same, with the cache path and the switch named: the tests keep their own file instead of
    /// the one a working Host uses, and a checker that never shares a file with a running product.</summary>
    public static UpdateCheck Open(string path, bool enabled, HostLog? log = null)
    {
        var check = new UpdateCheck(enabled, path, log);
        check._record = ReadRecord(check._path);
        return check;
    }

    /// <summary>The file the outcome is cached in, next to the Host log's default directory.</summary>
    public string RecordPath => _path;

    /// <summary>Start the one background request when the cached outcome is missing or older than a day.</summary>
    public void StartBackground()
    {
        if (!_enabled || IsFresh(_record))
        {
            return;
        }

        _ = Task.Run(RefreshAsync);
    }

    private static bool IsFresh(UpdateRecord? record) =>
        record is not null && DateTimeOffset.UtcNow - record.CheckedUtc <= Fresh;

    /// <summary>The state the client sees, from the snapshot only.</summary>
    public string State()
    {
        if (!_enabled)
        {
            return Disabled;
        }

        if (!IsFresh(_record))
        {
            return Pending;
        }

        return _record!.Reason is not null ? Unknown : Compared();
    }

    private string Compared()
    {
        if (!TryVersion(Program.ServerVersion, out var current) || !TryTag(_record!.Tag, out var latest))
        {
            return Unknown;
        }

        return latest.CompareTo(current) > 0 ? Available : UpToDate;
    }

    /// <summary>The <c>update</c> field of a <c>kompas_health</c> answer.</summary>
    public JsonObject ToJson()
    {
        var state = State();
        var node = new JsonObject
        {
            ["status"] = state,
            ["current_version"] = Program.ServerVersion,
        };

        if (state is UpToDate or Available or Unknown && _record is { } record)
        {
            if (TryTag(record.Tag, out var latest))
            {
                node["latest_version"] = $"{latest.Major}.{latest.Minor}.{latest.Patch}";
            }

            node["checked_utc"] = record.CheckedUtc.ToString("O");
            node["release_url"] = record.Url;
            node["release_title"] = record.Title;
            if (record.Reason is { } reason)
            {
                node["reason"] = reason;
            }
        }

        if (state == Available && TryVersion(Program.ServerVersion, out var current) && TryTag(_record!.Tag, out var newer))
        {
            node["may_break"] = BreaksContract(current, newer);
            node["how_to_update"] = HowToUpdate(_record.Url);
        }

        return node;
    }

    /// <summary>The one line <c>initialize</c> may carry, or <c>null</c> when nothing is known yet.</summary>
    public string? InstructionsLine => State() == Available && _record is { } record && TryTag(record.Tag, out var latest)
        ? $"Установлена версия {Program.ServerVersion}, доступна {latest.Major}.{latest.Minor}.{latest.Patch}: "
          + "подробности и порядок обновления — kompas_health, поле update."
        : null;

    /// <summary>Whether a version change may break the contract: a new MAJOR, or a new MINOR while the
    /// product is still pre-release (MAJOR 0).</summary>
    /// <remarks>INVARIANT: this is the project's versioning rule — while MAJOR is 0 the breaking change is
    /// MINOR, after that only MAJOR is. The code tests MAJOR = 0, so that number IS the rule, not history.
    /// History: docs/decisions/releases.md#versioning</remarks>
    public static bool BreaksContract(Version3 current, Version3 latest) =>
        latest.Major != current.Major || (current.Major == 0 && latest.Major == 0 && latest.Minor != current.Minor);

    private static string HowToUpdate(string? url) =>
        "Обновляет пользователь или агент с его согласия: запустите установщик выпуска без -Tag — он "
        + "переключит установку на новую версию, — затем перезапустите сервер в клиенте, новая версия "
        + "заработает после перезапуска"
        + (url is { Length: > 0 } ? $". При may_break = true сначала прочитайте заметки выпуска: {url}." : ".");

    // ---- The request ----

    /// <summary>One check, awaited by the tests and by nothing else.</summary>
    public async Task RefreshAsync()
    {
        if (!_enabled)
        {
            // INVARIANT: switched off means no request at all, not a request whose result is ignored.
            return;
        }

        UpdateRecord record;
        try
        {
            var body = await FetchAsync().ConfigureAwait(false);
            record = body is null ? UpdateRecord.Failed("feed_unreachable") : Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or FormatException or NotSupportedException)
        {
            // A failed check is a named state, not a warning in other tools' answers.
            record = UpdateRecord.Failed(ex is TaskCanceledException ? "timeout" : ex.GetType().Name + ": " + ex.Message);
        }

        _record = record;
        WriteRecord(_path, record);
        _log?.Write("info", "update check", new { state = record.Reason is null ? "checked" : "unknown", tag = record.Tag, reason = record.Reason });
    }

    /// <summary>The feed body, or <c>null</c> when the answer was not 200.</summary>
    private async Task<string?> FetchAsync()
    {
        // A local file is the test instrument: it replaces the source without touching the address.
        if (Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } local)
        {
            return File.ReadAllText(local);
        }

        using var http = new HttpClient { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KompasMCP/" + Program.ServerVersion);
        using var response = await http.GetAsync(FeedUrl).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
    }

    /// <summary>Read a <c>releases/latest</c> answer. Drafts and prereleases are not a release to compare
    /// with, and an unreadable tag is a reason rather than a string comparison.</summary>
    private static UpdateRecord Parse(string body)
    {
        if (JsonNode.Parse(body) is not JsonObject release)
        {
            return UpdateRecord.Failed("release_is_not_an_object");
        }

        if (release["draft"]?.GetValue<bool>() == true)
        {
            return UpdateRecord.Failed("release_is_draft");
        }

        if (release["prerelease"]?.GetValue<bool>() == true)
        {
            return UpdateRecord.Failed("release_is_prerelease");
        }

        var tag = release["tag_name"]?.GetValue<string>();
        if (tag is null || !TagPattern.IsMatch(tag))
        {
            return UpdateRecord.Failed($"tag_is_not_vMAJOR.MINOR.PATCH: {tag ?? "нет поля tag_name"}");
        }

        return new UpdateRecord(tag, release["html_url"]?.GetValue<string>(), release["name"]?.GetValue<string>(), null, DateTimeOffset.UtcNow);
    }

    // ---- The cached outcome ----

    private static UpdateRecord? ReadRecord(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node)
            {
                return null;
            }

            var checkedUtc = DateTimeOffset.Parse(node["checked_utc"]?.GetValue<string>() ?? string.Empty, null, System.Globalization.DateTimeStyles.RoundtripKind);
            return new UpdateRecord(
                node["tag"]?.GetValue<string>(),
                node["release_url"]?.GetValue<string>(),
                node["release_title"]?.GetValue<string>(),
                node["reason"]?.GetValue<string>(),
                checkedUtc);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            // A damaged cache is treated as no cache: the next check rewrites it.
            return null;
        }
    }

    private void WriteRecord(string path, UpdateRecord record)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var node = new JsonObject
            {
                ["checked_utc"] = record.CheckedUtc.ToString("O"),
                ["tag"] = record.Tag,
                ["release_url"] = record.Url,
                ["release_title"] = record.Title,
                ["reason"] = record.Reason,
            };
            File.WriteAllText(path, node.ToJsonString(KompJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The cache is an optimisation; the answer of this run is already in memory.
            _log?.Write("warn", "update check not cached", new { path, problem = ex.Message });
        }
    }

    // ---- Versions ----

    /// <summary>A three-number version, the only form a release tag and <c>ServerVersion</c> share.</summary>
    public readonly record struct Version3(int Major, int Minor, int Patch) : IComparable<Version3>
    {
        public int CompareTo(Version3 other) =>
            Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
    }

    /// <summary>The leading <c>MAJOR.MINOR.PATCH</c> of the assembly version; a suffix after it is not compared.</summary>
    public static bool TryVersion(string? text, out Version3 version)
    {
        version = default;
        var match = Regex.Match(text ?? string.Empty, @"^(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
        return match.Success && TryBuild(match, out version);
    }

    /// <summary>A release tag, which must be exactly <c>vMAJOR.MINOR.PATCH</c>.</summary>
    public static bool TryTag(string? tag, out Version3 version)
    {
        version = default;
        var match = TagPattern.Match(tag ?? string.Empty);
        return match.Success && TryBuild(match, out version);
    }

    private static bool TryBuild(Match match, out Version3 version)
    {
        version = default;
        if (!int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch))
        {
            return false;
        }

        version = new Version3(major, minor, patch);
        return true;
    }

    private sealed record UpdateRecord(string? Tag, string? Url, string? Title, string? Reason, DateTimeOffset CheckedUtc)
    {
        public static UpdateRecord Failed(string reason) => new(null, null, null, reason, DateTimeOffset.UtcNow);
    }
}
