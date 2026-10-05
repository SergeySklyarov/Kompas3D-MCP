using System.Text.RegularExpressions;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Visibility guarantees that cannot be checked without KOMPAS, but can be checked in the code.</summary>
/// <remarks>INVARIANT: the "visible to the user" mode refreshes the view after modelling and never resets the
/// user camera and zoom — the second half bans <c>ZoomPrevNextOrAll</c> and <c>ksZoom*</c>, which change exactly
/// the camera. LIMIT: the check is static because a 3D document in API5 exposes no camera state (no scale
/// getter on <c>ksDocument3D</c>, only <c>ksGetZoomScale</c> on the 2D editor), so "the camera did not change"
/// cannot be confirmed by a number at acceptance. INVARIANT: the visibility answer is never derived from PID
/// availability — a hidden window has both an HWND and a PID, so <c>ProcessIdOf(...) is not null</c> would read
/// as a false <c>visible=true</c>. History: docs/decisions/tests.md#api5-visibility-2</remarks>
public sealed class Api5SessionVisibilityGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KompasMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> AdapterSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter"), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

    /// <summary>Code lines without comments: a "this used to be" comment matches the forbidden expression
    /// as text, but the ban applies to executable code.</summary>
    private static IEnumerable<string> CodeLines(params string[] paths) => paths
        .SelectMany(path => File.ReadLines(path))
        .Select(line => line.TrimStart())
        .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                       && !line.StartsWith("///", StringComparison.Ordinal));

    [Fact]
    public void Adapter_NeverCallsZoom_MustNotResetUserCamera()
    {
        var pattern = new Regex(@"\b(ZoomPrevNextOrAll|ksZoom|ksZoomScale|ksZoomPrevNextOrAll)\s*\(",
            RegexOptions.Compiled);

        var offenders = AdapterSources()
            .SelectMany(path => File.ReadLines(path)
                .Select((line, number) => (path, number, line))
                .Where(row => pattern.IsMatch(row.line)
                              // a comment or XML doc counts as a reference, not a call
                              && !row.line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ApplicationVisibility_IsNotInferredFromProcessIdHandle()
    {
        var code = string.Join("\n", CodeLines(
            Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", "Api5Session.cs")));

        // The very line that let the defect through acceptance: "a PID is obtained" was passed off as "visible".
        Assert.False(code.Contains("Visible = ProcessIdOf", StringComparison.Ordinal),
            "видимость снова выводится из доступности PID по HWND — скрытое окно даёт и HWND, и PID");

        // INVARIANT: the definition rests on observing the window, not on an indirect sign.
        Assert.Contains("Visible = observed.Visible", code, StringComparison.Ordinal);
        Assert.Contains("IsWindowVisible",
            File.ReadAllText(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter",
                "Api5Session.Visibility.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenMode_StillShortCircuitsBeforeAnyWindowTraffic()
    {
        var visibility = File.ReadAllText(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter",
            "Api5Session.Visibility.cs"));

        // INVARIANT: hidden mode returns before touching the window — otherwise "hidden mode is checked
        // separately" is a promise, not behaviour.
        var guard = visibility.IndexOf("if (!document.DocumentsVisible)", StringComparison.Ordinal);
        var refresh = visibility.IndexOf("ksRefreshActiveWindow()", guard, StringComparison.Ordinal);
        Assert.True(guard >= 0, "нет проверки режима в RefreshViewAfterMutation");
        Assert.True(refresh > guard, "перерисовка допускается до проверки видимого режима");
    }
}
