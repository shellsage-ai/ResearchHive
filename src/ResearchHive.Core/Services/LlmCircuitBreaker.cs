using Microsoft.Extensions.Logging;

namespace ResearchHive.Core.Services;

/// <summary>
/// Circuit breaker for LLM providers. Tracks consecutive failures per provider
/// and opens the circuit after a threshold, preventing cascading timeout failures.
/// Modeled on the existing <see cref="CourtesyPolicy"/> pattern.
///
/// States:
///   Closed  — normal operation, calls pass through
///   Open    — circuit tripped after N failures, calls immediately fail for cooldown period
///   HalfOpen — after cooldown, one trial call allowed to test recovery
/// </summary>
public class LlmCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;
    private readonly ILogger<LlmCircuitBreaker>? _logger;

    private int _ollamaConsecutiveFailures;
    private int _cloudConsecutiveFailures;
    private DateTime _ollamaOpenedAt = DateTime.MinValue;
    private DateTime _cloudOpenedAt = DateTime.MinValue;

    public LlmCircuitBreaker(int failureThreshold = 5, int cooldownSeconds = 60, ILogger<LlmCircuitBreaker>? logger = null)
    {
        _failureThreshold = failureThreshold;
        _cooldownPeriod = TimeSpan.FromSeconds(cooldownSeconds);
        _logger = logger;
    }

    /// <summary>True if the Ollama circuit is open (too many consecutive failures).</summary>
    public bool IsOllamaOpen => _ollamaConsecutiveFailures >= _failureThreshold
        && (DateTime.UtcNow - _ollamaOpenedAt) < _cooldownPeriod;

    /// <summary>True if the cloud provider circuit is open.</summary>
    public bool IsCloudOpen => _cloudConsecutiveFailures >= _failureThreshold
        && (DateTime.UtcNow - _cloudOpenedAt) < _cooldownPeriod;

    /// <summary>Check if the Ollama circuit should allow a call (closed or half-open).</summary>
    public bool AllowOllamaCall()
    {
        if (_ollamaConsecutiveFailures < _failureThreshold) return true;
        if ((DateTime.UtcNow - _ollamaOpenedAt) >= _cooldownPeriod)
        {
            _logger?.LogInformation("Ollama circuit half-open — allowing trial call after cooldown");
            return true; // Half-open: allow one trial
        }
        return false;
    }

    /// <summary>Check if the cloud circuit should allow a call.</summary>
    public bool AllowCloudCall()
    {
        if (_cloudConsecutiveFailures < _failureThreshold) return true;
        if ((DateTime.UtcNow - _cloudOpenedAt) >= _cooldownPeriod)
        {
            _logger?.LogInformation("Cloud circuit half-open — allowing trial call after cooldown");
            return true;
        }
        return false;
    }

    /// <summary>Record a successful Ollama call — resets the failure counter.</summary>
    public void RecordOllamaSuccess()
    {
        if (_ollamaConsecutiveFailures > 0)
            _logger?.LogInformation("Ollama circuit closed — call succeeded after {Failures} failures", _ollamaConsecutiveFailures);
        _ollamaConsecutiveFailures = 0;
    }

    /// <summary>Record a failed Ollama call — increments counter and may open circuit.</summary>
    public void RecordOllamaFailure()
    {
        _ollamaConsecutiveFailures++;
        if (_ollamaConsecutiveFailures >= _failureThreshold)
        {
            _ollamaOpenedAt = DateTime.UtcNow;
            _logger?.LogWarning("Ollama circuit OPEN — {Failures} consecutive failures, cooldown {Seconds}s",
                _ollamaConsecutiveFailures, _cooldownPeriod.TotalSeconds);
        }
    }

    /// <summary>Record a successful cloud call.</summary>
    public void RecordCloudSuccess()
    {
        if (_cloudConsecutiveFailures > 0)
            _logger?.LogInformation("Cloud circuit closed — call succeeded after {Failures} failures", _cloudConsecutiveFailures);
        _cloudConsecutiveFailures = 0;
    }

    /// <summary>Record a failed cloud call.</summary>
    public void RecordCloudFailure()
    {
        _cloudConsecutiveFailures++;
        if (_cloudConsecutiveFailures >= _failureThreshold)
        {
            _cloudOpenedAt = DateTime.UtcNow;
            _logger?.LogWarning("Cloud circuit OPEN — {Failures} consecutive failures, cooldown {Seconds}s",
                _cloudConsecutiveFailures, _cooldownPeriod.TotalSeconds);
        }
    }

    /// <summary>Reset both circuits (e.g. after settings change).</summary>
    public void Reset()
    {
        _ollamaConsecutiveFailures = 0;
        _cloudConsecutiveFailures = 0;
    }

    // For testing
    internal int OllamaFailureCount => _ollamaConsecutiveFailures;
    internal int CloudFailureCount => _cloudConsecutiveFailures;
}
