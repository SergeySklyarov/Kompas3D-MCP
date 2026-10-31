using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rules of the event mark: what one burst of events is worth, and which of them are ours.</summary>
/// <remarks>INVARIANT: several events in a row are one bump; an event inside the own window is consumed and
/// dropped. These rules are what keeps a foreign rebuild at exactly +1 and our own at zero extra.
/// DOC: the v24 help mirror, page ksdocs3dnotify_events.html - the model raises Rebuild for both.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public class DocumentEventMarkTests
{
    [Fact]
    public void NoEvent_IsNothingToApply()
    {
        var mark = new DocumentEventMark();

        Assert.False(mark.TryTake(nowMs: 5000));
    }

    [Fact]
    public void ForeignBurst_IsOneBump()
    {
        var mark = new DocumentEventMark();
        mark.Raise();
        mark.Raise();
        mark.Raise();

        Assert.True(mark.TryTake(nowMs: 5000));
        Assert.False(mark.TryTake(nowMs: 5001));
    }

    [Fact]
    public void OwnEvent_InsideTheWindow_IsDropped()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise();

        Assert.False(mark.TryTake(nowMs: 1200));

        // Consumed, not kept: the next command must not see the same event again.
        Assert.False(mark.TryTake(nowMs: 1300));
        Assert.Equal(0, mark.Pending);
    }

    [Fact]
    public void ForeignEvent_AfterTheWindow_IsApplied()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise();

        Assert.True(mark.TryTake(nowMs: 1000 + DocumentEventMark.OwnWindowMs));
    }

    [Fact]
    public void OpeningTheWindow_DropsAMarkRaisedBefore()
    {
        var mark = new DocumentEventMark();
        mark.Raise();
        mark.OpenOwnWindow(nowMs: 1000);

        Assert.Equal(0, mark.Pending);
        Assert.False(mark.TryTake(nowMs: 5000));
    }

    [Fact]
    public void LaterForeignBurst_AfterOwnMutation_IsAppliedOnce()
    {
        var mark = new DocumentEventMark();
        mark.OpenOwnWindow(nowMs: 1000);
        mark.Raise();

        Assert.False(mark.TryTake(nowMs: 1100));

        mark.Raise();
        Assert.True(mark.TryTake(nowMs: 1000 + DocumentEventMark.OwnWindowMs + 1));
        Assert.False(mark.TryTake(nowMs: 1000 + DocumentEventMark.OwnWindowMs + 2));
    }
}
