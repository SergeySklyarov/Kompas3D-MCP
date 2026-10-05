using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Editing a native hole (order SM07 §3.4): three properties acceptance does NOT hold.</summary>
/// <remarks>INVARIANT, three claims acceptance cannot prove: (1) the hole feature is recognised in the edit
/// dispatcher BY TREE TYPE, before reading the API5 definition — a hole has no definition at all, so the reverse
/// order would make the edit unreachable; (2) every hole-edit field is rejected by the neighbouring families,
/// else it is accepted and swallowed (<c>keep_side</c> P5, <c>couplings</c>); (3) the derived countersink depth
/// is NOT declared writable and NOT asserted to match; a write into it has no effect (M.3).
/// LIMIT: the check is by SOURCE — the adapter does not load without KOMPAS (<c>Private=false</c>).
/// History: docs/decisions/tests.md#hole-edit-2</remarks>
public sealed class HoleEditClassificationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

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

    /// <summary>Adapter source without comment lines.</summary>
    private static string AdapterSource(string fileName) => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>Method body by signature: from it to the next member of the same nesting level.</summary>
    /// <remarks>INVARIANT: the parse is limited to ONE method — the same file has a READ route (<c>GetFeature</c>)
    /// where <c>entity.GetDefinition()</c> is on the first line, and a whole-file search would find that one.</remarks>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"метод «{signature}» не найден — разбор устарел");

        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(next > 0 ? next : source.Length)];
    }

    /// <summary>Schema name → C# property name, per the product's own naming policy.</summary>
    private static Dictionary<string, string> JsonNameToProperty()
    {
        var policy = KompJson.Options.PropertyNamingPolicy
                     ?? throw new InvalidOperationException(
                         "в KompJson.Options нет политики именования: сопоставление имён схемы с " +
                         "именами свойств стало бы догадкой, а не сверкой");

        return typeof(UpdateFeatureCommand).GetProperties()
            .ToDictionary(p => policy.ConvertName(p.Name), p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>Hole-edit fields — exactly those declared in the contract and the schema.</summary>
    private static readonly string[] HoleEditFields =
    [
        "diameter_mm", "counterbore_diameter_mm", "counterbore_depth_mm",
        "countersink_diameter_mm", "countersink_angle_deg", "expected_volume_delta_mm3",
    ];

    [Fact]
    public void HoleBranch_IsIdentifiedByTreeType_AndStandsBeforeTheApi5Definition()
    {
        // Two halves of one cause. First: recognition by the entity type in the tree (as in reading,
        // FindHoleEntity), not by the creation factory number. Second: the branch must precede the definition.
        var source = AdapterSource("Api5Session.Features.cs");
        var dispatcher = MethodBody(source, "public UpdateFeatureResult UpdateFeature(UpdateFeatureCommand command)");

        Assert.Contains("entity.type == KompasObjectTypes.Hole3D", dispatcher, StringComparison.Ordinal);
        Assert.Contains("return UpdateHole(document, entity, command", dispatcher, StringComparison.Ordinal);

        var branch = dispatcher.IndexOf("entity.type == KompasObjectTypes.Hole3D", StringComparison.Ordinal);
        var definition = dispatcher.IndexOf("var definition = entity.GetDefinition();", StringComparison.Ordinal);
        Assert.True(definition > 0, "чтение определения API5 в диспетчере не найдено — разбор устарел");
        Assert.True(branch < definition,
            "ветка отверстия стоит ПОСЛЕ чтения определения API5: у отверстия определения нет, и " +
            "такой порядок делал бы правку недостижимой при отказе GetDefinition()");

        // Discriminating control: the constant must be the measured tree number, not the factory number.
        var constants = AdapterSource("Api5Session.cs");
        Assert.Contains("public const int Hole3D = 583;", constants, StringComparison.Ordinal);
        Assert.Contains("public const int HoleOperation = 52;", constants, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHoleEditField_IsRejectedByTheNeighbouringFamilies()
    {
        // INVARIANT: a field in none of the foreign-field lists is accepted and NOT applied — another family
        // does not read it and there is no shared guard (how keep_side P5 and couplings behaved).
        var properties = JsonNameToProperty();
        var families = new (string File, string Owner)[]
        {
            ("Api5Session.FeatureEdit.B5.cs", "семейства B5 (кинематика, сечения, оболочка)"),
            ("Api5Session.Rotated.cs", "вращение"),
            ("Api5Session.PatternEdit.cs", "массив"),
            ("Api5Session.SolidOps.cs", "признаки B3 (общая таблица семейственных полей)"),
        };

        foreach (var field in HoleEditFields)
        {
            Assert.True(properties.ContainsKey(field),
                $"поле «{field}» названо здесь, а свойства с таким именем в " +
                $"{nameof(UpdateFeatureCommand)} нет: имя разошлось с контрактом");

            var property = properties[field];
            foreach (var (file, owner) in families)
            {
                Assert.True(AdapterSource(file).Contains(property, StringComparison.Ordinal),
                    $"поле «{field}» ({property}) не отвергается семейством «{owner}»: файл {file} " +
                    "его не называет, значит вызов с ним будет принят и проглочен");
            }
        }
    }

    [Fact]
    public void CountersinkDepth_IsNotWritableAndNotAssertedAsWritten()
    {
        // Three independent halves of one requirement (order SM07 §3.4).
        // (1) Contract: there is NO field for the derived depth — declaring it would promise a write with no effect.
        var properties = JsonNameToProperty();
        Assert.DoesNotContain("countersink_depth_mm", properties.Keys);
        Assert.DoesNotContain("CountersinkDepthMm", typeof(UpdateFeatureCommand).GetProperties()
            .Select(p => p.Name));

        // (2) Tool schema: the field is absent there too, else the Host would pass the call through to COM.
        var published = (ToolCatalog.All.Single(t => t.Name == "kompas_update_feature").InputSchema
                         ["properties"] as JsonObject)
                        ?? throw new InvalidOperationException(
                            "у kompas_update_feature нет свойств в схеме — проверка стала вакуумом");
        Assert.DoesNotContain("countersink_depth_mm", published.Select(p => p.Key));

        // (3) Adapter: the written number is not checked; the derived value is PUBLISHED as derived.
        var hole = AdapterSource("Api5Session.Hole.cs");
        Assert.Contains("countersink_depth_derived", hole, StringComparison.Ordinal);
        Assert.DoesNotContain("countersink_depth_read_back", hole, StringComparison.Ordinal);

        // (4) Bridge: on edit, CountersinkDepth is NOT written — checked against the edit method body, not the
        //     whole file: at CREATION it is written deliberately (zero, for contract completeness).
        var bridge = AdapterSource("Api7/Api7Bridge.cs");
        var write = bridge.IndexOf("public static (bool Written, string? Failure, double? ReportedDepthMm) TryWriteCountersink",
            StringComparison.Ordinal);
        Assert.True(write > 0, "метод правки зенковки не найден — разбор устарел");
        var next = bridge.IndexOf("public static ", write + 1, StringComparison.Ordinal);
        var body = bridge[write..(next > 0 ? next : bridge.Length)];
        Assert.DoesNotContain("CountersinkDepth =", body, StringComparison.Ordinal);
        Assert.Contains("countersink.CountersinkDiameter", body, StringComparison.Ordinal);
        Assert.Contains("countersink.CountersinkAngle", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HoleEditWrites_AreGuardedByPositivity_OnTheMeasuredGround()
    {
        // MEASURED: KOMPAS accepts a zero diameter and builds a feature with no material at unchanged volume,
        // so on edit positivity is checked before COM — "accepted" there does not mean "applied".
        var hole = AdapterSource("Api5Session.Hole.cs");

        foreach (var field in new[]
                 {
                     "diameter_mm", "depth_mm", "counterbore_diameter_mm", "counterbore_depth_mm",
                     "countersink_diameter_mm", "countersink_angle_deg",
                 })
        {
            Assert.Contains($"PositiveHoleEditValue(\"{field}\"", hole, StringComparison.Ordinal);
        }

        // Fields of a FOREIGN mode are rejected by the same order: by field name and feature mode.
        Assert.Contains("RejectForeignHoleEditField(", hole, StringComparison.Ordinal);
        Assert.Contains("HoleModeOfRead(", hole, StringComparison.Ordinal);
    }

    [Fact]
    public void DepthMm_IsOwnedByTheHoleFamily_AndRefusedByTheThroughModes()
    {
        // MEASURED defect: editing a BLIND hole was refused with INVALID_ARGUMENT before COM, because depth_mm
        // is an extrusion field in the role table and an OWN field of the blind_flat mode.
        var hole = AdapterSource("Api5Session.Hole.cs");
        var ops = AdapterSource("Api5Session.SolidOps.cs");

        // (1) The family declares the field its own — otherwise there is no exception at all.
        Assert.Contains("ownFields: new[] { \"depth_mm\" }", hole, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyCollection<string>? ownFields = null", ops, StringComparison.Ordinal);
        Assert.Contains("&& !Owned(f.Name)", ops, StringComparison.Ordinal);
        Assert.Contains("command.DepthMm is not null && !Owned(\"depth_mm\")", ops, StringComparison.Ordinal);

        // (2) For THROUGH modes the depth is still foreign: the exception was granted to the FAMILY, not the mode.
        Assert.Contains("RejectForeignHoleEditField(command, \"through_counterbore\", \"depth_mm\"",
            hole, StringComparison.Ordinal);
        Assert.Contains("RejectForeignHoleEditField(command, \"through_countersink\", \"depth_mm\"",
            hole, StringComparison.Ordinal);

        // (3) Discriminating control: the exception was granted to EXACTLY ONE caller.
        var callers = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter"), "*.cs",
                SearchOption.AllDirectories)
            .SelectMany(f => File.ReadLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Count(l => l.Contains("ownFields:", StringComparison.Ordinal));
        Assert.Equal(1, callers);
    }

    [Fact]
    public void HoleEditAddress_IsProvenBySingleHole_AndTakenFromTheReadingRoute()
    {
        // INVARIANT: the feature address is GUARANTEED by the setup, not guessed — for a hole edit the setup is
        // the hole's uniqueness. With several, the call must be refused BEFORE the write.
        var hole = AdapterSource("Api5Session.Hole.cs");

        Assert.Contains("Api7Hole.Count(container) != 1", hole, StringComparison.Ordinal);
        Assert.Contains("holes_count", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.Read(container, 0)", hole, StringComparison.Ordinal);
        foreach (var write in new[] { "TryWriteBlindFlat(container, 0", "TryWriteCounterbore(", "TryWriteCountersink(" })
        {
            Assert.Contains(write, hole, StringComparison.Ordinal);
        }

        // Discriminating control: the address is NOT taken by indexing the collection by hand.
        Assert.DoesNotContain("Holes3D[0]", hole, StringComparison.Ordinal);
    }
}
