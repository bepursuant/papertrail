using Papertrail.Configuration;
using Papertrail.Hosting;
using Papertrail.Imaging;
using Papertrail.Jobs;
using Papertrail.Protocol;
using Papertrail.Scanning;
using Papertrail.Security;

namespace Papertrail.Core.Tests;

public class RuntimeAndRegistryTests
{
    private const string Origin = TestRuntime.Origin;

    [Fact]
    public void Launch_url_is_accepted()
    {
        using var runtime = TestRuntime.Create();
        Assert.Equal(ProtocolOutcome.Launched, runtime.HandleProtocolUrl("papertrail://launch").Outcome);
    }

    [Fact]
    public void Protocol_scan_for_unpaired_origin_is_rejected_and_never_prompts()
    {
        var consent = new RecordingConsent(allow: true);
        using var runtime = TestRuntime.Create(consent);

        var result = runtime.HandleProtocolUrl(PapertrailUri.BuildScan(Origin, ScanJobManager.NewId(), new ScanOptions()));

        Assert.Equal(ProtocolOutcome.OriginNotPaired, result.Outcome);
        Assert.Empty(runtime.Jobs.List());
        Assert.Empty(consent.ScanRequests);
    }

    [Fact]
    public async Task Protocol_scan_for_paired_origin_runs_under_the_requested_id()
    {
        using var runtime = TestRuntime.Create();
        runtime.Origins.Pair(Origin);
        string id = ScanJobManager.NewId();

        var result = runtime.HandleProtocolUrl(PapertrailUri.BuildScan("https://APP.example.com:443", id, new ScanOptions { Source = PaperSource.Flatbed }));

        Assert.Equal(ProtocolOutcome.ScanStarted, result.Outcome);
        var job = runtime.Jobs.Get(id, Origin);
        Assert.NotNull(job);
        Assert.Equal(ScanTrigger.ProtocolUrl, job.Trigger);
        await job.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(job.Pages);
    }

    [Fact]
    public void Garbage_protocol_url_is_reported_invalid()
    {
        using var runtime = TestRuntime.Create();
        Assert.Equal(ProtocolOutcome.Invalid, runtime.HandleProtocolUrl("papertrail://rm-rf").Outcome);
    }

    [Fact]
    public void Policy_origins_count_as_paired_and_pairings_persist_to_disk()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var policy = new PapertrailPolicy { AllowedOrigins = ["https://Corp.Example.com/"] };
            var registry = new OriginRegistry(new SettingsStore(path), policy);

            Assert.True(registry.IsPaired("https://corp.example.com"));
            Assert.False(registry.IsPaired(Origin));

            registry.Pair(Origin);
            registry.SetAutoApproveScans(Origin, true);

            var reloaded = new OriginRegistry(new SettingsStore(path), new PapertrailPolicy());
            Assert.True(reloaded.IsPaired(Origin));
            Assert.True(reloaded.ShouldAutoApproveScans(Origin));

            reloaded.Unpair(Origin);
            Assert.False(new OriginRegistry(new SettingsStore(path), new PapertrailPolicy()).IsPaired(Origin));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            var store = new SettingsStore(path);
            Assert.Empty(store.Snapshot().PairedOrigins);
            Assert.Equal(PapertrailSettings.DefaultPort, store.Snapshot().Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Denial_cooldown_expires()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new OriginRegistry(new SettingsStore(null), new PapertrailPolicy(), time);

        registry.RecordDenial(Origin);
        Assert.True(registry.IsInDenialCooldown(Origin));

        time.Advance(OriginRegistry.DenialCooldown + TimeSpan.FromSeconds(1));
        Assert.False(registry.IsInDenialCooldown(Origin));
    }

    [Theory]
    [InlineData(ColorMode.Color, 2)]
    [InlineData(ColorMode.Gray, 0)]
    [InlineData(ColorMode.BlackWhite, 0)]
    public async Task Mock_pages_are_valid_a4_pngs(ColorMode mode, byte pngColorType)
    {
        var backend = new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.Zero });
        var pages = await backend.ScanAsync(new ScanOptions { Dpi = 300, ColorMode = mode, ScannerId = MockScannerBackend.FlatbedId }, null, CancellationToken.None);

        var page = Assert.Single(pages);
        Assert.True(PngEncoder.TryReadSize(page.Data, out int w, out int h));
        Assert.Equal((827, 1169), (w, h)); // rendered at the 100 dpi mock cap
        Assert.Equal((w, h), (page.Width, page.Height));
        Assert.Equal(pngColorType, page.Data[25]);
    }

    [Fact]
    public async Task Single_instance_channel_forwards_arguments()
    {
        string pipe = $"papertrail-test-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = new SingleInstanceChannel(pipe);
        channel.Start(args =>
        {
            received.TrySetResult(args);
            return Task.CompletedTask;
        });

        Assert.True(await SingleInstanceChannel.TrySendAsync(["papertrail://launch", "x y"], pipe, TimeSpan.FromSeconds(5)));
        Assert.Equal(["papertrail://launch", "x y"], await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Sending_without_a_running_instance_fails_fast()
    {
        Assert.False(await SingleInstanceChannel.TrySendAsync(["papertrail://launch"], $"papertrail-none-{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200)));
    }
}
