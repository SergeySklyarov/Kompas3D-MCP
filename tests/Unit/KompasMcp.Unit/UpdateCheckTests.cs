using System.Text.Json.Nodes;
using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The release notice: which version wins, what the client is told, and what a failed check
/// leaves behind. No network: the source is the local feed file the order names as the instrument.</summary>
public class UpdateCheckTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kompas-update-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousFeed;

    public UpdateCheckTests()
    {
        Directory.CreateDirectory(_directory);
        _previousFeed = Environment.GetEnvironmentVariable(UpdateCheck.FeedVariable);
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

    private static UpdateCheck.Version3 Current()
    {
        Assert.True(UpdateCheck.TryVersion(Program.ServerVersion, out var current), "the assembly version must be MAJOR.MINOR.PATCH");
        return current;
    }

    private static string Release(string tag, bool draft = false, bool prerelease = false) =>
        new JsonObject
        {
            ["tag_name"] = tag,
            ["name"] = "Выпуск " + tag,
            ["html_url"] = "https://github.com/SergeySklyarov/Kompas3D-MCP/releases/tag/" + tag,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
        }.ToJsonString();

    /// <summary>Run one check against the given feed body and return the answer of kompas_health.</summary>
    private async Task<JsonObject> CheckAsync(string? feedBody, bool enabled = true)
    {
        var feed = Path.Combine(_directory, "feed.json");
        if (feedBody is not null)
        {
            File.WriteAllText(feed, feedBody);
        }

        Environment.SetEnvironmentVariable(UpdateCheck.FeedVariable, feed);
        var check = UpdateCheck.Open(CachePath, enabled);
        await check.RefreshAsync();
        return check.ToJson();
    }

    private static string Status(JsonObject answer) => answer["status"]!.GetValue<string>();

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
        var current = Current();
        var answer = await CheckAsync(Release($"v{current.Major}.{current.Minor}.{current.Patch}"));

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
        var current = Current();
        var answer = await CheckAsync(Release($"v{current.Major + 1}.0.0"), enabled: false);

        Assert.Equal(UpdateCheck.Disabled, Status(answer));
        Assert.False(File.Exists(CachePath));
    }

    // ---- The cached outcome ----

    [Fact]
    public void FreshOutcome_IsUsedWithoutACheck()
    {
        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow, "v99.0.0"));
        var check = UpdateCheck.Open(CachePath, enabled: true);

        Assert.Equal(UpdateCheck.Available, check.State());
        Assert.False(string.IsNullOrWhiteSpace(check.InstructionsLine));
    }

    [Fact]
    public void StaleOutcome_IsNotUsedAndTheStateIsPending()
    {
        File.WriteAllText(CachePath, Cached(DateTimeOffset.UtcNow.AddHours(-25), "v99.0.0"));
        Assert.Equal(UpdateCheck.Pending, UpdateCheck.Open(CachePath, enabled: true).State());
    }

    [Fact]
    public void DamagedOutcome_IsTreatedAsAbsent()
    {
        File.WriteAllText(CachePath, "{oops");
        Assert.Equal(UpdateCheck.Pending, UpdateCheck.Open(CachePath, enabled: true).State());
    }

    [Fact]
    public async Task Check_LeavesTheOutcomeForTheNextStart()
    {
        var current = Current();
        var newer = $"v{current.Major + 1}.0.0";
        await CheckAsync(Release(newer));

        var restarted = UpdateCheck.Open(CachePath, enabled: true);
        Assert.Equal(UpdateCheck.Available, restarted.State());
        Assert.Contains("доступна", restarted.InstructionsLine ?? string.Empty);
    }

    private static string Cached(DateTimeOffset checkedUtc, string tag) => new JsonObject
    {
        ["checked_utc"] = checkedUtc.ToString("O"),
        ["tag"] = tag,
        ["release_url"] = "https://example.invalid/release",
        ["release_title"] = "Выпуск",
        ["reason"] = null,
    }.ToJsonString();
}
