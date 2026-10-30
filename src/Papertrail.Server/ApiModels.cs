using Papertrail.Barcodes;
using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.Server;

public sealed record StatusResponse(string App, string Version, int ApiVersion, string Backend, bool Paired, bool PairingAllowed);

public sealed record PairResponse(bool Paired);

public sealed record ScannersResponse(IReadOnlyList<ScannerInfo> Scanners);

public sealed record ErrorResponse(string Error, string Message);

/// <param name="BarcodeStatus">Only when the scan asked for barcodes, like <paramref name="Barcodes"/>.</param>
public sealed record PageResponse(
    int Index,
    string MediaType,
    int Width,
    int Height,
    int Size,
    string Url,
    BarcodeStatus? BarcodeStatus = null,
    IReadOnlyList<BarcodeResult>? Barcodes = null);

/// <param name="Barcodes">Every page's barcodes in one list; only when the scan asked for barcodes.</param>
public sealed record ScanJobResponse(
    string Id,
    ScanJobStatus Status,
    ScanTrigger Trigger,
    OutputFormat Format,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    ErrorResponse? Error,
    IReadOnlyList<PageResponse> Pages,
    IReadOnlyList<BarcodeResult>? Barcodes = null)
{
    public static ScanJobResponse From(ScanJob job) => new(
        job.Id,
        job.Status,
        job.Trigger,
        job.Options.Format,
        job.CreatedAt,
        job.FinishedAt,
        job.ErrorCode is null ? null : new ErrorResponse(job.ErrorCode, job.ErrorMessage ?? job.ErrorCode),
        job.Pages.Select(p => WithBarcodes(new PageResponse(p.Index, p.MediaType, p.Width, p.Height, p.Data.Length, $"/v1/scans/{job.Id}/pages/{p.Index}"), job.Barcodes)).ToArray(),
        job.Barcodes?.All);

    private static PageResponse WithBarcodes(PageResponse page, JobBarcodes? barcodes) =>
        barcodes?.ForPage(page.Index) is { } found ? page with { BarcodeStatus = found.Status, Barcodes = found.Barcodes } : page;
}
