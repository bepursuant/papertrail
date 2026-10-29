using Papertrail.Protocol;
using Papertrail.Scanning;

namespace Papertrail.Core.Tests;

public class PapertrailUriTests
{
    private const string RequestId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("papertrail://launch")]
    [InlineData("papertrail://launch/")]
    [InlineData("papertrail:launch")]
    [InlineData("PAPERTRAIL://")]
    public void Parses_launch(string url)
    {
        Assert.True(PapertrailUri.TryParse(url, out var command, out _));
        Assert.Equal(ProtocolAction.Launch, command!.Action);
    }

    [Fact]
    public void Parses_scan_with_all_options()
    {
        string url = $"papertrail://scan/?v=1&origin=https%3A%2F%2Fapp.example.com&request={RequestId}&scannerId=My%20Scanner&dpi=300&colorMode=gray&source=feeder&duplex=true&showDriverUi=1&maxPages=5";

        Assert.True(PapertrailUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(ProtocolAction.Scan, command!.Action);
        Assert.Equal("https://app.example.com", command.Origin);
        Assert.Equal(RequestId, command.RequestId);
        Assert.Equal(
            new ScanOptions { ScannerId = "My Scanner", Dpi = 300, ColorMode = ColorMode.Gray, Source = PaperSource.Feeder, Duplex = true, ShowDriverUi = true, MaxPages = 5 },
            command.Options);
    }

    [Fact]
    public void Build_and_parse_round_trip()
    {
        var options = new ScanOptions { ScannerId = "Fujitsu fi-7160 & co", Dpi = 600, ColorMode = ColorMode.BlackWhite, Source = PaperSource.Flatbed, Duplex = false, MaxPages = 2 };
        string url = PapertrailUri.BuildScan("https://app.example.com", RequestId, options);

        Assert.True(PapertrailUri.TryParse(url, out var command, out string? error), error);
        Assert.Equal(options, command!.Options);
        Assert.Equal("https://app.example.com", command.Origin);
    }

    [Fact]
    public void First_duplicate_parameter_wins()
    {
        string url = $"papertrail://scan?origin=https%3A%2F%2Fa.example&origin=https%3A%2F%2Fevil.example&request={RequestId}";
        Assert.True(PapertrailUri.TryParse(url, out var command, out _));
        Assert.Equal("https://a.example", command!.Origin);
    }

    [Theory]
    [InlineData("https://example.com", "not a papertrail")]
    [InlineData("papertrail://format-disk", "unknown action")]
    [InlineData("papertrail://scan?v=2&origin=https%3A%2F%2Fa.example&request=0123456789abcdef", "unsupported protocol version")]
    [InlineData("papertrail://scan?request=0123456789abcdef", "missing 'origin'")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example", "missing 'request'")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&dpi=-1", "dpi must be a whole number")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&dpi=5000", "dpi must be between")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&colorMode=sepia", "unknown colorMode")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&source=1", "unknown source")]
    [InlineData("papertrail://scan?origin=https%3A%2F%2Fa.example&request=0123456789abcdef&duplex=maybe", "duplex must be true or false")]
    public void Rejects_invalid_urls(string url, string expectedError)
    {
        Assert.False(PapertrailUri.TryParse(url, out _, out string? error));
        Assert.Contains(expectedError, error);
    }

    [Fact]
    public void Rejects_overlong_urls()
    {
        Assert.False(PapertrailUri.TryParse("papertrail://scan?x=" + new string('a', 3000), out _, out _));
    }
}
