using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Contracts.Ipc;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>
/// Правка родного отверстия (наряд SM07 §3.4): три свойства, которые прогон приёмки НЕ держит, а
/// расхождение между контрактом, схемой и адаптером ловится здесь.
/// </summary>
/// <remarks>
/// <para>
/// <b>Почему три, и почему именно эти.</b> Приёмка доказывает ПОВЕДЕНИЕ на живой модели — что правка
/// применяется, что объём уходит на аналитику, что чужие поля отвергаются. Она не может доказать
/// согласованность трёх мест между собой и не может заметить «поле, которого нет ни в одном
/// ведомстве»: такое поле не отвергается ничем и не применяется ничем, поэтому на модели выглядит
/// как успешный вызов. Здесь проверяется именно это:
/// <list type="number">
/// <item>признак отверстия опознаётся в диспетчере правки ПО ТИПУ ДЕРЕВА и ветка стоит до чтения
/// определения API5 — у отверстия определения нет вовсе, и обратный порядок сделал бы правку
/// недостижимой;</item>
/// <item>каждое поле правки отверстия отвергается чужими семействами — иначе оно принимается и
/// проглатывается (измеренный класс: <c>keep_side</c> П5, <c>couplings</c> 20.09.2026);</item>
/// <item>производная глубина зенковки НЕ объявлена записываемой и НЕ проверяется на совпадение с
/// запрошенной — при способе «диаметр + угол» запись в неё не действует (M.3), и требовать
/// совпадения значило бы требовать от приёмки заведомо ложного утверждения.</item>
/// </list>
/// </para>
/// <para>
/// <b>Почему по ИСХОДНИКАМ, а не по сборке.</b> Сборка адаптера не загружается в процесс без
/// установленного КОМПАСа: поля её типов имеют типы interop'а, а interop намеренно не копируется в
/// вывод (<c>Private=false</c> в <c>build/KompasInterop.props</c>). Тот же приём применён в
/// <c>SolidFeatureClassificationTests</c>. Комментарии из разбираемого текста исключаются: иначе
/// тест находил бы имя в объяснении, а не в коде.
/// </para>
/// </remarks>
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

    /// <summary>Исходник адаптера без строк комментариев.</summary>
    private static string AdapterSource(string fileName) => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>Тело метода по сигнатуре: от неё до следующего члена того же уровня вложенности.</summary>
    /// <remarks>
    /// Разбор обязан быть ограничен ОДНИМ методом. В том же файле есть маршрут ЧТЕНИЯ
    /// (<c>GetFeature</c>), где <c>entity.GetDefinition()</c> стоит на первой же строке, и поиск по
    /// всему файлу находил бы именно его: тест тогда утверждал бы порядок строк в чужом методе и
    /// проходил бы независимо от того, где стоит ветка отверстия.
    /// </remarks>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, $"метод «{signature}» не найден — разбор устарел");

        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(next > 0 ? next : source.Length)];
    }

    /// <summary>Имя в схеме → имя свойства C#, по политике именования самого продукта.</summary>
    private static Dictionary<string, string> JsonNameToProperty()
    {
        var policy = KompJson.Options.PropertyNamingPolicy
                     ?? throw new InvalidOperationException(
                         "в KompJson.Options нет политики именования: сопоставление имён схемы с " +
                         "именами свойств стало бы догадкой, а не сверкой");

        return typeof(UpdateFeatureCommand).GetProperties()
            .ToDictionary(p => policy.ConvertName(p.Name), p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>Поля правки отверстия — ровно те, что объявлены в контракте и в схеме.</summary>
    private static readonly string[] HoleEditFields =
    [
        "diameter_mm", "counterbore_diameter_mm", "counterbore_depth_mm",
        "countersink_diameter_mm", "countersink_angle_deg", "expected_volume_delta_mm3",
    ];

    [Fact]
    public void HoleBranch_IsIdentifiedByTreeType_AndStandsBeforeTheApi5Definition()
    {
        // Две половины одной причины. Первая: опознание по типу сущности в дереве — как в чтении
        // (FindHoleEntity), а не по номеру фабрики создания: 52 при создании, 583 в дереве (проба
        // N.1), и поиск по 52 не нашёл бы признак никогда. Вторая: ветка обязана стоять ДО чтения
        // определения API5 — у отверстия определения нет вовсе (типа ksHoleDefinition в вендорском
        // интеропе не существует), и опознание через определение было бы невозможно.
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

        // Различающий контроль: константа обязана быть тем самым измеренным номером дерева, а не
        // номером фабрики. Иначе тест проходил бы и на опознании по 52.
        var constants = AdapterSource("Api5Session.cs");
        Assert.Contains("public const int Hole3D = 583;", constants, StringComparison.Ordinal);
        Assert.Contains("public const int HoleOperation = 52;", constants, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHoleEditField_IsRejectedByTheNeighbouringFamilies()
    {
        // Поле, не попавшее ни в один перечень чужих полей, — это поле, которое адаптер примет и
        // НЕ применит: признак другого семейства его не читает, а общего сторожа для него нет. Ровно
        // так вёл себя keep_side у разделения (П5) и couplings у фаски (измерено 20.09.2026).
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
        // Три независимые половины одного требования наряда SM07 §3.4.
        // (1) Контракт: поля для производной глубины НЕТ. Объявить его значило бы обещать запись,
        //     которая не действует.
        var properties = JsonNameToProperty();
        Assert.DoesNotContain("countersink_depth_mm", properties.Keys);
        Assert.DoesNotContain("CountersinkDepthMm", typeof(UpdateFeatureCommand).GetProperties()
            .Select(p => p.Name));

        // (2) Схема инструмента: того же поля нет и там, иначе Host с additionalProperties:false
        //     пропустил бы вызов до COM, а адаптер не знал бы, что с ним делать.
        var published = (ToolCatalog.All.Single(t => t.Name == "kompas_update_feature").InputSchema
                         ["properties"] as JsonObject)
                        ?? throw new InvalidOperationException(
                            "у kompas_update_feature нет свойств в схеме — проверка стала вакуумом");
        Assert.DoesNotContain("countersink_depth_mm", published.Select(p => p.Key));

        // (3) Адаптер: записанное число не проверяется на совпадение, а производное ПУБЛИКУЕТСЯ
        //     отдельной проверкой с прямым текстом о том, что оно производно.
        var hole = AdapterSource("Api5Session.Hole.cs");
        Assert.Contains("countersink_depth_derived", hole, StringComparison.Ordinal);
        Assert.DoesNotContain("countersink_depth_read_back", hole, StringComparison.Ordinal);

        // (4) Мост: на правке в CountersinkDepth НЕ пишут. Утверждение проверяется по телу метода
        //     правки, а не по всему файлу: при СОЗДАНИИ этот член пишется намеренно (глубина
        //     передаётся нулём для полноты контракта и не притворяется значимой).
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
        // Нулевой диаметр КОМПАС принимает и строит признак без материала при неизменном объёме —
        // измерено на СОЗДАНИИ (тот же класс, что нулевой катет фаски, F.12). Поэтому на правке
        // положительность проверяется до COM, а не оставляется на усмотрение ядра: «принято» там не
        // означает «применено». Проверяется КАЖДОЕ записываемое числовое поле по имени, а не
        // «где-то в файле есть проверка»: пропущенное поле — это поле без проверки.
        var hole = AdapterSource("Api5Session.Hole.cs");

        foreach (var field in new[]
                 {
                     "diameter_mm", "depth_mm", "counterbore_diameter_mm", "counterbore_depth_mm",
                     "countersink_diameter_mm", "countersink_angle_deg",
                 })
        {
            Assert.Contains($"PositiveHoleEditValue(\"{field}\"", hole, StringComparison.Ordinal);
        }

        // Поля ЧУЖОГО режима отвергаются тем же порядком: по имени поля и режима признака.
        Assert.Contains("RejectForeignHoleEditField(", hole, StringComparison.Ordinal);
        Assert.Contains("HoleModeOfRead(", hole, StringComparison.Ordinal);
    }

    [Fact]
    public void DepthMm_IsOwnedByTheHoleFamily_AndRefusedByTheThroughModes()
    {
        // Измеренный дефект первой поставки с веткой отверстия (20.09.2026): правка ГЛУХОГО
        // отверстия отвергалась INVALID_ARGUMENT до COM, потому что depth_mm — поле выдавливания в
        // таблице ролей и одновременно СВОЁ поле режима blind_flat. Отказ был на строке, которая
        // обязана проходить, и нашла его приёмка (F08.15/16/19/20.edit), а не чтение кода.
        //
        // Здесь держатся обе половины исключения и различающий контроль к нему.
        var hole = AdapterSource("Api5Session.Hole.cs");
        var ops = AdapterSource("Api5Session.SolidOps.cs");

        // (1) Семейство объявляет поле своим — иначе исключения нет вовсе.
        Assert.Contains("ownFields: new[] { \"depth_mm\" }", hole, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyCollection<string>? ownFields = null", ops, StringComparison.Ordinal);
        Assert.Contains("&& !Owned(f.Name)", ops, StringComparison.Ordinal);
        Assert.Contains("command.DepthMm is not null && !Owned(\"depth_mm\")", ops, StringComparison.Ordinal);

        // (2) У СКВОЗНЫХ режимов глубина по-прежнему чужая: исключение выдано СЕМЕЙСТВУ, а режим
        //     решает своё. Обе половины названы по имени режима, а не «где-то в файле есть отказ».
        Assert.Contains("RejectForeignHoleEditField(command, \"through_counterbore\", \"depth_mm\"",
            hole, StringComparison.Ordinal);
        Assert.Contains("RejectForeignHoleEditField(command, \"through_countersink\", \"depth_mm\"",
            hole, StringComparison.Ordinal);

        // (3) Различающий контроль: исключение выдано РОВНО ОДНОМУ вызывающему. Второй `ownFields:`
        //     означал бы, что поле объявило своим ещё одно семейство, и сторож на нём перестал бы
        //     отвергать чужое.
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
        // Адрес признака — урок F-11: он ОБЕСПЕЧИВАЕТСЯ постановкой, а не подбирается. У правки
        // отверстия постановка — единственность отверстия в документе: при нескольких соответствие
        // «признак дерева ↔ запись Holes3D» ничем не доказано, и вызов обязан быть отвергнут ДО
        // записи. Проверяются обе половины: и наличие отказа, и то, что адрес берётся маршрутом
        // ЧТЕНИЯ — индексом в IHoles3D через Api7Hole, а не перебором тел и не по имени.
        var hole = AdapterSource("Api5Session.Hole.cs");

        Assert.Contains("Api7Hole.Count(container) != 1", hole, StringComparison.Ordinal);
        Assert.Contains("holes_count", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.Read(container, 0)", hole, StringComparison.Ordinal);
        foreach (var write in new[] { "TryWriteBlindFlat(container, 0", "TryWriteCounterbore(", "TryWriteCountersink(" })
        {
            Assert.Contains(write, hole, StringComparison.Ordinal);
        }

        // Различающий контроль: адрес НЕ берётся обращением к коллекции по индексу «руками» —
        // такая запись обошла бы единственное место, где адрес доказан.
        Assert.DoesNotContain("Holes3D[0]", hole, StringComparison.Ordinal);
    }
}
