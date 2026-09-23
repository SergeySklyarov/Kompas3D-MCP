using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KompasMcp.Api7Probe;

internal enum Verdict
{
    Pass,
    Fail,
    Unknown,
    Skipped,
}

/// <summary>
/// One measured step. <see cref="Verdict.Unknown"/> is a first-class answer: ADR-003 §4 forbids
/// promoting an unmeasured question to a conclusion, so a step that could not be decided stays
/// UNKNOWN in the report rather than being quietly dropped or rounded into PASS.
/// </summary>
internal sealed class ProbeStep
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

    public void Observe(string format, params object?[] args) =>
        Observations.Add(string.Format(CultureInfo.InvariantCulture, format, args));

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

/// <summary>Collects steps; writes JSON (machine-checkable) and Markdown (human review).</summary>
internal sealed class ProbeReport
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public string Title { get; init; } = "Проба API7 — родное «Отверстие» в КОМПАС-3D v24";

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public List<ProbeStep> Steps { get; } = new();

    public Dictionary<string, object?> Environment { get; } = new(StringComparer.Ordinal);

    public ProbeStep Begin(string id, string title, string? question = null)
    {
        var step = new ProbeStep { Id = id, Title = title, Question = question };
        Steps.Add(step);
        return step;
    }

    /// <summary>
    /// Records an observation against the step that is already open. Needed where the note is
    /// produced by a helper that has no step handle of its own (drawing an axis, for instance) but
    /// belongs to the step being measured.
    /// </summary>
    public void Note(string stepId, string message)
    {
        var step = Steps.FirstOrDefault(s => string.Equals(s.Id, stepId, StringComparison.Ordinal));
        if (step is null)
        {
            return;
        }

        step.Observe(message);
    }

    /// <summary>Flushes whatever has been collected so far to the two report files.</summary>
    /// <remarks>
    /// The probe runs COM against an out-of-process server that can hang inside a call. When it does,
    /// the STA thread never comes back and <c>Write</c> at the end of <c>Main</c> is never reached —
    /// which is how a run that measured five minutes of facts ends up on disk with an empty step in
    /// it. <c>--keep</c> makes this reachable at will, and a crash path calls it too.
    /// </remarks>
    public void Flush(string jsonPath, string markdownPath) => Write(jsonPath, markdownPath);

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

        // The passport is a deliverable in its own right, and it has to describe THIS run: writing
        // it only in --passport mode left an env-passport.md on disk next to a report from a later
        // run, which is exactly how a stale artefact starts looking like a current one.
        var passportStep = Steps.FirstOrDefault(s => s.Id == "A7.0");
        if (passportStep is not null)
        {
            var directory = Path.GetDirectoryName(jsonPath)!;
            File.WriteAllText(
                Path.Combine(directory, "env-passport.json"),
                JsonSerializer.Serialize(new { run_id = RunId, started_utc = StartedUtc, environment = Environment, step = passportStep }, JsonOpts),
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(directory, "env-passport.md"),
                RenderPassport(passportStep),
                new UTF8Encoding(false));
        }
    }

    private string RenderMarkdown(object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        var sb = new StringBuilder();
        sb.AppendLine("# " + Title);
        sb.AppendLine();
        sb.AppendLine($"Run: `{RunId:N}` · Started: {StartedUtc:O} · Finished: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Сгенерировано `tools/KompasMcp.Api7Probe`. Это измерения над реальным");
        sb.AppendLine("КОМПАС-3D v24, а не пересказ ADR: ненулевой объект или S_OK доказательством");
        sb.AppendLine("не считаются (ADR-003 §3), verdict=unknown означает открытый вопрос.");
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

    /// <summary>
    /// The passport rendered on its own: hashes and versions are the part a reviewer checks with a
    /// different tool from the one that reads the measurements, and they must say which run they
    /// belong to.
    /// </summary>
    private string RenderPassport(ProbeStep passport)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Паспорт среды пробы API7 (ADR-003 §4)");
        sb.AppendLine();
        sb.AppendLine($"Run: `{RunId:N}` · Started: {StartedUtc:O} · Finished: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();
        sb.AppendLine("Сгенерировано `tools/KompasMcp.Api7Probe` вместе с отчётом прогона, поэтому описывает");
        sb.AppendLine("ровно тот запуск, чьи числа лежат в `api7-probe-report.md`. Ничего в установке КОМПАС");
        sb.AppendLine("не открывалось на запись: библиотека типов читается через `LoadTypeLibEx(REGKIND_NONE)`.");
        sb.AppendLine();
        sb.AppendLine($"**Вердикт шага:** `{passport.Verdict.ToString().ToLowerInvariant()}` — {passport.Conclusion}");
        sb.AppendLine();
        sb.AppendLine("## Окружение прогона");
        sb.AppendLine();
        foreach (var pair in Environment)
        {
            sb.AppendLine($"- `{pair.Key}`: {Api5.Raw(pair.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Наблюдения");
        sb.AppendLine();
        foreach (var line in passport.Observations)
        {
            sb.AppendLine($"- {line}");
        }

        sb.AppendLine();
        if (passport.Data.Count > 0)
        {
            sb.AppendLine("## Данные");
            sb.AppendLine();
            foreach (var pair in passport.Data)
            {
                sb.AppendLine($"- `{pair.Key}` = {Api5.Raw(pair.Value)}");
            }

            sb.AppendLine();
        }

        foreach (var error in passport.Errors)
        {
            sb.AppendLine("- ОШИБКА: " + error);
        }

        return sb.ToString();
    }

    private static string Format(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject obj => string.Join(", ", obj.Select(p => $"{p.Key}={Format(p.Value)}")),
        JsonArray arr => "[" + string.Join(", ", arr.Select(Format)) + "]",
        JsonValue val => val.ToString(),
        _ => value.ToJsonString(),
    };
}
