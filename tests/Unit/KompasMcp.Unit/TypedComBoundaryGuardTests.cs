using Xunit;

namespace KompasMcp.Unit;

/// <summary>The "the product talks to KOMPAS only in a typed way" boundary.</summary>
/// <remarks>INVARIANT: late binding (<c>Type.InvokeMember</c>, <c>dynamic</c> over an RCW, a direct
/// <c>IDispatch</c> call) is the emergency route of the research probe only, never carried into <c>src/</c>.
/// MEASURED: a write through <c>IDispatch</c> crashed the shared process but not the isolated one, and the
/// same write silently did not persist — a silently unaccepted write returning S_OK is worse than a refusal,
/// because acceptance would count it a success.
/// LIMIT: the check is static by source — "no late binding" is a property of code, not an observable number.
/// History: docs/decisions/tests.md#typed-com-2</remarks>
public sealed class TypedComBoundaryGuardTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KompasMcp.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException(
            "KompasMcp.sln не найден выше " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", project), "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal));

    /// <summary>Executable code lines: a comment or XML doc counts as a reference, not a call.</summary>
    private static IEnumerable<(string Path, int Number, string Line)> CodeLines(IEnumerable<string> paths) =>
        paths.SelectMany(path => File.ReadLines(path).Select((line, number) => (path, number, line)))
            .Where(row => !row.line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                          && !row.line.TrimStart().StartsWith("///", StringComparison.Ordinal));

    [Theory]
    [InlineData("KompasMcp.Api5Adapter")]
    [InlineData("KompasMcp.Worker")]
    [InlineData("KompasMcp.Host")]
    [InlineData("KompasMcp.Contracts")]
    [InlineData("KompasMcp.Domain")]
    public void ProductCode_NeverBindsToKompasLateBound(string project)
    {
        // Late binding under any name — the same forms the probe's Late.cs uses; copying any into the
        // product carries over exactly what the boundary guards against.
        var patterns = new[]
        {
            ("InvokeMember", "Type.InvokeMember — позднее связывание в продукте"),
            ("IDispatchComObject", "прямая работа с IDispatch-объектом в продукте"),
            ("IDispatchNative", "собственное объявление IDispatch в продукте"),
            ("GetIDsOfNames", "позднее связывание по имени члена в продукте"),
        };

        var offenders = new List<string>();
        foreach (var path in SourceFiles(project))
        {
            foreach (var (file, number, line) in CodeLines([path]))
            {
                foreach (var (needle, what) in patterns)
                {
                    if (line.Contains(needle, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{number + 1}: {what} — «{line.Trim()}»");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"{project}: нарушение границы типизированного COM:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ProductCode_MustNotDeclareDynamicReceivers()
    {
        // dynamic allows a call that compiles without a signature check and goes to IDispatch.
        var offenders = new List<string>();
        foreach (var path in SourceFiles("KompasMcp.Api5Adapter"))
        {
            foreach (var (file, number, line) in CodeLines([path]))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        line, @"\bdynamic\s+\w+\s*[=;,)]|\bdynamic\s*\*?\s+\w+"))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number + 1}: «{line.Trim()}»");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "в адаптере появились dynamic-получатели — вызовы уйдут в позднее связывание:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void LateBindingRoute_StillLivesOnlyInTheIsolatedProbe()
    {
        // The inverse of the same requirement: the emergency route must stay in the probes project. If it
        // disappears from there while still absent from the product, the check below has become a vacuum — that
        // must be learned, not celebrated as a green test.
        var probe = Path.Combine(RepoRoot, "tools", "KompasMcp.Api7Probe", "Late.cs");
        Assert.True(File.Exists(probe),
            "позднее связывание больше нигде не разрешено, но и исследовательский файл пропал: " +
            "границу пора пересматривать явно, а не молча");
        var text = File.ReadAllText(probe);
        Assert.Contains("IDispatchNative", text, StringComparison.Ordinal);
        Assert.Contains("GetIDsOfNames", text, StringComparison.Ordinal);
    }
}
