using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The service grant a scenario's own call may declare: one FILE, by equality only.</summary>
/// <remarks>INVARIANT: a neighbour in the same folder, a sibling whose name continues the granted one and a
/// file inside a granted folder all stay denied — the grant exists for one copy file, not for a folder.
/// History: docs/decisions/scenario.md#checkpoint-service-path</remarks>
public class ServicePathGrantTests
{
    private static readonly string Copy = @"C:\install\control-copies\scenario\op-r1.m3d";

    [Fact]
    public void TheGrantedFile_IsAdmitted()
    {
        Assert.True(ToolInvoker.IsServicePath(Copy, Copy));
    }

    [Fact]
    public void NeighbourInTheSameFolder_IsDenied()
    {
        var neighbour = Path.Combine(Path.GetDirectoryName(Copy)!, "other.m3d");

        Assert.False(ToolInvoker.IsServicePath(neighbour, Copy));
    }

    [Fact]
    public void SiblingWhoseNameContinuesTheGrantedOne_IsDenied()
    {
        Assert.False(ToolInvoker.IsServicePath(Copy + "0", Copy));
    }

    [Fact]
    public void FileInsideAGrantedFolder_IsDenied()
    {
        var inside = Path.Combine(Copy, "files", "001-part.m3d");

        Assert.False(ToolInvoker.IsServicePath(inside, Copy));
    }

    [Fact]
    public void NoGrant_AdmitsNothing()
    {
        Assert.False(ToolInvoker.IsServicePath(Copy, null));
    }
}
