using System.Text.RegularExpressions;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>A check that cannot fail is not a check: `passed` must come from a comparison.</summary>
/// <remarks>INVARIANT: a <c>NamedCheck</c> built with the literal <c>true</c> publishes confirmation
/// without comparing anything — the defect class the client read as "the product verified this".
/// The list below is the ONLY place such a literal may stay, each entry with its reason.
/// LIMIT: the guard reads the adapter's SOURCE, so it sees the form and not the meaning: a comparison
/// that is always true still passes. History: docs/decisions/tests.md#check-truth-guard</remarks>
public sealed class CheckTruthGuardTests
{
    /// <summary>Both constructor spellings the adapter uses, with the line break the long calls take.
    /// <c>passed: true</c> is banned outright — it is the same literal without a constructor.</summary>
    private static readonly Regex LiteralTrue = new(
        @"new\s*(?:NamedCheck\s*)?\(\s*""(?<name>[A-Za-z0-9_]+)""\s*,\s*true\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex PassedTrue = new(@"passed\s*:\s*true\b", RegexOptions.Compiled);

    /// <summary>Literals allowed to stay, by <c>file:check</c>, each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // Kept on purpose: the check names its own applicability in `Observed`, so a look-only edit that
        // rebuilds no picture is a read fact there, not a failed edit (debt 45).
        ["Api5Session.SectionViews.cs:view_updated"] =
            "применимость названа в Observed: look-правка геометрию вида не трогает",
        // The rest are outside the FALSE_CHECKS_SWEEP list: the sweep's regular expression required a
        // comma after `true`, so `new("name", true)` never entered it. Named here, not examined there.
        ["Api5Session.Assembly.cs:inserted_component_read_back"] = Outside,
        ["Api5Session.Assembly.cs:inserted_address_confirmed"] = Outside,
        ["Api5Session.Assembly.cs:broken_named"] = Outside,
        ["Api5Session.Chamfer.cs:api7_write_applied"] = Outside,
        ["Api5Session.Chamfer.cs:set_chamfer_param"] = Outside,
        ["Api5Session.CopyGeometry.cs:source_readable"] = Outside,
        ["Api5Session.Exchange.cs:file_created"] = Outside,
        ["Api5Session.FilletEdit.cs:api7_write_applied"] = Outside,
        ["Api5Session.Geometry.cs:feature_created"] = Outside,
        ["Api5Session.Pattern.cs:pattern_created"] = Outside,
        ["Api5Session.SectionViews.cs:view_is_section"] = Outside,
        ["Api5Session.StlExport.cs:file_created"] = Outside,
        ["Api5Session.Thread.cs:tree_object_present"] = Outside,
    };

    private const string Outside =
        "вне перечня §3.1: та же форма, нарядом не разбиралась";

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

    /// <summary>Source with comment lines blanked: line NUMBERS stay, so the failure names the real line.
    /// A "this used to be" comment matches the banned form as text, but the ban is on executable code.</summary>
    private static string WithoutComments(string text) => string.Join("\n", text
        .Split('\n')
        .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [Fact]
    public void Adapter_ChecksWithLiteralTrue_AreOnlyTheAllowedOnes()
    {
        var found = new List<string>();
        var offenders = new List<string>();

        foreach (var path in AdapterSources())
        {
            var file = Path.GetFileName(path);
            var text = WithoutComments(File.ReadAllText(path));

            foreach (Match match in LiteralTrue.Matches(text))
            {
                var key = file + ":" + match.Groups["name"].Value;
                found.Add(key);
                if (!Allowed.ContainsKey(key))
                {
                    offenders.Add($"{file}:{LineOf(text, match.Index)} — {match.Groups["name"].Value}");
                }
            }

            foreach (Match match in PassedTrue.Matches(text))
            {
                offenders.Add($"{file}:{LineOf(text, match.Index)} — passed: true");
            }
        }

        Assert.True(offenders.Count == 0,
            "проверка с литералом true ничего не сравнивает и в список разрешённых не внесена: "
            + string.Join("; ", offenders));

        // An entry whose check was fixed or renamed must leave the list: otherwise the list rots into a
        // permission for a name nobody uses, and the next check may take it.
        var stale = Allowed.Keys.Where(key => !found.Contains(key)).ToList();
        Assert.True(stale.Count == 0, "в списке разрешённых есть проверки, которых в коде нет: "
            + string.Join("; ", stale));
    }
}
