using ResearchHive.Core.Configuration;
using System.Collections.Concurrent;

namespace ResearchHive.Core.Services;

/// <summary>
/// Enforces polite browsing courtesy rules per spec/SEARCH_LANES_AND_COURTESY.md:
/// - Max concurrent fetches overall
/// - Max 1 concurrent fetch per domain  
/// - Delays with jitter between requests to same domain
/// - Exponential backoff on 429/503/timeouts
/// - Circuit breaker for repeated failures
/// - Caching/dedupe
/// </summary>
public class CourtesyPolicy
{
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domainSemaphores = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastRequestTime = new();
    private readonly ConcurrentDictionary<string, int> _domainFailures = new();
    private readonly ConcurrentDictionary<string, bool> _circuitBroken = new();
    private readonly ConcurrentDictionary<string, DateTime> _circuitBrokenTime = new();
    private readonly ConcurrentDictionary<string, string> _urlCache = new(); // canonical url -> snapshot id

    public CourtesyPolicy(AppSettings settings)
    {
        _settings = settings;
        _globalSemaphore = new SemaphoreSlim(settings.MaxConcurrentFetches);
    }

    public string CanonicalizeUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var canonical = $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}";
            if (!string.IsNullOrEmpty(uri.Query))
                canonical += uri.Query;
            return canonical.TrimEnd('/');
        }
        catch
        {
            return url.ToLowerInvariant().TrimEnd('/');
        }
    }

    public string GetDomain(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); }
        catch { return "unknown"; }
    }

    public bool IsCached(string canonicalUrl) => _urlCache.ContainsKey(canonicalUrl);

    public void CacheUrl(string canonicalUrl, string snapshotId) => _urlCache[canonicalUrl] = snapshotId;

    public string? GetCachedSnapshotId(string canonicalUrl) =>
        _urlCache.TryGetValue(canonicalUrl, out var id) ? id : null;

    public bool IsCircuitBroken(string domain)
    {
        if (!_circuitBroken.TryGetValue(domain, out var broken) || !broken)
            return false;

        // Auto-reset after 5 minutes (transient failures shouldn't permanently block a domain)
        if (_circuitBrokenTime.TryGetValue(domain, out var brokenTime) &&
            (DateTime.UtcNow - brokenTime).TotalMinutes >= 5)
        {
            _circuitBroken[domain] = false;
            _domainFailures[domain] = 0;
            return false;
        }

        return true;
    }

    public async Task<bool> AcquireSlotAsync(string url, CancellationToken ct = default)
    {
        var domain = GetDomain(url);

        if (IsCircuitBroken(domain))
            return false;

        await _globalSemaphore.WaitAsync(ct);

        var domainSem = _domainSemaphores.GetOrAdd(domain, _ => new SemaphoreSlim(1));
        await domainSem.WaitAsync(ct);

        // Enforce delay with jitter
        if (_lastRequestTime.TryGetValue(domain, out var lastTime))
        {
            var elapsed = DateTime.UtcNow - lastTime;
            var minDelay = TimeSpan.FromSeconds(_settings.MinDomainDelaySeconds);
            var maxDelay = TimeSpan.FromSeconds(_settings.MaxDomainDelaySeconds);
            var targetDelay = TimeSpan.FromSeconds(
                _settings.MinDomainDelaySeconds +
                Random.Shared.NextDouble() * (_settings.MaxDomainDelaySeconds - _settings.MinDomainDelaySeconds));

            if (elapsed < targetDelay)
            {
                await Task.Delay(targetDelay - elapsed, ct);
            }
        }

        _lastRequestTime[domain] = DateTime.UtcNow;
        return true;
    }

    public void ReleaseSlot(string url)
    {
        var domain = GetDomain(url);
        if (_domainSemaphores.TryGetValue(domain, out var domainSem))
            domainSem.Release();
        _globalSemaphore.Release();
    }

    public void RecordSuccess(string url)
    {
        var domain = GetDomain(url);
        _domainFailures[domain] = 0;
    }

    public TimeSpan RecordFailure(string url, int httpStatus = 0)
    {
        var domain = GetDomain(url);
        var failures = _domainFailures.AddOrUpdate(domain, 1, (_, c) => c + 1);

        if (failures >= _settings.CircuitBreakerThreshold)
        {
            _circuitBroken[domain] = true;
            _circuitBrokenTime[domain] = DateTime.UtcNow;
        }

        // Exponential backoff
        var backoff = TimeSpan.FromSeconds(
            Math.Min(_settings.BackoffBaseSeconds * Math.Pow(2, failures - 1), 60));
        return backoff;
    }

    public RequestLog CreateRequestLog(string url, string domain) => new()
    {
        Url = url,
        Domain = domain,
        TimestampUtc = DateTime.UtcNow,
    };
}

public class RequestLog
{
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
    public int HttpStatus { get; set; }
    public double DelayUsedMs { get; set; }
    public string? Error { get; set; }
    public bool WasBlocked { get; set; }
    public bool WasCached { get; set; }
}
