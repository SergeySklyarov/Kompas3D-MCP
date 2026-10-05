using System.Reflection;
using System.Text.RegularExpressions;
using KompasMcp.Contracts;
using KompasMcp.Host;
using KompasMcp.Host.Catalog;
using Xunit;

namespace KompasMcp.Unit;

/// <summary>INVARIANT: the instructions sent in the MCP handshake name only tools and error codes that exist.
/// A stale name there is worse than a missing one — the model is told to call a tool it cannot call, or to
/// expect a code the server never returns.</summary>
/// <remarks>MEASURED: the instructions carried only call order and session ownership, while the pitfalls that
/// actually cost calls (millimetres, revision-stale references, ambiguous selection, union needing a shared
/// surface) lived only in per-tool descriptions the model reads one call at a time.
/// History: docs/decisions/tests.md#server-instructions</remarks>
public class ServerInstructionsTests
{
    /// <summary>Every tool name mentioned, e.g. <c>kompas_export_image</c>. A slash-separated pair
    /// (<c>kompas_create_document/kompas_open_document</c>) yields both names: the slash is not part of the
    /// identifier.</summary>
    private static IReadOnlyList<string> ToolNamesIn(string text) =>
        Regex.Matches(text, @"\bkompas_[a-z0-9_]+\b")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Every error code mentioned, written in the public constant style (upper snake case, at least
    /// one underscore). Deliberately narrower than "any capitalised word": prose like <c>CAD</c> or
    /// <c>API5</c> must not be mistaken for a code.</summary>
    private static IReadOnlyList<string> ErrorCodesIn(string text) =>
        Regex.Matches(text, @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b")
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Instructions_MentionOnlyToolsThatExist()
    {
        var known = ToolCatalog.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var mentioned = ToolNamesIn(Program.Instructions);

        // A guard on the guard: if the regex silently matches nothing, the assertion below would pass
        // vacuously and prove nothing about the text.
        Assert.NotEmpty(mentioned);

        foreach (var name in mentioned)
        {
            Assert.True(known.Contains(name), $"Инструкции называют инструмент {name}, которого нет в ToolCatalog.All.");
        }
    }

    [Fact]
    public void Instructions_MentionOnlyErrorCodesThatExist()
    {
        var known = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var mentioned = ErrorCodesIn(Program.Instructions);
        Assert.NotEmpty(mentioned);

        foreach (var code in mentioned)
        {
            Assert.True(known.Contains(code), $"Инструкции называют код {code}, которого нет в ErrorCodes.");
        }
    }

    [Fact]
    public void Instructions_CarryThePitfallsTheModelNeeds()
    {
        // The five subjects the order named. Each is checkable by a substring that cannot be produced by
        // accident, so a later edit that quietly drops one fails here rather than in the field.
        var text = Program.Instructions;

        Assert.Contains("миллиметр", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STALE_REFERENCE", text, StringComparison.Ordinal);
        Assert.Contains("AMBIGUOUS_SELECTION", text, StringComparison.Ordinal);
        Assert.Contains("verification.level", text, StringComparison.Ordinal);
        Assert.Contains("19.09.2026", text, StringComparison.Ordinal);
    }
}
