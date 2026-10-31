using System.Runtime.InteropServices;
using KompasMcp.Api5Adapter;
using KompasMcp.Api5Adapter.Com;
using KompasMcp.Contracts;
using KompasMcp.Domain.Documents;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>Rules of the subscription itself: what a failed one leaves behind, and how often it is
/// released.</summary>
/// <remarks>INVARIANT: a document without a subscription answers <c>conservative</c> and still opens; a
/// subscription is released exactly once, however many times its owner lets go of it.
/// DOC: the v24 help mirror, page <c>eventscommon.html</c> - releasing the events is the subscriber's job.
/// History: docs/decisions/adapter-core.md#document-events</remarks>
public class DocumentEventSubscriptionTests
{
    [Fact]
    public void NoSource_IsNoSubscriptionAndConservative()
    {
        var subscription = DocumentEventSubscription.TrySubscribe(null, new DocumentEventMark(), out var error);

        Assert.Null(subscription);
        Assert.Equal("notify_source_null", error);
        Assert.Equal(ExternalChangeDetection.Conservative, Api5Session.DetectionFor(subscription));
    }

    [Fact]
    public void SourceWithoutConnectionPoint_IsNoSubscriptionAndConservative()
    {
        var subscription = DocumentEventSubscription.TrySubscribe(
            new object(), new DocumentEventMark(), out var error);

        Assert.Null(subscription);
        Assert.Equal("connection_point_container_absent", error);
        Assert.Equal(ExternalChangeDetection.Conservative, Api5Session.DetectionFor(subscription));
    }

    [Fact]
    public void SecondDispose_ReleasesOnce()
    {
        var release = new CountingRelease();
        var subscription = DocumentEventSubscription.Create(release, cookie: 7);

        subscription.Dispose();
        subscription.Dispose();

        Assert.Equal(1, release.UnadviseCalls);
        Assert.Equal(1, release.ReleaseCalls);
    }

    [Fact]
    public void RefusedUnadvise_IsJournaledAndStillReleased()
    {
        var release = new CountingRelease { Refuse = true };
        var journal = new List<string>();
        var subscription = DocumentEventSubscription.Create(release, cookie: 7, journal.Add);

        subscription.Dispose();

        Assert.Equal(1, release.UnadviseCalls);
        Assert.Equal(1, release.ReleaseCalls);
        Assert.Contains(journal, line => line.StartsWith("unadvise refused", StringComparison.Ordinal));
    }

    private sealed class CountingRelease : IAdviseRelease
    {
        public int UnadviseCalls;
        public int ReleaseCalls;
        public bool Refuse;

        public void Unadvise(int cookie)
        {
            UnadviseCalls++;
            if (Refuse)
            {
                throw new COMException("refused");
            }
        }

        public void Release() => ReleaseCalls++;
    }
}
