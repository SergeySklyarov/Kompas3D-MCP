using System.Text.Json.Nodes;
using KompasMcp.Domain.Files;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The copy plan of an assembly's dependency graph: deduplication, bottom-up order, refusals, manifest
/// and the comparison with what the copy reports.</summary>
/// <remarks>INVARIANT: one file inserted many times is ONE entry; a refusal is decided before any folder
/// exists. History: docs/decisions/scenario.md#assembly-checkpoint</remarks>
public class AssemblyCopyPlanTests : IDisposable
{
    private readonly string _directory;

    public AssemblyCopyPlanTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "asm-plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string File(string name, string content = "m3d")
    {
        var folder = Path.Combine(_directory, Path.GetDirectoryName(name) ?? ".");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(_directory, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void SameFileInsertedTwice_IsOneEntryWithBothInsertions()
    {
        var part = File("part-a.m3d");
        var links = new[]
        {
            new AssemblyComponentLink(part, null, IsAssembly: false, Insertions: 1),
            new AssemblyComponentLink(part, null, IsAssembly: false, Insertions: 1),
        };

        var plan = AssemblyCopyPlan.Build(links, Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);

        Assert.True(plan.Ok);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal(2, entry.Insertions);
        Assert.Equal(AssemblyCopyPlan.ModeCopy, entry.Mode);
        Assert.NotNull(entry.CopyPath);
    }

    [Fact]
    public void SameNameFromDifferentFolders_GetsDifferentCopyNames()
    {
        var first = File("a/bracket.m3d", "one");
        var second = File("b/bracket.m3d", "two");
        var links = new[]
        {
            new AssemblyComponentLink(first, null, false, 1),
            new AssemblyComponentLink(second, null, false, 1),
        };

        var plan = AssemblyCopyPlan.Build(links, Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);

        Assert.True(plan.Ok);
        Assert.Equal(2, plan.Entries.Count);
        Assert.NotEqual(plan.Entries[0].CopyPath, plan.Entries[1].CopyPath);
        Assert.Equal("bracket.m3d", Path.GetFileName(plan.Entries[0].CopyPath!).Substring(4));
    }

    [Fact]
    public void ReadOnlyMode_CopiesNothingAndSaysSo()
    {
        var part = File("part-a.m3d");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) },
            Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeReadOnly);

        Assert.True(plan.Ok);
        var entry = Assert.Single(plan.Entries);
        Assert.Null(entry.CopyPath);
        Assert.Equal(AssemblyCopyPlan.ModeReadOnly, entry.Mode);
    }

    [Fact]
    public void MissingComponentFile_IsRefusedAndNamed()
    {
        var missing = Path.Combine(_directory, "absent.m3d");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(missing, null, false, 1) },
            Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);

        Assert.False(plan.Ok);
        Assert.Equal("component_file_missing", plan.Refusal!.Reason);
        Assert.Contains(missing, plan.Refusal.Files);
        Assert.Empty(plan.Entries);
        Assert.False(Directory.Exists(Path.Combine(_directory, "copy")));
    }

    [Fact]
    public void UnnamedComponent_IsRefused()
    {
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(string.Empty, null, false, 1) },
            Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);

        Assert.False(plan.Ok);
        Assert.Equal("component_file_unnamed", plan.Refusal!.Reason);
    }

    [Fact]
    public void GraphOverTheLimit_IsRefusedByName()
    {
        var links = Enumerable.Range(0, AssemblyCopyPlan.MaxFiles + 1)
            .Select(index => new AssemblyComponentLink(File($"p{index}.m3d"), null, false, 1))
            .ToArray();

        var plan = AssemblyCopyPlan.Build(links, Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);

        Assert.False(plan.Ok);
        Assert.Equal("graph_too_large", plan.Refusal!.Reason);
    }

    [Fact]
    public void BottomUp_PutsTheNestedAssemblyBeforeItsParent()
    {
        var detail = File("detail.m3d");
        var subassembly = File("sub.m3d");
        var top = File("top.m3d");
        var links = new[]
        {
            new AssemblyComponentLink(top, null, IsAssembly: true, 1, Depth: 0),
            new AssemblyComponentLink(subassembly, top, IsAssembly: true, 1, Depth: 1),
            new AssemblyComponentLink(detail, subassembly, IsAssembly: false, 2, Depth: 2),
        };

        var plan = AssemblyCopyPlan.Build(links, Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var order = AssemblyCopyPlan.BottomUp(plan.Entries);
        Assert.Equal(2, order.Count);
        Assert.Equal(subassembly, order[0].SourcePath);
        Assert.Equal(top, order[1].SourcePath);
    }

    [Fact]
    public void Manifest_NamesEveryFileWithItsModeAndHash()
    {
        var part = File("part-a.m3d", "content");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 3) },
            Path.Combine(_directory, "copy"), AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var manifest = AssemblyCopyPlan.Manifest(
            File("top.m3d"), AssemblyCopyPlan.ModeCopy, DateTimeOffset.UtcNow, plan.Entries,
            verified: true, Array.Empty<string>());

        Assert.Equal(AssemblyCopyPlan.ManifestFormat, manifest["format"]!.GetValue<string>());
        Assert.Equal(1, manifest["file_count"]!.GetValue<int>());
        Assert.True(manifest["verified"]!.GetValue<bool>());
        var row = ((JsonArray)manifest["files"]!)[0]!;
        Assert.Equal(part, row["source_path"]!.GetValue<string>());
        Assert.Equal(plan.Entries[0].CopyPath, row["copy_path"]!.GetValue<string>());
        Assert.Equal(3, row["insertions"]!.GetValue<int>());
        Assert.Equal(64, row["sha256"]!.GetValue<string>().Length);
    }

    [Fact]
    public void Verify_AcceptsLinksInsideTheCopyFolder()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var readBack = new[] { new AssemblyComponentLink(plan.Entries[0].CopyPath!, null, false, 1) };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 1);

        Assert.True(passed);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Verify_RejectsALinkThatStayedOnTheSourceFile()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        // The copy still points at the client's file: that is exactly the defect ASC.03 looks for.
        var readBack = new[] { new AssemblyComponentLink(part, null, false, 1) };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 1);

        Assert.False(passed);
        Assert.Contains(mismatches, text => text.Contains("вне каталога копии"));
    }

    [Fact]
    public void Verify_RejectsADifferentComponentCount()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var readBack = new[] { new AssemblyComponentLink(plan.Entries[0].CopyPath!, null, false, 1) };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 2);

        Assert.False(passed);
        Assert.Contains(mismatches, text => text.Contains("число компонентов"));
    }

    [Fact]
    public void Verify_AcceptsAReadOnlyLinkThatStillPointsAtTheSource()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeReadOnly);
        Assert.True(plan.Ok);

        var readBack = new[] { new AssemblyComponentLink(part, null, false, 1) };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 1);

        Assert.True(passed);
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Verify_RejectsALinkThePlanNeverNamed()
    {
        var part = File("part-a.m3d");
        var stranger = File("stranger.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        // The plan's own file is where it promised, and the COUNT matches — only the other direction can
        // see that the copy kept a link to a file this checkpoint never copied.
        var readBack = new[]
        {
            new AssemblyComponentLink(plan.Entries[0].CopyPath!, null, false, 1),
            new AssemblyComponentLink(stranger, null, false, 1),
        };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 2);

        Assert.False(passed);
        Assert.Contains(mismatches, text => text.Contains("не входит в read_only"));
    }

    [Fact]
    public void Verify_RejectsADifferentInsertionCount()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 2) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);
        Assert.Equal(2, plan.Entries[0].Insertions);

        var readBack = new[] { new AssemblyComponentLink(plan.Entries[0].CopyPath!, null, false, 1) };
        var (passed, mismatches) = AssemblyCopyPlan.Verify(plan.Entries, folder, readBack, expectedComponents: 1);

        Assert.False(passed);
        Assert.Contains(mismatches, text => text.Contains("вставлен 1 раз"));
    }

    [Fact]
    public void Space_RefusesAGraphLargerThanTheVolumeCanHold()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var huge = new[]
        {
            plan.Entries[0] with { SizeBytes = 1_000_000_000_000_000_000L },
        };
        var (fits, needed, available) = AssemblyCopyPlan.Space(huge, folder);

        Assert.False(fits);
        Assert.True(needed > available);
        Assert.Equal(1_000_000_000_000_000_000L + AssemblyCopyPlan.DiskReserveBytes, needed);
    }

    [Fact]
    public void Space_AcceptsAGraphThatFits()
    {
        var part = File("part-a.m3d");
        var folder = Path.Combine(_directory, "copy");
        var plan = AssemblyCopyPlan.Build(
            new[] { new AssemblyComponentLink(part, null, false, 1) }, folder, AssemblyCopyPlan.ModeCopy);
        Assert.True(plan.Ok);

        var (fits, needed, _) = AssemblyCopyPlan.Space(plan.Entries, folder);

        Assert.True(fits);
        Assert.Equal(AssemblyCopyPlan.DiskReserveBytes + plan.Entries[0].SizeBytes, needed);
    }
}
