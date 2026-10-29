using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Papertrail.Configuration;
using Papertrail.Consent;
using Papertrail.Jobs;
using Papertrail.Protocol;
using Papertrail.Scanning;
using Papertrail.Security;

namespace Papertrail;

/// <summary>Everything a host (tray app, dev host, tests) needs, wired together.</summary>
public sealed class PapertrailRuntime : IDisposable
{
    public PapertrailRuntime(
        IScannerBackend backend,
        IConsentService consent,
        SettingsStore settings,
        PapertrailPolicy policy,
        TimeProvider? time = null,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        Backend = backend;
        Consent = consent;
        Settings = settings;
        Policy = policy;
        Origins = new OriginRegistry(settings, policy, time);
        Jobs = new ScanJobManager(backend, consent, Origins, time, logger: loggerFactory.CreateLogger<ScanJobManager>());
        Logger = loggerFactory.CreateLogger<PapertrailRuntime>();
    }

    public IScannerBackend Backend { get; }

    public IConsentService Consent { get; }

    public SettingsStore Settings { get; }

    public PapertrailPolicy Policy { get; }

    public OriginRegistry Origins { get; }

    public ScanJobManager Jobs { get; }

    public int Port => Policy.Port ?? Settings.Snapshot().Port;

    private ILogger Logger { get; }

    /// <summary>Handles a papertrail:// URL received on the command line or from a second instance.</summary>
    public ProtocolResult HandleProtocolUrl(string url)
    {
        if (!PapertrailUri.TryParse(url, out var command, out string? error))
        {
            Logger.LogWarning("Ignoring invalid papertrail URL: {Error}", error);
            return new ProtocolResult(ProtocolOutcome.Invalid, error);
        }

        if (command!.Action == ProtocolAction.Launch)
        {
            return new ProtocolResult(ProtocolOutcome.Launched, null);
        }

        // The origin in a protocol URL is an unverified claim. We only act on it for origins that
        // were already paired through the HTTP API (where the browser vouches for Origin), and the
        // result can only be collected by that origin using the unguessable request id.
        if (!Origins.TryNormalize(command.Origin, out string origin) || !Origins.IsPaired(origin))
        {
            Logger.LogWarning("Rejected papertrail:// scan for unpaired origin {Origin}", command.Origin);
            return new ProtocolResult(ProtocolOutcome.OriginNotPaired, command.Origin);
        }

        try
        {
            var job = Jobs.Start(origin, command.Options, ScanTrigger.ProtocolUrl, command.RequestId);
            return new ProtocolResult(ProtocolOutcome.ScanStarted, job.Id);
        }
        catch (JobRejectedException ex)
        {
            Logger.LogWarning("Rejected papertrail:// scan: {Code}", ex.Code);
            return new ProtocolResult(ProtocolOutcome.Rejected, ex.Message);
        }
    }

    public void Dispose() => Jobs.Dispose();
}

public enum ProtocolOutcome
{
    Launched,
    ScanStarted,
    OriginNotPaired,
    Rejected,
    Invalid,
}

public sealed record ProtocolResult(ProtocolOutcome Outcome, string? Detail);
