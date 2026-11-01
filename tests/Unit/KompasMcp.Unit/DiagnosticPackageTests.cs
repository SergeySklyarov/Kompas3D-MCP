using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Host;
using KompasMcp.Host.Diagnostics;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The diagnostic package (spec 1.4 item 10, 1.12) without KOMPAS: the allow list, the path labels,
/// the limits and the refusals.</summary>
/// <remarks>TEST: every case below is one promise the package makes — an unknown field never travels, a
/// path never travels, the salt is per package, a torn tail is counted and not fatal, and a destination
/// outside the allowed roots or already taken is refused BEFORE the file exists.
/// History: docs/decisions/diagnostics.md#package</remarks>
public class DiagnosticPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kompas-mcp-tests", "diag-" + Guid.NewGuid().ToString("N")[..8]);

    public DiagnosticPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is noise, not a failed assertion.
        }
    }

    private HostOptions Options(params string[] writable) => new() { WritableRoots = writable };

    private string WriteJournal(params string[] lines)
    {
        var path = Path.Combine(_root, "operations.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static string JournalLine(string operationId = "op-1") => new JsonObject
    {
        ["operation_id"] = operationId,
        ["tool"] = "kompas_extrude",
        ["arguments_hash"] = "deadbeef",
        ["document_id"] = "0123456789abcdef0123456789abcdef",
        ["base_revision"] = 4,
        ["started_utc"] = "2026-10-31T05:00:00.0000000+00:00",
        ["outcome"] = "succeeded",
        ["result_json"] = new JsonObject { ["revision"] = 5, ["verification"] = new JsonObject { ["level"] = "structure_checked", ["checks"] = new JsonArray() } }.ToJsonString(),
        ["error"] = null,
        ["finished_utc"] = "2026-10-31T05:00:02.0000000+00:00",
        ["needs_reconciliation"] = false,
    }.ToJsonString();

    [Fact]
    public void UnknownFields_AreDroppedAndCounted()
    {
        var notes = new List<string>();
        var path = WriteJournal(JournalLine());

        var (records, read, dropped, fields, truncated) =
            DiagnosticPackage.ProjectJournal(path, new byte[16], notes);

        Assert.Equal(1, read);
        Assert.Equal(0, dropped);
        Assert.False(truncated);
        var record = Assert.Single(records);
        // arguments_hash and needs_reconciliation are NOT in the allow list; the error block and the
        // free-text parts of the result are dropped field by field.
        Assert.False(record.ContainsKey("arguments_hash"));
        Assert.False(record.ContainsKey("needs_reconciliation"));
        Assert.False(record.ContainsKey("error"));
        Assert.True(fields >= 2);
        Assert.Equal(5, record["revision_after"]!.GetValue<long>());
        Assert.Equal("structure_checked", record["verification_status"]!.GetValue<string>());
        Assert.Equal(2000L, record["elapsed_ms"]!.GetValue<long>());
    }

    [Fact]
    public void Hresult_OfAnError_IsKept_AndItsMessageIsNot()
    {
        var notes = new List<string>();
        var line = new JsonObject
        {
            ["operation_id"] = "op-2",
            ["tool"] = "kompas_extrude",
            ["outcome"] = "failed",
            ["started_utc"] = "2026-10-31T05:00:00.0000000+00:00",
            ["error"] = new JsonObject { ["code"] = "GEOMETRY_FAILED", ["message"] = @"D:\work\ivanov\model.m3d", ["hresult"] = -2147221504 },
        }.ToJsonString();
        var path = WriteJournal(line);

        var (records, _, _, fields, _) = DiagnosticPackage.ProjectJournal(path, new byte[16], notes);

        var record = Assert.Single(records);
        Assert.Equal(-2147221504, record["hresult"]!.GetValue<int>());
        Assert.DoesNotContain("ivanov", record.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(fields >= 2);
    }

    [Fact]
    public void TornLastLine_IsDroppedAndCounted_TheBuildSurvives()
    {
        var notes = new List<string>();
        var path = Path.Combine(_root, "operations.jsonl");
        File.WriteAllText(path, JournalLine() + "\n{\"operation_id\":\"op-2\",\"to");

        var (records, read, dropped, _, _) = DiagnosticPackage.ProjectJournal(path, new byte[16], notes);

        Assert.Equal(2, read);
        Assert.Equal(1, dropped);
        Assert.Single(records);
    }

    [Fact]
    public void MissingFile_IsNamed_AndNotAnError()
    {
        var notes = new List<string>();
        var (records, read, dropped, _, _) = DiagnosticPackage.ProjectJournal(
            Path.Combine(_root, "absent.jsonl"), new byte[16], notes);

        Assert.Empty(records);
        Assert.Equal(0, read);
        Assert.Equal(0, dropped);
        Assert.Single(notes);
    }

    [Fact]
    public void Tail_IsBounded_AndSaysItWasCut()
    {
        var path = Path.Combine(_root, "operations.jsonl");
        var lines = Enumerable.Range(0, 12).Select(i => JournalLine("op-" + i));
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        var (taken, truncated) = DiagnosticPackage.TailLines(path, 5);

        Assert.Equal(5, taken.Count);
        Assert.True(truncated);
        Assert.Contains("op-11", taken[^1]);
    }

    [Fact]
    public void PathLabel_DiffersBetweenPackages_AndMatchesInsideOne()
    {
        var path = @"D:\work\KompasMCP\scratch\model.m3d";
        var first = DiagnosticPackage.MaskPath(path, new byte[16]);
        var samePackage = DiagnosticPackage.MaskPath(path.ToUpperInvariant().Replace('/', '\\'), new byte[16]);
        var otherSalt = new byte[16];
        otherSalt[0] = 1;
        var second = DiagnosticPackage.MaskPath(path, otherSalt);

        Assert.Equal(first, samePackage);
        Assert.NotEqual(first, second);
        Assert.StartsWith("path:", first, StringComparison.Ordinal);
        Assert.EndsWith(".m3d", first, StringComparison.Ordinal);
        Assert.DoesNotContain("kompasmcp", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Package_IsWritten_WithManifestAndNoSalt()
    {
        var journal = WriteJournal(JournalLine());
        var options = new HostOptions
        {
            WritableRoots = new[] { _root },
            JournalPath = journal,
            LogPath = Path.Combine(_root, "host.jsonl"),
        };
        File.WriteAllText(options.LogPath,
            new JsonObject
            {
                ["ts_utc"] = "2026-10-31T05:00:00.0000000+00:00",
                ["level"] = "info",
                ["message"] = @"открыт D:\work\ivanov\model.m3d",
                ["category"] = "KompasMcp.Host.HostSession",
            }.ToJsonString() + "\n");

        var target = Path.Combine(_root, "diag.zip");
        var result = DiagnosticPackage.Build(options, target, allowOverwrite: false);

        Assert.True(File.Exists(target));
        Assert.Equal(new FileInfo(target).Length, result.SizeBytes);
        Assert.Contains(result.Files, f => f.Name == "manifest.json");
        Assert.Contains(result.Files, f => f.Name == "operations.jsonl" && f.RecordsKept == 1);

        using var zip = ZipFile.OpenRead(target);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("environment.json"));
        Assert.NotNull(zip.GetEntry("processes.json"));
        var manifest = Read(zip, "manifest.json");
        var operations = Read(zip, "operations.jsonl");
        var log = Read(zip, "host-log.jsonl");
        Assert.Contains("diag-1", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("salt", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("op-1", operations, StringComparison.Ordinal);
        // The free-text message of the log never travels, and neither does the user's name.
        Assert.DoesNotContain("ivanov", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ivanov", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingFile_WithoutOverwrite_IsRefused()
    {
        var target = Path.Combine(_root, "diag.zip");
        File.WriteAllText(target, "not a package");
        var options = Options(_root);

        var refusal = Assert.Throws<KompasContractException>(
            () => DiagnosticPackage.Build(options, target, allowOverwrite: false));

        Assert.Equal(ErrorCodes.PathNotAllowed, refusal.Code);
        Assert.Equal("not a package", File.ReadAllText(target));
    }

    [Fact]
    public void PathOutsideTheRoots_IsRefused_BeforeAnythingIsWritten()
    {
        var outside = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests", "outside-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "diag.zip");
        try
        {
            var refusal = Assert.Throws<KompasContractException>(
                () => DiagnosticPackage.Build(Options(_root), target, allowOverwrite: false));

            Assert.Equal(ErrorCodes.PathNotAllowed, refusal.Code);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void NonZipTarget_IsRefused()
    {
        var refusal = Assert.Throws<KompasContractException>(
            () => DiagnosticPackage.Build(Options(_root), Path.Combine(_root, "diag.txt"), allowOverwrite: false));

        Assert.Equal(ErrorCodes.InvalidArgument, refusal.Code);
    }

    private static string Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
