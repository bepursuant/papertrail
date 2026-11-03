using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.Core.Tests;

public class AdfAutoStartTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public async Task Already_loaded_feeder_scans_without_waiting()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true, Source = PaperSource.Feeder }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Equal(2, job.Pages.Count);
    }

    [Fact]
    public async Task Waits_until_the_feeder_is_loaded_then_scans()
    {
        var paper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new MockScannerBackend(new MockScannerOptions
        {
            PageDelay = TimeSpan.FromMilliseconds(1),
            FeederSheets = 1,
            HoldPaperUntil = paper.Task,
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true, Source = PaperSource.Auto }, ScanTrigger.Api);
        await WaitForStatus(job, ScanJobStatus.WaitingForPaper);

        paper.SetResult();
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Single(job.Pages);
    }

    [Fact]
    public async Task Click_to_scan_does_not_wait_for_paper()
    {
        var paper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new MockScannerBackend(new MockScannerOptions
        {
            PageDelay = TimeSpan.FromMilliseconds(1),
            FeederSheets = 1,
            HoldPaperUntil = paper.Task,
            AdfWaitTimeout = TimeSpan.FromSeconds(30),
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { Source = PaperSource.Feeder }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Completed, job.Status);
        Assert.Single(job.Pages);
        Assert.False(paper.Task.IsCompleted);
    }

    [Fact]
    public async Task Flatbed_fails_immediately()
    {
        var paper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new MockScannerBackend(new MockScannerOptions
        {
            PageDelay = TimeSpan.FromMilliseconds(1),
            HoldPaperUntil = paper.Task,
            AdfWaitTimeout = TimeSpan.FromSeconds(30),
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var started = DateTime.UtcNow;
        var job = runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true, ScannerId = MockScannerBackend.FlatbedId }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("no_adf", job.ErrorCode);
        Assert.Contains("document feeder", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(job.Pages);
        Assert.False(paper.Task.IsCompleted);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Flatbed_source_is_rejected_before_a_scan_starts()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);

        var ex = Assert.Throws<JobRejectedException>(() =>
            runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true, Source = PaperSource.Flatbed }, ScanTrigger.Api));

        Assert.Equal("invalid_options", ex.Code);
        Assert.Contains("flatbed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Times_out_when_no_paper_is_placed()
    {
        var backend = new MockScannerBackend(new MockScannerOptions
        {
            PageDelay = TimeSpan.FromMilliseconds(1),
            HoldPaperUntil = new TaskCompletionSource().Task,
            AdfWaitTimeout = TimeSpan.FromMilliseconds(40),
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true }, ScanTrigger.Api);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Failed, job.Status);
        Assert.Equal("no_paper", job.ErrorCode);
        Assert.Contains("document feeder", job.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(job.Pages);
    }

    [Fact]
    public async Task Canceling_the_wait_does_not_scan()
    {
        var backend = new MockScannerBackend(new MockScannerOptions
        {
            PageDelay = TimeSpan.FromMilliseconds(1),
            HoldPaperUntil = new TaskCompletionSource().Task,
            AdfWaitTimeout = TimeSpan.FromSeconds(30),
        });
        using var runtime = TestRuntime.Create(backend: backend);
        runtime.Origins.Pair(Origin);

        var job = runtime.Jobs.Start(Origin, new ScanOptions { AdfAutoStart = true }, ScanTrigger.Api);
        await WaitForStatus(job, ScanJobStatus.WaitingForPaper);
        Assert.True(runtime.Jobs.Delete(job.Id, Origin));
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ScanJobStatus.Canceled, job.Status);
        Assert.Empty(job.Pages);
    }

    private static async Task WaitForStatus(ScanJob job, ScanJobStatus status)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (job.Status != status)
        {
            if (job.IsFinished || DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Job status was {job.Status} ({job.ErrorCode}), expected {status}.");
            }

            await Task.Delay(10);
        }
    }
}
