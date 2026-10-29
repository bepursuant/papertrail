using System.Globalization;
using Microsoft.Extensions.Logging;
using Papertrail;
using Papertrail.Configuration;
using Papertrail.Consent;
using Papertrail.Hosting;
using Papertrail.Scanning;
using Papertrail.Server;

// Headless papertrail host with the mock scanner. Runs anywhere .NET runs (Linux CI, macOS,
// Windows without a scanner). Consent prompts are answered automatically, so never point this
// at real hardware.

var port = PapertrailRuntime.FixedPort;
bool allow = true;
var preApproved = new List<string>();
var urls = new List<string>();
int pageDelayMs = 400;
string? settingsPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--consent":
            allow = args[++i] switch
            {
                "allow" => true,
                "deny" => false,
                var other => throw new ArgumentException($"--consent must be allow or deny, not '{other}'."),
            };
            break;
        case "--allow-origin":
            preApproved.Add(args[++i]);
            break;
        case "--page-delay-ms":
            pageDelayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--settings":
            settingsPath = args[++i];
            break;
        case "-h" or "--help":
            Console.WriteLine("""
                papertrail dev host (mock scanner)

                  --port <n>             API port (default 47115; the real app always uses 47115)
                  --consent allow|deny   auto-answer pairing prompts (default allow)
                  --allow-origin <url>   pre-approve an origin, like policy.json AllowedOrigins
                  --page-delay-ms <n>    simulated time per page (default 400)
                  --settings <path>      persist pairings to a JSON file (default: in memory)
                  papertrail://...       forward a protocol URL to the running host (or handle it at startup)
                """);
            return 0;
        default:
            urls.Add(args[i]);
            break;
    }
}

string pipeName = $"{SingleInstanceChannel.DefaultPipeName}-dev-{port}";
if (urls.Count > 0 && await SingleInstanceChannel.TrySendAsync(urls.ToArray(), pipeName, TimeSpan.FromMilliseconds(500)))
{
    Console.WriteLine($"Forwarded {urls.Count} URL(s) to the running dev host.");
    return 0;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("papertrail");

var policy = new PapertrailPolicy { AllowedOrigins = preApproved };
var settings = new SettingsStore(settingsPath);
var consent = new LoggingConsent(new FixedConsentService(allow), logger);
using var runtime = new PapertrailRuntime(
    new MockScannerBackend(new MockScannerOptions { PageDelay = TimeSpan.FromMilliseconds(pageDelayMs) }),
    consent,
    settings,
    policy,
    loggerFactory: loggerFactory,
    port: port);

runtime.Jobs.JobFinished += job => logger.LogInformation("Scan {Id} for {Origin}: {Status} ({Pages} pages)", job.Id, job.Origin, job.Status, job.Pages.Count);

var app = PapertrailServer.Build(runtime, b =>
{
    b.UseQuietLogging();
    b.Logging.ClearProviders();
    b.Logging.AddSimpleConsole(o => o.SingleLine = true);
});

await using var channel = new SingleInstanceChannel(pipeName);
channel.Start(forwarded =>
{
    foreach (string url in forwarded)
    {
        var result = runtime.HandleProtocolUrl(url);
        logger.LogInformation("papertrail URL -> {Outcome} {Detail}", result.Outcome, result.Detail);
    }

    return Task.CompletedTask;
});

foreach (string url in urls)
{
    var result = runtime.HandleProtocolUrl(url);
    logger.LogInformation("papertrail URL -> {Outcome} {Detail}", result.Outcome, result.Detail);
}

logger.LogInformation("papertrail dev host (mock scanner) listening on http://127.0.0.1:{Port}; consent={Consent}", port, allow ? "allow" : "deny");
await app.RunAsync();
return 0;

internal sealed class LoggingConsent(IConsentService inner, ILogger logger) : IConsentService
{
    public Task<bool> RequestPairingAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pairing request from {Origin} (auto-answered)", request.Origin);
        return inner.RequestPairingAsync(request, cancellationToken);
    }
}
