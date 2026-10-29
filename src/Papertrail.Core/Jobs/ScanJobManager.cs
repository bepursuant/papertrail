using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Papertrail.Scanning;
using Papertrail.Security;

namespace Papertrail.Jobs;

public sealed class JobRejectedException : Exception
{
    public JobRejectedException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Owns scan jobs: running one scan at a time, keeping results in memory until they expire, and
/// releasing them only to the origin that owns the job. Scans from paired origins never prompt.
/// </summary>
public sealed partial class ScanJobManager : IDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(10);

    /// <summary>A papertrail:// scan that its origin does not claim within this time never starts.</summary>
    public static readonly TimeSpan DefaultClaimTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Backstop for a backend that never returns. The TWAIN backend has its own, shorter
    /// no-activity timeout; this one only guarantees the page always gets an answer.
    /// </summary>
    public static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromMinutes(15);
    private const int MaxJobs = 32;

    private readonly IScannerBackend _backend;
    private readonly OriginRegistry _origins;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _claimTimeout;
    private readonly TimeSpan _scanTimeout;
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ITimer _purgeTimer;
    private readonly ILogger _logger;

    public ScanJobManager(
        IScannerBackend backend,
        OriginRegistry origins,
        TimeProvider? time = null,
        TimeSpan? retention = null,
        TimeSpan? claimTimeout = null,
        ILogger<ScanJobManager>? logger = null,
        TimeSpan? scanTimeout = null)
    {
        _logger = logger ?? NullLogger<ScanJobManager>.Instance;
        _backend = backend;
        _origins = origins;
        _time = time ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
        _claimTimeout = claimTimeout ?? DefaultClaimTimeout;
        _scanTimeout = scanTimeout ?? DefaultScanTimeout;
        _purgeTimer = _time.CreateTimer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised (on a worker thread) whenever a job reaches a terminal state.</summary>
    public event Action<ScanJob>? JobFinished;

    public static bool IsValidRequestId(string? id) => id is not null && RequestIdPattern().IsMatch(id);

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Queues a scan. The caller must already have verified that <paramref name="origin"/> is paired.</summary>
    public ScanJob Start(string origin, ScanOptions options, ScanTrigger trigger, string? requestedId = null)
    {
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new JobRejectedException("invalid_options", string.Join(" ", problems));
        }

        if (!_origins.IsPaired(origin))
        {
            throw new JobRejectedException("origin_not_paired", "This site is not paired with papertrail.");
        }

        PurgeExpired();
        if (_jobs.Count >= MaxJobs)
        {
            throw new JobRejectedException("too_many_jobs", "Too many scans are pending. Delete finished scans and retry.");
        }

        string id = requestedId ?? NewId();
        if (!IsValidRequestId(id))
        {
            throw new JobRejectedException("invalid_request_id", "request id must be 16-64 characters of [A-Za-z0-9_-].");
        }

        var job = new ScanJob(id, origin, options, trigger, _time.GetUtcNow());
        if (!_jobs.TryAdd(id, job))
        {
            throw new JobRejectedException("duplicate_request_id", "A scan with this request id already exists.");
        }

        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    /// <summary>Returns the job only if <paramref name="origin"/> owns it.</summary>
    public ScanJob? Get(string id, string origin) =>
        _jobs.TryGetValue(id, out var job) && job.Origin == origin ? job : null;

    /// <summary>
    /// Like <see cref="Get"/>, and lets a papertrail:// job start: the request now comes with a
    /// browser-verified Origin, which proves the link was opened by that site's page.
    /// </summary>
    public ScanJob? Claim(string id, string origin)
    {
        var job = Get(id, origin);
        job?.Claim();
        return job;
    }

    public IReadOnlyList<ScanJob> List() => _jobs.Values.OrderBy(j => j.CreatedAt).ToArray();

    public bool Delete(string id, string origin)
    {
        if (Get(id, origin) is not { } job)
        {
            return false;
        }

        job.Cancellation.Cancel();
        Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
        _jobs.TryRemove(id, out _);
        return true;
    }

    public void PurgeExpired()
    {
        var cutoff = _time.GetUtcNow() - _retention;
        foreach (var job in _jobs.Values)
        {
            if (job.IsFinished && job.FinishedAt < cutoff)
            {
                _jobs.TryRemove(job.Id, out _);
            }
        }
    }

    public void Dispose()
    {
        _purgeTimer.Dispose();
        foreach (var job in _jobs.Values)
        {
            job.Cancellation.Cancel();
        }
    }

    private async Task RunAsync(ScanJob job)
    {
        var ct = job.Cancellation.Token;
        try
        {
            if (!job.IsClaimed)
            {
                await job.Claimed.WaitAsync(_claimTimeout, _time, ct).ConfigureAwait(false);
            }

            await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("papertrail:// scan {JobId} for {Origin} was never collected; not scanning", job.Id, job.Origin);
            Complete(job, ScanJobStatus.Canceled, "not_claimed", "No page from this site collected the scan, so it was not started.");
            return;
        }
        catch (OperationCanceledException)
        {
            Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
            return;
        }

        try
        {
            job.SetStatus(ScanJobStatus.Scanning);
            var pages = await _backend.ScanAsync(job.Options, job.AddPage, ct).WaitAsync(_scanTimeout, _time, ct).ConfigureAwait(false);
            if (pages.Count == 0)
            {
                Complete(job, ScanJobStatus.Failed, "no_pages", "The scanner returned no pages. Is there paper in the feeder?");
                return;
            }

            Complete(job, ScanJobStatus.Completed);
        }
        catch (OperationCanceledException)
        {
            Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
        }
        catch (TimeoutException)
        {
            _logger.LogError("Scan job {JobId} did not finish within {Timeout}; giving up on it", job.Id, _scanTimeout);
            job.Cancellation.Cancel();
            Complete(job, ScanJobStatus.Failed, "scan_timeout", "The scanner did not finish the scan. Check the scanner, then try again.");
        }
        catch (ScannerException ex)
        {
            _logger.LogWarning(ex, "Scan job {JobId} failed: {Code}", job.Id, ex.Code);
            Complete(job, ScanJobStatus.Failed, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan job {JobId} failed", job.Id);
            Complete(job, ScanJobStatus.Failed, "scanner_error", "The scanner failed unexpectedly. See the papertrail log for details.");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void Complete(ScanJob job, ScanJobStatus status, string? code = null, string? message = null)
    {
        if (job.Finish(status, _time.GetUtcNow(), code, message))
        {
            JobFinished?.Invoke(job);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex RequestIdPattern();
}
