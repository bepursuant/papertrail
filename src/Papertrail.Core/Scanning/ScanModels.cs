using System.Text.Json.Serialization;

namespace Papertrail.Scanning;

[JsonConverter(typeof(CamelCaseEnumConverter<ColorMode>))]
public enum ColorMode
{
    Color,
    Gray,
    BlackWhite,
}

[JsonConverter(typeof(CamelCaseEnumConverter<PaperSource>))]
public enum PaperSource
{
    Auto,
    Flatbed,
    Feeder,
}

public sealed record ScanOptions
{
    public const int MinDpi = 50;
    public const int MaxDpi = 1200;
    public const int MaxPageLimit = 500;

    /// <summary>Scanner id from <c>/v1/scanners</c>; null means the default scanner.</summary>
    public string? ScannerId { get; init; }

    public int Dpi { get; init; } = 200;

    public ColorMode ColorMode { get; init; } = ColorMode.Color;

    public PaperSource Source { get; init; } = PaperSource.Auto;

    public bool Duplex { get; init; }

    /// <summary>Show the vendor's TWAIN driver dialog instead of scanning with these settings.</summary>
    public bool ShowDriverUi { get; init; }

    /// <summary>0 means no limit.</summary>
    public int MaxPages { get; init; }

    /// <summary>Returns a list of validation problems; empty when the options are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Dpi is < MinDpi or > MaxDpi)
        {
            errors.Add($"dpi must be between {MinDpi} and {MaxDpi}.");
        }

        if (MaxPages is < 0 or > MaxPageLimit)
        {
            errors.Add($"maxPages must be between 0 and {MaxPageLimit}.");
        }

        if (ScannerId is { Length: > 256 })
        {
            errors.Add("scannerId is too long.");
        }

        if (!Enum.IsDefined(ColorMode))
        {
            errors.Add("colorMode is not supported.");
        }

        if (!Enum.IsDefined(Source))
        {
            errors.Add("source is not supported.");
        }

        return errors;
    }
}

public sealed record ScannerInfo(string Id, string Name, string? Manufacturer, string Backend, bool IsDefault);

public sealed class ScannedPage
{
    public ScannedPage(int index, string mediaType, int width, int height, byte[] data)
    {
        Index = index;
        MediaType = mediaType;
        Width = width;
        Height = height;
        Data = data;
    }

    public int Index { get; }

    public string MediaType { get; }

    public int Width { get; }

    public int Height { get; }

    public byte[] Data { get; }
}

/// <summary>A scanner-side failure with a message that is safe to show to the web page.</summary>
public sealed class ScannerException : Exception
{
    public ScannerException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IScannerBackend
{
    /// <summary>Short identifier such as "twain" or "mock".</summary>
    string Name { get; }

    Task<IReadOnlyList<ScannerInfo>> ListScannersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Scans all pages for one job. <paramref name="onPage"/> is invoked as each page arrives so
    /// callers can expose partial progress.
    /// </summary>
    Task<IReadOnlyList<ScannedPage>> ScanAsync(ScanOptions options, Action<ScannedPage>? onPage, CancellationToken cancellationToken);
}
