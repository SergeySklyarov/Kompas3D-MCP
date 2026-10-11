using System.Runtime.InteropServices;
using KompasAPI7;
using KompasMcp.Api5Adapter.Api7;
using KompasMcp.Contracts;
using KompasMcp.Domain.Com;
using KompasMcp.Domain.Geometry;

namespace KompasMcp.Api5Adapter;

/// <summary>The ONE place a body mutation settles the model, and the ONE place its outcome is collected.</summary>
/// <remarks>INVARIANT: a rebuild recomputes the model that is ALREADY built, so the document volume must
/// not move; when it moves, the state the mutation read is not a state of the built model, and every
/// check computed on it (volume delta, declared expectation, face and body counts) is computed on a state
/// the model does not owe. The settlement of one operation IS the "before" state of the next: a rebuild
/// invalidates the model's handles, so an invisible rebuild before a mutation would refuse a caller that
/// uses a reference taken by the previous command.
/// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
public partial class Api5Session
{
    /// <summary>The settlement marks of the CURRENT command. Cleared by the dispatcher before the
    /// mutation and drained into the answer after it, so one command never inherits the marks of the
    /// previous one.</summary>
    private readonly List<string> _rebuildSettlementNotes = new();

    /// <summary>Whether the current command left a model whose stability was NOT established: two
    /// consecutive readings never agreed within the attempt limit.</summary>
    private bool _rebuildStabilityUnestablished;

    /// <summary>Drop the marks of the previous command. Called before the mutation, not after: a command
    /// that refuses must not carry the marks of the command that ran before it.</summary>
    public void ResetRebuildSettlement()
    {
        _rebuildSettlementNotes.Clear();
        _rebuildStabilityUnestablished = false;
    }

    /// <summary>What the settlement of this command did NOT confirm, in the words of
    /// <see cref="RebuildSettlement"/>; empty when the first control rebuild left the model alone.</summary>
    public IReadOnlyList<string> RebuildSettlementNotes => _rebuildSettlementNotes;

    /// <summary>True when stability was NOT established within the attempt limit: no result check of this
    /// command may then claim geometry.</summary>
    public bool RebuildStabilityUnestablished => _rebuildStabilityUnestablished;

    /// <summary>Bring the model to a state of the BUILT model after the mutation's own rebuild, and
    /// collect what that cost. The control rebuild is <c>ksDocument3D.RebuildDocument()</c> for every
    /// route, including the API7 ones: that is the call MEASURED to restore a feature the previous
    /// rebuild left unapplied, and the call <c>kompas_rebuild</c> already uses.</summary>
    /// <remarks>A document with no solid body has no volume to compare, so it is not settled and not
    /// marked: "no body yet" and "the volume could not be read" are different states, and only the
    /// second one is a gap worth naming.
    /// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
    private void SettleAfterMutation(DocumentEntry document)
    {
        if (!HasSolidBody(document))
        {
            return;
        }

        var (attempts, baseline, observed, stable, _) = SettleRebuild(document);
        Record(new RebuildSettlementOutcome(attempts, baseline, observed, stable));
    }

    /// <summary>The API7 route's rebuild followed by the control rebuild. The API7 write is carried into
    /// the model by <c>IPart7.RebuildModel(true)</c> plus <c>ksDocument3D.RebuildDocument()</c>; the
    /// settlement then repeats only the second call, which is the one measured to restore an unapplied
    /// feature.</summary>
    private void RebuildApi7AndSettle(IModelContainer container, DocumentEntry document)
    {
        Api7Bridge.Rebuild(container, document.Document3D);
        SettleAfterMutation(document);
    }

    /// <summary>Settle a model that was just BUILT by opening or importing, before any client reads it:
    /// a reopened model is a state of the built model only if its rebuild applied every feature.</summary>
    /// <remarks>WHY THE FAILURE IS NAMED AND NOT THROWN. The document IS open (or imported) by the time
    /// this runs; failing the call would hide that from the caller. A connection loss is the one case
    /// that must NOT be swallowed — the session's loss machinery owns it — so it propagates unchanged.
    /// History: docs/decisions/adapter-core.md#rebuild-verify</remarks>
    public void SettleBuiltModel(DocumentEntry document)
    {
        try
        {
            SettleAfterMutation(document);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException && !ComHResult.IsDisconnected(ex))
        {
            _rebuildSettlementNotes.Add(RebuildSettlement.NotChecked);
        }
    }

    private void Record(RebuildSettlementOutcome outcome)
    {
        if (outcome.Note is { } note)
        {
            _rebuildSettlementNotes.Add(note);
        }

        _rebuildStabilityUnestablished |= outcome.StabilityUnestablished;
    }

    /// <summary>Whether the document holds a solid body right now. A read that fails answers false: the
    /// settlement is an auxiliary step of a mutation, and an auxiliary read must never turn a mutation
    /// into a refusal.</summary>
    private static bool HasSolidBody(DocumentEntry document)
    {
        if (document.Kind != DocumentKind.Part || document.Document is null)
        {
            return false;
        }

        try
        {
            return document.PartNow().GetMainBody() is not null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
    }
}
