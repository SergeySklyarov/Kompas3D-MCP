using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace KompasMcp.Domain.Files;

/// <summary>One component instance read from an assembly, reduced to what a copy of the graph needs.</summary>
/// <param name="SourcePath">Absolute path of the component's source file, as the assembly reports it.</param>
/// <param name="ParentPath">Source path of the assembly that inserts it; <c>null</c> for a top-level component.</param>
/// <param name="IsAssembly">The component is itself an assembly, so its own links have to be reassigned.</param>
/// <param name="Insertions">How many times this file is inserted under this parent.</param>
/// <param name="Depth">Nesting level below the top assembly, as the structure read reports it.</param>
public sealed record AssemblyComponentLink(
    string SourcePath, string? ParentPath, bool IsAssembly, int Insertions, int Depth = 0);

/// <summary>One file of the copied graph: where it came from, where the copy is, and how it is referenced.</summary>
public sealed record AssemblyCopyEntry(
    string SourcePath,
    string? CopyPath,
    string Mode,
    long SizeBytes,
    string Sha256,
    int Insertions,
    bool IsAssembly,
    int Depth);

/// <summary>A refusal decided BEFORE the first step: the reason code and the components it names.</summary>
/// <remarks>INVARIANT: a refused checkpoint writes nothing — the caller checks the plan first and only then
/// copies, so "refused" never leaves a half-written folder behind.
/// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
public sealed record AssemblyCopyRefusal(string Reason, string Message, IReadOnlyList<string> Files);

/// <summary>Outcome of building a copy plan: the entries, or the NAMED refusal and the components it names.</summary>
public sealed record AssemblyCopyPlanResult(
    IReadOnlyList<AssemblyCopyEntry> Entries, AssemblyCopyRefusal? Refusal)
{
    public bool Ok => Refusal is null;
}

/// <summary>Builds the copy plan of an assembly's dependency graph and writes its manifest.</summary>
/// <remarks>INVARIANT: one file, inserted many times, is copied once — the graph is keyed by the SOURCE PATH,
/// not by the component instance. INVARIANT: two files with the same name from different folders get
/// different copy names, because the copy name carries an ordinal. LIMIT: the copy name is derived from the
/// ordinal and the file name only, so the copied folder does not mirror the source tree.
/// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
public static class AssemblyCopyPlan
{
    /// <summary>Manifest format version, written into the copy folder.</summary>
    /// <remarks>LIMIT: version 1 only; a reader of another version must refuse rather than guess.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public const string ManifestFormat = "assembly-copy-v1";

    /// <summary>Upper bound on the number of files one checkpoint copies.</summary>
    /// <remarks>LIMIT: 500 files; a bigger graph is refused with <c>graph_too_large</c> instead of running for
    /// an unbounded time inside a tool call. History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public const int MaxFiles = 500;

    /// <summary>Reference modes a component can have in the copy.</summary>
    public const string ModeCopy = "copy";

    public const string ModeReadOnly = "read_only";

    /// <summary>Free space left on the volume after a copy, below which the checkpoint is refused.</summary>
    /// <remarks>INVARIANT: a copy that fills the volume would fail halfway and leave the client's own
    /// document without the space it needs. 64 MiB is enough for the client's next save of a graph at the
    /// 500-file limit. History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public const long DiskReserveBytes = 64L * 1024 * 1024;

    /// <summary>The volume a copy folder lives on, and whether it can hold <paramref name="entries"/>.</summary>
    /// <remarks>INVARIANT: the sum of the graph's file sizes plus the reserve must fit the volume BEFORE the
    /// first write, so "not enough space" is a refusal and not a half-written folder.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public static (bool Fits, long NeededBytes, long AvailableBytes) Space(
        IReadOnlyList<AssemblyCopyEntry> entries, string copyFolder)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyFolder);

        var needed = entries.Where(e => e.CopyPath is not null).Sum(e => e.SizeBytes) + DiskReserveBytes;
        var root = Path.GetPathRoot(Path.GetFullPath(copyFolder));
        if (string.IsNullOrEmpty(root))
        {
            return (false, needed, 0);
        }

        try
        {
            var available = new DriveInfo(root).AvailableFreeSpace;
            return (available >= needed, needed, available);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // An unreadable volume is not a proven shortage: the copy attempt reports its own failure.
            return (true, needed, 0);
        }
    }

    /// <summary>Plan the copies of <paramref name="links"/> below <paramref name="copyFolder"/>.</summary>
    /// <remarks>INVARIANT: the plan is decided before anything is written, so every refusal (missing file,
    /// too large graph) is reported with the full list of names and no folder is created.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public static AssemblyCopyPlanResult Build(
        IReadOnlyList<AssemblyComponentLink> links, string copyFolder, string externalParts)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyFolder);

        // Keyed by source path: the same file inserted five times is ONE file of the graph.
        var bySource = new Dictionary<string, (bool IsAssembly, int Insertions, int Depth)>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.SourcePath))
            {
                return new AssemblyCopyPlanResult(Array.Empty<AssemblyCopyEntry>(), new AssemblyCopyRefusal(
                    "component_file_unnamed",
                    "У компонента не прочитан путь файла-источника: копировать нечего, а угадывать путь нельзя.",
                    Array.Empty<string>()));
            }

            var depth = link.Depth;
            if (bySource.TryGetValue(link.SourcePath, out var known))
            {
                bySource[link.SourcePath] = (known.IsAssembly || link.IsAssembly,
                    known.Insertions + Math.Max(1, link.Insertions), Math.Min(known.Depth, depth));
            }
            else
            {
                bySource[link.SourcePath] = (link.IsAssembly, Math.Max(1, link.Insertions), depth);
            }
        }

        var missing = bySource.Keys.Where(path => !File.Exists(path)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length > 0)
        {
            return new AssemblyCopyPlanResult(Array.Empty<AssemblyCopyEntry>(), new AssemblyCopyRefusal(
                "component_file_missing",
                $"Файлы компонентов не найдены на диске ({missing.Length}): "
                + string.Join(", ", missing.Take(5))
                + ". Контрольная точка не снимается, потому что копия графа без них не была бы "
                + "копией сборки.",
                missing));
        }

        if (bySource.Count > MaxFiles)
        {
            return new AssemblyCopyPlanResult(Array.Empty<AssemblyCopyEntry>(), new AssemblyCopyRefusal(
                "graph_too_large",
                $"В графе {bySource.Count} файлов при пределе {MaxFiles}: контрольная точка не снимается, "
                + "чтобы вызов не шёл неограниченное время.",
                Array.Empty<string>()));
        }

        var entries = new List<AssemblyCopyEntry>(bySource.Count);
        var ordinal = 0;
        foreach (var pair in bySource.OrderBy(p => p.Value.Depth).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ordinal++;
            var info = new FileInfo(pair.Key);
            var copy = externalParts == ModeReadOnly
                ? null
                : Path.Combine(copyFolder, "files", $"{ordinal:D3}-{info.Name}");
            entries.Add(new AssemblyCopyEntry(
                pair.Key,
                copy,
                copy is null ? ModeReadOnly : ModeCopy,
                info.Length,
                Sha256Of(pair.Key),
                pair.Value.Insertions,
                pair.Value.IsAssembly,
                pair.Value.Depth));
        }

        return new AssemblyCopyPlanResult(entries, null);
    }

    /// <summary>The copies of assemblies, deepest first: a subassembly is reassigned before the assembly
    /// that inserts it, so the parent never points at a file that still points at the originals.</summary>
    public static IReadOnlyList<AssemblyCopyEntry> BottomUp(IReadOnlyList<AssemblyCopyEntry> entries) =>
        entries.Where(e => e.IsAssembly && e.CopyPath is not null)
            .OrderByDescending(e => e.Depth)
            .ThenBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>The manifest of the copy folder: one row per file of the graph plus the top-level facts.</summary>
    /// <remarks>INVARIANT: a <c>read_only</c> row has <c>copy_path: null</c>, so a reader cannot mistake the
    /// original file for a protected copy. History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public static JsonObject Manifest(
        string sourceAssembly, string externalParts, DateTimeOffset capturedAt,
        IReadOnlyList<AssemblyCopyEntry> entries, bool verified, IReadOnlyList<string> verificationNotes)
    {
        var files = new JsonArray();
        foreach (var entry in entries)
        {
            files.Add(new JsonObject
            {
                ["source_path"] = entry.SourcePath,
                ["copy_path"] = entry.CopyPath,
                ["mode"] = entry.Mode,
                ["size_bytes"] = entry.SizeBytes,
                ["sha256"] = entry.Sha256,
                ["insertions"] = entry.Insertions,
                ["is_assembly"] = entry.IsAssembly,
                ["depth"] = entry.Depth,
            });
        }

        return new JsonObject
        {
            ["format"] = ManifestFormat,
            ["source_assembly"] = sourceAssembly,
            ["external_parts"] = externalParts,
            ["captured_at"] = capturedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["verified"] = verified,
            ["verification_notes"] = new JsonArray(verificationNotes.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
            ["file_count"] = entries.Count,
            ["files"] = files,
        };
    }

    /// <summary>Compare the links read back from the copy with the plan.</summary>
    /// <remarks>INVARIANT: a link is checked against the COPY FOLDER, not against the plan's string: a copy
    /// that points outside it is a broken checkpoint even when the plan said "copy". INVARIANT: the check
    /// runs BOTH ways — a link the plan never named is a mismatch too, else a copy that kept a link to the
    /// client's file passes whenever the component count happens to match.
    /// History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
    public static (bool Passed, IReadOnlyList<string> Mismatches) Verify(
        IReadOnlyList<AssemblyCopyEntry> entries, string copyFolder,
        IReadOnlyList<AssemblyComponentLink> readBack, int expectedComponents)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(readBack);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyFolder);

        var mismatches = new List<string>();
        var inside = Path.GetFullPath(copyFolder) + Path.DirectorySeparatorChar;
        var readOnlySources = entries
            .Where(entry => entry.Mode == ModeReadOnly)
            .Select(entry => Path.GetFullPath(entry.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var observed = LinksOf(entry, readBack);
            if (observed.Length == 0)
            {
                mismatches.Add($"файл «{Path.GetFileName(entry.SourcePath)}» не найден среди ссылок копии");
                continue;
            }

            foreach (var link in observed)
            {
                var path = link.SourcePath;
                if (entry.Mode == ModeCopy)
                {
                    if (!Path.GetFullPath(path).StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add($"ссылка «{path}» ведёт вне каталога копии");
                    }
                }
                else if (!string.Equals(path, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"read_only-ссылка «{path}» не совпала с исходным файлом");
                }
            }

            var insertions = observed.Sum(link => Math.Max(1, link.Insertions));
            if (insertions != entry.Insertions)
            {
                mismatches.Add($"файл «{Path.GetFileName(entry.SourcePath)}» вставлен {insertions} раз, "
                    + $"в исходной сборке {entry.Insertions}");
            }
        }

        // The other direction: nothing in the copy may point at a file this plan does not know.
        foreach (var link in readBack)
        {
            var full = Path.GetFullPath(link.SourcePath);
            if (full.StartsWith(inside, StringComparison.OrdinalIgnoreCase)
                || readOnlySources.Contains(full))
            {
                continue;
            }

            mismatches.Add($"ссылка «{link.SourcePath}» ведёт вне каталога копии и не входит в read_only "
                + "этого плана");
        }

        var total = readBack.Count;
        if (total != expectedComponents)
        {
            mismatches.Add($"число компонентов в копии {total}, в исходной сборке {expectedComponents}");
        }

        // A refusal names what was actually read back: without it the caller sees "did not match" and has
        // no way to tell a dropped instance from a link that stayed on the client's file.
        if (mismatches.Count > 0)
        {
            mismatches.Add("прочитано: " + string.Join(", ", readBack.Take(6).Select(link =>
                Path.GetFileName(link.SourcePath) + "×" + Math.Max(1, link.Insertions))));
        }

        return (mismatches.Count == 0, mismatches);
    }

    /// <summary>The links a read-back graph reports for one planned file: its source, or its copy.</summary>
    private static AssemblyComponentLink[] LinksOf(
        AssemblyCopyEntry entry, IReadOnlyList<AssemblyComponentLink> readBack) => readBack
        .Where(link => string.Equals(link.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase)
            || (entry.CopyPath is not null
                && string.Equals(link.SourcePath, entry.CopyPath, StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
