using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.Server;

public sealed record StatusResponse(string App, string Version, int ApiVersion, string Backend, bool Paired, bool PairingAllowed);

public sealed record PairResponse(bool Paired);

public sealed record ScannersResponse(IReadOnlyList<ScannerInfo> Scanners);

public sealed record ErrorResponse(string Error, string Message);

public sealed record PageResponse(int Index, string MediaType, int Width, int Height, int Size, string Url);

public sealed record ScanJobResponse(
    string Id,
    ScanJobStatus Status,
    ScanTrigger Trigger,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    ErrorResponse? Error,
    IReadOnlyList<PageResponse> Pages)
{
    public static ScanJobResponse From(ScanJob job) => new(
        job.Id,
        job.Status,
        job.Trigger,
        job.CreatedAt,
        job.FinishedAt,
        job.ErrorCode is null ? null : new ErrorResponse(job.ErrorCode, job.ErrorMessage ?? job.ErrorCode),
        job.Pages.Select(p => new PageResponse(p.Index, p.MediaType, p.Width, p.Height, p.Data.Length, $"/v1/scans/{job.Id}/pages/{p.Index}")).ToArray());
}
