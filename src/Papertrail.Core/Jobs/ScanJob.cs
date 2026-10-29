using System.Text.Json.Serialization;
using Papertrail.Scanning;

namespace Papertrail.Jobs;

[JsonConverter(typeof(CamelCaseEnumConverter<ScanJobStatus>))]
public enum ScanJobStatus
{
    Queued,
    AwaitingConsent,
    Scanning,
    Completed,
    Denied,
    Failed,
    Canceled,
}

[JsonConverter(typeof(CamelCaseEnumConverter<ScanTrigger>))]
public enum ScanTrigger
{
    /// <summary>POST /v1/scans with a verified Origin header.</summary>
    Api,

    /// <summary>papertrail://scan URL; the origin is a claim that is only honored for paired origins.</summary>
    ProtocolUrl,
}

public sealed class ScanJob
{
    private readonly object _gate = new();
    private readonly List<ScannedPage> _pages = [];
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ScanJob(string id, string origin, ScanOptions options, ScanTrigger trigger, DateTimeOffset createdAt)
    {
        Id = id;
        Origin = origin;
        Options = options;
        Trigger = trigger;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public string Origin { get; }

    public ScanOptions Options { get; }

    public ScanTrigger Trigger { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public ScanJobStatus Status { get; private set; } = ScanJobStatus.Queued;

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool IsFinished => Status is ScanJobStatus.Completed or ScanJobStatus.Denied or ScanJobStatus.Failed or ScanJobStatus.Canceled;

    /// <summary>Completes when the job reaches a terminal status.</summary>
    public Task Finished => _finished.Task;

    internal CancellationTokenSource Cancellation { get; } = new();

    public IReadOnlyList<ScannedPage> Pages
    {
        get
        {
            lock (_gate)
            {
                return _pages.ToArray();
            }
        }
    }

    internal void AddPage(ScannedPage page)
    {
        lock (_gate)
        {
            _pages.Add(page);
        }
    }

    internal void SetStatus(ScanJobStatus status)
    {
        lock (_gate)
        {
            if (!IsFinished)
            {
                Status = status;
            }
        }
    }

    /// <returns>False when the job had already finished.</returns>
    internal bool Finish(ScanJobStatus status, DateTimeOffset at, string? errorCode = null, string? errorMessage = null)
    {
        lock (_gate)
        {
            if (IsFinished)
            {
                return false;
            }

            Status = status;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            FinishedAt = at;
        }

        _finished.TrySetResult();
        return true;
    }
}
