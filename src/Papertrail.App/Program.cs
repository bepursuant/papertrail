using System.IO;
using Papertrail.Hosting;

namespace Papertrail.App;

internal static class Program
{
    private const string MutexName = @"Local\Papertrail.SingleInstance.v1";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        if (options.Register || options.Unregister)
        {
            return RunRegistration(options, exePath);
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isPrimary);
        if (!isPrimary)
        {
            if (options.Urls.Count > 0)
            {
                SingleInstanceChannel.TrySendAsync(options.Urls.ToArray()).GetAwaiter().GetResult();
            }

            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(options);
        Application.Run(context);
        return 0;
    }

    private static int RunRegistration(AppOptions options, string exePath)
    {
        try
        {
            if (options.Register)
            {
                WindowsRegistration.RegisterProtocol(exePath);
                if (options.AutoStart)
                {
                    WindowsRegistration.SetAutoStart(true, exePath);
                }
            }
            else
            {
                WindowsRegistration.UnregisterProtocol();
                WindowsRegistration.SetAutoStart(false, exePath);
            }

            if (!options.Quiet)
            {
                MessageBox.Show(
                    options.Register ? $"papertrail:// links now open\n{exePath}" : "papertrail:// links are no longer handled.",
                    "papertrail",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            if (!options.Quiet)
            {
                MessageBox.Show(ex.Message, "papertrail registration failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }
}

internal sealed class AppOptions
{
    public List<string> Urls { get; } = [];

    public bool Register { get; private set; }

    public bool Unregister { get; private set; }

    public bool AutoStart { get; private set; }

    public bool Quiet { get; private set; }

    public bool UseMockScanner { get; private set; }

    /// <summary>Started by the sign-in Run key: stay silent until needed.</summary>
    public bool Background { get; private set; }

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        // Launched by the URL handler ("Papertrail.exe" "%1"): treat everything as URLs so a
        // crafted link can never smuggle in command-line switches.
        if (args.Length > 0 && args[0].StartsWith("papertrail:", StringComparison.OrdinalIgnoreCase))
        {
            options.Urls.AddRange(args.Where(a => a.StartsWith("papertrail:", StringComparison.OrdinalIgnoreCase)));
            return options;
        }

        foreach (string arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--register": options.Register = true; break;
                case "--unregister": options.Unregister = true; break;
                case "--autostart": options.AutoStart = true; break;
                case "--quiet": options.Quiet = true; break;
                case "--mock": options.UseMockScanner = true; break;
                case "--background": options.Background = true; break;
            }
        }

        return options;
    }
}
