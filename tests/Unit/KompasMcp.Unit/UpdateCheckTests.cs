using System.Text.Json.Nodes;
using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The release notice: which version wins, what the client is told, and what a failed check
/// leaves behind. No network: the source is the local feed file the order names as the instrument.</summary>
public class UpdateCheckTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kompas-update-tests", Guid.NewGuid().ToString("N"));
    private readonly string _feed;
    private readonly string? _previousFeed;

    public UpdateCheckTests()
    {
        Directory.CreateDirectory(_directory);
        _previousFeed = Environment.GetEnvironmentVariable(UpdateCheck.FeedVariable);
        // TEST: the feed is always a local path, so no test can reach the network. A missing file is a
        // failed check, which is exactly what the states below are built on.
        _feed = Path.Combine(_directory, "feed.json");
        Environment.SetEnvironmentVariable(UpdateCheck.FeedVariable, _feed);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(UpdateCheck.FeedVariable, _previousFeed);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    private string CachePath => Path.Combine(_directory, "update-check.json");

    private void WriteFeed(string body) => File.WriteAllText(_feed, body);

    private static UpdateCheck.Version3 Current()
    {
        Assert.True(UpdateCheck.TryVersion(Program.ServerVersion, out var current), "the assembly version must be MAJOR.MINOR.PATCH");
        return current;
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false, int padding = 0) =>
        new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = "Выпуск " + tag,
            ["html_url"] = "https://github.com/SergeySklyarov/Kompas3D-MCP/releases/tag/" + tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["body"] = new string('x', padding),
        }.ToJsonString();

    /// <summary>Run one check against the given feed body and return the answer of kompas_health.</summary>
    private async Task<JsonObject> CheckAsync(string? feedBody, bool enabled = true)
    {
        if (feedBody is not null)
        {
            WriteFeed(feedBody);
        }

        var check = UpdateCheck.Open(CachePath, enabled);
        await check.RefreshAsync();
        return check.ToJson();
    }

    private static string Status(JsonObject answer) => answer["status"]!.GetValue<string>();

    /// <summary>Wait for the background request the Host itself started; the source is a local file.</summary>
    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "the background check did not land in time");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static string Tag(UpdateCheck.Version3 version) => $"v{version.Major}.{version.Minor}.{version.Patch}";

    // ---- The comparison rule ----

    [Theory]
    [InlineData(0, 8, 0, 0, 10, 0, true)]     // before 1.0.0 a new MINOR is the breaking one
    [InlineData(0, 10, 0, 0, 10, 1, false)]   // a patch is never breaking
    [InlineData(1, 2, 0, 1, 3, 0, false)]     // after 1.0.0 a new MINOR is not breaking
    [InlineData(1, 2, 0, 2, 0, 0, true)]      // a new MAJOR always is
    public void MayBreak_FollowsTheVersionRule(int fromMajor, int fromMinor, int fromPatch, int toMajor, int toMinor, int toPatch, bool expected)
    {
        var current = new UpdateCheck.Version3(fromMajor, fromMinor, fromPatch);
        var latest = new UpdateCheck.Version3(toMajor, toMinor, toPatch);
        Assert.Equal(expected, UpdateCheck.BreaksContract(current, latest));
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("v0.10.0", true)]
    [InlineData("1.2.3", false)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3-rc1", false)]
    [InlineData("", false)]
    public void Tag_IsComparedOnlyWhenItIsThreeNumbers(string tag, bool accepted) =>
        Assert.Equal(accepted, UpdateCheck.TryTag(tag, out _));

    [Fact]
    public void Version_KeepsTheLeadingTriple()
    {
        Assert.True(UpdateCheck.TryVersion("0.10.0+8f2a1c", out var version));
        Assert.Equal(new UpdateCheck.Version3(0, 10, 0), version);
    }

    // ---- The states the client sees ----

    [Fact]
    public async Task NewerRelease_IsAvailableWithTheWayToUpdate()
    {
        var current = Current();
        var newer = $"v{current.Major + 1}.0.0";
        var answer = await CheckAsync(Release(newer));

        Assert.Equal(UpdateCheck.Available, Status(answer));
        Assert.Equal(newer[1..], answer["latest_version"]!.GetValue<string>());
        Assert.True(answer["may_break"]!.GetValue<bool>());
        Assert.Contains("установщик", answer["how_to_update"]!.GetValue<string>());
        Assert.Contains("перезапустите сервер", answer["how_to_update"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameVersion_IsUpToDateWithoutTheWayToUpdate()
    {
        var answer = await CheckAsync(Release(Tag(Current())));

        Assert.Equal(UpdateCheck.UpToDate, Status(answer));
        Assert.False(answer.ContainsKey("how_to_update"));
        Assert.False(answer.ContainsKey("may_break"));
    }

    [Fact]
    public async Task OlderRelease_IsUpToDate()
    {
        var current = Current();
        var older = current.Patch > 0
            ? $"v{current.Major}.{current.Minor}.{current.Patch - 1}"
            : $"v{current.Major}.{Math.Max(0, current.Minor - 1)}.0";
        Assert.Equal(UpdateCheck.UpToDate, Status(await CheckAsync(Release(older))));
    }

    [Theory]
    [InlineData(true, false, "draft")]
    [InlineData(false, true, "prerelease")]
    [InlineData(false, false, "tag_is_not")]
    public async Task NotAReleaseToCompare_IsUnknownWithAReason(bool draft, bool prerelease, string reason)
    {
        var tag = reason == "tag_is_not" ? "release-9.9.9" : "v99.0.0";
        var answer = await CheckAsync(Release(tag, draft, prerelease));

        Assert.Equal(UpdateCheck.Unknown, Status(answer));
        Assert.Contains(reason, answer["reason"]!.GetValue<string>());
        Assert.False(answer.ContainsKey("how_to_update"));
    }

    [Fact]
    public async Task BrokenJson_IsUnknown()
    {
        var answer = await CheckAsync("{not json");
        Assert.Equal(UpdateCheck.Unknown, Status(answer));
        Assert.False(string.IsNullOrWhiteSpace(answer["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task UnreachableFeed_IsUnknown()
    {
        Environment.SetEnvironmentVariable(UpdateCheck.FeedVariable, Path.Combine(_directory, "missing.json"));
        var check = UpdateCheck.Open(CachePath, enabled: true);
        await check.RefreshAsync();

        Assert.Equal(UpdateCheck.Unknown, Status(check.ToJson()));
    }

    [Fact]
    public async Task Disabled_MakesNoRequestAndSaysSo()
    {
        // The feed would answer "available" if it were read: the state proves the request never ran.
        var answer = await CheckAsync(Release($"v{Current().Major + 1}.0.0"), enabled: false);

        Assert.Equal(UpdateCheck.Disabled, Status(answer));
        Assert.False(File.Exists(CachePath));
    }

    // ---- The cached answer is what a call sees ----

    [Fact]
    public void FreshOutcome_IsWhatTheCallSees()
    {
        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow, "v99.0.0"));
        var check = UpdateCheck.Open(CachePath, enabled: true);

        Assert.Equal(UpdateCheck.Available, check.State());
        Assert.False(string.IsNullOrWhiteSpace(check.InstructionsLine));
    }

    [Fact]
    public void StaleOutcome_IsStillWhatTheCallSeesWithItsOwnTime()
    {
        // TEST: an answer older than the term is not hidden — it is what a call reads, and
        // `checked_utc` names its time; the re-check runs behind it and the feed here fails.
        var checkedUtc = DateTimeOffset.UtcNow.AddHours(-7);
        File.WriteAllText(CachePath, Cached(checkedUtc, Tag(Current())));

        var answer = UpdateCheck.Open(CachePath, enabled: true).ToJson();

        Assert.Equal(UpdateCheck.UpToDate, Status(answer));
        Assert.Equal(checkedUtc.ToString("O"), answer["checked_utc"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_AsksEvenWithAFreshCache()
    {
        // TEST: a start asks whatever the cache says. MEASURED: an answer taken before the release was
        // published cancelled the request and named the previous release for the rest of the day.
        var current = Current();
        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow, Tag(current)));
        WriteFeed(Release($"v{current.Major + 1}.0.0"));

        var check = UpdateCheck.Open(CachePath, enabled: true);
        Assert.Equal(UpdateCheck.UpToDate, check.State());
        check.StartBackground();

        await WaitUntil(() => check.State() == UpdateCheck.Available);
    }

    [Fact]
    public async Task Call_DoesNotWaitForTheRequest()
    {
        // TEST: a call reads the snapshot. The feed is large enough that the request cannot have
        // finished, so the first answer is the cached one and the new one arrives afterwards.
        var current = Current();
        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow, Tag(current)));
        WriteFeed(Release($"v{current.Major + 1}.0.0", padding: 8 * 1024 * 1024));

        var check = UpdateCheck.Open(CachePath, enabled: true);
        check.StartBackground();
        Assert.Equal(UpdateCheck.UpToDate, check.State());

        await WaitUntil(() => check.State() == UpdateCheck.Available);
    }

    [Fact]
    public async Task Ageing_FollowsTheSixHourTerm()
    {
        // TEST: five hours old is still trusted and the call is not re-asked; seven hours old starts a
        // re-check behind the answer — the term is six hours, not a day.
        var current = Current();
        WriteFeed(Release($"v{current.Major + 1}.0.0"));

        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow.AddHours(-5), Tag(current)));
        var young = UpdateCheck.Open(CachePath, enabled: true);
        Assert.Equal(UpdateCheck.UpToDate, young.State());
        await Task.Delay(300);
        Assert.Equal(UpdateCheck.UpToDate, young.State());

        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow.AddHours(-7), Tag(current)));
        var old = UpdateCheck.Open(CachePath, enabled: true);
        Assert.Equal(UpdateCheck.UpToDate, old.State());
        await WaitUntil(() => old.State() == UpdateCheck.Available);
    }

    [Fact]
    public void CacheOfAnotherHostVersion_IsNotUpToDate()
    {
        // TEST: `up_to_date` of a cache written by another Host version is not repeated — its answer
        // predates the version now installed; the same cache written by this version is repeated.
        var same = Tag(Current());
        var other = Path.Combine(_directory, "other-version.json");
        File.WriteAllText(other, Cached(DateTimeOffset.UtcNow, same, hostVersion: "0.0.1"));
        Assert.NotEqual(UpdateCheck.UpToDate, UpdateCheck.Open(other, enabled: true).State());

        var mine = Path.Combine(_directory, "this-version.json");
        File.WriteAllText(mine, Cached(DateTimeOffset.UtcNow, same));
        Assert.Equal(UpdateCheck.UpToDate, UpdateCheck.Open(mine, enabled: true).State());
    }

    [Fact]
    public void DamagedOutcome_IsNotAnAnswer()
    {
        File.WriteAllText(CachePath, "{oops");
        var state = UpdateCheck.Open(CachePath, enabled: true).State();

        Assert.True(state is UpdateCheck.Pending or UpdateCheck.Unknown, "a damaged cache is not an answer: " + state);
    }

    [Fact]
    public async Task Check_LeavesTheOutcomeForTheNextStart()
    {
        var newer = $"v{Current().Major + 1}.0.0";
        await CheckAsync(Release(newer));

        var restarted = UpdateCheck.Open(CachePath, enabled: true);
        Assert.Equal(UpdateCheck.Available, restarted.State());
        Assert.Contains("доступна", restarted.InstructionsLine ?? string.Empty);
    }

    private static string Cached(DateTimeOffset checkedUtc, string tag, string? hostVersion = null) => new JsonObject
    {
        ["checked_utc"] = checkedUtc.ToString("O"),
        ["host_version"] = hostVersion ?? Program.ServerVersion,
        ["tag"] = tag,
        ["release_url"] = "https://example.invalid/release",
        ["release_title"] = "Выпуск",
        ["reason"] = null,
    }.ToJsonString();
}
