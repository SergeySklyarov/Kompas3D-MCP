namespace KompasMcp.Contracts.Ipc;

/// <summary>Which lane of the Worker answers a command.</summary>
public enum WorkerLane
{
    /// <summary>Answered OFF the STA thread. INVARIANT: a control command must stay answerable while the
    /// CAD lane is busy, because that is exactly how the Host tells "KOMPAS is busy" from "the Worker is
    /// dead".</summary>
    Control,

    /// <summary>Answered on the single STA thread that owns every COM pointer, one command at a
    /// time.</summary>
    Cad,
}

/// <summary>What a command does. The kind decides how an unobserved outcome is reported: only a model
/// mutation can leave the model in a state nobody has read.</summary>
public enum WorkerAction
{
    /// <summary>Reads the session, the model or a document; changes nothing.</summary>
    Read,

    /// <summary>Writes a FILE and leaves the model untouched. A lost connection during one of them leaves
    /// nothing to reconcile, so the honest answer is the refusal a read gets, not "the outcome is
    /// unknown". LIMIT, NAMED: <c>doc.save</c> is deliberately NOT here - it rewrites the document the
    /// model is reconciled against, so it keeps the mutation kind.
    /// History: docs/decisions/adapter-core.md#disconnect-exports</summary>
    FileWrite,

    /// <summary>Can leave the model in an unknown state.</summary>
    ModelMutation,
}

/// <summary>One row of the single Worker-command table.</summary>
/// <param name="Command">The command name on the Host→Worker pipe (a <see cref="WorkerCommands"/>
/// constant).</param>
/// <param name="Lane">Which thread answers it.</param>
/// <param name="Action">What it does to the model and to the file system.</param>
/// <param name="WorkerBudgetMs">How long the Worker lets it run before declaring the outcome
/// unknown.</param>
public sealed record WorkerCommandFacts(string Command, WorkerLane Lane, WorkerAction Action, int WorkerBudgetMs);

/// <summary>SINGLE table of Worker-command facts: lane, action kind and budget, one source for the Host
/// and the Worker.</summary> <remarks>
/// <para>INVARIANT: nothing else may restate these facts. With a second table the Host waits less than
/// the Worker and declares OUTCOME_UNKNOWN, restarting a Worker that is still running the command.
/// History: docs/decisions/contracts.md#budgets-single-table</para>
/// <para>INVARIANT: a command absent from the table is answered as a CAD-lane read with the default
/// budget, which is what both former tables did with an unknown name.</para>
/// </remarks>
public static class WorkerCommandTable
{
    /// <summary>The facts of a command the table does not name. Its shape is the former behaviour of both
    /// tables for an unknown name, not a new rule.</summary>
    private static readonly WorkerCommandFacts UnknownCommand =
        new(string.Empty, WorkerLane.Cad, WorkerAction.Read, CommandBudgets.DefaultMs);

    private static readonly IReadOnlyList<WorkerCommandFacts> Rows = Build();

    private static readonly IReadOnlyDictionary<string, WorkerCommandFacts> ByCommand =
        Rows.ToDictionary(row => row.Command, StringComparer.Ordinal);

    /// <summary>Every row, in declaration order.</summary>
    public static IReadOnlyList<WorkerCommandFacts> All => Rows;

    /// <summary>The row of a command, or the unknown-command fallback.</summary>
    public static WorkerCommandFacts Of(string command) =>
        ByCommand.TryGetValue(command, out var facts) ? facts : UnknownCommand;

    /// <summary>Whether the command is answered off the STA thread.</summary>
    public static bool IsControlLane(string command) => Of(command).Lane == WorkerLane.Control;

    /// <summary>The Worker budget of a command, milliseconds.</summary>
    public static int BudgetMs(string command) => Of(command).WorkerBudgetMs;

    /// <summary>Whether a command can leave the MODEL in an unknown state. A file-writing read cannot: it
    /// writes a file, not the model.</summary>
    public static bool ChangesModel(string command) => Of(command).Action == WorkerAction.ModelMutation;

    /// <summary>Whether a command's answer must not be built on a swallowed connection loss.</summary>
    /// <remarks>Control-lane commands are excluded: they never touch KOMPAS, and `kompas_health` must keep
    /// answering when the CAD session is gone - that is exactly when it is asked. Excluded BY NAME:
    /// `session.inventory`, whose consumer (the release guard) already refuses on an unread inventory, so
    /// routing it through the latch would change the refusal code of `kompas_release_session`;
    /// `app.disconnect`, which TEARS OUR OBJECTS DOWN, so a disconnect-class HRESULT there is the expected
    /// consequence of our own action, not a loss; `sys.shutdown`, which ends the process that would answer.
    /// History: docs/decisions/adapter-core.md#disconnect-latch</remarks>
    public static bool MustNotAnswerAcrossALoss(string command) =>
        !ChangesModel(command)
        && !IsControlLane(command)
        && command != WorkerCommands.SessionInventory
        && command != WorkerCommands.Disconnect
        && command != WorkerCommands.Shutdown;

    /// <summary>Whether the observation window is open while this command runs. Every CAD-lane command
    /// except the teardown opens it; control-lane commands never do, because they run OFF the STA thread
    /// (the ROT enumeration binds foreign objects there, and a dead entry of another instance is not our
    /// loss). History: docs/decisions/adapter-core.md#disconnect-latch</summary>
    public static bool ObservesConnectionLoss(string command) =>
        !IsControlLane(command) && command != WorkerCommands.Disconnect;

    private static IReadOnlyList<WorkerCommandFacts> Build()
    {
        var rows = new List<WorkerCommandFacts>();

        void Control(string command, WorkerAction action, int budgetMs = 0) =>
            rows.Add(new WorkerCommandFacts(command, WorkerLane.Control, action, Budget(budgetMs)));

        void Cad(string command, WorkerAction action, int budgetMs = 0) =>
            rows.Add(new WorkerCommandFacts(command, WorkerLane.Cad, action, Budget(budgetMs)));

        // ---- control lane ----
        // Not free: the probe enumerates the ROT and the process list.
        Control(WorkerCommands.Ping, WorkerAction.Read, 2_000);
        Control(WorkerCommands.EnvironmentProbe, WorkerAction.Read, 10_000);

        // ---- session and document lifecycle ----
        // Launching or attaching an instance starts a process and waits for its answer.
        Cad(WorkerCommands.Connect, WorkerAction.ModelMutation, 180_000);
        Cad(WorkerCommands.Disconnect, WorkerAction.ModelMutation);
        Cad(WorkerCommands.SessionInventory, WorkerAction.Read);
        Cad(WorkerCommands.ListDocuments, WorkerAction.Read);
        Cad(WorkerCommands.CreateDocument, WorkerAction.ModelMutation);
        Cad(WorkerCommands.OpenDocument, WorkerAction.ModelMutation);
        Cad(WorkerCommands.GetContext, WorkerAction.Read);
        Cad(WorkerCommands.SaveDocument, WorkerAction.ModelMutation);
        Cad(WorkerCommands.CloseDocument, WorkerAction.ModelMutation);
        Cad(WorkerCommands.Shutdown, WorkerAction.Read);

        // ---- reads of the model ----
        Cad(WorkerCommands.ListFeatures, WorkerAction.Read);
        Cad(WorkerCommands.ListSketches, WorkerAction.Read);
        Cad(WorkerCommands.ListBodies, WorkerAction.Read);
        Cad(WorkerCommands.Measure, WorkerAction.Read);
        Cad(WorkerCommands.ResolveSelection, WorkerAction.Read);
        Cad(WorkerCommands.ReadTopology, WorkerAction.Read);
        Cad(WorkerCommands.SketchStatus, WorkerAction.Read);
        Cad(WorkerCommands.GetFeature, WorkerAction.Read);
        Cad(WorkerCommands.PatternRead, WorkerAction.Read);
        Cad(WorkerCommands.UnitProbe, WorkerAction.ModelMutation);
        Cad(WorkerCommands.Rebuild, WorkerAction.ModelMutation);

        // ---- sketches ----
        Cad(WorkerCommands.CreateSketch, WorkerAction.ModelMutation);
        Cad(WorkerCommands.EditSketch, WorkerAction.ModelMutation);
        Cad(WorkerCommands.FinishSketch, WorkerAction.ModelMutation);
        // Changing a sketch support rebuilds the dependent body: the measured apply step
        // (sketch.Update()) is included in this budget.
        Cad(WorkerCommands.SetSketchPlane, WorkerAction.ModelMutation, 240_000);
        // An entity edit opens an edit session and re-reads the entity afterwards; the enumeration reads
        // the drawing container object by object.
        Cad(WorkerCommands.ListSketchEntities, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.EditSketchEntity, WorkerAction.ModelMutation, 240_000);

        // ---- sketch constraints and dimensions ----
        // The API7 route plus an edit session (BeginEdit/EndEdit) and a re-read of the object's
        // constraint list inside it; enumeration makes one COM call per object.
        Cad(WorkerCommands.CreateSketchConstraint, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ListSketchConstraints, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.DeleteSketchConstraint, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.CreateSketchDimension, WorkerAction.ModelMutation, 240_000);

        // ---- features ----
        Cad(WorkerCommands.Extrude, WorkerAction.ModelMutation);
        Cad(WorkerCommands.Fillet, WorkerAction.ModelMutation);
        Cad(WorkerCommands.Chamfer, WorkerAction.ModelMutation);
        // A native hole takes the API7 route (bridge + TransferInterface + RebuildModel): longer than a
        // pure API5 mutation, so the budget is above the default, not guessed.
        Cad(WorkerCommands.Hole, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.Rotated, WorkerAction.ModelMutation);
        // Kinematics and shell take the API5 route but both rebuild the document and re-read the bodies
        // after the rebuild; sections take the API7 bridge (TransferInterface per section) plus Rebuild.
        Cad(WorkerCommands.Sweep, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.Loft, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.Shell, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.UpdateFeature, WorkerAction.ModelMutation);
        Cad(WorkerCommands.SuppressFeature, WorkerAction.ModelMutation);
        Cad(WorkerCommands.DeleteFeature, WorkerAction.ModelMutation);
        Cad(WorkerCommands.PatternGrid, WorkerAction.ModelMutation);
        Cad(WorkerCommands.PatternCircular, WorkerAction.ModelMutation);
        Cad(WorkerCommands.PatternMirror, WorkerAction.ModelMutation);

        // ---- body operations ----
        // The same API7 route as the hole plus a re-read of every body of the document after the
        // rebuild, so the budget equals the hole's.
        Cad(WorkerCommands.SolidBoolean, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SolidSplit, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SolidCutByPlane, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SolidReposition, WorkerAction.ModelMutation, 240_000);

        // ---- auxiliary geometry ----
        // The API7 route (bridge + QI(IAuxiliaryGeomContainer) + Rebuild); the enumeration reads the
        // collection with one COM call per element.
        Cad(WorkerCommands.CreateAuxGeometry, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ListAuxGeometry, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.UpdatePlane, WorkerAction.ModelMutation, 240_000);

        // ---- assembly ----
        // Inserting a component reads a file from disk and rebuilds the document; the structure
        // enumeration makes one COM call per component.
        Cad(WorkerCommands.ListComponents, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.InsertComponent, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetComponentPlacement, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ReplaceComponent, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.CheckComponentLinks, WorkerAction.Read, 240_000);
        // Deleting a component walks the mate collection AND asks the kernel (ObjectConstraints) first,
        // then re-reads the structure: one COM call per mate plus the rebuild.
        Cad(WorkerCommands.DeleteComponent, WorkerAction.ModelMutation, 240_000);
        // Interference makes one COM call per body pair, plus a second call per intersecting pair when
        // faces are asked for.
        Cad(WorkerCommands.CheckInterference, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.MeasureGap, WorkerAction.Read, 240_000);

        // ---- mates ----
        // A mate rebuilds the assembly (Update() + RebuildDocument), and the enumeration reads each mate
        // object through two interfaces.
        Cad(WorkerCommands.ListMates, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.CreateMate, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetMateParameter, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetMateFixed, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.DeleteMate, WorkerAction.ModelMutation, 240_000);

        // ---- drawings ----
        // Views and export go through the API7 bridge (TransferInterface + AddStandartViews or the
        // converter), which is longer than a pure API5 call; dimensions and the stamp are API7 too.
        Cad(WorkerCommands.CreateDrawingViews, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.CreateSectionView, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.GetSectionView, WorkerAction.Read, 60_000);
        Cad(WorkerCommands.DeleteSectionView, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ListDrawingViews, WorkerAction.Read);
        Cad(WorkerCommands.ListDimensions, WorkerAction.Read);
        Cad(WorkerCommands.AddDimension, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetTitleBlock, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ExportDrawing, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetTechnicalDemand, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.EditView, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.GetTitleBlock, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.GetTechnicalDemand, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.RebuildDrawingViews, WorkerAction.ModelMutation);

        // ---- variables and material ----
        // A write goes through Set + RebuildModel + a re-read of the collection; the material write goes
        // through SetMaterial + Update + a re-read of name and density. Creation takes the API7 bridge
        // plus RebuildDocument; binding re-reads the feature, its collection and the model volume.
        Cad(WorkerCommands.ListVariables, WorkerAction.Read);
        Cad(WorkerCommands.SetVariableValue, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.SetVariableExpression, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.GetMaterial, WorkerAction.Read);
        Cad(WorkerCommands.SetMaterial, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.CreateVariable, WorkerAction.ModelMutation, 240_000);
        Cad(WorkerCommands.ListFeatureParameters, WorkerAction.Read, 240_000);
        Cad(WorkerCommands.BindParameter, WorkerAction.ModelMutation, 240_000);

        // ---- file exchange ----
        // A converter reads or writes a whole document through a file: the longest route in the product.
        // STL tessellates the model, writes the file and then reads that file back in full.
        Cad(WorkerCommands.ExportStep, WorkerAction.FileWrite, 300_000);
        Cad(WorkerCommands.ImportStep, WorkerAction.ModelMutation, 300_000);
        Cad(WorkerCommands.ExportStl, WorkerAction.FileWrite, 300_000);
        // A raster snapshot renders the document and (in file mode) writes a file: longer than a read,
        // shorter than a converter. The budget is a number, not the default: a high-resolution snapshot
        // measured in seconds, and hitting the common budget would look like a product failure.
        Cad(WorkerCommands.ExportImage, WorkerAction.FileWrite, 240_000);

        return rows;

        static int Budget(int budgetMs) => budgetMs == 0 ? CommandBudgets.DefaultMs : budgetMs;
    }
}
