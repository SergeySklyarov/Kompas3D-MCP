using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rules of the event mark: what one burst of events is worth, and which of them are ours.</summary>
/// <remarks>INVARIANT: several events in a row are one bump; an event of our own burst is dropped where it
/// arrives, however late it is consumed. These rules are what keeps a foreign rebuild at exactly +1 and our
/// own at zero extra.
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
        mark.Raise();
        mark.Raise();
        mark.Raise();

        Assert.True(mark.TryTake());
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void OwnBurst_IsDropped()
    {
        var mark = new DocumentEventMark();
        mark.ExpectOwnBurst();
        mark.Raise();
        mark.Raise();

        Assert.False(mark.TryTake());
        Assert.Equal(0, mark.Pending);
    }

    [Fact]
    public void OwnEvent_ConsumedSecondsLater_IsStillOurs()
    {
        // The regression this rule exists for: our own rebuild is delivered in the gap after the command,
        // while the next command may arrive seconds later. Attribution must not depend on that gap.
        var mark = new DocumentEventMark();
        mark.ExpectOwnBurst();

        Assert.False(mark.TryTake());
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void EventBeyondTheOwnBurst_IsApplied()
    {
        // Measured: one own action raises one or two events. A third one is not ours.
        var mark = new DocumentEventMark();
        mark.ExpectOwnBurst();
        mark.Raise();
        mark.Raise();
        mark.Raise();

        Assert.True(mark.TryTake());
    }

    [Fact]
    public void ExpectingABurst_DropsAMarkRaisedBefore()
    {
        var mark = new DocumentEventMark();
        mark.Raise();
        mark.ExpectOwnBurst();

        Assert.Equal(0, mark.Pending);
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void LaterForeignBurst_AfterOwnMutation_IsAppliedOnce()
    {
        var mark = new DocumentEventMark();
        mark.ExpectOwnBurst();
        mark.Raise();

        Assert.False(mark.TryTake());

        mark.Raise();
        Assert.True(mark.TryTake());
        Assert.False(mark.TryTake());
    }

    [Fact]
    public void TakingAMark_EndsTheBurst()
    {
        // A burst that raised no event must not swallow a change that comes after the next command started.
        var mark = new DocumentEventMark();
        mark.ExpectOwnBurst();

        Assert.False(mark.TryTake());

        mark.Raise();
        Assert.True(mark.TryTake());
    }
}
