using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KompasMcp.Host;

/// <summary>
/// Routes framework and MCP-SDK log records into the Host's JSONL log.
/// </summary>
/// <remarks>
/// Why this exists rather than a one-line <c>AddConsole()</c>: stdout is the MCP transport, and
/// the default console logger writes there — one SDK warning would corrupt the protocol stream for
/// every client. The obvious alternative was to keep silencing logs (an earlier build called
/// <c>ClearProviders()</c> and nothing else), which is how an SDK-side failure surfaced to the
/// client as a bare "An error occurred invoking …" with no diagnosable trace anywhere. So:
/// diagnostics go to the log file and, for warnings and above, to stderr, never to stdout.
///
/// Repeated messages are rate-limited because a broken tool called in a loop must not fill the
/// disk at call rate.
/// </remarks>
public sealed class HostLogProvider : ILoggerProvider
{
    private const int ThrottleWindowSeconds = 60;
    private const int MaxPerWindow = 20;

    private readonly HostLog _log;
    private readonly ConcurrentDictionary<string, (DateTimeOffset First, int Count)> _seen = new(StringComparer.Ordinal);

    public HostLogProvider(HostLog log) => _log = log;

    public ILogger CreateLogger(string categoryName) => new HostLogger(categoryName, this);

    public void Dispose() => _log.Flush();

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        if (!ShouldLog(category, message))
        {
            return;
        }

        _log.Write(MapLevel(level), "sdk: " + message, new
        {
            category,
            exception_type = exception?.GetType().Name,
            exception_message = exception?.Message,
            // The top frames are what identifies the fault; the full log file keeps them readable.
            stack_head = Head(exception?.StackTrace),
        });

        if (level >= LogLevel.Warning && exception is not null)
        {
            StderrWriter.WriteLine($"[host] {level} {category}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string? Head(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return null;
        }

        var lines = stack!.Split('\n');
        return string.Join(" | ", lines.Take(5).Select(l => l.Trim()));
    }

    private static string MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        _ => "error",
    };

    private bool ShouldLog(string category, string message)
    {
        var key = category + "\n" + message;
        var now = DateTimeOffset.UtcNow;
        var entry = _seen.AddOrUpdate(key,
            _ => (now, 1),
            (_, current) => current.First + TimeSpan.FromSeconds(ThrottleWindowSeconds) < now
                ? (current.First, 1)
                : (current.First, current.Count + 1));

        return entry.Count <= MaxPerWindow;
    }

    private sealed class HostLogger : ILogger
    {
        private readonly string _category;
        private readonly HostLogProvider _owner;

        public HostLogger(string category, HostLogProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            try
            {
                _owner.Write(_category, logLevel, formatter(state, exception), exception);
            }
            catch (ObjectDisposedException)
            {
                // The host is shutting down; losing a trailing log line is correct behaviour.
            }
        }
    }
}
