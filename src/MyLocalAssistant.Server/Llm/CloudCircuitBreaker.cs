using Microsoft.Extensions.Logging;

namespace MyLocalAssistant.Server.Llm;

/// <summary>
/// Per-provider circuit breaker with three states:
/// <list type="bullet">
///   <item><b>Closed</b>  — normal operation; failures are counted.</item>
///   <item><b>Open</b>    — after <see cref="FailureThreshold"/> consecutive failures the circuit
///                          opens for <see cref="OpenDuration"/>; calls throw immediately.</item>
///   <item><b>HalfOpen</b> — after the open window expires, one probe request is allowed through;
///                           success closes the circuit, failure reopens it.</item>
/// </list>
/// Thread-safe via a single lock; designed to wrap the SSE stream generator in cloud providers.
/// </summary>
public sealed class CloudCircuitBreaker
{
    public enum State { Closed, Open, HalfOpen }

    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly ILogger _log;
    private readonly string _providerName;
    private readonly object _lock = new();

    private State _state = State.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    public CloudCircuitBreaker(string providerName, ILogger log,
        int failureThreshold = 5, int openSeconds = 60)
    {
        _providerName = providerName;
        _log = log;
        _failureThreshold = failureThreshold;
        _openDuration = TimeSpan.FromSeconds(openSeconds);
    }

    public State CurrentState
    {
        get
        {
            lock (_lock)
            {
                if (_state == State.Open && DateTimeOffset.UtcNow - _openedAt >= _openDuration)
                {
                    _state = State.HalfOpen;
                    _log.LogInformation("Circuit [{Provider}] → HalfOpen (probe allowed).", _providerName);
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// Wraps an async enumerable producer with circuit breaker logic.
    /// Throws <see cref="CircuitOpenException"/> immediately if the circuit is Open.
    /// On success the circuit closes; on failure the failure counter advances.
    /// </summary>
    public async IAsyncEnumerable<string> ExecuteAsync(
        Func<IAsyncEnumerable<string>> producer,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var s = CurrentState;
        if (s == State.Open)
            throw new CircuitOpenException($"Provider '{_providerName}' circuit is open. Calls are temporarily blocked after repeated failures.");

        var yieldedAny = false;
        Exception? failure = null;
        IAsyncEnumerable<string>? stream = null;

        try
        {
            stream = producer();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            RecordFailure();
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        await foreach (var token in stream!.WithCancellation(ct))
        {
            yieldedAny = true;
            yield return token;
        }

        if (yieldedAny || failure is null)
            RecordSuccess();
    }

    private void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state != State.Closed)
                _log.LogInformation("Circuit [{Provider}] → Closed (recovered).", _providerName);
            _state = State.Closed;
            _consecutiveFailures = 0;
        }
    }

    private void RecordFailure()
    {
        lock (_lock)
        {
            if (_state == State.HalfOpen)
            {
                // probe failed — reopen
                _state = State.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _log.LogWarning("Circuit [{Provider}] → Open (probe failed; backing off {Sec}s).",
                    _providerName, _openDuration.TotalSeconds);
                return;
            }
            _consecutiveFailures++;
            if (_state == State.Closed && _consecutiveFailures >= _failureThreshold)
            {
                _state = State.Open;
                _openedAt = DateTimeOffset.UtcNow;
                _log.LogWarning("Circuit [{Provider}] → Open after {N} failures (backing off {Sec}s).",
                    _providerName, _consecutiveFailures, _openDuration.TotalSeconds);
            }
        }
    }
}

/// <summary>Thrown when a call is rejected because the circuit is open.</summary>
public sealed class CircuitOpenException(string message) : Exception(message);
