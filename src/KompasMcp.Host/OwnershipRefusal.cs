using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KompasMcp.Host;

/// <summary>
/// Отказ старта, который КЛИЕНТ ВИДИТ.
/// </summary>
/// <remarks>
/// <para>
/// ЗАЧЕМ. Когда Хост не может работать, у него есть ровно два честных исхода: назвать причину или
/// промолчать. Пока он просто завершался, клиент видел <c>MCP error -32000: Connection closed</c> —
/// то есть «сервер упал», из чего нельзя понять ни что случилось, ни что делать. Измерено
/// 21.09.2026: агент остался с НУЛЁМ инструментов <c>kompas_*</c> и без единого слова причины.
/// </para>
/// <para>
/// ЧТО ДЕЛАЕТ. Поднимает минимальный транспорт MCP на тех же stdin/stdout: отвечает на каждый
/// запрос JSON-RPC ошибкой с именованным кодом и читаемым текстом, уведомления игнорирует и
/// завершается, когда клиент закрывает вход. Инструментов не публикуется ни одного — молчаливой
/// деградации нет ни в одну сторону: клиент получает либо инструменты, либо названную причину.
/// </para>
/// <para>
/// ПОЧЕМУ СВОЙ ЦИКЛ, А НЕ СЕРВЕР SDK. Сервер SDK начинается с <c>initialize</c> и отвечает на него
/// успехом, а «успешный <c>initialize</c> и пустой <c>tools/list</c>» читается клиентом как рабочий
/// сервер без инструментов — ровно та подмена, которую этот класс устраняет.
/// </para>
/// </remarks>
internal static class OwnershipRefusal
{
    /// <summary>
    /// Код JSON-RPC для отказа уровня транспорта. Диапазон -32000…-32099 отдан реализации сервера;
    /// читаемый текст важнее кода, поэтому код здесь один на все отказы, а причина — в сообщении.
    /// </summary>
    private const int RefusalJsonRpcCode = -32050;

    /// <summary>
    /// Обслужить клиента отказом. Возвращает код выхода процесса: 0 — клиент закрыл вход.
    /// </summary>
    public static int Serve(string code, string message, string remedy)
    {
        StderrWriter.WriteLine($"{code}: {message}");

        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                // Не наш кадр — не наше дело. Молчать здесь нельзя: ответ на неразобранный запрос
                // был бы выдумкой о том, чего клиент не спрашивал.
                continue;
            }

            if (request is not JsonObject envelope || !envelope.TryGetPropertyValue("id", out var id) || id is null)
            {
                // Уведомление: ответа не требует.
                continue;
            }

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = RefusalJsonRpcCode,
                    ["message"] = code + ": " + message,
                    ["data"] = new JsonObject
                    {
                        ["code"] = code,
                        ["host_pid"] = Environment.ProcessId,
                        ["remedy"] = remedy,
                    },
                },
            };

            stdout.WriteLine(response.ToJsonString());
        }

        return 0;
    }
}
