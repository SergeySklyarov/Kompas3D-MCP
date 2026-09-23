using KompasMcp.Domain.Paths;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// File-boundary rules from spec 1.12. Every case here exists because the naive version of the
/// check looks correct and is not.
/// </summary>
public class PathPolicyTests : IDisposable
{
    private readonly string _root;
    private readonly string _other;

    public PathPolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "roots-" + Guid.NewGuid().ToString("N")[..8]);
        _other = Path.Combine(_root, "sibling");
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_other, "secrets"));
    }

    private PathPolicy Policy(bool allowUnc = false) => new(
        readOnlyRoots: new[] { Path.Combine(_root, "models") },
        writableRoots: new[] { _root },
        allowUncPaths: allowUnc);

    [Fact]
    public void SiblingWithRootAsTextPrefix_IsNotInsideTheRoot()
    {
        // "D:\work" is a text prefix of "D:\workspace\evil.a3d": a StartsWith check without a
        // separator admits it, which is the exact bug this test pins.
        var outside = Path.Combine(_root + "extension", "evil.a3d");
        var decision = Policy().Evaluate(outside, intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
        Assert.NotNull(decision.DeniedReason);
    }

    [Fact]
    public void DotDotEscape_IsDeniedAfterCanonicalisation()
    {
        var sneaky = Path.Combine(_root, "models", "..", "..", "escape.a3d");
        var decision = Policy().Evaluate(sneaky, intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
    }

    [Fact]
    public void InsideWritableRoot_AllowsWrite()
    {
        var target = Path.Combine(_root, "out", "part.m3d");
        var decision = Policy().Evaluate(target, intendToWrite: true);
        Assert.Equal(PathAccess.Writable, decision.Access);
        Assert.Equal(Path.GetFullPath(target), decision.CanonicalPath);
    }

    [Fact]
    public void ReadOnlyRoot_AllowsRead_ButNotWrite()
    {
        var model = Path.Combine(_root, "models", "housing.m3d");

        Assert.Equal(PathAccess.ReadOnly, Policy().Evaluate(model, intendToWrite: false).Access);

        // The access decision is the contract; the reason string is Russian prose for a human and
        // deliberately not asserted on.
        var write = Policy().Evaluate(model, intendToWrite: true);
        Assert.Equal(PathAccess.Denied, write.Access);
        Assert.NotNull(write.DeniedReason);
    }

    [Fact]
    public void UncPath_IsRefusedByDefault()
    {
        var decision = Policy().Evaluate(@"\\fileserver\share\part.m3d", intendToWrite: false);
        Assert.Equal(PathAccess.Denied, decision.Access);
        Assert.Contains("UNC", decision.DeniedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceNamespacePath_IsRefused()
    {
        // "\\?\C:\x" bypasses plain containment reasoning, so it must not be reasoned about at all.
        var decision = Policy().Evaluate(@"\\?\C:\Windows\Temp\x.m3d", intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
    }

    [Fact]
    public void EmptyAndInvalidPaths_AreRefusedWithoutThrowing()
    {
        Assert.Equal(PathAccess.Denied, Policy().Evaluate("", intendToWrite: true).Access);
        Assert.Equal(PathAccess.Denied, Policy().Evaluate("   ", intendToWrite: true).Access);
        Assert.Equal(PathAccess.Denied, Policy().Evaluate("bad\0path", intendToWrite: true).Access);
    }

    [Fact]
    public void TheRootDirectoryItselfIsNotAFileTarget()
    {
        var decision = Policy().Evaluate(_root, intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
    }

    [Theory]
    [InlineData("bad:name?.png")]
    [InlineData("stream.png:payload")]
    [InlineData("wild*.png")]
    [InlineData("quote\".png")]
    [InlineData("pipe|.png")]
    [InlineData("less<.png")]
    public void FileNameCharactersInvalidOnWindows_AreRefusedInsideTheWritableRoot(string leaf)
    {
        // Измерено пробой P4 наряда KOMPAS_EXPORT_IMAGE, а не выведено из общих соображений:
        // ядро КОМПАС на такое имя НЕ отказывает. Оно вернуло успех, базовый файл `bad` остался
        // нулевым, а полезная нагрузка (8639 байт) ушла в АЛЬТЕРНАТИВНЫЙ ПОТОК NTFS:
        //   FILE=bad LEN=0 STREAMS=:$DATA=0|name?.png=8639
        // `Path.GetInvalidPathChars()` этот путь пропускает — набор там уже таблицы имён, — поэтому
        // проверка ведётся по компонентам и по таблице ИМЁН.
        var decision = Policy().Evaluate(Path.Combine(_root, "out", leaf), intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
        Assert.NotNull(decision.DeniedReason);
    }

    [Fact]
    public void AlternateDataStreamSyntax_IsRefusedByNameNotByAccident()
    {
        // Отдельно от Theory: здесь важно, что отказ пришёл ИМЕНОВАННО от проверки компонент, а не
        // от исключения канонизации. Иначе тест был бы зелёным по случайной причине.
        var decision = Policy().Evaluate(Path.Combine(_root, "out", "bad:name?.png"), intendToWrite: true);
        Assert.Equal(PathAccess.Denied, decision.Access);
        Assert.Contains("недопустимые в имени файла", decision.DeniedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void DriveLetterColon_IsNotMistakenForAStreamSeparator()
    {
        // Запрет ':' обязан не ломать обычный путь: двоеточие диска — часть КОРНЯ, а не имени.
        var target = Path.Combine(_root, "out", "part.png");
        var decision = Policy().Evaluate(target, intendToWrite: true);
        Assert.Equal(PathAccess.Writable, decision.Access);
        Assert.Equal(Path.GetFullPath(target), decision.CanonicalPath);
    }

    [Fact]
    public void CaseOnlyDifference_IsStillTheSameFileOnWindows()
    {
        var upper = Path.Combine(_root, "MODELS", "x.m3d");
        var decision = Policy().Evaluate(upper, intendToWrite: false);
        Assert.Equal(PathAccess.ReadOnly, decision.Access);
    }

    [Fact]
    public void ContainmentIsSeparatorAware_NotTextPrefix()
    {
        Assert.True(PathPolicy.IsWithin(@"D:\work\a\b.m3d", @"D:\work"));
        Assert.False(PathPolicy.IsWithin(@"D:\workspace\a\b.m3d", @"D:\work"));
        Assert.False(PathPolicy.IsWithin(@"D:\work", @"D:\work"));
    }

    [Fact]
    public void DriveRootKeepsItsTrailingSeparatorMeaning()
    {
        // Trimming "D:\" to "D:" changes it to "current directory on D", which would then be
        // compared against paths on an entirely different root.
        var decision = new PathPolicy(Array.Empty<string>(), new[] { "D:\\" })
            .Evaluate(@"D:\work\whatever.m3d", intendToWrite: true);
        Assert.Equal(PathAccess.Writable, decision.Access);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp leftovers are cleaned by the OS.
        }
    }
}
