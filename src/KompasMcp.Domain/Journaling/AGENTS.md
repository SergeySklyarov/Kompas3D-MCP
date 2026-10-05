# Journaling (KompasMcp.Domain/Journaling)

Durable, append-only journal of mutations and the single owner of the CAD session. The journal is a
SAFETY mechanism: it exists so that a replay after a crash cannot apply a mutation a second time.

## Files

- `OperationJournal.cs` - the journal and the idempotency decisions (`TryBegin`/`Finish`).
- `NamedFileLock.cs` - named cross-process mutex serialising journal writes.
- `HostOwnership.cs` - the single owner of the session per `journal_path`.

## Invariants

- INVARIANT: intent is appended and flushed BEFORE the command reaches COM; the terminal state after.
- INVARIANT: journal writes run under the named lock - a lost record would make a replay look like
  "never ran" and re-apply the mutation. No lock → no write (`JOURNAL_UNAVAILABLE`).
- INVARIANT: a torn tail is repaired only under a held lock; unlocked repair corrupts a foreign line.
- INVARIANT: "already running" is a barrier (`Proceed=false`), not a licence to start again.
- INVARIANT: a clean failure may be replayed with the same `operation_id`; a partial effect may not.
- INVARIANT: ownership is identified by pid AND generation; transport start takes no ownership.
- INVARIANT: the lock is not held during COM - only to read and write the owner record.
- LIMIT: the durability boundary is "visible through the filesystem to other readers", not power loss.
- LIMIT: idleness never strips ownership from a live owner (no handover timeout in this delivery).

## DOC

- `ipart7_getsummmatrix.html`, `ksdocument3d_partcollection.html`.

## History

`docs/decisions/journaling.md` - append atomicity, replay sharing, torn tail, same-operation-id,
terminal write, append IO failure, ownership model.
