using System.Text;
using System.Text.Json.Nodes;
using KompasMcp.Contracts.Schema;

namespace KompasMcp.Host.Catalog;

/// <summary>The presentation of the catalog: a fixed set of tools whose size does not grow with the
/// number of operations.</summary>
/// <remarks>INVARIANT: the operations are <see cref="ToolCatalog.All"/> and nothing else. A second list
/// of operations would drift from the schemas the direct tools validate against, and the whole point of
/// this catalog is that ONE handler answers both paths.
/// History: docs/decisions/host.md#catalog-mechanism</remarks>
public static class CompactCatalog
{
    public const string FindTool = "kompas_find_operations";
    public const string DescribeTool = "kompas_describe_operation";
    public const string RunTool = "kompas_run";

    /// <summary>Tools published beside the three catalog tools: a session cannot
    /// start, keep its context or hand itself back without them.</summary>
    /// <remarks>MEASURED: every session begins with connect and get_context and every mutation is
    /// followed by a fresh context, so these calls happen on every path and cost nothing to keep
    /// direct. Everything else is reached through <see cref="RunTool"/>.
    /// TEST: CompactCatalogTests.CompactListingIsFixedAndFullIsUntouched.</remarks>
    private static readonly string[] DirectNames =
    {
        "kompas_health", "kompas_connect", "kompas_session_status", "kompas_acquire_session",
        "kompas_release_session", "kompas_disconnect", "kompas_create_document", "kompas_open_document",
        "kompas_close_document", "kompas_get_context", "kompas_capabilities",
    };

    /// <summary>Groups shown by <see cref="FindTool"/>. Presentation only: the operation set stays
    /// <see cref="ToolCatalog.All"/>, and a unit test holds every name to a group.</summary>
    private static readonly Dictionary<string, string> Groups = new(StringComparer.Ordinal)
    {
        ["kompas_health"] = "session", ["kompas_connect"] = "session",
        ["kompas_session_status"] = "session", ["kompas_acquire_session"] = "session",
        ["kompas_release_session"] = "session", ["kompas_disconnect"] = "session",
        ["kompas_create_document"] = "document", ["kompas_open_document"] = "document",
        ["kompas_close_document"] = "document", ["kompas_save_document"] = "document",
        ["kompas_get_context"] = "document", ["kompas_list_documents"] = "document",
        ["kompas_capabilities"] = "document", ["kompas_probe_units"] = "document",
        ["kompas_rebuild"] = "document",
        ["kompas_create_sketch"] = "sketch", ["kompas_edit_sketch"] = "sketch",
        ["kompas_edit_sketch_entity"] = "sketch", ["kompas_finish_sketch"] = "sketch",
        ["kompas_get_sketch_status"] = "sketch", ["kompas_list_sketches"] = "sketch",
        ["kompas_list_sketch_entities"] = "sketch", ["kompas_list_sketch_constraints"] = "sketch",
        ["kompas_create_sketch_constraint"] = "sketch", ["kompas_delete_sketch_constraint"] = "sketch",
        ["kompas_create_sketch_dimension"] = "sketch", ["kompas_set_sketch_plane"] = "sketch",
        ["kompas_create_aux_geometry"] = "sketch", ["kompas_list_aux_geometry"] = "sketch",
        ["kompas_update_plane"] = "sketch",
        ["kompas_extrude"] = "feature", ["kompas_rotated"] = "feature", ["kompas_loft"] = "feature",
        ["kompas_sweep"] = "feature", ["kompas_hole"] = "feature", ["kompas_fillet"] = "feature",
        ["kompas_chamfer"] = "feature", ["kompas_shell"] = "feature", ["kompas_draft"] = "feature",
        ["kompas_draft_from_edges"] = "feature", ["kompas_create_thread"] = "feature",
        ["kompas_update_feature"] = "feature", ["kompas_delete_feature"] = "feature",
        ["kompas_list_features"] = "feature", ["kompas_get_feature"] = "feature",
        ["kompas_list_feature_parameters"] = "feature", ["kompas_set_feature_suppressed"] = "feature",
        ["kompas_boolean"] = "boolean", ["kompas_split"] = "boolean", ["kompas_cut_by_plane"] = "boolean",
        ["kompas_create_sheet_metal_body"] = "sheet_metal", ["kompas_get_sheet_metal"] = "sheet_metal",
        ["kompas_edit_sheet_metal_body"] = "sheet_metal",
        ["kompas_pattern_grid"] = "pattern", ["kompas_pattern_circular"] = "pattern",
        ["kompas_pattern_mirror"] = "pattern", ["kompas_get_pattern"] = "pattern",
        ["kompas_insert_component"] = "assembly", ["kompas_replace_component"] = "assembly",
        ["kompas_reposition"] = "assembly", ["kompas_set_component_placement"] = "assembly",
        ["kompas_delete_component"] = "assembly", ["kompas_list_components"] = "assembly",
        ["kompas_check_component_links"] = "assembly", ["kompas_check_interference"] = "assembly",
        ["kompas_create_mate"] = "assembly", ["kompas_delete_mate"] = "assembly",
        ["kompas_list_mates"] = "assembly", ["kompas_set_mate_parameter"] = "assembly",
        ["kompas_set_mate_fixed"] = "assembly",
        ["kompas_set_dismantle"] = "assembly", ["kompas_list_dismantle_steps"] = "assembly",
        ["kompas_create_section"] = "feature", ["kompas_set_section_mode"] = "feature",
        ["kompas_list_sections"] = "feature",
        ["kompas_create_drawing_views"] = "drawing", ["kompas_list_drawing_views"] = "drawing",
        ["kompas_edit_view"] = "drawing", ["kompas_rebuild_drawing_views"] = "drawing",
        ["kompas_add_dimension"] = "drawing", ["kompas_list_dimensions"] = "drawing",
        ["kompas_create_section_view"] = "drawing", ["kompas_get_section_view"] = "drawing",
        ["kompas_update_section_view"] = "drawing", ["kompas_delete_section_view"] = "drawing",
        ["kompas_get_title_block"] = "drawing", ["kompas_set_title_block"] = "drawing",
        ["kompas_get_technical_demand"] = "drawing", ["kompas_set_technical_demand"] = "drawing",
        ["kompas_export_drawing"] = "drawing",
        ["kompas_create_variable"] = "variables", ["kompas_list_variables"] = "variables",
        ["kompas_set_variable"] = "variables", ["kompas_bind_parameter"] = "variables",
        ["kompas_get_material"] = "variables", ["kompas_set_material"] = "variables",
        ["kompas_measure"] = "inspection", ["kompas_measure_gap"] = "inspection",
        ["kompas_read_topology"] = "inspection", ["kompas_list_bodies"] = "inspection",
        ["kompas_resolve_selection"] = "inspection",
        ["kompas_copy_geometry"] = "master", ["kompas_list_copies_geometry"] = "master",
        ["kompas_synchronise_copy"] = "master",
        ["kompas_export_step"] = "file", ["kompas_export_stl"] = "file",
        ["kompas_export_image"] = "file", ["kompas_import_step"] = "file",
    };

    private static readonly Dictionary<string, ToolDefinition> ByName =
        ToolCatalog.All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>The direct subset, in catalog order.</summary>
    public static IReadOnlyList<ToolDefinition> Direct { get; } =
        ToolCatalog.All.Where(t => DirectNames.Contains(t.Name, StringComparer.Ordinal)).ToArray();

    /// <summary>The operations reached through <see cref="RunTool"/>: the registry MINUS the direct
    /// subset.</summary>
    /// <remarks>INVARIANT: this is derived, never listed. A literal list of converted names would drift
    /// from the registry, and an operation missing from it would be refused by a mode that claims to
    /// serve the whole catalog. History: docs/decisions/host.md#catalog-mechanism</remarks>
    public static IReadOnlyList<string> ViaRun { get; } =
        ToolCatalog.All.Where(t => !DirectNames.Contains(t.Name, StringComparer.Ordinal))
            .Select(t => t.Name).ToArray();

    /// <summary>The three tools the catalog adds. They are NOT in <see cref="ToolCatalog.All"/>: the
    /// registry answers operations only, and these three answer the catalog itself.</summary>
    public static IReadOnlyList<ToolDefinition> Tools { get; } = BuildTools();

    /// <summary>What the Host publishes in <c>tools/list</c>: the direct subset plus the three tools.</summary>
    public static IReadOnlyList<ToolDefinition> Listing() => Direct.Concat(Tools).ToArray();

    public static bool IsCompactTool(string? name) =>
        name is FindTool or DescribeTool or RunTool;

    public static ToolDefinition? Operation(string id) =>
        ByName.TryGetValue(id, out var tool) ? tool : null;

    public static string GroupOf(string name) => Groups.TryGetValue(name, out var group) ? group : "other";

    /// <summary>How the client reaches the operation: directly or through <c>kompas_run</c>. Every
    /// registry entry is reachable, so there is no third answer.</summary>
    /// <remarks>INVARIANT: the answer is one of two, because this catalog serves the whole registry.
    /// A third value would tell a client an operation exists and cannot be called, which it
    /// does not do. TEST: CompactCatalogTests.EveryOperationIsReachableExactlyOneWay.
    /// History: docs/decisions/host.md#catalog-mechanism</remarks>
    public static string Reach(string name) =>
        DirectNames.Contains(name, StringComparer.Ordinal) ? "direct" : "run";


    /// <summary>Ranked search over ids, titles, descriptions and groups.</summary>
    /// <remarks>INVARIANT: the search reads the SAME descriptions the direct tools publish, so a
    /// Russian word from the tool's own title always finds it — a hand-written index would answer a
    /// vocabulary the product does not speak.
    /// History: docs/decisions/host.md#catalog-mechanism</remarks>
    public static IReadOnlyList<(ToolDefinition Tool, int Score)> Search(string? query, string? group)
    {
        var words = Tokens(query);
        var groupFilter = string.IsNullOrWhiteSpace(group) ? null : group.Trim().ToLowerInvariant();

        var hits = new List<(ToolDefinition Tool, int Score)>();
        foreach (var tool in ToolCatalog.All)
        {
            if (groupFilter is not null && !string.Equals(GroupOf(tool.Name), groupFilter, StringComparison.Ordinal))
            {
                continue;
            }

            var score = Score(tool, words);
            if (words.Count == 0 || score > 0)
            {
                hits.Add((tool, score));
            }
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Tool.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static int Score(ToolDefinition tool, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return 1;
        }

        var name = tool.Name.ToLowerInvariant();
        var title = tool.Title.ToLowerInvariant();
        var description = tool.Description.ToLowerInvariant();
        var group = GroupOf(tool.Name);

        var score = 0;
        foreach (var word in words)
        {
            if (name.Contains(word, StringComparison.Ordinal))
            {
                score += 6;
            }

            if (title.Contains(word, StringComparison.Ordinal))
            {
                score += 5;
            }

            if (string.Equals(group, word, StringComparison.Ordinal))
            {
                score += 4;
            }

            if (description.Contains(word, StringComparison.Ordinal))
            {
                score += 2;
            }
        }

        return score;
    }

    private static List<string> Tokens(string? query)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return words;
        }

        var current = new StringBuilder();
        foreach (var ch in query.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 1)
            {
                words.Add(current.ToString());
            }

            current.Clear();
        }

        if (current.Length > 1)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>The instructions of the one catalog: the client must learn the call order
    /// find -> describe -> run, because an operation is an id and not a tool name it can guess.</summary>
    /// <remarks>INVARIANT: the text names only tools the client can call - published ones, or registry
    /// ids reached through <c>kompas_run</c>; a stale name would send the model nowhere.
    /// TEST: CompactCatalogTests.InstructionsNameCallableTools.
    /// History: docs/decisions/host.md#catalog-mode-removed</remarks>
    public static string Instructions => InstructionsText;

    private const string InstructionsText = """
        Сервер управляет одним экземпляром КОМПАС-3D v24 через COM. Каталог здесь короткий и не растёт:
        операции вызываются по id, а не отдельными инструментами. Порядок работы:
        kompas_find_operations (найти по русскому слову или группе) → kompas_describe_operation
        (полная схема входа, оговорки, коды отказа) → kompas_run (id и args). Аргументы проверяются по
        той же схеме, что у прямого инструмента, ДО исполнения: отказ называет неверное или неизвестное
        поле, а неизвестный id — ближайшие id. Затем обычный порядок сеанса: kompas_health →
        kompas_connect → kompas_create_document/kompas_open_document → kompas_get_context → работа →
        перечитывание контекста → сохранение.
        Ревизия отменяет выданные ранее ссылки: это не защита от ошибки, а правило модели.
        Ответы содержат verification.level: он показывает, что реально проверено, а что нет.
        Одновременно моделью работает только один сеанс: SESSION_NOT_ACQUIRED или SESSION_OWNER_ACTIVE
        означает, что сеанс занят или требует явного захвата (kompas_session_status, затем
        kompas_acquire_session). После захвата прежние document_id и revision недействительны.
        Каждая операция реестра достижима: kompas_find_operations и kompas_describe_operation
        называют путь вызова (direct - опубликованный инструмент, run - через kompas_run).

        Типичные ошибки в этом сервере:
        - Единицы — миллиметры и мм²/мм³. Другие единицы не принимаются и не пересчитываются.
        - Ссылки и document_id привязаны к ревизии: любая мутация делает прежние ссылки устаревшими
          (STALE_REFERENCE). Это защита, а не сбой: перечитайте kompas_get_context и ссылки заново,
          а не повторяйте вызов с тем же expected_revision.
        - Грань выбирается предикатом через kompas_resolve_selection (через kompas_run), а не
          «первой» или «текущей». Два подходящих кандидата — отказ AMBIGUOUS_SELECTION; ослабьте
          предикат, а не угадывайте. Неизвестное серверу поле предиката — тоже отказ
          INVALID_ARGUMENT, а не молчание.
        - Обычное объединение требует пересечения или общей поверхности тел (указание заказчика от
          19.09.2026). Касание ребром, касание точкой и несвязные тела — честный отказ ядра, а не
          ошибка сервера; не заменяйте это обходными построениями.
        - verification.level в ответе говорит, что реально проверено. Уровень ниже ожидаемого —
          повод перечитать модель (kompas_get_context, list/get/measure), а не повторять вызов:
          повтор не поднимет уровень доказательства.
        - Отказ мутации несёт ДВА разных признака, и они не противоречат друг другу: partial_effects
          говорит, что отказ не гарантированно без последствий (снята контрольная копия, модель в
          памяти КОМПАСа не откатывается), а feature_left_in_tree — только ИЗМЕРЕНИЕ по счётчику
          признаков: остался ли в дереве признак, который клиент обязан удалить. Значение false у
          второго НЕ отменяет partial_effects=true; что именно откачено, называет rollback_scope.
        - Снимок (kompas_export_image через kompas_run) — вспомогательный канал, а не
          доказательство. Проекция задаётся полем view по имени типа
          (front/rear/up/down/left/right/isometric/dimetric), а не по локализованной подписи вида;
          применённый вид подтверждается обратным чтением. Прежний вид возвращается после снимка, но
          только если он восстановим (прочитан и его тип опубликован): в видимом окне до первого
          SetCurrent он не читается, и вызов с keep_view=false отказывает VIEW_UNAVAILABLE, не меняя
          вид окна. keep_view=true — согласие оставить запрошенную проекцию.
        """;

    /// <summary>Ids close to an unknown one, by common prefix and by shared words: a model that
    /// mistypes a name gets a candidate instead of an empty refusal.</summary>
    public static IReadOnlyList<string> NearestIds(string id)
    {
        var wanted = id.ToLowerInvariant();
        return ToolCatalog.All
            .Select(tool => (tool.Name, Distance: Similarity(wanted, tool.Name.ToLowerInvariant())))
            .Where(row => row.Distance > 0)
            .OrderByDescending(row => row.Distance)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .Take(5)
            .Select(row => row.Name)
            .ToArray();
    }

    private static int Similarity(string left, string right)
    {
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix])
        {
            prefix++;
        }

        var shared = Tokens(left).Count(word => right.Contains(word, StringComparison.Ordinal));
        return prefix + (shared * 4);
    }

    /// <summary>A ready-to-call example for a search hit: the required fields only, with a placeholder
    /// per type, so the model edits a shape instead of inventing one.</summary>
    public static JsonObject Example(ToolDefinition tool)
    {
        var args = new JsonObject();
        if (tool.InputSchema["properties"] is JsonObject properties && tool.InputSchema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(node => node!.GetValue<string>()))
            {
                if (properties[name] is JsonObject schema)
                {
                    args[name] = Placeholder(schema);
                }
            }
        }

        return new JsonObject { ["id"] = tool.Name, ["args"] = args };
    }

    private static JsonNode? Placeholder(JsonObject schema)
    {
        if (schema["enum"] is JsonArray values && values.Count > 0)
        {
            return JsonNode.Parse(values[0]!.ToJsonString());
        }

        return schema["type"]?.GetValue<string>() switch
        {
            "string" => JsonValue.Create(schema["format"]?.GetValue<string>() == "uuid" ? "<uuid>" : "<значение>"),
            "number" or "integer" => JsonValue.Create(0),
            "boolean" => JsonValue.Create(false),
            "array" => new JsonArray(),
            "object" => new JsonObject(),
            _ => JsonValue.Create("<значение>"),
        };
    }

    /// <summary>One line of purpose, cut the same way the generated tool listing cuts it.</summary>
    public static string Purpose(ToolDefinition tool) => ToolListing.Summary(tool.Description);

    private static IReadOnlyList<ToolDefinition> BuildTools()
    {
        var find = new ToolDefinition(
            FindTool,
            "Найти операцию каталога",
            "Поиск операции по русским и английским словам и по группе. Возвращает id, одну строку "
            + "назначения, группу и готовый пример вызова у лучших совпадений. Дальше - "
            + DescribeTool + " (полная схема) и " + RunTool + " (вызов).",
            Sch.Obj(
                "Поиск операции",
                new[] { "query" },
                Sch.Props(
                    ("query", Sch.Str(
                        "Слова для поиска: русские или английские, как в назначении операции "
                        + "(«выдавить», «эскиз», «резьба», «extrude»). Пустая строка - все операции "
                        + "выбранной группы.")),
                    ("group", Sch.Nullable(Sch.Str(
                        "Ограничить группой: session, document, sketch, feature, boolean, pattern, "
                        + "assembly, drawing, variables, inspection, file."))),
                    ("limit", Sch.Nullable(Sch.Int("Сколько совпадений вернуть, по умолчанию 20.",
                        1, ToolCatalog.All.Count))))),
            new ToolBehaviour(ReadOnly: true, Destructive: false, RequiresOperationId: false,
                RequiresDocument: false, RequiresExpectedRevision: false, HostLocal: true),
            string.Empty);

        var describe = new ToolDefinition(
            DescribeTool,
            "Описание операции",
            "Полная схема входа операции - та же, что в schemas/ и в прямом инструменте, - вместе с "
            + "группой, оговорками и готовым примером вызова. Ничего не исполняет.",
            Sch.Obj(
                "Описание операции",
                new[] { "id" },
                Sch.Props(
                    ("id", Sch.Str("id операции из " + FindTool + ", например kompas_extrude.")))),
            new ToolBehaviour(ReadOnly: true, Destructive: false, RequiresOperationId: false,
                RequiresDocument: false, RequiresExpectedRevision: false, HostLocal: true),
            string.Empty);

        // The annotations are honest at the worst case: kompas_run can mutate the model, delete a
        // feature and write a file, so it is neither read-only nor non-destructive. LIMIT: a client
        // cannot see the per-operation annotations, only these (docs/decisions/host.md#catalog-mechanism).
        var run = new ToolDefinition(
            RunTool,
            "Выполнить операцию каталога",
            "Вызывает операцию по её id с аргументами. Аргументы проверяются по ТОЙ ЖЕ схеме, что у "
            + "прямого инструмента, ДО исполнения: отказ называет неверное или неизвестное поле и его "
            + "схему. Неизвестный id - ближайшие id. Инструмент пишущий: он может менять модель, "
            + "удалять признаки и писать файлы - аннотации у него не «только чтение».",
            Sch.Obj(
                "Выполнить операцию",
                new[] { "id" },
                Sch.Props(
                    ("id", Sch.Str("id операции из " + FindTool + ".")),
                    ("args", new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = true,
                        ["description"] = "Аргументы операции: ровно поля её схемы (" + DescribeTool
                            + "). Неизвестное поле отвергается до исполнения.",
                    }))),
            new ToolBehaviour(ReadOnly: false, Destructive: true, RequiresOperationId: false,
                RequiresDocument: false, RequiresExpectedRevision: false, HostLocal: true),
            string.Empty);

        return new[] { find, describe, run };
    }
}
