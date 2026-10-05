using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>B3 feature-edit fields and tree-type addressing: agreement of contract, schema and adapter.</summary>
/// <remarks>TEST: contract and catalog BY VALUE (types load in the test process); the adapter BY SOURCE — its
/// assembly does not load without KOMPAS (interop not copied: <c>Private=false</c>, <c>build/KompasInterop.props</c>).
/// LIMIT: consistency of the three places only; the client-visible foreign-field refusal is proved by acceptance.
/// History: docs/decisions/tests.md#solid-feature-2</remarks>
public sealed class SolidFeatureClassificationTests
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

    /// <summary>Adapter source without comment lines: the parse must not find a name in an explanation.</summary>
    /// <param name="fileName">Which <c>Api5Session</c> part is parsed; several on purpose, never a foreign file.</param>
    private static string AdapterSource(string fileName = "Api5Session.SolidOps.cs") => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>Fields assigned to families: schema name → owning families.</summary>
    private static readonly Dictionary<string, string[]> FamilyFields = new(StringComparer.Ordinal)
    {
        ["operation"] = ["boolean"],
        ["plane"] = ["split", "cut_by_plane"],
        ["keep_side"] = ["cut_by_plane"],
        ["target_body_ref"] = ["cut_by_plane"],
        ["expected_part_volumes_mm3"] = ["split"],
        ["reposition_kind"] = ["reposition"],
        ["reposition_vector_mm"] = ["reposition"],
        ["reposition_axis_point_mm"] = ["reposition"],
        ["reposition_axis_direction_mm"] = ["reposition"],
        ["reposition_axis_point2_mm"] = ["reposition"],
        ["reposition_angle_deg"] = ["reposition"],
        // INVARIANT: pattern belongs to the pattern family, which READS it; other families reject it via their
        // own branch — "family", not "not applicable".
        ["pattern"] = ["pattern"],
        // INVARIANT: six native-hole edit fields. Role "family", not "not applicable" — the hole branch READS
        // them, others reject them via the shared table; the volume-delta expectation belongs to THIS family (P5).
        ["diameter_mm"] = ["hole"],
        ["counterbore_diameter_mm"] = ["hole"],
        ["counterbore_depth_mm"] = ["hole"],
        ["countersink_diameter_mm"] = ["hole"],
        ["countersink_angle_deg"] = ["hole"],
        ["expected_volume_delta_mm3"] = ["hole"],
    };

    /// <summary>Fields not belonging to the B3 families: extrusion, chamfer, fillet, rotation and the B5
    /// families. Rejected as NOT APPLICABLE to a B3 feature.</summary>
    private static readonly string[] NotApplicableFields =
    [
        "depth_mm", "end_condition", "sketch_ref", "distance1_mm", "distance2_mm", "angle_deg",
        "direction", "radius_mm", "edge_refs", "base_object_refs", "rotation_angle_deg",
        "rotation_direction",
        // INVARIANT (queue B5 §11): not assigned to B3 families, so rejected as not applicable rather than
        // reaching a family that does not read them.
        "shift_mode", "section_refs", "thickness_mm", "thin_inward", "face_refs",
        // MEASURED: queue B5 added SIX editable fields; the sixth, couplings, was in neither this list nor the
        // adapter guard — role set by EXPERIMENT: it applied on a chamfer, while shift_mode was rejected.
        "couplings",
    ];

    /// <summary>Addressing fields: they select the feature, they do not define it.</summary>
    private static readonly string[] AddressingFields = ["feature_ref", "expected_revision"];

    /// <summary>Analytic geometry expectations. They belong to no family deliberately: the same expectation is
    /// declared for a translation and a boolean edit, so a family would forbid a legal call.</summary>
    private static readonly string[] ExpectationFields = ["expected_volume_mm3", "expected_bbox_mm"];

    /// <summary>Schema name → C# property name, per the product's OWN naming policy.</summary>
    private static Dictionary<string, string> JsonNameToProperty()
    {
        var policy = KompJson.Options.PropertyNamingPolicy
                     ?? throw new InvalidOperationException(
                         "в KompJson.Options нет политики именования: сопоставление имён схемы с " +
                         "именами свойств стало бы догадкой, а не сверкой");

        return typeof(UpdateFeatureCommand).GetProperties()
            .ToDictionary(p => policy.ConvertName(p.Name), p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>Parsed adapter table: field name → family names (already as contract strings).</summary>
    private static Dictionary<string, string[]> ParsedFamilyTable()
    {
        var source = AdapterSource();

        // The family constants live in DIFFERENT adapter files, so all three are scanned for constants; the
        // table itself is parsed ONLY from SolidOps.cs.
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fileName in new[]
                 {
                     "Api5Session.SolidOps.cs", "Api5Session.PatternEdit.cs", "Api5Session.Hole.cs",
                 })
        {
            foreach (Match match in Regex.Matches(
                         AdapterSource(fileName), @"private const string (\w+Family) = ""([a-z_]+)"";"))
            {
                constants[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     source, @"new\(""([a-z0-9_]+)"",\s*new\[\]\s*\{([^}]*)\}"))
        {
            var owners = match.Groups[2].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => constants.TryGetValue(name, out var family)
                    ? family
                    : throw new InvalidOperationException(
                        $"в таблице адаптера поле «{match.Groups[1].Value}» приписано неизвестной "
                        + $"константе «{name}»: перечень семейств и таблица разошлись"))
                .ToArray();
            table[match.Groups[1].Value] = owners;
        }

        Assert.True(table.Count > 0,
            "таблица семейственных полей в адаптере не разобрана — либо переименована, либо " +
            "изменила форму; тогда эта проверка стала вакуумом, и об этом надо узнать");
        return table;
    }

    [Fact]
    public void AdapterTable_ClassifiesExactlyTheExpectedFields()
    {
        // Discriminating control: the table must also NOT contain extras — a field added "just in case" would
        // refuse a legal call as foreign.
        var parsed = ParsedFamilyTable();

        Assert.Equal(
            FamilyFields.Keys.OrderBy(k => k, StringComparer.Ordinal),
            parsed.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (field, owners) in FamilyFields)
        {
            Assert.Equal(
                owners.OrderBy(o => o, StringComparer.Ordinal),
                parsed[field].OrderBy(o => o, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void EveryClassifiedField_IsAPropertyOfTheUpdateCommand()
    {
        // Catches a typo and a rename: a field named by a string absent from the contract would reject nothing.
        var properties = JsonNameToProperty();

        foreach (var field in FamilyFields.Keys)
        {
            Assert.True(properties.ContainsKey(field),
                $"таблица адаптера знает поле «{field}», а в {nameof(UpdateFeatureCommand)} такого " +
                "свойства нет: имя разошлось с контрактом");
        }
    }

    [Fact]
    public void EveryClassifiedField_IsPublishedInTheToolSchema()
    {
        // MEASURED defect class (P4): a field in the contract and the adapter but not declared in the schema —
        // the Host with additionalProperties:false rejects the call before COM.
        var schema = ToolCatalog.All.Single(t => t.Name == "kompas_update_feature").InputSchema;
        var published = (schema["properties"] as JsonObject)
                        ?? throw new InvalidOperationException(
                             "у kompas_update_feature нет свойств в схеме — проверка стала вакуумом");

        foreach (var field in FamilyFields.Keys)
        {
            Assert.True(published.ContainsKey(field),
                $"поле «{field}» правит признак B3, но в схеме kompas_update_feature не объявлено: " +
                "клиент не сможет его прислать, а strict-валидация отвергнет вызов");
        }

        // Expectations and addressing even more so: without them the edit does not run at all.
        foreach (var field in ExpectationFields.Concat(AddressingFields))
        {
            Assert.True(published.ContainsKey(field),
                $"поле «{field}» объявлено в схеме как обязательное или как ожидание, а в схеме его нет");
        }
    }

    [Fact]
    public void EveryCommandProperty_IsClassifiedExactlyOnce()
    {
        // Every command field must have a ROLE: one in none of the four departments is accepted and silently
        // ignored (defect P5). A new contract field fails here on purpose — the author must decide its role.
        var properties = JsonNameToProperty();

        var buckets = new[]
        {
            ("семейственное B3", FamilyFields.Keys.ToArray()),
            ("неприменимое к B3", NotApplicableFields),
            ("адресация", AddressingFields),
            ("ожидание геометрии", ExpectationFields),
        };

        var classified = buckets.SelectMany(b => b.Item2).ToArray();

        Assert.Equal(classified.Length, classified.Distinct(StringComparer.Ordinal).Count());

        var unclassified = properties.Keys
            .Except(classified, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.True(unclassified.Length == 0,
            $"поля команды не отнесены ни к одной роли: {string.Join(", ", unclassified)}. "
            + "Поле без роли адаптер примет и не применит — решите, чья это роль, и впишите его "
            + "в таблицу семейств, в список неприменимых, в адресацию или в ожидания");

        var phantom = classified
            .Except(properties.Keys, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        Assert.True(phantom.Length == 0,
            $"в ведомствах названы поля, которых в {nameof(UpdateFeatureCommand)} нет: "
            + string.Join(", ", phantom));

        // INVARIANT: completeness also fixes the contract size — growth shows as this line failing; it is 40
        // with the six native-hole edit fields.
        Assert.Equal(40, properties.Count);
    }

    [Fact]
    public void AdapterGuard_RejectsEveryNotApplicableField_AndNoFamilyField()
    {
        // The check reads the REFUSAL CONDITION, not a comment; the reverse half (does not reject family
        // fields) is the discriminating control. The condition starts with a DOUBLE paren: depth_mm became
        // conditional — it belongs to both extrusion and a blind hole — so the regex matches that form.
        var source = AdapterSource();
        var guard = Regex.Match(source, @"if \(\(command\.DepthMm is not null[\s\S]*?\)\s*\n\s*\{");

        Assert.True(guard.Success,
            "условие отказа на неприменимые поля не найдено — оно переписано, и эта проверка " +
            "перестала что-либо утверждать");

        // Exactly one exception: depth_mm is rejected IF the family did not declare it its own — else a
        // condition always rejecting depth_mm would pass (the blind-hole edit failure).
        Assert.Contains("command.DepthMm is not null && !Owned(\"depth_mm\")", guard.Value,
            StringComparison.Ordinal);

        var properties = JsonNameToProperty();
        foreach (var field in NotApplicableFields)
        {
            Assert.True(guard.Value.Contains(properties[field], StringComparison.Ordinal),
                $"поле «{field}» не отвергается как неприменимое: оно дойдёт до семейства, которое " +
                "его не читает, и будет молча проглочено");
        }

        foreach (var field in FamilyFields.Keys)
        {
            Assert.False(guard.Value.Contains(properties[field], StringComparison.Ordinal),
                $"семейственное поле «{field}» отвергается как неприменимое — законный вызов " +
                "перестанет проходить");
        }
    }

    [Fact]
    public void TreeTypeConstants_AreTheMeasuredOnes_AndNotTheCreationNumber()
    {
        // MEASURED (probe b3-measure-feature-types.py, reading ksEntity.type via kompas_list_features): in
        // three of four families the FACTORY number and the TREE number differ.
        var source = AdapterSource("Api5Session.cs");
        var constants = Regex.Matches(source, @"public const int (\w+) = (\d+);")
            .ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value), StringComparer.Ordinal);

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BooleanOperation"] = 69,
            ["SplitSolid"] = 633,
            ["CutByPlane"] = 50,
            ["BodyRepositionFeature"] = 79,
        };

        foreach (var (name, number) in expected)
        {
            Assert.True(constants.TryGetValue(name, out var actual),
                $"константа {name} не найдена — адресация признака по типу стала невозможной");
            Assert.Equal(number, actual);
        }

        // Discriminating control against the number that suggests itself: the CREATION number for
        // o3d_BodyReposition differs from the tree number. A fixed defect that must not return.
        Assert.DoesNotContain(569, expected.Values);
        Assert.All(constants, pair => Assert.NotEqual(569, pair.Value));

        // The type is an addressing sign: two families with one number would merge and hit the wrong feature.
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
    }

    [Fact]
    public void DeclaredExpectationRule_IsWrittenOnce()
    {
        // Root cause of defect P6: the rule "an expectation is declared" was written TWICE, and the copies
        // diverged (boolean required an expectation, translation specifically VOLUME). This test holds uniqueness.
        var source = AdapterSource();

        var rule = Regex.Matches(
            source, @"command\.ExpectedVolumeMm3 is not null \|\| command\.ExpectedBboxMm is not null");
        Assert.True(rule.Count == 1,
            "правило «ожидание объявлено» записано " + rule.Count + " раз(а), а обязано — один: "
            + "две копии уже разошлись один раз (П6), и разойдутся снова");

        var callSites = Regex.Matches(source, @"DeclaresExpectation\(command\)").Count;
        Assert.True(callSites == 2,
            "мест применения правила «ожидание объявлено» — " + callSites + ", ожидалось 2 (правка "
            + "переноса и правка булевой операции); новая копия правила вместо вызова — это возврат П6");
    }
}
