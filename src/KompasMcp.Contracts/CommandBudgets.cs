namespace KompasMcp.Contracts.Ipc;

/// <summary>SINGLE command-budget table: one for the Host and Worker, not two that drift apart.</summary>
/// <remarks>
/// <para>INVARIANT: the Host budget is the Worker budget plus <see cref="HostMarginMs"/>. The Host is
/// responsible for delivering the frame, not for the COM call, so it must wait LONGER; a smaller Host
/// budget turns every long but normal call into "outcome unknown" plus a Worker restart.
/// History: docs/decisions/contracts.md#budgets-single-table</para>
/// </remarks>
public static class CommandBudgets
{
    /// <summary>Default budget for a command not named in the table.</summary>
    public const int DefaultMs = 120_000;

    /// <summary>How much longer than the Worker budget the Host waits. Named as a number, not a "small
    /// margin": the margin IS the difference between "the Host saw the Worker time out" and "the Host gave
    /// up first".</summary>
    public const int HostMarginMs = 30_000;

    /// <summary>Worker-side command budget (STA lane), milliseconds.</summary>
    public static int WorkerBudgetMs(string command) => command switch
    {
        // The probe can enumerate the ROT and the process list: fast, but not free.
        WorkerCommands.EnvironmentProbe => 10_000,
        WorkerCommands.Ping => 2_000,
        WorkerCommands.Connect => 180_000,
        WorkerCommands.ExportStep or WorkerCommands.ImportStep => 300_000,
        // A raster snapshot renders the document and (in file mode) writes a file: longer than a read,
        // shorter than a converter. The budget is a number, not inherited from the default: a
        // high-resolution snapshot measured in seconds (probe P6), and hitting the common budget would
        // look like a product failure.
        WorkerCommands.ExportImage => 240_000,
        // A native hole takes the API7 route (bridge + TransferInterface + RebuildModel): longer than a
        // pure API5 mutation, so the budget is above the default, not guessed.
        WorkerCommands.Hole => 240_000,
        // B3 body operations take the same API7 route plus re-reading every body of the document after the
        // rebuild, so the budget equals the hole's.
        WorkerCommands.SolidBoolean or WorkerCommands.SolidSplit
            or WorkerCommands.SolidCutByPlane or WorkerCommands.SolidReposition => 240_000,
        // B5: kinematics and shell take the API5 route but both rebuild the document and re-read the bodies
        // after the rebuild; sections take the API7 bridge (TransferInterface per section) plus Rebuild.
        WorkerCommands.Sweep or WorkerCommands.Loft or WorkerCommands.Shell => 240_000,
        // Auxiliary geometry takes the API7 route (bridge + QI(IAuxiliaryGeomContainer) + Rebuild), and the
        // enumeration reads the collection with one COM call per element.
        WorkerCommands.CreateAuxGeometry or WorkerCommands.ListAuxGeometry => 240_000,
        WorkerCommands.UpdatePlane => 240_000,
        // Changing a sketch support rebuilds the dependent body: the measured application step
        // (sketch.Update()) is included in this budget.
        WorkerCommands.SetSketchPlane => 240_000,
        WorkerCommands.ListSketchEntities or WorkerCommands.EditSketchEntity => 240_000,
        // Assembly: inserting a component reads a file from disk and rebuilds the document, and the
        // structure enumeration makes one COM call per component.
        WorkerCommands.InsertComponent or WorkerCommands.ReplaceComponent => 240_000,
        WorkerCommands.ListComponents or WorkerCommands.SetComponentPlacement
            or WorkerCommands.CheckComponentLinks => 240_000,
        // A mate rebuilds the assembly (Update() + RebuildDocument), and the enumeration reads each mate
        // object through two interfaces.
        WorkerCommands.ListMates or WorkerCommands.CreateMate or WorkerCommands.SetMateParameter
            or WorkerCommands.SetMateFixed or WorkerCommands.DeleteMate => 240_000,
        _ => DefaultMs,
    };

    /// <summary>How long the Host waits for a command's answer: the Worker budget plus
    /// <see cref="HostMarginMs"/>.</summary>
    public static int HostBudgetMs(string command) => WorkerBudgetMs(command) + HostMarginMs;
}
