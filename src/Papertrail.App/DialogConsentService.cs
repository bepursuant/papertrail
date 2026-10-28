using Papertrail.Consent;

namespace Papertrail.App;

/// <summary>Shows <see cref="ConsentForm"/> on the UI thread; cancellation closes the dialog as a denial.</summary>
internal sealed class DialogConsentService(SynchronizationContext ui) : IConsentService
{
    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken) =>
        ShowAsync(() => ConsentForm.ForPairing(request), f => f.Approved, cancellationToken);

    public Task<ScanConsent> RequestScanAsync(ScanConsentRequest request, CancellationToken cancellationToken) =>
        ShowAsync(() => ConsentForm.ForScan(request), f => new ScanConsent(f.Approved, f.Approved && f.Remember), cancellationToken);

    private Task<T> ShowAsync<T>(Func<ConsentForm> create, Func<ConsentForm, T> read, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(
            _ =>
            {
                var form = create();
                var registration = cancellationToken.Register(() => ui.Post(_ => form.Close(), null));
                form.FormClosed += (_, _) =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(read(form));
                    form.Dispose();
                };
                form.Show();
                form.Activate();
            },
            null);
        return tcs.Task;
    }
}
