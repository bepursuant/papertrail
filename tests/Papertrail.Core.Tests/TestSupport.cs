using Papertrail.Configuration;
using Papertrail.Consent;
using Papertrail.Scanning;

namespace Papertrail.Core.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class TestRuntime
{
    public const string Origin = "https://app.example.com";

    public static PapertrailRuntime Create(
        IConsentService? consent = null,
        PapertrailPolicy? policy = null,
        TimeProvider? time = null,
        IScannerBackend? backend = null) =>
        new(
            backend ?? new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(1), FeederSheets = 2 }),
            consent ?? new FixedConsentService(allow: true),
            new SettingsStore(path: null),
            policy ?? new PapertrailPolicy(),
            time);
}

/// <summary>Consent stub that records requests and can remember "don't ask again".</summary>
internal sealed class RecordingConsent(bool allow, bool remember = false) : IConsentService
{
    public List<ScanConsentRequest> ScanRequests { get; } = [];

    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken) => Task.FromResult(allow);

    public Task<ScanConsent> RequestScanAsync(ScanConsentRequest request, CancellationToken cancellationToken)
    {
        lock (ScanRequests)
        {
            ScanRequests.Add(request);
        }

        return Task.FromResult(new ScanConsent(allow, remember));
    }
}

/// <summary>Never answers until canceled, like a user who walked away.</summary>
internal sealed class SilentConsent : IConsentService
{
    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => false, TaskScheduler.Default);

    public async Task<ScanConsent> RequestScanAsync(ScanConsentRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return ScanConsent.Deny;
    }
}
