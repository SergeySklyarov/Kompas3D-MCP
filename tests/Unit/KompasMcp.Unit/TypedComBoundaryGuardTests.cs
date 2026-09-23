using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Граница «продукт говорит с КОМПАСом только типизированно» (поручение от 12.09.2026, п. 4).
/// </summary>
/// <remarks>
/// <para>
/// API7 допускается в продукте (ADR-004 §1, §4) — но только через сгенерированные вендором
/// интерфейсы. Позднее связывание (<c>Type.InvokeMember</c>, <c>dynamic</c> поверх RCW, прямой
/// вызов <c>IDispatch</c>) остаётся аварийным маршрутом исследовательской пробы
/// <c>tools/KompasMcp.Api7Probe</c> и в <c>src/</c> не переносится. Причина измерена, а не
/// соблюдается «для чистоты»: проба E (2026-09-12) дала, что запись <c>IExtrusion.Sketch</c>
/// через <c>IDispatch</c> в общем процессе роняла его кодом 0xC0000409, а в изолированном — нет;
/// при этом та же запись молча не сохраняла значение и перечитывалась прежним объектом.
/// Молчаливо непринятая запись, возвращающая S_OK, хуже отказа именно тем, что приёмка посчитала
/// бы её успехом.
/// </para>
/// <para>
/// Проверка статическая по исходникам: «позднего связывания нет» — это свойство кода, а не
/// наблюдаемое число, и подтвердить его прогоном нельзя (отказ проявится только на том вызове,
/// который до него дойдёт).
/// </para>
/// </remarks>
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

    /// <summary>Строки исполняемого кода: комментарий и XML-документацию считаем ссылкой, а не вызовом.</summary>
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
        // Позднее связывание под любым именем: InvokeMember (в том числе Type.InvokeMember),
        // local-объявление IDispatch с его GetIDsOfNames/Invoke и dynamic-получатель. Именно этот
        // набор образует аварийный маршрут в tools/KompasMcp.Api7Probe/Late.cs — копия любой из
        // этих форм в продукт переносит то, от чего граница и защищает.
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
        // dynamic разрешает вызов, который компилируется без проверки сигнатуры и уходит в IDispatch.
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
        // Инверсия того же требования: аварийный маршрут обязан остаться в probes-проекте. Если
        // он исчезнет оттуда, а в продукте его по-прежнему нет, — значит проверка ниже стала
        // вакуумом, и об этом надо узнать, а не радоваться зелёному тесту.
        var probe = Path.Combine(RepoRoot, "tools", "KompasMcp.Api7Probe", "Late.cs");
        Assert.True(File.Exists(probe),
            "позднее связывание больше нигде не разрешено, но и исследовательский файл пропал: " +
            "границу пора пересматривать явно, а не молча");
        var text = File.ReadAllText(probe);
        Assert.Contains("IDispatchNative", text, StringComparison.Ordinal);
        Assert.Contains("GetIDsOfNames", text, StringComparison.Ordinal);
    }
}
