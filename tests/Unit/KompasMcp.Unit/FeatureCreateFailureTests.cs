using KompasMcp.Domain.Geometry;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The snapshot written when a solid feature's Create() answers false.</summary>
/// <remarks>WHY IT IS TESTED WITHOUT KOMPAS. The snapshot exists because such a refusal cannot be
/// reproduced on demand; the one part of it that must not fail silently is its SHAPE - the same keys on
/// every refusal, and an unread value named rather than dropped. That part is a pure assembly and can be
/// held here. The reads themselves need a live session and are covered by the acceptance row.
/// History: docs/decisions/tests.md#feature-create-failure-snapshot</remarks>
public class FeatureCreateFailureTests
{
    private static FeatureCreateFailure.Observation Full() => new(
        Operation: "cut",
        DirectionType: 0,
        EndCondition: "blind (0)",
        DepthMm: 2.5288,
        DraftMm: null,
        TargetBodyRef: "body:90af853e18974fb198ed4457addd7cd0",
        SketchPlane: "XY +3.74 mm",
        FeatureCount: 5,
        BodyCount: 2,
        SketchState: "under_defined (ksStateUnderConstrained, raw=2)",
        SketchStateUnavailable: null,
        SketchProfileEntities: 16,
        SketchProfileAreaMm2: 1.213102401338439,
        SketchProfileAreaUnavailable: null,
        SessionSeconds: 412.5,
        OperationOrdinal: 37,
        CreateFalseCount: 1);

    private static FeatureCreateFailure.Observation NothingRead() => new(
        Operation: "cut",
        DirectionType: 0,
        EndCondition: "blind (0)",
        DepthMm: null,
        DraftMm: null,
        TargetBodyRef: null,
        SketchPlane: null,
        FeatureCount: null,
        BodyCount: null,
        SketchState: null,
        SketchStateUnavailable: "не прочитано: мост API7 не построился",
        SketchProfileEntities: null,
        SketchProfileAreaMm2: null,
        SketchProfileAreaUnavailable: "профиль не замкнут",
        SessionSeconds: 1.0,
        OperationOrdinal: 1,
        CreateFalseCount: 1);

    [Fact]
    public void SameKeysOnEveryRefusal()
    {
        // INVARIANT: an absent key is indistinguishable from "we forgot to fill it", which is the loss
        // this snapshot exists to end. The two observations below differ in every nullable field.
        var full = FeatureCreateFailure.Snapshot(Full());
        var bare = FeatureCreateFailure.Snapshot(NothingRead());

        Assert.Equal(full.Keys.OrderBy(k => k), bare.Keys.OrderBy(k => k));
        Assert.Contains("feature_count", full.Keys);
        Assert.Contains("session_seconds", full.Keys);
        Assert.Contains("operation_ordinal", full.Keys);
        Assert.Contains("create_false_count", full.Keys);
        Assert.Contains("sketch_state", full.Keys);
        Assert.Contains("sketch_plane", full.Keys);
    }

    [Fact]
    public void TheSnapshotIsTheAssembledSet_NotTheRawObservation()
    {
        // MEASURED: the first revision of the adapter passed the Observation RECORD into the refusal's
        // details instead of Snapshot(...). Every value was then null or raw and two extra keys appeared
        // (`sketch_state_unavailable`, `sketch_profile_area_unavailable`), so "unread" and "not asked
        // for" merged again. The acceptance row caught it; this test keeps it caught without KOMPAS.
        var keys = FeatureCreateFailure.Snapshot(Full()).Keys.OrderBy(k => k).ToArray();
        Assert.Equal(
            new[]
            {
                "body_count", "create_false_count", "depth_mm", "direction_type", "draft_mm",
                "end_condition", "feature_count", "operation", "operation_ordinal", "session_seconds",
                "sketch_plane", "sketch_profile_area_mm2", "sketch_profile_entities", "sketch_state",
                "target_body_ref",
            },
            keys);
        Assert.DoesNotContain("sketch_state_unavailable", keys);
        Assert.DoesNotContain("sketch_profile_area_unavailable", keys);
    }

    [Fact]
    public void UnreadValuesAreNamed_NotDroppedAndNotZero()
    {
        var bare = FeatureCreateFailure.Snapshot(NothingRead());

        // "0 features" and "the collection did not answer" are different claims; publishing 0 for both
        // would make an unread count look measured.
        var featureCount = Assert.IsType<string>(bare["feature_count"]);
        Assert.Contains("не прочитано", featureCount);
        Assert.DoesNotContain("0", featureCount);

        Assert.Contains("не прочитано", Assert.IsType<string>(bare["sketch_state"]));
        // "not specified" and "not read" are different claims: the target body was not asked for, and
        // wording it as an unread value would blame a route that was never called.
        Assert.Equal("не задано: тело не выбиралось", bare["target_body_ref"]);
        Assert.Contains("не прочитан", Assert.IsType<string>(bare["sketch_plane"]));
        // The caller's own reason survives verbatim: a generic wording would hide which route failed.
        Assert.Equal("профиль не замкнут", bare["sketch_profile_area_mm2"]);
    }

    [Fact]
    public void ReadValuesSurviveWithTheirNumbers()
    {
        var full = FeatureCreateFailure.Snapshot(Full());

        Assert.Equal("cut", full["operation"]);
        Assert.Equal(0, full["direction_type"]);
        Assert.Equal("blind (0)", full["end_condition"]);
        Assert.Equal(2.5288, full["depth_mm"]);
        Assert.Equal(5, full["feature_count"]);
        Assert.Equal(2, full["body_count"]);
        Assert.Equal(16, full["sketch_profile_entities"]);
        Assert.Equal(1.213102401338439, full["sketch_profile_area_mm2"]);
        Assert.Equal("XY +3.74 mm", full["sketch_plane"]);
        Assert.Equal(37, full["operation_ordinal"]);
        Assert.Equal(1, full["create_false_count"]);
        Assert.Equal(412.5, full["session_seconds"]);
    }

    [Fact]
    public void ThroughModeCarriesNoDepth()
    {
        // A through cut discards the depth in the solver, so a snapshot that reported the field as "not
        // read" would blame the instrument for a value the mode does not have.
        var through = FeatureCreateFailure.Snapshot(
            Full() with { EndCondition = "through (1)", DepthMm = null });
        Assert.Contains("не задано", Assert.IsType<string>(through["depth_mm"]));
    }
}
