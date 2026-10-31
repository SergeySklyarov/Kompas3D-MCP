using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rules of the event mark: what one burst of events is worth, and which of them are ours.</summary>
/// <remarks>INVARIANT: several events in a row are one bump; an event that arrives inside the own window is
/// dropped where it arrives, however late it is consumed. These rules are what keeps a foreign rebuild at
/// exactly +1 and our own at zero extra.
/// DOC: the v24 help mirror, page ksdocs3dnotify_events.html - the model raises Rebuild for both.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public class DocumentEventMarkTests
{
    [Fact]
    public void NoEvent_IsNothingToApply()
    {
        var mark = new DocumentEventMark();

        Assert.False(mark.TryTake());
    }

    [Fact]
    public void ForeignBurst_IsOneBump()
    {
        var mark = new DocumentEventMark();
        mark.Raise(nowMs: 5000);
        mark.Raise(nowMs: 5001);
        mark.Raise(nowMs: 5002);

        Assert.True(mark.TryTake());
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void OwnEvent_InsideTheWindow_IsDropped()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise(nowMs: 1200);

        Assert.False(mark.TryTake());
        Assert.Equal(0, mark.Pending);
    }

    [Fact]
    public void OwnEvent_TakenLongAfterTheWindow_IsStillOurs()
    {
        // The regression this rule exists for: our own rebuild is delivered within 400 ms of the mutation,
        // while the next command may arrive seconds later. Attribution must not depend on that gap.
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise(nowMs: 1400);

        Assert.False(mark.TryTake());
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void ForeignEvent_AfterTheWindow_IsApplied()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise(nowMs: 1000 + DocumentEventMark.OwnWindowMs);

        Assert.True(mark.TryTake());
    }

    [Fact]
    public void OpeningTheWindow_DropsAMarkRaisedBefore()
    {
        var mark = new DocumentEventMark();
        mark.Raise(nowMs: 500);
        mark.OpenOwnWindow(nowMs: 1000);

        Assert.Equal(0, mark.Pending);
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void LaterForeignBurst_AfterOwnMutation_IsAppliedOnce()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise(nowMs: 1100);

        Assert.False(mark.TryTake());

        mark.Raise(nowMs: 1000 + DocumentEventMark.OwnWindowMs + 1);
        Assert.True(mark.TryTake());
        Assert.False(mark.TryTake());
    }
}
