using System.Collections.Concurrent;
using Papertrail.Configuration;

namespace Papertrail.Security;

/// <summary>Decides which web origins may use papertrail, combining machine policy and user pairings.</summary>
public sealed class OriginRegistry
{
    public static readonly TimeSpan DenialCooldown = TimeSpan.FromMinutes(10);

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

    public bool AlwaysConfirmScans => _policy.AlwaysConfirmScans ?? false;

    public bool TryNormalize(string? raw, out string origin) =>
        OriginRules.TryNormalize(raw, AllowInsecureLoopbackOrigins, out origin);

    public bool IsPaired(string origin) =>
        _policyOrigins.Contains(origin) || _settings.Snapshot().PairedOrigins.Any(p => p.Origin == origin);

    public bool IsPolicyOrigin(string origin) => _policyOrigins.Contains(origin);

    public bool ShouldAutoApproveScans(string origin) =>
        !AlwaysConfirmScans && _settings.Snapshot().PairedOrigins.Any(p => p.Origin == origin && p.AutoApproveScans);

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

    public void SetAutoApproveScans(string origin, bool value)
    {
        _settings.Update(s =>
        {
            var entry = s.PairedOrigins.FirstOrDefault(p => p.Origin == origin);
            if (entry is null && _policyOrigins.Contains(origin))
            {
                entry = new PairedOrigin { Origin = origin, PairedAt = _time.GetUtcNow() };
                s.PairedOrigins.Add(entry);
            }

            if (entry is not null)
            {
                entry.AutoApproveScans = value;
            }
        });
    }

    public void RecordDenial(string origin) => _deniedUntil[origin] = _time.GetUtcNow() + DenialCooldown;

    public bool IsInDenialCooldown(string origin) =>
        _deniedUntil.TryGetValue(origin, out var until) && until > _time.GetUtcNow();
}
