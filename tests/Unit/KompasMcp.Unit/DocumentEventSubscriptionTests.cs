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

    [Fact]
    public void RefusedFindConnectionPoint_IsNoSubscriptionAndConservative()
    {
        var subscription = DocumentEventSubscription.TrySubscribe(
            new RefusingContainer(failOnFind: true), new DocumentEventMark(), out var error);

        Assert.Null(subscription);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(ExternalChangeDetection.Conservative, Api5Session.DetectionFor(subscription));
    }

    [Fact]
    public void RefusedAdvise_IsNoSubscriptionAndConservative()
    {
        var subscription = DocumentEventSubscription.TrySubscribe(
            new RefusingContainer(failOnFind: false), new DocumentEventMark(), out var error);

        Assert.Null(subscription);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(ExternalChangeDetection.Conservative, Api5Session.DetectionFor(subscription));
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

    /// <summary>The two places a real subscription fails: no connection point for the interface, and a
    /// point that refuses the sink.</summary>
    /// <remarks>ASSUMPTION: both failures reach the caller as <c>COMException</c>; the measured probe saw
    /// only the successful route, so the refusal shape is taken from COM rather than observed.
    /// History: docs/decisions/adapter-core.md#document-events</remarks>
    private sealed class RefusingContainer : IConnectionPointContainer
    {
        private readonly bool _failOnFind;

        public RefusingContainer(bool failOnFind) => _failOnFind = failOnFind;

        public void EnumConnectionPoints(out IntPtr enumerator) => enumerator = IntPtr.Zero;

        public void FindConnectionPoint(ref Guid riid, out IConnectionPoint point)
        {
            if (_failOnFind)
            {
                throw new COMException("no connection point for the interface");
            }

            point = new RefusingPoint();
        }
    }

    private sealed class RefusingPoint : IConnectionPoint
    {
        public void GetConnectionInterface(out Guid iid) => iid = Guid.Empty;

        public void GetConnectionPointContainer(out IntPtr container) => container = IntPtr.Zero;

        public void Advise(object sink, out int cookie) => throw new COMException("advise refused");

        public void Unadvise(int cookie)
        {
        }

        public void EnumConnections(out IntPtr enumerator) => enumerator = IntPtr.Zero;
    }
}
