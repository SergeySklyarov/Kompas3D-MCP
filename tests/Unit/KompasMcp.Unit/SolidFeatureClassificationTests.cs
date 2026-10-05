using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Classification of the B3 feature-edit fields and addressing by tree type number — what ties the contract, the published schema and the adapter together, which an acceptance run does not replace.</summary>
/// <remarks>TEST: two ways of checking. What lives in the contract and catalog (<see cref="UpdateFeatureCommand"/>, the tool schema) is checked BY VALUE — the types are available to the test process. What lives in the adapter is checked BY SOURCE: the adapter assembly does not load without KOMPAS installed, because its interop types are deliberately not copied to the output (<c>Private=false</c> in <c>build/KompasInterop.props</c>).
/// LIMIT: these checks hold the CONSISTENCY of the three places and do not prove that a foreign-field refusal reaches the client — that is proved by acceptance (row <c>B3.28</c> sends <c>plane + keep_side</c> to a split feature and gets <c>INVALID_ARGUMENT</c> with <c>foreign_fields == ["keep_side"]</c> on a live model).
/// History: docs/decisions/tests.md#solid-feature</remarks>
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

    /// <summary>Adapter source without comments. Comments are excluded deliberately: the parsed text also
    /// describes the parse itself, and without stripping them the test would find names in an explanation, not
    /// in the code.</summary>
    /// <param name="fileName">Which part of <c>Api5Session</c> is parsed. Several on purpose: the family-field
    /// table is in <c>Api5Session.SolidOps.cs</c> and the tree type numbers are in <c>Api5Session.cs</c>;
    /// merging them would let the test find a name in a foreign file.</param>
    private static string AdapterSource(string fileName = "Api5Session.SolidOps.cs") => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ---------------------------------------------------------------------------------------
    // Expectation: what the command fields are for the B3 families. Listed COMPLETELY here, and the
    // listing is the subject of the check, not its decoration: a divergence from the contract and the
    // schema is caught below.
    // ---------------------------------------------------------------------------------------

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
        // own branch that returns before the rest — "family", not "not applicable".
        ["pattern"] = ["pattern"],
        // INVARIANT (order SM07 §3.2, queue B2): six native-hole edit fields. Role "family", not "not
        // applicable" — the hole branch READS them, and other families reject them via the shared table
        // (SolidOps.cs). The volume-delta expectation belongs to THIS family deliberately: only the hole reads
        // it, so declaring it a shared expectation would accept it on a translation and a boolean and silently
        // not apply it (same class as keep_side, P5).
        ["diameter_mm"] = ["hole"],
        ["counterbore_diameter_mm"] = ["hole"],
        ["counterbore_depth_mm"] = ["hole"],
        ["countersink_diameter_mm"] = ["hole"],
        ["countersink_angle_deg"] = ["hole"],
        ["expected_volume_delta_mm3"] = ["hole"],
    };

    /// <summary>Command fields not belonging to the B3 families: extrusion, chamfer, fillet, rotation and the
    /// three families of the last mandatory queue B5 (kinematics, sections, shell). Rejected by a separate
    /// adapter check as NOT APPLICABLE to a B3 feature.</summary>
    private static readonly string[] NotApplicableFields =
    [
        "depth_mm", "end_condition", "sketch_ref", "distance1_mm", "distance2_mm", "angle_deg",
        "direction", "radius_mm", "edge_refs", "base_object_refs", "rotation_angle_deg",
        "rotation_direction",
        // INVARIANT (queue B5 §11): these fields are not assigned to B3 families, so they must be rejected as
        // not applicable rather than reach a family that does not read them.
        "shift_mode", "section_refs", "thickness_mm", "thin_inward", "face_refs",
        // MEASURED (order B1–B5 §3.2, step C): queue B5 added SIX editable fields; the sixth, couplings, was in
        // neither this list nor the adapter guard, and the completeness check failed on exactly that. The role is
        // set by EXPERIMENT, not convenience: probe scratch/_couplings_scope_probe.py showed a call
        // "distance1_mm + couplings" on a chamfer returned success and volume 79840 → 79955 (the edit applied,
        // no chain), while shift_mode and section_refs in the same call were rejected INVALID_ARGUMENT. The
        // adapter guard (SolidOps.cs, Features.cs, Rotated.cs, PatternEdit.cs) now names the same field, so
        // "not applicable" here is verified behaviour, not a test note.
        "couplings",
    ];

    /// <summary>Addressing fields: they select the feature, they do not define it.</summary>
    private static readonly string[] AddressingFields = ["feature_ref", "expected_revision"];

    /// <summary>Analytic geometry expectations. They belong to no family deliberately: the same expectation is
    /// declared for a translation and a boolean edit, so assigning them to a family would forbid a legal
    /// call.</summary>
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

        // The family constants live in DIFFERENT adapter files: the family-field table in SolidOps.cs, the
        // pattern-family constant in PatternEdit.cs (queue B4), the hole-family constant in Hole.cs (order SM07,
        // queue B2). Looking for them in one file would demand moving a constant for the test. The table itself
        // is parsed ONLY from SolidOps.cs: extending the parse to a second file would let the test find an entry
        // in a foreign place.
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
        // Discriminating control of the check itself: the table must not only contain the expected but also NOT
        // contain extras. A table with a field added "just in case" would refuse a legal call — rejecting an
        // applicable field as foreign.
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
        // Catches a typo and a rename: a field named in the table by a string absent from the contract would
        // reject nothing — it simply never occurs in a command.
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
        // MEASURED defect class (§9.1 P4): a field exists in the contract and the adapter but is not declared
        // in the schema, so the Host with additionalProperties:false rejects the call before COM and the client
        // sees "unsupported" where everything is implemented. This happened with base_object_refs on the fillet
        // edge-set reduction: the route and currency were ready, the publication was missing.
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
        // The main completeness check: every command field must have a ROLE. A field in none of the four
        // departments is one the adapter rejects neither as foreign to a family nor as not applicable, i.e. it
        // accepts and silently ignores it. This is how keep_side behaved until it was assigned to split (defect
        // P5): the call passed but the parameter was not applied.
        //
        // A new contract field will fail this check — and that is its purpose: the author must decide its role
        // rather than leave the default.
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

        // INVARIANT: completeness also fixes the contract size — growth of the field count shows as this line
        // failing. The count is recounted from the contract, never fitted to a run. It reached 40 with the six
        // native-hole edit fields (order SM07 §3.2), each on a measured route (step M.6 of probe
        // scratch/_hole_edit_probe.py). History: docs/decisions/tests.md#solid-feature
        Assert.Equal(40, properties.Count);
    }

    [Fact]
    public void AdapterGuard_RejectsEveryNotApplicableField_AndNoFamilyField()
    {
        // The check reads the REFUSAL CONDITION, not a list in a comment: what matters is that the adapter
        // rejects exactly these fields. The reverse half (does not reject family fields) is the discriminating
        // control — without it the check would also pass on a condition rejecting everything.
        var source = AdapterSource();
        // The condition starts with a DOUBLE paren since 20.09.2026 (order SM07 §3.2): the first field became
        // conditional — `command.DepthMm is not null && !Owned("depth_mm")` — because depth_mm belongs to both
        // extrusion and a blind hole. The regex was updated TOGETHER with the condition, and that is not
        // cosmetics: the old regex simply would not find the condition and would fail the test.
        var guard = Regex.Match(source, @"if \(\(command\.DepthMm is not null[\s\S]*?\)\s*\n\s*\{");

        Assert.True(guard.Success,
            "условие отказа на неприменимые поля не найдено — оно переписано, и эта проверка " +
            "перестала что-либо утверждать");

        // There is exactly one exception, named explicitly: depth_mm is rejected as not applicable IF the
        // family did not declare it its own. Otherwise the check below would pass on a condition that always
        // rejects depth_mm — which is exactly how the blind-hole edit failed (measured by rows
        // F08.15/16/19/20.edit, 20.09.2026).
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
        // MEASURED by probe scratch/b3-measure-feature-types.py (reading ksEntity.type in the tree via
        // kompas_list_features), not inferred from the vendor enum: in three of four families the FACTORY number
        // and the TREE number differ — measured separately on rotation (29 = 29, against the hole analogy) and
        // on the hole (52 → 583).
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

        // Discriminating control against the very number that suggests itself: 569 — o3d_BodyReposition, the
        // CREATION number. The feature appears in the tree under 79, and a search for 569 would never find it —
        // this is a fixed defect that must not return under the guise of a "refinement".
        Assert.DoesNotContain(569, expected.Values);
        Assert.All(constants, pair => Assert.NotEqual(569, pair.Value));

        // The type is an addressing sign, so two families with one number would merge into one department and
        // the edit would hit the wrong feature.
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
    }

    [Fact]
    public void DeclaredExpectationRule_IsWrittenOnce()
    {
        // Root cause of defect P6: the rule "an expectation is declared" was written TWICE. The copies diverged
        // — the boolean edit required an expectation, the translation required specifically VOLUME, so a
        // declared and matching bounding box was reported as undeclared. While the expression stands in one
        // place it has nothing to diverge from; this test holds exactly the uniqueness.
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
