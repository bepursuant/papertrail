using System.Collections.Concurrent;
using Papertrail.Configuration;

namespace Papertrail.Security;

/// <summary>Decides which web origins may use papertrail, combining machine policy and user pairings.</summary>
public sealed class OriginRegistry
{
    public static readonly TimeSpan DenialCooldown = TimeSpan.FromMinutes(10);

    /// <summary>An unanswered pairing prompt counts as "no" after this long.</summary>
    public static readonly TimeSpan PairingPromptTimeout = TimeSpan.FromMinutes(2);

    private readonly SettingsStore _settings;
    private readonly PapertrailPolicy _policy;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _policyOrigins;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _deniedUntil = new(StringComparer.Ordinal);

    public OriginRegistry(SettingsStore settings, PapertrailPolicy policy, TimeProvider? time = null)
    {
        _settings = settings;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _policyOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in policy.AllowedOrigins)
        {
            if (OriginRules.TryNormalize(raw, AllowInsecureLoopbackOrigins, out string origin))
            {
                _policyOrigins.Add(origin);
            }
        }
    }

    public bool AllowPairing => _policy.AllowPairing ?? true;

    public bool AllowInsecureLoopbackOrigins => _policy.AllowInsecureLoopbackOrigins ?? true;

    public bool TryNormalize(string? raw, out string origin) =>
        OriginRules.TryNormalize(raw, AllowInsecureLoopbackOrigins, out origin);

    public bool IsPaired(string origin) =>
        _policyOrigins.Contains(origin) || _settings.Snapshot().PairedOrigins.Any(p => p.Origin == origin);

    public bool IsPolicyOrigin(string origin) => _policyOrigins.Contains(origin);

    public IReadOnlyList<PairedOrigin> ListUserPairings() => _settings.Snapshot().PairedOrigins;

    public IReadOnlyCollection<string> PolicyOrigins => _policyOrigins;

    public void Pair(string origin)
    {
        _deniedUntil.TryRemove(origin, out _);
        _settings.Update(s =>
        {
            if (!s.PairedOrigins.Any(p => p.Origin == origin))
            {
                s.PairedOrigins.Add(new PairedOrigin { Origin = origin, PairedAt = _time.GetUtcNow() });
            }
        });
    }

    public void Unpair(string origin) =>
        _settings.Update(s => s.PairedOrigins.RemoveAll(p => p.Origin == origin));

    public void RecordDenial(string origin) => _deniedUntil[origin] = _time.GetUtcNow() + DenialCooldown;

    public bool IsInDenialCooldown(string origin) =>
        _deniedUntil.TryGetValue(origin, out var until) && until > _time.GetUtcNow();
}
