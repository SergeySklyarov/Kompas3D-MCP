using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kompas6API5;
using Kompas6Constants;
using KompasAPI7;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Bom;
using KompasMcp.Domain.Files;

namespace KompasMcp.Api5Adapter;

/// <summary>BOM domain — block <c>BOM_MINIMAL</c>, profile <c>bom-minimal-v1</c>, modes <c>BOM-01…BOM-02</c>.</summary>
/// <remarks>DOC: <c>kspartcollection_getcount.html</c>/<c>kspartcollection_getbyindex.html</c> for the
/// collection, <c>kspart_props.html</c> for <c>fileName</c>/<c>name</c>/<c>marking</c>/<c>material</c>/
/// <c>standardComponent</c>, <c>kspart_hidden.html</c> and <c>kspart_excluded.html</c> for the filters.
/// INVARIANT: a nested level is reached by OPENING the sub-assembly file, because <c>ksPart</c> has no
/// component collection in API5; such a document is closed in <c>finally</c> and never stays in the session.
/// History: docs/decisions/bom.md#bom-tool</remarks>
public partial class Api5Session
{
    /// <summary>Read the BOM of an open assembly.</summary>
    public GetBomResult GetBom(GetBomCommand command)
    {
        var document = RequireAssemblyForBom(command.DocumentId);
        var notes = new List<string>();
        var options = new BomOptions(command.IncludeHidden, command.IncludeExcluded, command.IncludeStandard,
            command.GroupBy);
        var (summary, walked) = BuildBom(document, options, command.Recurse, notes);

        return new GetBomResult(
            summary.Rows.Select(r => new BomLineDto(r.File, r.Name, r.Marking, r.Material, r.Instances)).ToList(),
            summary.Instances,
            summary.UniqueFiles,
            new BomSkippedDto(summary.Skipped.Hidden, summary.Skipped.Excluded, summary.Skipped.Standard),
            walked,
            options.GroupBy,
            BomRoute(command.Recurse),
            notes);
    }

    /// <summary>Write the same rows to CSV or JSON; the extension selects the format.</summary>
    /// <remarks>INVARIANT: the file carries the rows of the SAME builder as <see cref="GetBom"/>, and the
    /// answer reports the bytes on disk (size, SHA256), not the intent to write them.
    /// History: docs/decisions/bom.md#bom-tool</remarks>
    public ExportBomResult ExportBom(ExportBomCommand command)
    {
        var document = RequireAssemblyForBom(command.DocumentId);
        var notes = new List<string>();
        var format = FormatOf(command.OutputPath)
            ?? throw new KompasContractException(
                ErrorCodes.InvalidArgument,
                $"Расширение файла выгрузки BOM не поддерживается: {Path.GetExtension(command.OutputPath)}. "
                + "Допустимы .csv и .json.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["output_path"] = command.OutputPath });

        var options = new BomOptions(command.IncludeHidden, command.IncludeExcluded, command.IncludeStandard,
            command.GroupBy);
        var (summary, _) = BuildBom(document, options, command.Recurse, notes);

        var temporary = AtomicPublish.TemporarySibling(command.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(command.OutputPath) ?? ".");
        if (format == "csv")
        {
            File.WriteAllText(temporary, ToCsv(summary.Rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        else
        {
            File.WriteAllText(temporary, ToJson(summary.Rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        AtomicPublish.WriteFrom(temporary, command.OutputPath, allowOverwrite: true);

        var info = new FileInfo(command.OutputPath);
        if (!info.Exists || info.Length == 0)
        {
            throw new KompasContractException(
                ErrorCodes.ExportFailed,
                $"Файл выгрузки BOM не создан или пуст: {command.OutputPath}",
                RetryPolicy.SameOperationId,
                partialEffects: info.Exists);
        }

        return new ExportBomResult(command.OutputPath, format, info.Length, FileHash.Sha256(command.OutputPath),
            summary.Rows.Count, summary.Instances, notes);
    }

    private static string BomRoute(bool recurse) =>
        "api5:ksDocument3D.PartCollection(true) → ksPart(fileName, name, marking, material, hidden, excluded, "
        + "standardComponent)"
        + (recurse ? "; вложенные — открытием файла подсборки" : string.Empty);

    // ── BOM-03 ──

    /// <summary>Properties of ONE component, read from the <c>ksPart</c> its reference addresses.</summary>
    /// <remarks>DOC: <c>kspart_props.html</c> lists the eight properties, each with its own page
    /// (<c>kspart_filename</c>, <c>_name</c>, <c>_marking</c>, <c>_material</c>, <c>_hidden</c>,
    /// <c>_excluded</c>, <c>_standardcomponent</c>, <c>_multibodyparts</c>).
    /// INVARIANT: the reference is resolved by the SAME registry and the SAME ordinal route as
    /// <c>kompas_delete_component</c>, so a stale reference is refused instead of reading a foreign
    /// component. A nested component has no address at all — refused, not guessed.
    /// History: docs/decisions/bom.md#component-properties</remarks>
    public GetComponentPropertiesResult GetComponentProperties(GetComponentPropertiesCommand command)
    {
        var document = RequireAssemblyForBom(command.DocumentId, "свойства компонента");
        var stored = References.Require(command.ComponentRef, document.Id, document.Revision);
        if (stored.Payload is not ComponentPayload payload)
        {
            throw new KompasContractException(
                ErrorCodes.StaleReference,
                $"Ссылка «{command.ComponentRef}» не адресует компонент: это {stored.Kind}.",
                RetryPolicy.ReacquireContext);
        }

        var part = ComponentPart5(document, payload, out var addressNote, "чтение свойств");

        return new GetComponentPropertiesResult(
            new ComponentPropertiesDto(
                Text(() => part.fileName),
                Text(() => part.name),
                Text(() => part.marking),
                Text(() => part.material),
                Bool(() => part.hidden),
                Bool(() => part.excluded),
                Bool(() => part.standardComponent),
                Bool(() => part.MultiBodyParts)),
            "api5:ksDocument3D.PartCollection(true) → ksPartCollection.GetByIndex → ksPart(fileName, name, "
            + "marking, material, hidden, excluded, standardComponent, MultiBodyParts)",
            [addressNote]);
    }

    // ── walk ──

    /// <summary>The rows plus the number of instances the walk enumerated, which the sum is checked against.</summary>
    private (BomSummaryResult Summary, int Walked) BuildBom(
        DocumentEntry document, BomOptions options, bool recurse, List<string> notes)
    {
        var instances = new List<BomInstance>();
        var cache = new Dictionary<string, IReadOnlyList<BomInstance>>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>();
        WalkBom(BomChildren(document.Document3D, notes), inherited: null, recurse, depth: 0, document,
            instances, cache, chain, notes);

        var summary = BomSummary.Build(instances, options);
        var walked = instances.Count;
        if (summary.Instances + summary.Skipped.Total != walked)
        {
            // The sum is the point of the answer: a row that lost instances must be visible, not silent.
            notes.Add($"bom_sum_mismatch — сумма строк {summary.Instances} и пропущенных "
                + $"{summary.Skipped.Total} не равна числу экземпляров обхода {walked}");
        }

        return (summary, walked);
    }

    /// <summary>Emit one level and, when asked, descend into every sub-assembly by opening its file.</summary>
    /// <remarks>INVARIANT: the parent's <c>hidden</c>/<c>excluded</c> flags apply to its contents, so a
    /// hidden sub-assembly does not reappear through its children.
    /// LIMIT: a component whose file has no children is a detail — the route cannot tell them apart without
    /// opening the file, so <c>recurse</c> opens every component file.
    /// History: docs/decisions/bom.md#bom-tool</remarks>
    private void WalkBom(
        IReadOnlyList<BomInstance> level,
        BomInstance? inherited,
        bool recurse,
        int depth,
        DocumentEntry document,
        List<BomInstance> sink,
        Dictionary<string, IReadOnlyList<BomInstance>> cache,
        List<string> chain,
        List<string> notes)
    {
        foreach (var raw in level)
        {
            var instance = Inherit(raw, inherited);
            sink.Add(instance);

            if (!recurse)
            {
                continue;
            }

            if (instance.File is not { Length: > 0 } file)
            {
                notes.Add($"bom_component_without_file — у компонента «{instance.Name ?? "?"}» файл не прочитан: "
                    + "вложенные не обойдены");
                continue;
            }

            if (depth + 1 > MaxStructureDepth)
            {
                notes.Add($"bom_depth_limit — обход остановлен на глубине {MaxStructureDepth}");
                continue;
            }

            if (chain.Any(p => PathsEqual(p, file)))
            {
                throw new KompasContractException(
                    ErrorCodes.InvalidArgument,
                    $"Цикл ссылок в сборке: файл «{file}» уже обходится по этому пути "
                    + $"({string.Join(" → ", chain)} → {file}).",
                    RetryPolicy.Never,
                    details: new Dictionary<string, object?> { ["path"] = file });
            }

            if (!cache.TryGetValue(file, out var children))
            {
                children = BomChildrenOfFile(document, file, notes);
                cache[file] = children;
            }

            if (children.Count == 0)
            {
                continue;
            }

            chain.Add(file);
            try
            {
                WalkBom(children, instance, recurse, depth + 1, document, sink, cache, chain, notes);
            }
            finally
            {
                chain.RemoveAt(chain.Count - 1);
            }
        }
    }

    /// <summary>The parent's filter flags applied to a child: a hidden parent hides what it contains.</summary>
    private static BomInstance Inherit(BomInstance child, BomInstance? parent) => parent is null
        ? child
        : child with
        {
            Hidden = child.Hidden == true || parent.Hidden == true ? true : child.Hidden,
            Excluded = child.Excluded == true || parent.Excluded == true ? true : child.Excluded,
        };

    /// <summary>Direct components of a 3D document through the documented array.</summary>
    private List<BomInstance> BomChildren(ksDocument3D document, List<string> notes)
    {
        var instances = new List<BomInstance>();
        try
        {
            if (document.PartCollection(true) is not ksPartCollection collection)
            {
                notes.Add("bom_collection_not_returned — PartCollection(true) не вернул ksPartCollection: "
                    + "компоненты не перечислены");
                return instances;
            }

            var count = collection.GetCount();
            for (var index = 0; index < count; index++)
            {
                if (collection.GetByIndex(index) is ksPart part)
                {
                    instances.Add(ReadBomInstance(part));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"bom_collection_failed — PartCollection(true) прервался: {ex.Message}");
        }

        return instances;
    }

    /// <summary>One instance as <c>ksPart</c> answers. An unread value stays null, not empty and not false.</summary>
    private static BomInstance ReadBomInstance(ksPart part) => new(
        Text(() => part.fileName),
        Text(() => part.name),
        Text(() => part.marking),
        Text(() => part.material),
        Bool(() => part.hidden),
        Bool(() => part.excluded),
        Bool(() => part.standardComponent));

    /// <summary>Components of a sub-assembly FILE. A document the client already has open is used as it is
    /// and NOT closed; one opened here is closed in <c>finally</c>.</summary>
    private IReadOnlyList<BomInstance> BomChildrenOfFile(DocumentEntry document, string path, List<string> notes)
    {
        var open = TryFindDocumentByPath(path);
        if (open is not null)
        {
            return BomChildren(open.Document3D, notes);
        }

        if (!File.Exists(path))
        {
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Файл компонента не найден: {path}. Вложенные компоненты не обойдены — это отказ, а не "
                + "пропуск: по одному имени файла нельзя сказать, деталь это или подсборка.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["path"] = path });
        }

        var application = RequireApplication(document.ApplicationId);
        var opened = (ksDocument3D)application.Application.Document3D();
        if (!opened.Open(path, !application.DocumentsVisible))
        {
            ComApartment.Release(opened);
            throw new KompasContractException(
                ErrorCodes.DocumentNotFound,
                $"Файл компонента не открывается: {path}",
                RetryPolicy.Never,
                details: new Dictionary<string, object?> { ["path"] = path });
        }

        try
        {
            return BomChildren(opened, notes);
        }
        finally
        {
            CloseWalkedDocument(application, opened, notes);
        }
    }

    /// <summary>Close a document opened only for the walk, with the option that answers "do not save".</summary>
    private static void CloseWalkedDocument(ApplicationEntry application, ksDocument3D document, List<string> notes)
    {
        try
        {
            if (application.Application.TransferInterface(document, (int)ksAPITypeEnum.ksAPI7Dual, 0)
                    is IKompasDocument document7)
            {
                if (!document7.Close(DocumentCloseOptions.kdDoNotSaveChanges))
                {
                    notes.Add("bom_walk_close_unconfirmed — IKompasDocument.Close(kdDoNotSaveChanges) вернул false");
                }

                return;
            }

            document.close();
            notes.Add("bom_walk_close_api5 — документ обхода закрыт через ksDocument3D.close(): у него нет "
                + "выбора «не сохранять»");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            notes.Add($"bom_walk_close_failed — документ обхода не закрыт: {ex.Message}");
        }
        finally
        {
            ComApartment.Release(document);
        }
    }

    // ── output ──

    private static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => "csv",
        ".json" => "json",
        _ => null,
    };

    /// <summary>CSV with a UTF-8 BOM and ';' as the separator — the pair a Russian-locale spreadsheet
    /// expects; both are named in the tool description because a reader cannot see them in the file.</summary>
    private static string ToCsv(IReadOnlyList<BomRow> rows)
    {
        var text = new StringBuilder("file;name;marking;material;instances\n");
        foreach (var row in rows)
        {
            text.Append(Cell(row.File)).Append(';')
                .Append(Cell(row.Name)).Append(';')
                .Append(Cell(row.Marking)).Append(';')
                .Append(Cell(row.Material)).Append(';')
                .Append(row.Instances.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString();
    }

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
    }

    private static string ToJson(IReadOnlyList<BomRow> rows)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("file", row.File);
                writer.WriteString("name", row.Name);
                writer.WriteString("marking", row.Marking);
                writer.WriteString("material", row.Material);
                writer.WriteNumber("instances", row.Instances);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The assembly document, refused with <c>WRONG_DOCUMENT_KIND</c>: a BOM is a fact about an
    /// assembly, and a part or a drawing has no component tree to summarise.</summary>
    private DocumentEntry RequireAssemblyForBom(string documentId, string operation = "сводка BOM")
    {
        var document = RequireDocument(documentId);
        if (document.Kind != DocumentKind.Assembly)
        {
            throw new KompasContractException(
                ErrorCodes.WrongDocumentKind,
                $"Документ «{documentId}» имеет тип {document.Kind}: {operation} — только по "
                + "документу-сборке.",
                RetryPolicy.Never,
                details: new Dictionary<string, object?>
                {
                    ["kind"] = document.Kind.ToString(),
                    ["supported_kinds"] = new[] { "assembly" },
                });
        }

        return document;
    }
}
