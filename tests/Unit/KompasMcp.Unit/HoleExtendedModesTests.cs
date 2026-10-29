using System.Text.Json.Nodes;
using KompasMcp.Contracts;
using KompasMcp.Domain.Geometry;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Extended hole modes (SM-07, order HOLE_SPHERE_CONIC): what acceptance cannot prove.</summary>
/// <remarks>INVARIANT, four claims: (1) both new modes are branched on CREATE and on EDIT, and the edit
/// switch has no catch-all write — a new mode written through a neighbour's method changes the wrong
/// feature; (2) the cone angle is validated as the FULL apex angle inside (0; 180) before COM, and the
/// diameter-driven <c>ksCNDiameter</c> is never offered; (3) the schema publishes the new mode values and
/// the field, else the Host rejects the call before COM; (4) a blind hole's mode is told by its BOTTOM.
/// LIMIT: by SOURCE — the adapter does not load without KOMPAS (interop is not copied).
/// History: docs/decisions/adapter-features.md#hole-extended-modes</remarks>
public sealed class HoleExtendedModesTests
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

    /// <summary>Adapter source without comment lines: the parse must not find a name in an explanation.</summary>
    private static string AdapterSource(string fileName) => string.Join("\n",
        File.ReadLines(Path.Combine(RepoRoot, "src", "KompasMcp.Api5Adapter", fileName))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void BothNewModes_AreBranchedOnCreateAndOnEdit()
    {
        var hole = AdapterSource("Api5Session.Hole.cs");

        foreach (var mode in new[] { "HoleMode.BlindSphere", "HoleMode.ThroughConic" })
        {
            Assert.Contains("case " + mode + ":", hole, StringComparison.Ordinal);
        }

        // The sphere reuses the blind route with its own bottom, and the cone has its own route: a copy
        // that forgot the bottom would build a flat hole under the sphere mode's name.
        Assert.Contains("sphericalBottom: true", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.TryCreateConic(", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.TryWriteConic(", hole, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditSwitch_HasNoCatchAllWrite()
    {
        // MEASURED defect class: with a `default:` that writes the countersink, a mode without its own
        // branch was written through a NEIGHBOUR's method — the numbers land in a foreign interface.
        var bridge = AdapterSource("Api7/Api7Bridge.cs");
        Assert.Contains("TryWriteConic(", bridge, StringComparison.Ordinal);

        var hole = AdapterSource("Api5Session.Hole.cs");
        var write = hole.IndexOf("switch (mode.Value)", StringComparison.Ordinal);
        Assert.True(write > 0, "выбор записи по режиму не найден — разбор устарел");
        var body = hole[write..];

        // Every write method is reached through its OWN case label, and the fallback refuses.
        foreach (var pair in new[]
                 {
                     ("HoleMode.BlindSphere", "TryWriteBlindFlat("),
                     ("HoleMode.ThroughConic", "TryWriteConic("),
                     ("HoleMode.ThroughCountersink", "TryWriteCountersink("),
                 })
        {
            var label = body.IndexOf("case " + pair.Item1 + ":", StringComparison.Ordinal);
            Assert.True(label > 0, $"в выборе записи нет ветки {pair.Item1}");
            Assert.True(body.IndexOf(pair.Item2, label, StringComparison.Ordinal) > label,
                $"метод {pair.Item2} не привязан к своей ветке {pair.Item1}");
        }

        Assert.Contains("Правка режима", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ConeAngle_IsValidatedAsTheFullApexAngle_InsideTheOpenInterval()
    {
        var hole = AdapterSource("Api5Session.Hole.cs");

        // The interval is the one the countersink angle was measured in: outside it no cone is built.
        Assert.Contains("RequireConeAngle(command.ConicAngleDeg", hole, StringComparison.Ordinal);
        Assert.Contains("angle > 0d && angle < 180d", hole, StringComparison.Ordinal);
        Assert.Contains("ПОЛНЫЙ угол", hole, StringComparison.Ordinal);

        // The angle is REQUIRED on create: a cone without an angle is not a cone.
        Assert.Contains("command.ConicAngleDeg!.Value", hole, StringComparison.Ordinal);

        // LIMIT: the diameter-driven type is never written — MEASURED that it reads the angle back as 0
        // and removes a plain cylinder.
        var bridge = AdapterSource("Api7/Api7Bridge.cs");
        Assert.Contains("ConicByAngle", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("ksCNDiameter", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchema_PublishesBothNewModesAndTheAngleField()
    {
        // MEASURED defect class (P4): a value in the contract but not in the schema — the Host with
        // additionalProperties:false rejects the call before COM.
        var holeSchema = ToolCatalog.All.Single(t => t.Name == "kompas_hole").InputSchema;
        var modes = ((holeSchema["properties"] as JsonObject)?["mode"] as JsonObject)?["enum"] as JsonArray
                    ?? throw new InvalidOperationException("у kompas_hole нет перечня режимов в схеме");
        var published = modes.Select(v => v?.GetValue<string>()).ToArray();

        Assert.Contains("blind_sphere", published);
        Assert.Contains("through_conic", published);
        Assert.Contains("blind_flat", published);
        Assert.Contains("through_counterbore", published);
        Assert.Contains("through_countersink", published);
        Assert.Contains("conic_angle_deg", (holeSchema["properties"] as JsonObject)!.Select(p => p.Key));

        var editSchema = ToolCatalog.All.Single(t => t.Name == "kompas_update_feature").InputSchema;
        Assert.Contains("conic_angle_deg", (editSchema["properties"] as JsonObject)!.Select(p => p.Key));

        // The contract enum and the published list must not drift apart: a mode in one and not the other
        // is either an unreachable mode or a call the Host refuses.
        var contractModes = Enum.GetNames<HoleMode>()
            .Select(n => string.Concat(n.Select((c, i) =>
                i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())))
            .ToArray();
        Assert.Equal(contractModes.OrderBy(m => m, StringComparer.Ordinal),
            published.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void TheCounterdrill_IsBranchedOnCreateAndOnEdit_WithItsOwnWrite()
    {
        var hole = AdapterSource("Api5Session.Hole.cs");
        Assert.Contains("case HoleMode.ThroughCounterdrill:", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.TryCreateCounterdrill(", hole, StringComparison.Ordinal);
        Assert.Contains("Api7Hole.TryWriteCounterdrill(", hole, StringComparison.Ordinal);

        // The mode is read back from the vendor name, not guessed: without this line an edit of a
        // counterdrill refuses as an unknown mode.
        Assert.Contains("\"ksHTCounterdrill\" => HoleMode.ThroughCounterdrill", hole, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCounterdrill_RefusesASecondDiameter_AndRequiresItsThreeNumbers()
    {
        // MEASURED: ICountersinkSpotfacingHoleParameters has ONE diameter, shared by the spotfacing and
        // the countersink. Accepting countersink_diameter_mm would pass off as applied a number the
        // kernel never reads, and a spotfacing no wider than the pilot degenerates to no cone at all.
        var hole = AdapterSource("Api5Session.Hole.cs");
        // The label appears in TWO switches (create and edit), so the slice is anchored on the
        // validation method itself, not on the first match: a slice taken by the label alone ended at
        // the create switch's own default and proved nothing about the refusals.
        var create = hole.IndexOf("private static void ValidateHoleMode", StringComparison.Ordinal);
        Assert.True(create > 0, "проверка режима при создании не найдена — разбор устарел");
        var end = hole.IndexOf("private static void RequireSpotfacing", create, StringComparison.Ordinal);
        var branch = end > create ? hole[create..end] : hole[create..];
        Assert.Contains("RequireSpotfacing(command, \"through_counterdrill\")", branch, StringComparison.Ordinal);
        Assert.Contains("RequireConeAngle(command.CountersinkAngleDeg", branch, StringComparison.Ordinal);
        Assert.Contains("countersink_diameter_mm", branch, StringComparison.Ordinal);
        Assert.Contains("ОДИН диаметр", branch, StringComparison.Ordinal);

        // The edit side refuses the foreign fields of the neighbouring modes, and names its own.
        Assert.Contains("\"through_counterdrill\" =>", hole, StringComparison.Ordinal);
        Assert.Contains("counterbore_diameter_mm, counterbore_depth_mm, countersink_angle_deg",
            hole, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchema_PublishesTheCounterdrillMode()
    {
        var holeSchema = ToolCatalog.All.Single(t => t.Name == "kompas_hole").InputSchema;
        var modes = ((holeSchema["properties"] as JsonObject)?["mode"] as JsonObject)?["enum"] as JsonArray
                    ?? throw new InvalidOperationException("у kompas_hole нет перечня режимов в схеме");
        Assert.Contains("through_counterdrill", modes.Select(v => v?.GetValue<string>()));
    }

    [Fact]
    public void ABlindHolesMode_IsToldByItsBottom()    {
        // INVARIANT: ksHTBase is shared by the flat and the spherical blind hole, so reading the type
        // alone would edit a spherical hole as a flat one and silently flatten it.
        var hole = AdapterSource("Api5Session.Hole.cs");
        var read = hole.IndexOf("private static HoleMode? HoleModeOfRead", StringComparison.Ordinal);
        Assert.True(read > 0, "разбор режима по прочитанному не найден — разбор устарел");
        var end = hole.IndexOf("private static void ValidateHoleEdit", read, StringComparison.Ordinal);
        var body = end > read ? hole[read..end] : hole[read..];
        Assert.Contains("SphereEndFace", body, StringComparison.Ordinal);
        Assert.Contains("HoleMode.BlindSphere", body, StringComparison.Ordinal);

        // The read publishes the bottom in a stable lowercase form for the client, next to the vendor one.
        Assert.Contains("\"ksEFSphere\" => \"sphere\"", hole, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroRemoval_IsRefusedByTheSharedPolicy_WhateverTheMode()
    {
        // The guard is not per mode: the policy takes numbers, so it covers both new modes by
        // construction. Substituted volumes: an unchanged body with unchanged face count.
        var noEffect = HoleEffectPolicy.Evaluate(0, 160000d, 160000d, 6d, 6d, declaredMismatch: false);
        Assert.Equal(HoleEffectVerdict.NoGeometryChange, noEffect.Verdict);
        Assert.Equal(ErrorCodes.NoGeometryChange, noEffect.RefusalErrorCode);

        // And it stands on the CREATION path for every mode, before any mode-specific branch.
        var hole = AdapterSource("Api5Session.Hole.cs");
        Assert.Contains("HoleEffectPolicy.Evaluate(", hole, StringComparison.Ordinal);
        Assert.Contains("HoleEffectPolicy.NoEffectDetailCode", hole, StringComparison.Ordinal);
    }
}
