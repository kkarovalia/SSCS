using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// The PIN gate in front of the email settings, in both of its modes: asking for the PIN, and
/// setting one for the first time.
///
/// Converting and sending invoices never ask for this. It exists for one thing only: the address
/// converted files are sent to decides which store they land in, so anyone who can quietly change
/// it can misroute a store's whole delivery, and the PC is used by staff who come and go. Gating
/// the setting rather than the application keeps the everyday job unchanged, so nobody has a reason
/// to pass the PIN around.
/// </summary>
internal sealed class PinDialog : Form
{
    private readonly TextBox _pin;
    private readonly TextBox? _confirm;
    private readonly string? _storedHash;

    /// <summary>What the operator typed. Only meaningful when the dialog returned OK.</summary>
    public string Pin => _pin.Text;

    private PinDialog(string? storedHash, bool setting)
    {
        _storedHash = storedHash;

        Text = setting ? "Set an admin PIN" : "Admin PIN";
        AppIcon.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(460, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
        };
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Text = setting
                ? "Choose a PIN for the email settings"
                : "Enter the admin PIN to open the email settings",
            AutoSize = true, MaximumSize = new Size(400, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = setting
                ? "This PIN is only needed to change where converted files are sent. Converting and "
                  + "sending invoices never ask for it, so there is no reason for staff to know it.\n\n"
                  + "Write it down somewhere safe. It cannot be recovered - if it is lost, the email "
                  + "settings have to be set up again from scratch."
                : "This protects the address converted files are sent to.",
            AutoSize = true, MaximumSize = new Size(400, 0),
            ForeColor = Color.FromArgb(95, 105, 130), Margin = new Padding(0, 0, 0, 12),
        });

        layout.Controls.Add(FieldLabel(setting ? "New PIN" : "PIN"));
        _pin = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_pin);

        if (setting)
        {
            layout.Controls.Add(FieldLabel("Type it again"));
            _confirm = new TextBox { Width = 200, UseSystemPasswordChar = true, Margin = new Padding(0, 0, 0, 8) };
            layout.Controls.Add(_confirm);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var ok = new Button { Text = setting ? "Set PIN" : "OK", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) => { if (!MayClose(setting)) DialogResult = DialogResult.None; };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => _pin.Focus();
    }

    private bool MayClose(bool setting)
    {
        if (setting)
        {
            if (_pin.Text.Length < 4)
            {
                Warn("Please choose a PIN of at least 4 characters.");
                return false;
            }
            if (_pin.Text != _confirm!.Text)
            {
                Warn("The two PINs do not match. Please type the same PIN in both boxes.");
                _confirm.Clear();
                _confirm.Focus();
                return false;
            }
            return true;
        }

        if (!AdminPin.Verify(_pin.Text, _storedHash))
        {
            Warn("That PIN is not correct.");
            _pin.Clear();
            _pin.Focus();
            return false;
        }
        return true;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 3),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    /// <summary>
    /// Clears the way into the email settings. Sets a PIN the first time and asks for it every time
    /// after; returns the PIN's hash to store when a new one was just chosen, and null when the
    /// operator either cancelled or simply passed an existing PIN.
    /// </summary>
    public static (bool Allowed, string? NewPinHash) Authorize(IWin32Window owner, string? storedHash)
    {
        bool setting = string.IsNullOrWhiteSpace(storedHash);
        using var dialog = new PinDialog(storedHash, setting);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return (false, null);
        return (true, setting ? AdminPin.Hash(dialog.Pin) : null);
    }
}
