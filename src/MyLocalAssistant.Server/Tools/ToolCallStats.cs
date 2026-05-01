using System.Collections.Concurrent;

namespace MyLocalAssistant.Server.Tools;

/// <summary>
/// In-memory counters of tool invocations. Cleared on server restart; sufficient for
/// the admin UI's "what's actually being called" panel without dragging in OpenTelemetry
/// or a metrics DB. Thread-safe.
/// </summary>
public sealed class ToolCallStats
{
    private readonly ConcurrentDictionary<string, Counters> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _since = DateTimeOffset.UtcNow;

    private static string Key(string toolId, string toolName) => $"{toolId}::{toolName}";

    public void RecordSuccess(string toolId, string toolName, double elapsedMs)
    {
        var c = _byKey.GetOrAdd(Key(toolId, toolName), _ => new Counters());
        c.AddSuccess(elapsedMs);
    }

    public void RecordError(string toolId, string toolName, double elapsedMs)
    {
        var c = _byKey.GetOrAdd(Key(toolId, toolName), _ => new Counters());
        c.AddError(elapsedMs);
    }

    public void Reset()
    {
        _byKey.Clear();
        _since = DateTimeOffset.UtcNow;
    }

    public ToolCallStatsSnapshot Snapshot()
    {
        var rows = _byKey
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var parts = kv.Key.Split("::", 2);
                var c = kv.Value.Read();
                return new ToolCallStatRow(
                    ToolId: parts[0],
                    ToolName: parts.Length > 1 ? parts[1] : "",
                    Successes: c.successes,
                    Errors: c.errors,
                    AvgMs: c.successes + c.errors == 0 ? 0 : c.totalMs / (c.successes + c.errors),
                    MaxMs: c.maxMs);
            })
            .ToArray();
        return new ToolCallStatsSnapshot(_since, rows);
    }

    private sealed class Counters
    {
        private long _successes;
        private long _errors;
        private double _totalMs;
        private double _maxMs;
        private readonly object _lock = new();

        public void AddSuccess(double ms)
        {
            lock (_lock) { _successes++; _totalMs += ms; if (ms > _maxMs) _maxMs = ms; }
        }
        public void AddError(double ms)
        {
            lock (_lock) { _errors++; _totalMs += ms; if (ms > _maxMs) _maxMs = ms; }
        }
        public (long successes, long errors, double totalMs, double maxMs) Read()
        {
            lock (_lock) return (_successes, _errors, _totalMs, _maxMs);
        }
    }
}

public sealed record ToolCallStatRow(
    string ToolId,
    string ToolName,
    long Successes,
    long Errors,
    double AvgMs,
    double MaxMs);

public sealed record ToolCallStatsSnapshot(DateTimeOffset SinceUtc, IReadOnlyList<ToolCallStatRow> Rows);
