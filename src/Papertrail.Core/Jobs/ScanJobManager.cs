using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Papertrail.Consent;
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
/// Owns scan jobs: consent, running one scan at a time, keeping results in memory until they
/// expire, and releasing them only to the origin that owns the job.
/// </summary>
public sealed partial class ScanJobManager : IDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(2);
    private const int MaxJobs = 32;

    private readonly IScannerBackend _backend;
    private readonly IConsentService _consent;
    private readonly OriginRegistry _origins;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly ITimer _purgeTimer;
    private readonly ILogger _logger;

    public ScanJobManager(
        IScannerBackend backend,
        IConsentService consent,
        OriginRegistry origins,
        TimeProvider? time = null,
        TimeSpan? retention = null,
        ILogger<ScanJobManager>? logger = null)
    {
        _logger = logger ?? NullLogger<ScanJobManager>.Instance;
        _backend = backend;
        _consent = consent;
        _origins = origins;
        _time = time ?? TimeProvider.System;
        _retention = retention ?? DefaultRetention;
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
            await _scanLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Complete(job, ScanJobStatus.Canceled, "canceled", "The scan was canceled.");
            return;
        }

        try
        {
            if (!_origins.ShouldAutoApproveScans(job.Origin))
            {
                job.SetStatus(ScanJobStatus.AwaitingConsent);
                string scannerName = await DescribeScannerAsync(job.Options.ScannerId, ct).ConfigureAwait(false);
                var request = new ScanConsentRequest(job.Origin, scannerName, job.Options, job.Trigger, CanRemember: !_origins.AlwaysConfirmScans);

                using var consentTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                consentTimeout.CancelAfter(ConsentTimeout);
                ScanConsent decision;
                try
                {
                    decision = await _consent.RequestScanAsync(request, consentTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    decision = ScanConsent.Deny;
                }

                if (!decision.Allowed)
                {
                    Complete(job, ScanJobStatus.Denied, "consent_denied", "The user declined the scan.");
                    return;
                }

                if (decision.RememberForOrigin && request.CanRemember)
                {
                    _origins.SetAutoApproveScans(job.Origin, true);
                }
            }

            job.SetStatus(ScanJobStatus.Scanning);
            var pages = await _backend.ScanAsync(job.Options, job.AddPage, ct).ConfigureAwait(false);
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

    private async Task<string> DescribeScannerAsync(string? scannerId, CancellationToken ct)
    {
        try
        {
            var scanners = await _backend.ListScannersAsync(ct).ConfigureAwait(false);
            var match = scannerId is null
                ? scanners.FirstOrDefault(s => s.IsDefault) ?? scanners.FirstOrDefault()
                : scanners.FirstOrDefault(s => s.Id == scannerId);
            return match?.Name ?? scannerId ?? "the default scanner";
        }
        catch (ScannerException)
        {
            return scannerId ?? "the default scanner";
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
