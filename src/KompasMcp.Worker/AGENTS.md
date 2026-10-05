# Worker (KompasMcp.Worker)

The COM-owning process. It exists so a wedged KOMPAS call cannot take the MCP connection with it: the
Host keeps answering status while this process sits inside a call.

## Files

- `Program.cs` - pipe server, one Host connection per process, `Serve`/`ReadFramesAsync`.
- `CommandDispatcher.cs` - the only place a command reaches KOMPAS; control lane vs CAD lane.
- `BuildIdentity.cs` - which build of each assembly actually loaded (stale-copy detector).

## Invariants

- INVARIANT: every CAD command runs on ONE STA thread, one at a time, including reads.
- INVARIANT: `sys.ping` and `env.probe` answer on the control lane while the CAD lane is busy - that is
  how the Host tells "Worker dead" from "KOMPAS busy".
- INVARIANT: an expired command budget is answered `OUTCOME_UNKNOWN`; the process asks to be restarted.
- INVARIANT: an unexpected exception in a MUTATION is not a clean failure (`MutationCommands`).
- INVARIANT: every mutation answer carries the revision AFTER applying; the control copy is taken BEFORE.
- LIMIT: a command already on the STA lane cannot be cancelled - it runs to completion.

## History

`docs/decisions/worker-ipc.md` - build identity, payload contract, C1/C2 tagging, single connection.
