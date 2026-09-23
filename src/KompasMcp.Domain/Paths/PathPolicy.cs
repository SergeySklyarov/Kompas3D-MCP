using KompasMcp.Contracts;

namespace KompasMcp.Domain.Paths;

/// <summary>How a candidate path is classified against the configured roots.</summary>
public enum PathAccess
{
    /// <summary>Inside a read-only root: may be opened, never written.</summary>
    ReadOnly,

    /// <summary>Inside a writable root: may be created or replaced (subject to overwrite rules).</summary>
    Writable,

    /// <summary>Outside every allowed root, or unsafe for another reason. Always a refusal.</summary>
    Denied,
}

public sealed record PathDecision(
    PathAccess Access,
    string? CanonicalPath,
    string? DeniedReason,
    string? MatchedRoot);

/// <summary>
/// File-system boundary of the server (spec 1.12). Allowed roots come from configuration; the
/// decision is made on a resolved path, with separator-aware containment and an explicit refusal
/// of anything the checks cannot cover.
/// </summary>
/// <remarks>
/// Why not <c>StartsWith</c>: "D:\work" is a prefix of "D:\workspace\secret.a3d", so a naive
/// check admits a path outside the root. Why not only <c>Path.GetFullPath</c>: it collapses
/// <c>..</c> lexically but does not resolve junctions, so a directory inside the root that is
/// really a reparse point to elsewhere still passes. Both are handled here.
/// </remarks>
public sealed class PathPolicy
{
    private static readonly char[] DirectorySeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    public IReadOnlyList<string> ReadOnlyRoots { get; }

    public IReadOnlyList<string> WritableRoots { get; }

    public bool AllowUncPaths { get; }

    /// <param name="readOnlyRoots">Existing models the server may read but never write.</param>
    /// <param name="writableRoots">Scratch/export roots the server may create files in.</param>
    /// <param name="allowUncPaths">
    /// Default false. UNC is refused outright rather than half-supported: without a trustworthy
    /// way to resolve a remote share to a local canonical form, admitting it would quietly widen
    /// the sandbox (spec 1.12: "При отсутствии надёжной проверки UNC/reparse — отказ").
    /// </param>
    public PathPolicy(IEnumerable<string> readOnlyRoots, IEnumerable<string> writableRoots, bool allowUncPaths = false)
    {
        ReadOnlyRoots = NormalizeRoots(readOnlyRoots);
        WritableRoots = NormalizeRoots(writableRoots);
        AllowUncPaths = allowUncPaths;
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var list = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            list.Add(TrimTrailingSeparators(Path.GetFullPath(root)));
        }

        return list;
    }

    public PathDecision Evaluate(string candidate, bool intendToWrite)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Denied("Пустой путь.");
        }

        if (IsUnc(candidate))
        {
            return AllowUncPaths
                ? EvaluateLocalLike(candidate, intendToWrite)
                : Denied("UNC-пути отключены политикой (allow_unc_paths=false).");
        }

        if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return Denied("Путь содержит недопустимые символы.");
        }

        // The device-namespace and relative-with-drive-current forms ("\\?\C:\x", "C:file")
        // bypass plain containment reasoning, so canonicalise and then re-inspect.
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Denied($"Путь не канонизируется: {ex.Message}");
        }

        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return Denied("Формы путей Win32-namespace (\\\\?\\) не поддерживаются политикой.");
        }

        var invalidComponent = FirstInvalidNameComponent(full);
        if (invalidComponent is not null)
        {
            // ПОЧЕМУ ЭТА ПРОВЕРКА ВООБЩЕ НУЖНА — измерено, а не предположено (проба P4 наряда
            // KOMPAS_EXPORT_IMAGE). Ядро КОМПАС на такое имя НЕ отказывает: вызов вернул успех,
            // базовый файл `bad` остался нулевым, а 8639 байт полезной нагрузки ушли в
            // АЛЬТЕРНАТИВНЫЙ ПОТОК NTFS. Перечисление потоков каталога:
            //   FILE=bad LEN=0 STREAMS=:$DATA=0|name?.png=8639
            // Иначе говоря, «успех» без файла, который пользователь сможет найти. Отсекаем до COM.
            // `Path.GetInvalidPathChars()` здесь не годится: его набор уже таблицы ИМЁН и этот путь
            // пропускает (проверено модульным тестом, который на прежней версии был красным).
            return Denied(
                $"Компонента пути '{invalidComponent}' содержит символы, недопустимые в имени файла "
                + "(в том числе ':' — двоеточие после буквы диска открывает альтернативный поток NTFS, "
                + "и файл, который вернёт ядро, будет пустым).");
        }

        full = TrimTrailingSeparators(full);
        return EvaluateLocalLike(full, intendToWrite);
    }

    /// <summary>
    /// First path component containing a character Windows forbids in a file NAME, or null.
    /// </summary>
    /// <remarks>
    /// The separator itself is part of the forbidden table, so the path is split into components
    /// first and each component is checked on its own. The drive/root part is skipped: its colon
    /// is a volume designator, not a stream separator, and rejecting it would refuse every
    /// ordinary absolute path.
    /// </remarks>
    private static string? FirstInvalidNameComponent(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (root.Length >= fullPath.Length)
        {
            return null;
        }

        var forbidden = Path.GetInvalidFileNameChars();
        foreach (var component in fullPath[root.Length..].Split(DirectorySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (component.IndexOfAny(forbidden) >= 0)
            {
                return component;
            }
        }

        return null;
    }

    private PathDecision EvaluateLocalLike(string full, bool intendToWrite)
    {
        // The most specific matching root decides, not the first one. A read-only model root that
        // happens to live inside a writable scratch root must keep denying writes; matching the
        // enclosing writable root first silently granted write access to the protected tree.
        var all = ReadOnlyRoots.Select(root => (Root: root, Writable: false))
            .Concat(WritableRoots.Select(root => (Root: root, Writable: true)))
            .ToList();

        var exact = all.FirstOrDefault(r => PathsEqual(full, r.Root));
        if (exact.Root is not null)
        {
            // A root directory itself is never a document target.
            return Denied("Нельзя обратиться к самому корню как к файлу — укажите путь внутри него.");
        }

        var matches = all
            .Where(r => IsWithin(full, r.Root))
            .OrderByDescending(r => r.Root.Length)
            .ToList();

        if (matches.Count == 0)
        {
            return Denied(intendToWrite
                ? "Путь вне разрешённых для записи корней."
                : "Путь вне разрешённых корней.");
        }

        var winner = matches[0];

        if (winner.Writable)
        {
            var reparse = FirstReparseOnPath(full, winner.Root);
            if (reparse is not null)
            {
                return Denied($"Компонента пути '{reparse}' является reparse-точкой (junction/symlink): за её пределы выйти нельзя, поэтому доступ не выдаётся.");
            }

            return new PathDecision(PathAccess.Writable, full, null, winner.Root);
        }

        if (intendToWrite)
        {
            return Denied($"Путь '{full}' лежит в только-для-чтения корне '{winner.Root}'; запись запрещена.");
        }

        return new PathDecision(PathAccess.ReadOnly, full, null, winner.Root);
    }

    private bool IsWithinWritable(string full) =>
        WritableRoots.Any(root => IsWithin(full, root) || PathsEqual(full, root));

    private static PathDecision Denied(string reason) =>
        new(PathAccess.Denied, null, reason, null);

    /// <summary>
    /// First path component at or below <paramref name="root"/> that is a reparse point, or null.
    /// Walking from the deepest existing component upwards stops at the root so the check is
    /// bounded, and non-existing trailing components (a file about to be created) are skipped —
    /// a parent directory is what matters.
    /// </summary>
    private static string? FirstReparseOnPath(string fullPath, string root)
    {
        var chain = new List<string>();
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            chain.Add(current);
            if (PathsEqual(current, root))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length >= current.Length)
            {
                break;
            }

            current = parent;
        }

        foreach (var candidate in chain)
        {
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            var attributes = File.GetAttributes(candidate);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Separator-aware containment: "D:\work" must not admit "D:\workspace". A root that already
    /// ends with a separator (a drive root such as "D:\") is handled too — there the separator is
    /// part of the root, so the next character is a name, not a delimiter.
    /// </summary>
    public static bool IsWithin(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(DirectorySeparators);
        if (normalizedRoot.Length == 0)
        {
            return false;
        }

        if (candidate.Length <= normalizedRoot.Length)
        {
            return false;
        }

        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSeparator(candidate[normalizedRoot.Length]);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(TrimTrailingSeparators(a), TrimTrailingSeparators(b), StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingSeparators(string path)
    {
        if (path.Length > 3 && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar))
        {
            return path.TrimEnd(DirectorySeparators);
        }

        // Keep "D:\" intact — trimming to "D:" changes its meaning to "current dir on D".
        return path.Length == 3 && path[1] == ':' && IsSeparator(path[2]) ? path : path.TrimEnd(DirectorySeparators);
    }

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    public static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);
}
