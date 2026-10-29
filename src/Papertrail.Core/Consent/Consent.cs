using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.Consent;

public sealed record PairingRequest(string Origin);

public sealed record ScanConsentRequest(
    string Origin,
    string ScannerName,
    ScanOptions Options,
    ScanTrigger Trigger,
    bool CanRemember);

public sealed record ScanConsent(bool Allowed, bool RememberForOrigin = false)
{
    public static ScanConsent Deny { get; } = new(false);
}

/// <summary>Asks the person at the keyboard. Implementations must time out rather than hang forever.</summary>
public interface IConsentService
{
    Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken);

    Task<ScanConsent> RequestScanAsync(ScanConsentRequest request, CancellationToken cancellationToken);
}

/// <summary>Non-interactive consent for the mock dev host and tests. Never use with a real scanner.</summary>
public sealed class FixedConsentService : IConsentService
{
    private readonly bool _allow;
    private int _pairingRequests;
    private int _scanRequests;

    public FixedConsentService(bool allow)
    {
        _allow = allow;
    }

    public int PairingRequests => Volatile.Read(ref _pairingRequests);

    public int ScanRequests => Volatile.Read(ref _scanRequests);

    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pairingRequests);
        return Task.FromResult(_allow);
    }

    public Task<ScanConsent> RequestScanAsync(ScanConsentRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _scanRequests);
        return Task.FromResult(new ScanConsent(_allow));
    }
}
