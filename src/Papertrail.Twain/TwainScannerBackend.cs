using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NTwain;
using NTwain.Data;
using Papertrail.Scanning;
using ColorMode = Papertrail.Scanning.ColorMode;

namespace Papertrail.Twain;

/// <summary>
/// TWAIN scanning through NTwain (MIT). Uses whatever TWAIN DSM the scanner driver installed
/// (TWAINDSM.dll, or twain_32.dll for 32-bit processes); papertrail does not ship a DSM.
/// A process only sees data sources of its own bitness.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TwainScannerBackend : IScannerBackend, IDisposable
{
    private readonly StaWorker _worker = new("papertrail-twain");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TWIdentity _appId;
    private readonly ILogger _logger;

    public TwainScannerBackend(ILogger<TwainScannerBackend>? logger = null)
    {
        _logger = logger ?? NullLogger<TwainScannerBackend>.Instance;
        var version = typeof(TwainScannerBackend).Assembly.GetName().Version ?? new Version(0, 1);
        _appId = TWIdentity.Create(DataGroups.Image, version, "Pursuant Solutions", "papertrail", "papertrail", "papertrail browser scanning bridge");
    }

    public string Name => "twain";

    public static string DescribeEnvironment() =>
        $"TWAIN DSM: {PlatformInfo.Current.ExpectedDsmPath} (exists: {PlatformInfo.Current.DsmExists}), process: {(Environment.Is64BitProcess ? "64" : "32")}-bit";

    public async Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _worker.InvokeAsync<IReadOnlyList<ScannerInfo>>(() =>
            {
                var session = OpenSession();
                try
                {
                    string? defaultName = session.DefaultSource?.Name;
                    return session
                        .Select(s => new ScannerInfo(s.Name, s.Name, s.Manufacturer, Name, s.Name == defaultName))
                        .ToArray();
                }
                finally
                {
                    session.Close();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var done = await _worker.InvokeAsync(() => BeginScan(options, onPage, cancellationToken)).ConfigureAwait(false);
            return await done.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _worker.Dispose();

    /// <summary>Runs on the STA worker. Returns a task that completes when the source is disabled.</summary>
    private Task<IReadOnlyList<ScannedPage>> BeginScan(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ScannedPage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pages = new List<ScannedPage>();
        Exception? transferError = null;

        var session = OpenSession();
        session.StopOnTransferError = true;

        session.TransferReady += (_, e) =>
        {
            if (ct.IsCancellationRequested || (options.MaxPages > 0 && pages.Count >= options.MaxPages))
            {
                e.CancelAll = true;
            }
        };

        session.DataTransferred += (_, e) =>
        {
            try
            {
                using var native = e.GetNativeImageStream();
                if (native is null)
                {
                    transferError ??= new ScannerException("unsupported_transfer", "The scanner returned image data papertrail cannot read.");
                    return;
                }

                var page = ToPngPage(native, pages.Count);
                pages.Add(page);
                onPage?.Invoke(page);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert scanned page");
                transferError ??= ex;
            }
        };

        session.TransferError += (_, e) =>
        {
            _logger.LogWarning(e.Exception, "TWAIN transfer error {ReturnCode}", e.ReturnCode);
            transferError ??= e.Exception ?? new ScannerException("transfer_error", $"The scanner reported an error ({e.ReturnCode}).");
        };

        // Per the NTwain docs, closing from SourceDisabled is the supported way to end a job.
        session.SourceDisabled += (_, _) =>
        {
            try
            {
                session.CurrentSource?.Close();
                session.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing TWAIN session");
            }

            if (ct.IsCancellationRequested)
            {
                completion.TrySetCanceled(ct);
            }
            else if (pages.Count == 0 && transferError is not null)
            {
                completion.TrySetException(transferError as ScannerException
                    ?? new ScannerException("transfer_error", "The scan failed. Check the scanner and try again.", transferError));
            }
            else
            {
                completion.TrySetResult(pages);
            }
        };

        DataSource? source = null;
        try
        {
            source = PickSource(session, options.ScannerId);
            var rc = source.Open();
            if (rc != ReturnCode.Success)
            {
                throw new ScannerException("scanner_unavailable", $"Could not open '{source.Name}' ({rc}). Is it switched on and connected?");
            }

            ApplyCapabilities(source, options);

            rc = source.Enable(options.ShowDriverUi ? SourceEnableMode.ShowUI : SourceEnableMode.NoUI, false, IntPtr.Zero);
            if (rc != ReturnCode.Success)
            {
                throw new ScannerException("scan_not_started", $"'{source.Name}' did not start scanning ({rc}).");
            }
        }
        catch
        {
            if (source?.IsOpen == true)
            {
                source.Close();
            }

            session.Close();
            throw;
        }

        return completion.Task;
    }

    private TwainSession OpenSession()
    {
        if (!PlatformInfo.Current.IsSupported)
        {
            throw new ScannerException("twain_unavailable", "No TWAIN driver manager is installed. Install your scanner's TWAIN driver.");
        }

        var session = new TwainSession(_appId);
        var rc = session.Open();
        if (rc != ReturnCode.Success)
        {
            throw new ScannerException("twain_unavailable", $"Could not open the TWAIN driver manager ({rc}).");
        }

        return session;
    }

    private static DataSource PickSource(TwainSession session, string? scannerId)
    {
        var sources = session.ToList();
        if (sources.Count == 0)
        {
            throw new ScannerException("no_scanners", $"No TWAIN scanners were found for this {(Environment.Is64BitProcess ? "64" : "32")}-bit app.");
        }

        if (scannerId is not null)
        {
            return sources.FirstOrDefault(s => s.Name == scannerId)
                ?? throw new ScannerException("scanner_not_found", $"Scanner '{scannerId}' was not found.");
        }

        return session.DefaultSource ?? sources[0];
    }

    private void ApplyCapabilities(DataSource source, ScanOptions options)
    {
        var caps = source.Capabilities;
        TrySet(caps.ICapXferMech, XferMech.Native, "transfer mechanism");
        TrySet(caps.ICapPixelType, options.ColorMode switch
        {
            ColorMode.Gray => PixelType.Gray,
            ColorMode.BlackWhite => PixelType.BlackWhite,
            _ => PixelType.RGB,
        }, "color mode");
        TrySet(caps.ICapXResolution, (TWFix32)options.Dpi, "x resolution");
        TrySet(caps.ICapYResolution, (TWFix32)options.Dpi, "y resolution");

        switch (options.Source)
        {
            case PaperSource.Feeder:
                TrySet(caps.CapFeederEnabled, BoolType.True, "feeder");
                TrySet(caps.CapAutoFeed, BoolType.True, "auto feed");
                break;
            case PaperSource.Flatbed:
                TrySet(caps.CapFeederEnabled, BoolType.False, "feeder");
                break;
        }

        TrySet(caps.CapDuplexEnabled, options.Duplex ? BoolType.True : BoolType.False, "duplex");
        TrySet(caps.CapXferCount, options.MaxPages > 0 ? options.MaxPages : -1, "page count");
    }

    private void TrySet<T>(ICapWrapper<T> cap, T value, string what)
    {
        try
        {
            if (cap.IsSupported && cap.CanSet)
            {
                var rc = cap.SetValue(value);
                if (rc != ReturnCode.Success)
                {
                    _logger.LogInformation("Scanner rejected {Setting}={Value} ({ReturnCode}); using its default", what, value, rc);
                }
            }
        }
        catch (Exception ex)
        {
            // Drivers vary widely; a capability we cannot set should not abort the scan.
            _logger.LogInformation(ex, "Could not set {Setting}", what);
        }
    }

    private static ScannedPage ToPngPage(Stream native, int index)
    {
        using var image = Image.FromStream(native);
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        return new ScannedPage(index, "image/png", image.Width, image.Height, png.ToArray());
    }
}
