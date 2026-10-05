using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Editing a native hole (order SM07 §3.4): three properties the acceptance run does NOT hold, while a
/// disagreement between contract, schema and adapter is caught here.</summary>
/// <remarks>INVARIANT, three claims acceptance cannot prove (it proves behaviour on a live model, not the
/// agreement of the three places):
/// <list type="number">
/// <item>the hole feature is recognised in the edit dispatcher BY TREE TYPE, and the branch stands before
/// reading the API5 definition — a hole has no definition at all, so the reverse order would make the edit
/// unreachable;</item>
/// <item>every hole-edit field is rejected by the neighbouring families — otherwise it is accepted and
/// swallowed (measured class: <c>keep_side</c> P5, <c>couplings</c> 20.09.2026);</item>
/// <item>the derived countersink depth is NOT declared writable and NOT asserted to match the requested one
/// — with the "diameter + angle" method a write into it has no effect (M.3), so requiring a match would
/// demand a false claim from acceptance.</item>
/// </list>
/// LIMIT: the check is by SOURCE, not by assembly — the adapter assembly does not load without KOMPAS
/// installed (interop types are not copied to the output, <c>Private=false</c> in
/// <c>build/KompasInterop.props</c>). Comments are stripped from the parsed text, else the test would find
/// the name in an explanation, not in the code. History: docs/decisions/tests.md#hole-edit</remarks>
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
    /// <remarks>INVARIANT: the parse is limited to ONE method. The same file has a READ route
    /// (<c>GetFeature</c>) where <c>entity.GetDefinition()</c> is on the first line, and a whole-file search
    /// would find exactly that one: the test would then assert line order in a foreign method.</remarks>
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
        // Two halves of one cause. First: recognition by the entity type in the tree — as in reading
        // (FindHoleEntity), not by the creation factory number: 52 at creation, 583 in the tree (probe N.1),
        // and a search for 52 would never find the feature. Second: the branch must stand BEFORE reading the
        // API5 definition — a hole has no definition at all (type ksHoleDefinition does not exist in the
        // vendor interop), so recognition through a definition would be impossible.
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

        // Discriminating control: the constant must be the measured tree number, not the factory number —
        // otherwise the test would also pass on recognition by 52.
        var constants = AdapterSource("Api5Session.cs");
        Assert.Contains("public const int Hole3D = 583;", constants, StringComparison.Ordinal);
        Assert.Contains("public const int HoleOperation = 52;", constants, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHoleEditField_IsRejectedByTheNeighbouringFamilies()
    {
        // INVARIANT: a field in none of the foreign-field lists is one the adapter accepts and does NOT
        // apply — another family does not read it and there is no shared guard for it. This is how keep_side
        // on split (P5) and couplings on chamfer (measured 20.09.2026) behaved.
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
        // (1) Contract: there is NO field for the derived depth. Declaring it would promise a write that
        //     has no effect.
        var properties = JsonNameToProperty();
        Assert.DoesNotContain("countersink_depth_mm", properties.Keys);
        Assert.DoesNotContain("CountersinkDepthMm", typeof(UpdateFeatureCommand).GetProperties()
            .Select(p => p.Name));

        // (2) Tool schema: the same field is absent there too, else the Host with additionalProperties:false
        //     would pass the call through to COM and the adapter would not know what to do with it.
        var published = (ToolCatalog.All.Single(t => t.Name == "kompas_update_feature").InputSchema
                         ["properties"] as JsonObject)
                        ?? throw new InvalidOperationException(
                            "у kompas_update_feature нет свойств в схеме — проверка стала вакуумом");
        Assert.DoesNotContain("countersink_depth_mm", published.Select(p => p.Key));

        // (3) Adapter: the written number is not checked for a match, and the derived value is PUBLISHED by
        //     a separate check with explicit text saying it is derived.
        var hole = AdapterSource("Api5Session.Hole.cs");
        Assert.Contains("countersink_depth_derived", hole, StringComparison.Ordinal);
        Assert.DoesNotContain("countersink_depth_read_back", hole, StringComparison.Ordinal);

        // (4) Bridge: on edit, CountersinkDepth is NOT written. The claim is checked against the edit method
        //     body, not the whole file: at CREATION this member is written deliberately (depth is passed as
        //     zero for contract completeness and does not pretend to be meaningful).
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
        // MEASURED (at CREATION, same class as a zero chamfer leg, F.12): KOMPAS accepts a zero diameter and
        // builds a feature with no material at unchanged volume. So on edit positivity is checked before COM,
        // not left to the kernel — "accepted" there does not mean "applied". EVERY writable numeric field is
        // checked by name, not "there is some check in the file": a missed field is a field without a check.
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
        // MEASURED defect of the first delivery with the hole branch (20.09.2026): editing a BLIND hole was
        // refused with INVALID_ARGUMENT before COM, because depth_mm is an extrusion field in the role table
        // and at the same time an OWN field of the blind_flat mode. The refusal was on a line that must pass,
        // found by acceptance (F08.15/16/19/20.edit), not by reading the code. Both halves of the exception
        // and its discriminating control are held here.
        var hole = AdapterSource("Api5Session.Hole.cs");
        var ops = AdapterSource("Api5Session.SolidOps.cs");

        // (1) The family declares the field its own — otherwise there is no exception at all.
        Assert.Contains("ownFields: new[] { \"depth_mm\" }", hole, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyCollection<string>? ownFields = null", ops, StringComparison.Ordinal);
        Assert.Contains("&& !Owned(f.Name)", ops, StringComparison.Ordinal);
        Assert.Contains("command.DepthMm is not null && !Owned(\"depth_mm\")", ops, StringComparison.Ordinal);

        // (2) For THROUGH modes the depth is still foreign: the exception was granted to the FAMILY, and the
        //     mode decides its own. Both halves are named by mode, not "there is some refusal in the file".
        Assert.Contains("RejectForeignHoleEditField(command, \"through_counterbore\", \"depth_mm\"",
            hole, StringComparison.Ordinal);
        Assert.Contains("RejectForeignHoleEditField(command, \"through_countersink\", \"depth_mm\"",
            hole, StringComparison.Ordinal);

        // (3) Discriminating control: the exception was granted to EXACTLY ONE caller. A second `ownFields:`
        //     would mean another family declared the field its own, and the guard would stop rejecting it as foreign.
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
        // INVARIANT (lesson F-11): the feature address is GUARANTEED by the setup, not guessed. For a hole
        // edit the setup is the hole's uniqueness in the document — with several, the correspondence "tree
        // feature ↔ Holes3D entry" is unproved, and the call must be refused BEFORE the write. Both halves are
        // checked: the refusal exists, and the address comes from the READ route — an index into IHoles3D via
        // Api7Hole, not by iterating bodies or by name.
        var hole = AdapterSource("Api5Session.Hole.cs");

        Assert.Contains("Api7Hole.Count(container) != 1", hole, StringComparison.Ordinal);
        Assert.Contains("holes_count", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.Read(container, 0)", hole, StringComparison.Ordinal);
        foreach (var write in new[] { "TryWriteBlindFlat(container, 0", "TryWriteCounterbore(", "TryWriteCountersink(" })
        {
            Assert.Contains(write, hole, StringComparison.Ordinal);
        }

        // Discriminating control: the address is NOT taken by indexing the collection by hand — such a write
        // would bypass the only place where the address is proved.
        Assert.DoesNotContain("Holes3D[0]", hole, StringComparison.Ordinal);
    }
}
