using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Journaling;
using KompasMcp.Host;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: a mutation that addresses its document by a minted REFERENCE
/// (<c>sketch_ref</c>, <c>face_ref</c>, <c>edge_refs</c>, <c>body_ref</c>, <c>feature_ref</c>) is journaled
/// with the document it mutates — the ban and reconciliation work for it exactly as for a mutation that
/// names <c>document_id</c>. The document is taken BEFORE the command leaves, never from the reply.</summary>
/// <remarks>History: docs/decisions/host.md#reference-document</remarks>
public sealed class ReferenceDocumentTests : IDisposable
{
    private readonly string _journal;

    public ReferenceDocumentTests() =>
        _journal = Path.Combine(Path.GetTempPath(), "kompas-mcp-tests",
            "refdoc-" + Guid.NewGuid().ToString("N")[..8] + ".jsonl");

    private static string Ref(string kind) => kind + ":0123456789abcdef0123456789abcdef";

    private static string Ref(string kind, int ordinal) => kind + ":" + ordinal.ToString("x32");

    /// <summary>One mutation of each reference kind the catalogue addresses without <c>document_id</c>.</summary>
    public static TheoryData<string, string> Kinds => new()
    {
        { "kompas_extrude", "sketch_ref" },
        { "kompas_hole", "face_ref" },
        { "kompas_fillet", "edge_refs" },
        { "kompas_rib", "body_ref" },
        { "kompas_delete_feature", "feature_ref" },
        { "kompas_update_feature", "feature_ref" },
    };

    private static ToolDefinition Tool(string name) => ToolCatalog.All.Single(tool => tool.Name == name);

    private static ReferenceDocument.Decision Decide(string reference, string document) =>
        ReferenceDocument.Decide(
            new[] { reference },
            new Dictionary<string, string>(StringComparer.Ordinal) { [reference] = document });

    private static JournalRecord Blocking(string documentId) => new(
        Guid.NewGuid().ToString(),
        "kompas_extrude",
        "hash",
        documentId,
        7,
        DateTimeOffset.UnixEpoch,
        JournalOutcome.OutcomeUnknown,
        null,
        null,
        null,
        NeedsReconciliation: true);

    /// <summary>Every mutation without <c>document_id</c> names the objects it changes: the document is
    /// therefore reachable for the journal. A new mutation that names neither would be journaled blind.</summary>
    [Fact]
    public void EveryMutationWithoutADocumentNamesItsObjectsByReference()
    {
        var blind = ToolCatalog.All
            .Where(tool => tool.IsMutation)
            .Where(tool => tool.InputSchema["properties"] is JsonObject properties
                           && properties.ContainsKey("expected_revision")
                           && !properties.ContainsKey("document_id"))
            .Where(tool => ReferenceDocument.Fields(tool.InputSchema).Count == 0)
            .Select(tool => tool.Name)
            .ToArray();

        Assert.True(blind.Length == 0,
            "мутации без document_id и без ссылок на объекты модели: " + string.Join(", ", blind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheReferenceKindOfAMutationIsFoundInItsArguments(string tool, string field)
    {
        Assert.Contains(field, ReferenceDocument.Fields(Tool(tool).InputSchema), StringComparer.Ordinal);
    }

    /// <summary>INVARIANT: the references of the arguments — single values and lists alike — are the ones
    /// handed to the registry, and a repeat of one reference is asked once.</summary>
    [Fact]
    public void ValuesReadSingleReferencesAndListsWithoutRepeats()
    {
        var fields = ReferenceDocument.Fields(Tool("kompas_fillet").InputSchema);
        var edge = Ref("edge");
        var arguments = new JsonObject
        {
            ["edge_refs"] = new JsonArray(edge, Ref("edge", 2), edge),
            ["radius_mm"] = 2.0,
            ["expected_revision"] = 3,
        };

        var values = ReferenceDocument.Values(arguments, fields);

        Assert.Equal(2, values.Count);
        Assert.Contains(edge, values);
        Assert.DoesNotContain("radius_mm", fields);
    }

    /// <summary>A single owning document is used as it is; it is not "the first reference's document".</summary>
    [Fact]
    public void DecideTakesTheSingleDocumentOfSeveralReferences()
    {
        var sketch = Ref("sketch");
        var body = Ref("body");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [sketch] = "doc-1",
            [body] = "doc-1",
        };

        var decision = ReferenceDocument.Decide(new[] { sketch, body }, owners);

        Assert.Equal("doc-1", decision.DocumentId);
        Assert.Null(decision.Reason);
    }

    /// <summary>INVARIANT: a reference the registry does not know is NAMED, not silently dropped: the
    /// record then carries no document AND the reason, so reconciliation says why.</summary>
    [Fact]
    public void UnknownReferenceIsNamedInsteadOfRecordedAsAnEmptyDocument()
    {
        var decision = ReferenceDocument.Decide(new[] { Ref("sketch") }, new Dictionary<string, string>());

        Assert.Null(decision.DocumentId);
        Assert.Contains("реестре Worker", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>A reference of a FOREIGN document: the mutation is not attributed to either, because the
    /// Worker refuses such a call itself (STALE_REFERENCE), so nothing is applied under a guess.</summary>
    [Fact]
    public void ReferencesOfTwoDocumentsAreNotAttributedToOne()
    {
        var sketch = Ref("sketch");
        var body = Ref("body");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [sketch] = "doc-1",
            [body] = "doc-2",
        };

        var decision = ReferenceDocument.Decide(new[] { sketch, body }, owners);

        Assert.Null(decision.DocumentId);
        Assert.Contains("разным документам", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreachableRegistryIsANamedReasonNotARefusal()
    {
        var decision = ReferenceDocument.Decide(new[] { Ref("sketch") }, new Dictionary<string, string>(),
            failure: "канал Worker не отвечает");

        Assert.Null(decision.DocumentId);
        Assert.Contains("канал Worker не отвечает", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>INVARIANT: the ban is decided on the document the call MUTATES — the one the references
    /// resolved to — and it is not a repeat of the same <c>operation_id</c> that is being refused.</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void TheBanRefusesAReferenceMutationOfTheUnreconciledDocument(string toolName, string field)
    {
        var tool = Tool(toolName);
        var reference = Ref(field.TrimEnd('s'));
        var decision = Decide(reference, "doc-1");

        Assert.Equal("doc-1", decision.DocumentId);

        // The document of the reference is the one with the unknown outcome: refused.
        Assert.True(ToolInvoker.ReconciliationBanApplies(
            decision.DocumentId, replay: false, Blocking("doc-1"), tool, dirtyPolicy: null));

        // Another document is untouched by the same rule.
        Assert.False(ToolInvoker.ReconciliationBanApplies(
            decision.DocumentId, replay: false, Blocking("doc-2"), tool, dirtyPolicy: null));
    }

    /// <summary>A retry of the SAME operation_id is not a new command: the journal replays its recorded
    /// outcome, so the ban must not swallow that path (the ban runs before the journal decision).</summary>
    [Fact]
    public void TheBanDoesNotSwallowAReplayOfTheSameOperation()
    {
        var tool = Tool("kompas_extrude");

        Assert.False(ToolInvoker.ReconciliationBanApplies(
            "doc-1", replay: true, Blocking("doc-1"), tool, dirtyPolicy: null));
    }

    /// <summary>INVARIANT: a record whose document could not be determined keeps the REASON, survives the
    /// restart, and still demands reconciliation — the cause is not lost with the process.</summary>
    [Fact]
    public void ARecordWithoutADocumentKeepsTheReasonAcrossARestart()
    {
        var id = Guid.NewGuid().ToString();
        using (var journal = new OperationJournal(_journal))
        {
            journal.TryBegin(id, "kompas_extrude", "{\"depth_mm\":10}", documentId: null, baseRevision: 7,
                documentUnresolved: "ни одна из 1 ссылок не найдена в реестре Worker");
            journal.MarkUnknown(id, "бюджет истёк");
        }

        using var reopened = new OperationJournal(_journal);
        Assert.True(reopened.TryGet(id, out var record));

        Assert.Null(record!.DocumentId);
        Assert.Contains("реестре Worker", record.DocumentUnresolved, StringComparison.Ordinal);
        Assert.True(record.NeedsReconciliation);
    }

    /// <summary>A record WITH the resolved document is the one the ban finds, by the document alone.</summary>
    [Fact]
    public void AResolvedDocumentIsWhatTheJournalBlocksAndReconciles()
    {
        var id = Guid.NewGuid().ToString();
        using var journal = new OperationJournal(_journal);
        var decision = Decide(Ref("sketch"), "doc-1");

        journal.TryBegin(id, "kompas_extrude", "{\"depth_mm\":10}", decision.DocumentId, baseRevision: 7,
            documentUnresolved: decision.Reason);
        journal.MarkUnknown(id, "бюджет истёк");

        Assert.Equal(id, journal.NeedsReconciliationFor("doc-1")!.OperationId);
        Assert.Null(journal.NeedsReconciliationFor("doc-2"));
        Assert.True(journal.TryGet(id, out var record));
        Assert.Equal("doc-1", record!.DocumentId);
        Assert.Equal(7, record.BaseRevision);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_journal);
        }
        catch (IOException)
        {
            // A leftover temporary file is not a test failure.
        }
    }
}
