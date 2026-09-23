using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Классификация полей правки признака B3 и адресация по номеру типа в дереве — то, что связывает
/// контракт, опубликованную схему и адаптер, и чего прогон приёмки не заменяет.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему здесь два разных способа проверки.</b> Всё, что лежит в контракте и каталоге
/// (<see cref="UpdateFeatureCommand"/>, схема инструмента), проверяется ПО ЗНАЧЕНИЮ — типы доступны
/// процессу тестов. Всё, что лежит в адаптере, приходится проверять ПО ИСХОДНИКАМ: сборка адаптера
/// не загружается в процесс без установленного КОМПАСа, потому что поля его типов имеют типы
/// interop'а, а interop намеренно не копируется в вывод (<c>Private=false</c> в
/// <c>build/KompasInterop.props</c>) и подставляется на ходу только резолвером, который ставит
/// Worker. Такой же приём уже применён к двум другим свойствам кода —
/// <c>Api5SessionVisibilityGuardTests</c> и <c>TypedComBoundaryGuardTests</c>.
/// </para>
/// <para>
/// <b>Чего эти проверки НЕ доказывают.</b> Они держат СОГЛАСОВАННОСТЬ трёх мест — контракта,
/// схемы и таблицы адаптера — и не являются доказательством того, что отказ на чужое поле
/// действительно приходит клиенту. Это доказано приёмкой: строка <c>B3.28</c> посылает
/// <c>plane + keep_side</c> на признак разделения и получает <c>INVALID_ARGUMENT</c> с
/// <c>foreign_fields == ["keep_side"]</c> на живой модели. Разделение ролей намеренное: тест ловит
/// расхождение за миллисекунды, приёмка подтверждает поведение.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Исходник адаптера без комментариев. Комментарии исключены намеренно: правило «строка кода
    /// важнее упоминания» здесь не формальность — разбираемый текст описывает в том числе и сам
    /// разбор, и без отсечения комментариев тест находил бы имена в объяснении, а не в коде.
    /// </summary>
    /// <param name="fileName">Часть <c>Api5Session</c>, которую разбираем. Их несколько намеренно:
    /// таблица семейственных полей лежит в <c>Api5Session.SolidOps.cs</c>, а номера типов дерева —
    /// в <c>Api5Session.cs</c>, и склеивать их в один текст значило бы разрешить тесту находить
    /// имя в чужом файле.</param>
    private static string AdapterSource(string fileName = "Api5Session.SolidOps.cs") => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ---------------------------------------------------------------------------------------
    // Ожидание: чем поля команды являются для семейств B3. Здесь перечислено ПОЛНОСТЬЮ, и это
    // перечисление — предмет проверки, а не её украшение: расхождение с контрактом и со схемой
    // ловится ниже.
    // ---------------------------------------------------------------------------------------

    /// <summary>Поля, приписанные семействам: имя в схеме → семейства-владельцы.</summary>
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
        // Добавлено 20.09.2026. Поле pattern появилось в контракте вместе с очередью B4, но ни в одну
        // роль не попало — и проверка полноты падала на нём с тех пор. Роль именно «семейственное», а
        // не «неприменимое»: поле принадлежит семейству массива, и признак массива его ЧИТАЕТ;
        // отвергается оно только у признаков других семейств, причём своей веткой, которая
        // возвращается раньше остальных.
        ["pattern"] = ["pattern"],
        // Добавлено 20.09.2026 (наряд SM07 §3.2, очередь B2). Шесть полей правки родного отверстия.
        // Роль «семейственное», а не «неприменимое»: их ЧИТАЕТ ветка отверстия, а признаки остальных
        // семейств отвергают их по общей таблице (SolidOps.cs) — то есть отказ здесь проверяемое
        // поведение, а не запись в тесте. Ожидание дельты объёма приписано ЭТОМУ семейству
        // намеренно: его читает только отверстие, и объявить его общим ожиданием значило бы принять
        // его на переносе и булевой операции и молча не применить (тот же класс, что keep_side, П5).
        ["diameter_mm"] = ["hole"],
        ["counterbore_diameter_mm"] = ["hole"],
        ["counterbore_depth_mm"] = ["hole"],
        ["countersink_diameter_mm"] = ["hole"],
        ["countersink_angle_deg"] = ["hole"],
        ["expected_volume_delta_mm3"] = ["hole"],
    };

    /// <summary>
    /// Поля команды, не принадлежащие семействам B3: выдавливание, фаска, скругление, вращение и три
    /// семейства последней обязательной очереди B5 (кинематика, сечения, оболочка). Отвергаются
    /// отдельной проверкой адаптера как НЕПРИМЕНИМЫЕ к признаку B3.
    /// </summary>
    private static readonly string[] NotApplicableFields =
    [
        "depth_mm", "end_condition", "sketch_ref", "distance1_mm", "distance2_mm", "angle_deg",
        "direction", "radius_mm", "edge_refs", "base_object_refs", "rotation_angle_deg",
        "rotation_direction",
        // Добавлено 20.09.2026 вместе с правкой трёх семейств очереди B5 (наряд B5 §11). Поля не
        // приписаны семействам B3 — значит, обязаны отвергаться как неприменимые, а не достигать
        // семейства, которое их не читает.
        "shift_mode", "section_refs", "thickness_mm", "thin_inward", "face_refs",
        // Добавлено 20.09.2026 (наряд B1–B5 §3.2, шаг C). Очередь B5 добавила ШЕСТЬ правимых полей,
        // и пять из них попали и сюда, и в сторож адаптера, а шестое — couplings — ни туда, ни сюда.
        // Эта проверка полноты и уронила на нём, назвав ровно то, чем дефект и был. Роль определена
        // ОПЫТОМ, а не удобством: зонд scratch/_couplings_scope_probe.py показал, что вызов
        // «distance1_mm + couplings» на фаске возвращал успех и объём 79840 → 79955 (правка
        // применилась, цепочки нет), тогда как shift_mode и section_refs в том же вызове отвергались
        // INVALID_ARGUMENT. Сторож адаптера (SolidOps.cs, Features.cs, Rotated.cs, PatternEdit.cs)
        // дополнен тем же полем, поэтому «неприменимое» здесь — не запись в тесте, а проверяемое
        // поведение.
        "couplings",
    ];

    /// <summary>Поля адресации: ими признак выбирается, а не определяется.</summary>
    private static readonly string[] AddressingFields = ["feature_ref", "expected_revision"];

    /// <summary>
    /// Аналитические ожидания геометрии. Они не принадлежат ни одному семейству намеренно: одно и то
    /// же ожидание объявляется и для переноса, и для булевой правки, поэтому приписать их семейству
    /// значило бы запретить законный вызов.
    /// </summary>
    private static readonly string[] ExpectationFields = ["expected_volume_mm3", "expected_bbox_mm"];

    /// <summary>Имя в схеме → имя свойства C#, по политике именования САМОГО продукта.</summary>
    private static Dictionary<string, string> JsonNameToProperty()
    {
        var policy = KompJson.Options.PropertyNamingPolicy
                     ?? throw new InvalidOperationException(
                         "в KompJson.Options нет политики именования: сопоставление имён схемы с " +
                         "именами свойств стало бы догадкой, а не сверкой");

        return typeof(UpdateFeatureCommand).GetProperties()
            .ToDictionary(p => policy.ConvertName(p.Name), p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>Разобранная таблица адаптера: имя поля → имена семейств (уже как строки контракта).</summary>
    private static Dictionary<string, string[]> ParsedFamilyTable()
    {
        var source = AdapterSource();

        // Константы семейств лежат в РАЗНЫХ файлах адаптера: таблица семейственных полей — в
        // SolidOps.cs, константа семейства массива — в PatternEdit.cs (очередь B4), константа
        // семейства отверстия — в Hole.cs (наряд SM07, очередь B2). Искать их в одном файле значило
        // бы требовать переноса константы ради теста. Таблица при этом разбирается ТОЛЬКО из
        // SolidOps.cs: расширение разбора на второй файл позволило бы тесту находить запись в чужом
        // месте.
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
        // Различающий контроль к самой проверке: таблица обязана не только содержать ожидаемое, но и
        // НЕ содержать лишнего. Таблица, куда дописали поле «на всякий случай», отказывала бы на
        // законном вызове — то есть отвергала бы применимое поле как чужое.
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
        // Ловит опечатку и переименование: поле, названное в таблице строкой, которой в контракте
        // нет, не отвергло бы ничего — оно просто никогда не встретится в команде.
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
        // Это защита от ИЗМЕРЕННОГО класса дефекта (§9.1 П4): поле есть в контракте и в адаптере, но
        // не объявлено в схеме, поэтому Host с additionalProperties:false отвергает вызов ещё до COM,
        // и клиент видит «не поддерживается» там, где всё написано. Так уже было с base_object_refs
        // при сокращении набора рёбер скругления: маршрут и валюта были готовы, не хватало публикации.
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

        // Ожидания и адресация — тем более: без них правка не выполняется вовсе.
        foreach (var field in ExpectationFields.Concat(AddressingFields))
        {
            Assert.True(published.ContainsKey(field),
                $"поле «{field}» объявлено в схеме как обязательное или как ожидание, а в схеме его нет");
        }
    }

    [Fact]
    public void EveryCommandProperty_IsClassifiedExactlyOnce()
    {
        // Главная проверка полноты: у каждого поля команды обязана быть РОЛЬ. Поле, не попавшее ни в
        // одно из четырёх ведомств, — это поле, которое адаптер не отвергнет ни как чужое семейству,
        // ни как неприменимое, то есть примет и молча проигнорирует. Ровно так вёл себя keep_side,
        // пока его не приписали отсечению (дефект П5): вызов проходил, а параметр не применялся.
        //
        // Новое поле контракта уронит эту проверку — и это её назначение: автор обязан решить, чья
        // это роль, а не оставить решение по умолчанию.
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

        // Полнота заодно фиксирует размер контракта: рост числа полей виден как падение этой строки.
        // 26 → 27 (19.09.2026): добавлено target_body_ref — область применения признака отсечения на
        // ПРАВКЕ (наряд B3 §3.2 требует назначать её на создании и ВОССТАНАВЛИВАТЬ на правке).
        // 27 → 28 (очередь B4): добавлено pattern — и ЭТА СТРОКА НЕ БЫЛА ОБНОВЛЕНА, как и роль поля.
        // Обе проверки падали с тех пор: сначала на неотнесённом поле, и до числа дело не доходило.
        // 28 → 33 (20.09.2026): добавлены пять полей правки трёх семейств очереди B5 — shift_mode,
        // section_refs, thickness_mm, thin_inward, face_refs. Число выросло не «на всякий случай»:
        // каждое поле стоит на измеренном маршруте (шаги B5.13, B5.14), а закрытие семи обязательных
        // режимов очереди B5 требует именно действия edit у каждого из них.
        // 33 → 34 (20.09.2026, шаг C наряда B1–B5 §3.2): шестое поле той же очереди — couplings.
        // Прежняя редакция этого числа (33) была НЕВЕРНА с момента появления поля: она описывала
        // пять полей из шести, и заметить это было нечем, потому что проверка падала раньше — на
        // неотнесённом поле. Число пересчитано по контракту, а не подогнано под прогон.
        // 34 → 40 (20.09.2026, наряд SM07 §3.2): шесть полей правки родного отверстия — diameter_mm,
        // counterbore_diameter_mm, counterbore_depth_mm, countersink_diameter_mm,
        // countersink_angle_deg и ожидание дельты объёма expected_volume_delta_mm3. Число растёт не
        // «на всякий случай»: у каждого поля стоит измеренный маршрут (шаг M.6 зонда
        // scratch/_hole_edit_probe.py), а закрытие действия edit шести строк SM-07 требует именно их.
        Assert.Equal(40, properties.Count);
    }

    [Fact]
    public void AdapterGuard_RejectsEveryNotApplicableField_AndNoFamilyField()
    {
        // Проверка читает УСЛОВИЕ ОТКАЗА, а не список в комментарии: важно, что адаптер отвергает
        // именно эти поля. Обратная половина (не отвергает семейственные) — различающий контроль:
        // без неё проверка проходила бы и на условии, отвергающем всё подряд.
        var source = AdapterSource();
        // Условие начинается с ДВОЙНОЙ скобки с 20.09.2026 (наряд SM07 §3.2): первое поле стало
        // условным — `command.DepthMm is not null && !Owned("depth_mm")` — потому что depth_mm
        // принадлежит и выдавливанию, и глухому отверстию. Регулярное выражение обновлено ВМЕСТЕ с
        // условием, и это не косметика: прежняя редакция просто не нашла бы условие и уронила тест,
        // как он и обязан падать на переписанном стороже.
        var guard = Regex.Match(source, @"if \(\(command\.DepthMm is not null[\s\S]*?\)\s*\n\s*\{");

        Assert.True(guard.Success,
            "условие отказа на неприменимые поля не найдено — оно переписано, и эта проверка " +
            "перестала что-либо утверждать");

        // Исключение ровно одно и названо явно: depth_mm отвергается как неприменимое, ЕСЛИ
        // семейство не объявило его своим. Иначе проверка ниже проходила бы на условии, которое
        // отвергает depth_mm всегда, — а именно так правка глухого отверстия и не работала
        // (измерено строками F08.15/16/19/20.edit 20.09.2026).
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
        // Числа измерены прибором scratch/b3-measure-feature-types.py (чтение ksEntity.type на дереве
        // через kompas_list_features), а не выведены из вендорского перечисления: у трёх семейств из
        // четырёх номер ФАБРИКИ и номер ДЕРЕВА различаются, и это измерено отдельно на вращении
        // (29 = 29, вопреки аналогии с отверстием) и на отверстии (52 → 583).
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

        // Различающий контроль против именно того числа, которое напрашивается: 569 —
        // o3d_BodyReposition, номер СОЗДАНИЯ. Признак в дереве виден под 79, и поиск по 569 не нашёл
        // бы его никогда — это исправленный дефект, и он не должен вернуться под видом «уточнения».
        Assert.DoesNotContain(569, expected.Values);
        Assert.All(constants, pair => Assert.NotEqual(569, pair.Value));

        // Тип — это признак адресации, поэтому два семейства с одним номером слились бы в одно
        // ведомство, и правка попала бы не в тот признак.
        Assert.Equal(expected.Count, expected.Values.Distinct().Count());
    }

    [Fact]
    public void DeclaredExpectationRule_IsWrittenOnce()
    {
        // Корневая причина дефекта П6 — правило «ожидание объявлено», записанное ДВАЖДЫ. Копии
        // разошлись: булева правка требовала ожидания, а перенос требовал именно ОБЪЁМ, поэтому
        // объявленный и совпавший габарит отчитывался как необъявленный. Пока выражение стоит в
        // одном месте, разойтись ему не с чем; этот тест держит именно единственность.
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
