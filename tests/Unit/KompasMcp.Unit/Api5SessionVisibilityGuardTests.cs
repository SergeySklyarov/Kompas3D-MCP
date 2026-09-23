using System.Text.RegularExpressions;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Гарантии видимости, которые нельзя проверить без КОМПАС, но можно проверить по коду.
/// </summary>
/// <remarks>
/// Требования к режиму «видно пользователю» (поручение от 12.09.2026, п. 7) гласят: обновлять вид
/// после моделирования и не сбрасывать пользовательскую камеру и масштаб после каждой операции.
/// Второе — запрет на конкретный вызов: <c>ZoomPrevNextOrAll</c> и <c>ksZoom*</c> в API5 меняют
/// именно камеру. Проверка статическая, потому что камерного состояния у 3D-документа в API5
/// спросить нельзя (геттера масштаба у <c>ksDocument3D</c> нет — только <c>ksGetZoomScale</c> у
/// 2D-редактора), а значит «камера не изменилась» невозможно подтвердить числом при приёмке.
/// Молчаливый запрет на код — единственный честный способ держать это требование.
///
/// Проверяется и обратное: что ответ о видимости не вычисляется из доступности PID. Прежняя
/// реализация давала ложноположительный <c>visible=true</c> именно так — <c>ProcessIdOf(...) is
/// not null</c> на скрытое окно, у которого HWND есть.
/// </remarks>
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

    /// <summary>
    /// Строки кода без комментариев: комментарий «было вот так» текстом совпадает с запрещённым
    /// выражением, но запрет относится к исполняемому коду.
    /// </summary>
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
                              // комментарий или XML-документацию считаем ссылкой, а не вызовом
                              && !row.line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ApplicationVisibility_IsNotInferredFromProcessIdHandle()
    {
        var code = string.Join("\n", CodeLines(
            Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", "Api5Session.cs")));

        // Та самая строка, из-за которой дефект прошёл приёмку: «PID достаётся» выдавалось за «видно».
        Assert.False(code.Contains("Visible = ProcessIdOf", StringComparison.Ordinal),
            "видимость снова выводится из доступности PID по HWND — скрытое окно даёт и HWND, и PID");

        // Новое определение обязано опираться на наблюдение окна, а не на косвенный признак.
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

        // Скрытый режим должен выходить до обращения к окну: иначе «проверка скрытого режима
        // отдельно» превращается в обещание, а не в поведение.
        var guard = visibility.IndexOf("if (!document.DocumentsVisible)", StringComparison.Ordinal);
        var refresh = visibility.IndexOf("ksRefreshActiveWindow()", guard, StringComparison.Ordinal);
        Assert.True(guard >= 0, "нет проверки режима в RefreshViewAfterMutation");
        Assert.True(refresh > guard, "перерисовка допускается до проверки видимого режима");
    }
}
