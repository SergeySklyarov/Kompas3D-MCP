using System.Text;

namespace KompasMcp.Host;

/// <summary>The Host's stderr, explicitly UTF-8.</summary>
/// <remarks>MEASURED 21.09.2026 (probe P4): <c>Console.Error.WriteLine</c> wrote Russian text in the
/// console code page (cp866), not UTF-8, so a client reading stderr as UTF-8 saw
/// <c>?????? ????????: ????????? ????????????? ????? 1</c> — the refusal reason arrived unreadable.
/// INVARIANT: a dedicated writer is used, not <c>Console.OutputEncoding</c> — that property also
/// governs stdout, which carries MCP frames, so changing it for diagnostics would risk the protocol
/// channel itself. History: docs/decisions/host.md#stderr-utf8</remarks>
internal static class StderrWriter
{
    private static readonly object Gate = new();
    private static readonly StreamWriter Writer =
        new(Console.OpenStandardError(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

    public static void WriteLine(string message)
    {
        lock (Gate)
        {
            Writer.WriteLine(message);
        }
    }
}
