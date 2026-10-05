# Host (KompasMcp.Host)

MCP-facing process. It owns the transport, the tool catalog, the mutation journal and the single
owner of the CAD session; it never touches COM (ADR-001) - all CAD work goes to the Worker over IPC.

## Files

- `Program.cs` - startup, transport wiring, `JOURNAL_UNAVAILABLE` naming.
- `HostSession.cs` - one MCP session: ownership, journal, tool dispatch.
- `ToolInvoker.cs` - validates arguments, opens the journal, calls the Worker, records the outcome.
- `WorkerSupervisor.cs` - starts/stops the Worker, classifies "Worker dead" vs "KOMPAS busy".
- `Catalog/ToolCatalog.cs` - the published tool list, schemas and the `Mutation(...)` mirror.
- `ReleaseGuard.cs` - the release decision (`acknowledge_unknown_document_state`).
- `HostOptions.cs`, `StderrWriter.cs`, `HostLogProvider.cs` - configuration and logging.

## Invariants

- INVARIANT: no COM in the Host - every CAD call crosses the IPC boundary.
- INVARIANT: the intent record is durable before the command leaves for the Worker.
- INVARIANT: the tool catalog is the single source of truth for "which commands mutate".
- INVARIANT: a refusal that hits a customer prohibition is NAMED, not worked around.
- LIMIT: the unknown-document-state flag lives in the Host's MEMORY and is lost on Host restart.
- TEST: `ToolCatalogTests`, `HostSessionLifecycleTests`, `ReleaseGuardTests`.

## DOC

- `ksdocument3d_methods.html`, `ksdocument3d_properties.html` - no documented "document changed" flag.

## History

`docs/decisions/host.md` - catalog entries, transport startup, ownership handoff, release guard,
assembly-domain status.
