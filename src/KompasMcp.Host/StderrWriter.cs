using System.Text;

namespace KompasMcp.Host;

/// <summary>
/// stderr Хоста — ЯВНО в UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// ИЗМЕРЕНО 21.09.2026 (проба P4): <c>Console.Error.WriteLine</c> писал русский текст байтами
/// консольной кодовой страницы (cp866), а не UTF-8. Клиент, читающий stderr как UTF-8, видел
/// <c>?????? ????????: ????????? ????????????? ????? 1</c> — то есть причина отказа доходила в
/// нечитаемом виде. Наряд требует ровно обратного: «клиент видит причину, а не Connection closed».
/// Нечитаемая причина от читаемой не отличается по последствиям.
/// </para>
/// <para>
/// Почему свой писатель, а не <c>Console.OutputEncoding</c>: тот же параметр управляет и stdout, а
/// stdout несёт кадры MCP. Менять кодировку протокольного канала ради диагностики — платить за
/// сообщение об ошибке риском в самом канале.
/// </para>
/// </remarks>
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
