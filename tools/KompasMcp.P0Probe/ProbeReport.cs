using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KompasMcp.P0Probe;

public enum Verdict
{
    Pass,
    Fail,
    Unknown,
    Skipped,
}

/// <summary>
/// One investigation step. <see cref="VerdictUnknown"/> is a first-class answer: the whole point
/// of P0 is to record what is not yet known, instead of letting a later layer assume it.
/// </summary>
public sealed class ProbeStep
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public Verdict Verdict { get; set; } = Verdict.Unknown;

    public string? Question { get; set; }

    public string? Conclusion { get; set; }

    public List<string> Observations { get; } = new();

    public List<string> Errors { get; } = new();

    public Dictionary<string, object?> Data { get; } = new(StringComparer.Ordinal);

    public List<string> Artifacts { get; } = new();

    public TimeSpan Duration { get; set; }

    public void Observe(string message) => Observations.Add(message);

    public void Observe(string format, params object?[] args) => Observations.Add(string.Format(CultureInfo.InvariantCulture, format, args));

    public ProbeStep Fail(string reason)
    {
        Verdict = Verdict.Fail;
        Conclusion = reason;
        return this;
    }

    public ProbeStep Unknown(string reason)
    {
        if (Verdict != Verdict.Fail)
        {
            Verdict = Verdict.Unknown;
            Conclusion ??= reason;
        }

        return this;
    }

    public ProbeStep Pass(string conclusion)
    {
        if (Verdict is Verdict.Unknown or Verdict.Skipped)
        {
            Verdict = Verdict.Pass;
        }

        Conclusion ??= conclusion;
        return this;
    }
}

/// <summary>Collects steps and writes them as JSON (machine-checkable) plus Markdown (human review).</summary>
public sealed class ProbeReport
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Report heading. The probe started as the P0 investigation and grew a second, independent
    /// suite (P2); each has to be readable on its own, so the title is data rather than a literal.
    /// </summary>
    public string Title { get; init; } = "P0 — отчёт технического исследования";

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public List<ProbeStep> Steps { get; } = new();

    public Dictionary<string, object?> Environment { get; } = new(StringComparer.Ordinal);

    public ProbeStep Begin(string id, string title, string? question = null)
    {
        var step = new ProbeStep { Id = id, Title = title, Question = question };
        Steps.Add(step);
        return step;
    }

    public void Write(string jsonPath, string markdownPath)
    {
        var payload = new
        {
            run_id = RunId,
            started_utc = StartedUtc,
            finished_utc = DateTimeOffset.UtcNow,
            @interface = Environment,
            summary = new
            {
                pass = Steps.Count(s => s.Verdict == Verdict.Pass),
                fail = Steps.Count(s => s.Verdict == Verdict.Fail),
                unknown = Steps.Count(s => s.Verdict == Verdict.Unknown),
                skipped = Steps.Count(s => s.Verdict == Verdict.Skipped),
            },
            steps = Steps.Select(s => new
            {
                s.Id,
                s.Title,
                s.Question,
                verdict = s.Verdict.ToString().ToLowerInvariant(),
                s.Conclusion,
                s.Observations,
                s.Errors,
                data = s.Data.Count == 0 ? null : s.Data,
                s.Artifacts,
                duration_ms = (int)s.Duration.TotalMilliseconds,
            }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, JsonOpts), new UTF8Encoding(false));
        File.WriteAllText(markdownPath, RenderMarkdown(payload), new UTF8Encoding(false));
    }

    private string RenderMarkdown(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        var sb = new StringBuilder();
        sb.AppendLine($"# {Title}");
        sb.AppendLine();
        sb.AppendLine($"Run: `{RunId:N}` · Started: {StartedUtc:O} · Finished: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Сгенерировано `tools/KompasMcp.P0Probe`. Это фактические наблюдения над");
        sb.AppendLine("реальным КОМПАС-3D v24, а не пересказ ТЗ: шаг с verdict=unknown означает,");
        sb.AppendLine("что вопрос остался открытым и не должен считаться решённым.");
        sb.AppendLine();

        var summary = node["summary"]!.AsObject();
        sb.AppendLine($"**Итог:** PASS {summary["pass"]} · FAIL {summary["fail"]} · UNKNOWN {summary["unknown"]} · SKIPPED {summary["skipped"]}");
        sb.AppendLine();

        sb.AppendLine("## Окружение");
        sb.AppendLine();
        foreach (var pair in node["interface"]!.AsObject())
        {
            sb.AppendLine($"- `{pair.Key}`: {Format(pair.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Шаги");
        sb.AppendLine();

        foreach (var step in node["steps"]!.AsArray())
        {
            var s = step!.AsObject();
            sb.AppendLine($"### {s["id"]} — {s["title"]}  `[{s["verdict"]}]`");
            sb.AppendLine();
            if (s["question"] is not null)
            {
                sb.AppendLine($"> Вопрос: {s["question"]!.GetValue<string>()}");
                sb.AppendLine();
            }

            if (s["conclusion"] is not null)
            {
                sb.AppendLine($"**Вывод:** {s["conclusion"]!.GetValue<string>()}");
                sb.AppendLine();
            }

            if (s["observations"] is JsonArray obs && obs.Count > 0)
            {
                sb.AppendLine("Наблюдения:");
                sb.AppendLine();
                foreach (var o in obs)
                {
                    sb.AppendLine($"- {o?.GetValue<string>()}");
                }

                sb.AppendLine();
            }

            if (s["data"] is JsonObject dataObj && dataObj.Count > 0)
            {
                sb.AppendLine("Данные:");
                sb.AppendLine();
                foreach (var pair in dataObj)
                {
                    sb.AppendLine($"- `{pair.Key}` = {Format(pair.Value)}");
                }

                sb.AppendLine();
            }

            if (s["errors"] is JsonArray errs && errs.Count > 0)
            {
                sb.AppendLine("Ошибки:");
                sb.AppendLine();
                foreach (var e in errs)
                {
                    sb.AppendLine($"- {e?.GetValue<string>()}");
                }

                sb.AppendLine();
            }

            if (s["artifacts"] is JsonArray arts && arts.Count > 0)
            {
                sb.AppendLine("Артефакты: " + string.Join(", ", arts.Select(a => "`" + a?.GetValue<string>() + "`")));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Format(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject obj => string.Join(", ", obj.Select(p => $"{p.Key}={Format(p.Value)}")),
        JsonArray arr => "[" + string.Join(", ", arr.Select(Format)) + "]",
        // JsonValue.ToString() on a deserialized node yields the raw literal: unquoted for
        // strings, plain digits for numbers, "true"/"false" for booleans.
        JsonValue val => val.ToString(),
        _ => value.ToJsonString(),
    };
}
