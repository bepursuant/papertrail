using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Papertrail.Configuration;
using Papertrail.Hosting;
using Papertrail.Jobs;
using Papertrail.Scanning;
using Papertrail.Server;
using Papertrail.Twain;

namespace Papertrail.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppOptions _options;
    private readonly WindowsFormsSynchronizationContext _ui;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly PapertrailRuntime _runtime;
    private readonly WebApplication _server;
    private readonly SingleInstanceChannel _channel = new();
    private readonly NotifyIcon _tray;
    private readonly string _exePath = Environment.ProcessPath ?? Application.ExecutablePath;
    private ApprovedSitesForm? _sitesForm;
    private bool _disposed;

    public TrayApplicationContext(AppOptions options)
    {
        _options = options;
        _ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_ui);

        _logProvider = new FileLoggerProvider(AppPaths.LogDirectory);
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logProvider).SetMinimumLevel(LogLevel.Information));
        _logger = _loggerFactory.CreateLogger("papertrail");

        IScannerBackend backend = options.UseMockScanner
            ? new MockScannerBackend()
            : new TwainScannerBackend(_loggerFactory.CreateLogger<TwainScannerBackend>());
        if (!options.UseMockScanner)
        {
            _logger.LogInformation("{Environment}", TwainScannerBackend.DescribeEnvironment());
        }

        _runtime = new PapertrailRuntime(
            backend,
            new DialogConsentService(_ui),
            new SettingsStore(PapertrailPaths.UserSettingsFile),
            PapertrailPolicy.Load(PapertrailPaths.MachinePolicyFile),
            loggerFactory: _loggerFactory);
        _runtime.Jobs.JobFinished += job => _ui.Post(_ => OnJobFinished(job), null);

        _server = PapertrailServer.Build(_runtime, b =>
        {
            b.Logging.ClearProviders();
            b.Logging.AddProvider(_logProvider);
            b.UseQuietLogging();
        });

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "papertrail",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowApprovedSites();

        EnsureProtocolRegistration();
        StartServer();

        _channel.Start(args =>
        {
            _ui.Post(_ => OnForwardedArguments(args), null);
            return Task.CompletedTask;
        });

        HandleUrls(options.Urls);
    }

    // WinForms disposes the context when the message loop ends, and Main's using disposes it again.
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _tray.Visible = false;
            _tray.Dispose();
            Task.Run(() => _channel.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                Task.Run(() => _server.StopAsync(stopTimeout.Token)).GetAwaiter().GetResult();
            }

            _runtime.Dispose();
            (_runtime.Backend as IDisposable)?.Dispose();
            _loggerFactory.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        string backend = _options.UseMockScanner ? "mock scanner" : "TWAIN";
        menu.Items.Add(new ToolStripMenuItem($"papertrail {PapertrailServer.AppVersion} - 127.0.0.1:{_runtime.Port} ({backend})") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Approved websites...", null, (_, _) => ShowApprovedSites());

        var autoStart = new ToolStripMenuItem("Start when I sign in") { Checked = WindowsRegistration.IsAutoStartEnabled(), CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) => WindowsRegistration.SetAutoStart(autoStart.Checked, _exePath);
        menu.Items.Add(autoStart);

        menu.Items.Add("Open log folder", null, (_, _) => Process.Start(new ProcessStartInfo(_logProvider.LogDirectory) { UseShellExecute = true }));
        if (!_options.UseMockScanner)
        {
            menu.Items.Add("Diagnose scanner...", null, (_, _) => StartTool("--diagnose-scanner"));
            menu.Items.Add("Test scan...", null, (_, _) => StartTool("--test-scan"));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void StartTool(string tool)
    {
        _logger.LogInformation("Starting {Tool} from the tray menu", tool);
        try
        {
            Process.Start(new ProcessStartInfo(_exePath, tool) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogError(ex, "Could not start {Tool}", tool);
        }
    }

    private void EnsureProtocolRegistration()
    {
        try
        {
            if (!WindowsRegistration.IsProtocolRegistered(_exePath))
            {
                WindowsRegistration.RegisterProtocol(_exePath);
                _logger.LogInformation("Registered papertrail:// handler for {Path}", _exePath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogWarning(ex, "Could not register the papertrail:// handler");
        }
    }

    private void StartServer()
    {
        try
        {
            Task.Run(() => _server.StartAsync()).GetAwaiter().GetResult();
            _logger.LogInformation("Listening on http://127.0.0.1:{Port}", _runtime.Port);
            if (!_options.Background && _options.Urls.Count == 0)
            {
                Notify("papertrail is running", "Websites you approve can now scan documents.", ToolTipIcon.Info);
            }
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not listen on port {Port}", _runtime.Port);
            Notify("papertrail could not start", $"Port {_runtime.Port} is already in use, maybe by papertrail in another signed-in Windows session. Close the other program, then restart papertrail.", ToolTipIcon.Error);
        }
    }

    private void OnForwardedArguments(string[] args)
    {
        if (args is [AppOptions.ExitCommand])
        {
            _logger.LogInformation("Exiting at the request of another papertrail process (installer)");
            ExitThread();
            return;
        }

        HandleUrls(args);
    }

    private void HandleUrls(IEnumerable<string> urls)
    {
        foreach (string url in urls)
        {
            var result = _runtime.HandleProtocolUrl(url);
            switch (result.Outcome)
            {
                case ProtocolOutcome.OriginNotPaired:
                    Notify("Scan request blocked", "A website asked to scan but is not approved. Open the site and use its Scan button to approve it first.", ToolTipIcon.Warning);
                    break;
                case ProtocolOutcome.Invalid:
                case ProtocolOutcome.Rejected:
                    Notify("Scan request ignored", result.Detail ?? "The papertrail link was not valid.", ToolTipIcon.Warning);
                    break;
            }
        }
    }

    private void OnJobFinished(ScanJob job)
    {
        if (job.Status == ScanJobStatus.Failed)
        {
            Notify("Scan failed", job.ErrorMessage ?? "The scanner reported an error.", ToolTipIcon.Error);
        }
    }

    private void ShowApprovedSites()
    {
        if (_sitesForm is { IsDisposed: false })
        {
            _sitesForm.Activate();
            return;
        }

        _sitesForm = new ApprovedSitesForm(_runtime.Origins);
        _sitesForm.Show();
    }

    private void Notify(string title, string text, ToolTipIcon icon) => _tray.ShowBalloonTip(5000, title, text, icon);
}
