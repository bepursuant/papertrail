using System.Text.Json;
using Papertrail.Provenance;

namespace Papertrail.Configuration;

public sealed class PapertrailSettings
{
    public List<PairedOrigin> PairedOrigins { get; set; } = [];
}

public sealed class PairedOrigin
{
    public required string Origin { get; set; }

    public DateTimeOffset PairedAt { get; set; }

    /// <summary>
    /// Which approval wording the user accepted. 2 and later tell the user that stamped scans send
    /// their Windows user name and computer name to the site; missing means an older approval.
    /// </summary>
    public int? ApprovalVersion { get; set; }
}

/// <summary>
/// Machine-wide settings an administrator can deploy to %ProgramData%\Papertrail\policy.json.
/// Unset values fall back to defaults; set values win over user settings.
/// </summary>
public sealed class PapertrailPolicy
{
    /// <summary>Origins that are treated as paired without prompting.</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>When false, only <see cref="AllowedOrigins"/> may use papertrail.</summary>
    public bool? AllowPairing { get; set; }

    /// <summary>Accept http://localhost and http://127.0.0.1 pages (development). Default true.</summary>
    public bool? AllowInsecureLoopbackOrigins { get; set; }

    /// <summary>Provenance stamp rules; for example <c>{"mode": "required"}</c> stamps every scan.</summary>
    public ProvenanceStampPolicy? ProvenanceStamp { get; set; }

    public static PapertrailPolicy Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new PapertrailPolicy();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<PapertrailPolicy>(stream, PapertrailJson.Options) ?? new PapertrailPolicy();
    }
}

public static class PapertrailPaths
{
    public static string UserSettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Papertrail", "settings.json");

    public static string MachinePolicyFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Papertrail", "policy.json");
}

/// <summary>Thread-safe JSON settings file. Pass a null path for an in-memory store.</summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private PapertrailSettings _current;

    public SettingsStore(string? path)
    {
        _path = path;
        _current = Load(path);
    }

    public PapertrailSettings Snapshot()
    {
        lock (_gate)
        {
            return Clone(_current);
        }
    }

    public void Update(Action<PapertrailSettings> mutate)
    {
        lock (_gate)
        {
            var next = Clone(_current);
            mutate(next);
            Save(next);
            _current = next;
        }
    }

    private static PapertrailSettings Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new PapertrailSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<PapertrailSettings>(stream, PapertrailJson.Options) ?? new PapertrailSettings();
        }
        catch (JsonException)
        {
            // A corrupt file must not brick the app; it is rewritten on the next change.
            return new PapertrailSettings();
        }
    }

    private void Save(PapertrailSettings settings)
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, PapertrailJson.Indented));
        File.Move(temp, _path, overwrite: true);
    }

    private static PapertrailSettings Clone(PapertrailSettings s) =>
        JsonSerializer.Deserialize<PapertrailSettings>(JsonSerializer.Serialize(s, PapertrailJson.Options), PapertrailJson.Options)!;
}
