using KompasMcp.Host;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>The Host command line refuses a flag that needs a value and has none.</summary>
/// <remarks>TEST: <c>--emit-schemas</c> alone used to start the server and answer 0 with no schema
/// written, so the bad call looked successful. History: docs/decisions/host.md#cli-args</remarks>
public class HostCliArgumentTests
{
    [Fact]
    public void LastArgumentWithoutValue_IsRefused()
    {
        var refusal = Assert.Throws<ArgumentException>(
            () => Program.Argument(["--emit-schemas"], "--emit-schemas"));

        Assert.Contains("--emit-schemas", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NextArgumentBeingAFlag_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => Program.Argument(["--emit-schemas", "--print-schemas"], "--emit-schemas"));
        Assert.Throws<ArgumentException>(
            () => Program.Argument(["--config", "--emit-schemas", "out"], "--config"));
    }

    [Fact]
    public void FlagWithValue_ReturnsTheValue()
    {
        Assert.Equal("out", Program.Argument(["--emit-schemas", "out"], "--emit-schemas"));
        Assert.Equal(
            "config/kompas-mcp.example.json",
            Program.Argument(["--config", "config/kompas-mcp.example.json", "--emit-schemas", "out"], "--config"));
    }

    [Fact]
    public void AbsentFlag_ReturnsNull()
    {
        Assert.Null(Program.Argument(["--print-schemas"], "--emit-schemas"));
        Assert.Null(Program.Argument([], "--emit-schemas"));
    }
}
