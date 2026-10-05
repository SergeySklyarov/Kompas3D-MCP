using System.Text;

namespace KompasMcp.Host;

/// <summary>The Host's stderr, explicitly UTF-8.</summary>
/// <remarks>MEASURED: the console code page is cp866, not UTF-8, so a client reading stderr as UTF-8
/// saw <c>?????? ????????: ????????? ????????????? ????? 1</c> — an unreadable refusal reason.
/// INVARIANT: a dedicated writer, not <c>Console.OutputEncoding</c> — that property also governs
/// stdout, which carries MCP frames. History: docs/decisions/host.md#stderr-utf8</remarks>
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
