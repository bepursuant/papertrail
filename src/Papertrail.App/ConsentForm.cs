using Papertrail.Consent;
using Papertrail.Jobs;
using Papertrail.Scanning;

namespace Papertrail.App;

/// <summary>Pairing and per-scan confirmation dialog. Closing it any other way counts as "no".</summary>
internal sealed class ConsentForm : Form
{
    private readonly CheckBox? _remember;

    private ConsentForm(string heading, string origin, string details, string approveText, string denyText, string? rememberText)
    {
        Text = "papertrail";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);
        Font = new Font("Segoe UI", 9.5f);
        Icon = SystemIcons.Shield;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(460, 0),
        };

        layout.Controls.Add(new Label
        {
            Text = heading,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Regular),
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = origin,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label
        {
            Text = details,
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Margin = new Padding(0, 0, 0, 12),
        });

        if (rememberText is not null)
        {
            _remember = new CheckBox { Text = rememberText, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
            layout.Controls.Add(_remember);
        }

        var approve = new Button { Text = approveText, AutoSize = true, MinimumSize = new Size(96, 30) };
        var deny = new Button { Text = denyText, AutoSize = true, MinimumSize = new Size(96, 30) };
        approve.Click += (_, _) =>
        {
            Approved = true;
            Close();
        };
        deny.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(deny);
        buttons.Controls.Add(approve);
        layout.Controls.Add(buttons);

        Controls.Add(layout);

        // Deliberately no AcceptButton: pressing Enter must not approve access.
        CancelButton = deny;
        Shown += (_, _) => deny.Focus();
    }

    public bool Approved { get; private set; }

    public bool Remember => _remember?.Checked == true;

    public static ConsentForm ForPairing(PairingRequest request) => new(
        "Allow this website to use your scanners?",
        request.Origin,
        "The site will be able to list your scanners and ask to scan documents. You will still be asked before each scan unless you choose otherwise. Only allow sites you trust.",
        "Allow",
        "Don't allow",
        rememberText: null);

    public static ConsentForm ForScan(ScanConsentRequest request)
    {
        var o = request.Options;
        string mode = o.ColorMode switch
        {
            ColorMode.Gray => "grayscale",
            ColorMode.BlackWhite => "black & white",
            _ => "color",
        };
        string source = o.Source switch
        {
            PaperSource.Feeder => "document feeder",
            PaperSource.Flatbed => "flatbed",
            _ => "default paper source",
        };
        string details = $"wants to scan from {request.ScannerName}: {o.Dpi} dpi, {mode}, {source}{(o.Duplex ? ", both sides" : string.Empty)}."
            + (request.Trigger == ScanTrigger.ProtocolUrl ? "\n\nThis scan was started from a papertrail:// link." : string.Empty)
            + "\n\nPlace your document in the scanner, then click Scan.";

        return new ConsentForm(
            "Scan a document?",
            request.Origin,
            details,
            "Scan",
            "Cancel",
            request.CanRemember ? "Don't ask again for this site" : null);
    }
}
