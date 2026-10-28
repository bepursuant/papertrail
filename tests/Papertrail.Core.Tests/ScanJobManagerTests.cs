using Papertrail.Configuration;
using Papertrail.Consent;
using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.Core.Tests;

public class ScanJobManagerTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public async Task Completed_scan_returns_pages_to_the_owning_origin_only()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        runtime.Origins.Pair("https://other.example");

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(4, job.Pages.Count);
        Assert.Same(job, runtime.Jobs.Get(job.Id, Origin));
        Assert.Null(runtime.Jobs.Get(job.Id, "https://other.example"));
        Assert.False(runtime.Jobs.Delete(job.Id, "https://other.example"));
    }

    [Fact]
    public void Unpaired_origin_cannot_start_a_scan()
    {
        using var runtime = TestRuntime.Create();
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api));
        Assert.Equal("origin_not_paired", ex.Code);
    }

    [Fact]
    public async Task Declined_consent_finishes_as_denied_without_scanning()
    {
        var consent = new RecordingConsent(allow: false);
        using var runtime = TestRuntime.Create(consent);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Denied, job.Status);
        Assert.Equal("consent_denied", job.ErrorCode);
        Assert.Empty(job.Pages);
        Assert.Single(consent.ScanRequests);
        Assert.Equal("Papertrail Mock Feeder (duplex)", consent.ScanRequests[0].ScannerName);
    }

    [Fact]
    public async Task Remembered_consent_skips_the_prompt_next_time()
    {
        var consent = new RecordingConsent(allow: true, remember: true);
        using var runtime = TestRuntime.Create(consent);
        runtime.Origins.Pair(Origin);

        await runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api).Finished.WaitAsync(TimeSpan.FromSeconds(10));
        var second = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await second.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, second.Status);
        Assert.Single(consent.ScanRequests);
    }

    [Fact]
    public async Task Policy_can_force_a_prompt_for_every_scan()
    {
        var consent = new RecordingConsent(allow: true, remember: true);
        using var runtime = TestRuntime.Create(consent, new PapertrailPolicy { AlwaysConfirmScans = true });
        runtime.Origins.Pair(Origin);

        await runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api).Finished.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api).Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, consent.ScanRequests.Count);
        Assert.All(consent.ScanRequests, r => Assert.False(r.CanRemember));
    }

    [Fact]
    public async Task Scanner_errors_are_reported_with_their_code()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { ScannerId = MockScannerBackend.JammedId }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("paper_jam", job.ErrorCode);
    }

    [Fact]
    public async Task Max_pages_limits_the_feeder()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder, Duplex = true, MaxPages = 3 }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, job.Pages.Count);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it!!")]
    [InlineData("../../../../etc/passwd/x")]
    public void Rejects_bad_request_ids(string id)
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id));
        Assert.Equal("invalid_request_id", ex.Code);
    }

    [Fact]
    public void Rejects_duplicate_request_ids()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        string id = ScanJobManager.NewId();
        runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.ProtocolUrl, id));
        Assert.Equal("duplicate_request_id", ex.Code);
    }

    [Fact]
    public void Rejects_invalid_options()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        var ex = Assert.Throws<JobRejectedException>(() => runtime.Jobs.Start(Origin, new ScanOptions { Dpi = 10 }, ScanTrigger.Api));
        Assert.Equal("invalid_options", ex.Code);
    }

    [Fact]
    public async Task Delete_cancels_a_scan_waiting_for_consent()
    {
        using var runtime = TestRuntime.Create(new SilentConsent());
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await WaitUntil(() => job.Status == ScanJobStatus.AwaitingConsent);
        Assert.True(runtime.Jobs.Delete(job.Id, Origin));
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Null(runtime.Jobs.Get(job.Id, Origin));
    }

    [Fact]
    public async Task Finished_jobs_expire_after_the_retention_period()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var runtime = TestRuntime.Create(time: time);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(ScanJobManager.DefaultRetention - TimeSpan.FromSeconds(1));
        runtime.Jobs.PurgeExpired();
        Assert.NotNull(runtime.Jobs.Get(job.Id, Origin));

        time.Advance(TimeSpan.FromSeconds(2));
        runtime.Jobs.PurgeExpired();
        Assert.Null(runtime.Jobs.Get(job.Id, Origin));
    }

    [Fact]
    public async Task Scans_run_one_at_a_time()
    {
        var backend = new ConcurrencyProbe();
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var jobs = Enumerable.Range(0, 4).Select(_ => runtime.Jobs.Start(Origin, new ScanOptions(), ScanTrigger.Api)).ToArray();
        await Task.WhenAll(jobs.Select(j => j.Finished)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(jobs, j => Assert.Equal(ScanJobStatus.Completed, j.Status));
        Assert.Equal(1, backend.MaxConcurrent);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(10);
        }
    }

    private sealed class ConcurrencyProbe : IScannerBackend
    {
        private int _current;

        public int MaxConcurrent { get; private set; }

        public string Name => "probe";

        public Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScannerInfo>>([new ScannerInfo("probe", "Probe", null, "probe", true)]);

        public async Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(20, cancellationToken);
            Interlocked.Decrement(ref _current);
            var page = new ScannedPage(0, "image/png", 1, 1, [1]);
            onPage?.Invoke(page);
            return [page];
        }
    }
}
