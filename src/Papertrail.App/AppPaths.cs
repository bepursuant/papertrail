using System.IO;

namespace Papertrail.App;

internal static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Papertrail\logs: papertrail.log and the scanner diagnostics.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Papertrail", "logs");
}
