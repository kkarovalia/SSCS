using System.Net.Mail;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Where converted files are sent, and the mailbox they are sent from.
///
/// This is the most consequential screen in the application. SSCS decides which store an invoice
/// belongs to by the address the file arrived at — nothing inside the file does — so the "Send to"
/// box below is the store routing. That is why getting here needs the admin PIN.
///
/// The app password is write-only by design: it can be replaced but never read back, so the screen
/// cannot be used to harvest it. Leaving the box empty keeps whatever is already saved. Test Email
/// is here because the alternative is discovering a wrong password on a live delivery.
/// </summary>
internal sealed class EmailSettingsDialog : Form
{
    private readonly EmailSettings _original;
    private readonly bool _hadPassword;

    private readonly TextBox _to;
    private readonly TextBox _modisoftTo;
    private readonly TextBox _from;
    private readonly TextBox _password;
    private readonly TextBox _host;
    private readonly NumericUpDown _port;

    /// <summary>The settings as edited. Only meaningful when the dialog returned OK.</summary>
    public EmailSettings Result { get; private set; } = new();

    /// <summary>Set when the operator chose a new PIN from this dialog, so the caller can save it.</summary>
    public string? NewPinHash { get; private set; }

    public EmailSettingsDialog(EmailSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _original = current.Clone();
        _hadPassword = !string.IsNullOrWhiteSpace(current.ProtectedPassword);

        Text = "Email settings";
        AppIcon.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10f);
        MinimumSize = new Size(600, 380);
        Size = new Size(620, 860);

        // The fields scroll and the buttons are pinned to the bottom, so Save stays reachable on a
        // short screen or at 150% display scaling. Docked controls lay out in reverse add order, so
        // the scrolling area goes in first and the button strip after it.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16), AutoScroll = true,
        };
        var buttonStrip = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 8, 16, 12) };
        Controls.Add(layout);
        Controls.Add(buttonStrip);

        Shown += (_, _) => ClampToWorkingArea();

        layout.Controls.Add(new Label
        {
            Text = "Converted invoices are emailed with the Send Email button on the convert tabs.\n"
                 + "They are always saved to the output folder as well.",
            AutoSize = true, MaximumSize = new Size(500, 0), Margin = new Padding(0, 0, 0, 10),
            ForeColor = Color.FromArgb(95, 105, 130),
        });

        layout.Controls.Add(FieldLabel("Send to (the store's SSCS address)"));
        _to = Field(current.ToAddress);
        layout.Controls.Add(_to);
        layout.Controls.Add(Hint("This is what decides which store the invoice lands in. "
            + "Leave it empty if this store does not use SSCS."));

        layout.Controls.Add(FieldLabel("Send Modisoft files to (the store's own email)"));
        _modisoftTo = Field(current.ModisoftToAddress);
        layout.Controls.Add(_modisoftTo);
        layout.Controls.Add(Hint("PDF to Modisoft EDI sends its files here. Download the attachment and "
            + "import it into a purchase in Modisoft. Leave it empty if this store does not use Modisoft."));

        layout.Controls.Add(FieldLabel("Send from (the mailbox this PC signs in as)"));
        _from = Field(current.FromAddress);
        layout.Controls.Add(_from);
        layout.Controls.Add(Hint(
            "Use a mailbox kept only for this. If it is ever misused, the worst anyone can do is send "
            + "mail as it - they cannot read the store's real inbox."));

        layout.Controls.Add(FieldLabel("App password"));
        _password = Field(null);
        _password.UseSystemPasswordChar = true;
        layout.Controls.Add(_password);
        layout.Controls.Add(Hint(_hadPassword
            ? "A password is already saved. Leave this box empty to keep it, or type a new one to replace it. "
              + "It is stored encrypted and is never shown back."
            : "For Gmail this is the 16-character App Password from the Google account's security page, "
              + "not the normal account password. It is stored encrypted and is never shown back."));

        var serverRow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = new Padding(0, 8, 0, 0),
        };
        var serverCol = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = new Padding(0, 0, 16, 0) };
        serverCol.Controls.Add(FieldLabel("Mail server"));
        _host = new TextBox
        {
            Width = 240, Text = string.IsNullOrWhiteSpace(current.SmtpHost) ? EmailSettings.DefaultSmtpHost : current.SmtpHost,
        };
        serverCol.Controls.Add(_host);

        var portCol = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        portCol.Controls.Add(FieldLabel("Port"));
        _port = new NumericUpDown
        {
            Width = 90, Minimum = 1, Maximum = 65535,
            Value = current.SmtpPort is > 0 and <= 65535 ? current.SmtpPort : EmailSettings.DefaultSmtpPort,
        };
        portCol.Controls.Add(_port);

        serverRow.Controls.Add(serverCol);
        serverRow.Controls.Add(portCol);
        layout.Controls.Add(serverRow);
        layout.Controls.Add(Hint("Gmail uses smtp.gmail.com on port 587. Leave these alone unless your provider says otherwise."));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        save.Click += (_, _) => { if (!MayClose()) DialogResult = DialogResult.None; };

        var test = new Button { Text = "Send Test Email", AutoSize = true, Margin = new Padding(16, 0, 0, 0) };
        test.Click += OnSendTest;

        var changePin = new Button { Text = "Change PIN", AutoSize = true, Margin = new Padding(16, 0, 0, 0) };
        changePin.Click += OnChangePin;

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(test);
        buttons.Controls.Add(changePin);
        buttonStrip.Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => _to.Focus();
    }

    /// <summary>
    /// Keeps the dialog inside the screen, so the button strip along its bottom is never pushed off
    /// — which is what happens at 125-150% display scaling on a shorter laptop screen.
    /// </summary>
    private void ClampToWorkingArea()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, wa.Width);
        int h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height) Size = new Size(w, h);

        int x = Math.Max(wa.Left, Math.Min(Location.X, wa.Right - Width));
        int y = Math.Max(wa.Top, Math.Min(Location.Y, wa.Bottom - Height));
        if (x != Location.X || y != Location.Y) Location = new Point(x, y);
    }

    private static TextBox Field(string? value) => new()
    {
        Width = 380, Text = value ?? "", Margin = new Padding(0, 0, 0, 2),
    };

    private void OnChangePin(object? sender, EventArgs e)
    {
        // Passing null as the stored hash puts the dialog in "choose a new one" mode. The operator is
        // already past the gate to be here, so re-asking for the old PIN would add nothing.
        var (allowed, hash) = PinDialog.Authorize(this, null);
        if (!allowed || hash is null) return;
        NewPinHash = hash;
        MessageBox.Show(this, "The PIN has been changed. It takes effect when you press Save.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Sends a real message with the details currently on screen, using the typed password or, when
    /// that box was left empty, the saved one. Nothing is saved by testing.
    /// </summary>
    private void OnSendTest(object? sender, EventArgs e)
    {
        var draft = Compose(out string? problem);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string? password = _password.Text.Length > 0
            ? _password.Text
            : SecretProtector.Unprotect(_original.ProtectedPassword);

        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show(this, "Type the app password first, then send the test.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // One test per address filled in, so a store using both back offices proves both.
        var addresses = new[] { draft!.ToAddress, draft.ModisoftToAddress }
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        EmailSendResult result = EmailSendResult.Ok();
        var previous = Cursor;
        Cursor = Cursors.WaitCursor;
        Enabled = false;
        try
        {
            foreach (string address in addresses)
            {
                result = SmtpEmailSender.SendTest(draft, password, toAddress: address);
                if (!result.Sent) break;
            }
        }
        finally
        {
            Enabled = true;
            Cursor = previous;
        }

        if (result.Sent)
        {
            MessageBox.Show(this,
                $"A test message was sent to {string.Join(" and ", addresses)}.\n\nCheck that it arrived before relying on this.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this, result.ErrorMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Builds the settings from what is on screen. <paramref name="problem"/> comes back with the
    /// first thing wrong, or null when the fields are usable.
    /// </summary>
    private EmailSettings? Compose(out string? problem)
    {
        string to = _to.Text.Trim();
        string modisoftTo = _modisoftTo.Text.Trim();
        string from = _from.Text.Trim();
        string host = _host.Text.Trim();

        if (to.Length == 0 && modisoftTo.Length == 0)
        {
            problem = "Enter where converted files should be sent: the SSCS address, the Modisoft address, or both.";
            return null;
        }
        if (to.Length > 0 && !IsValidAddress(to)) { problem = $"\"{to}\" is not a valid email address."; return null; }
        if (modisoftTo.Length > 0 && !IsValidAddress(modisoftTo)) { problem = $"\"{modisoftTo}\" is not a valid email address."; return null; }
        if (from.Length == 0) { problem = "Enter the mailbox this PC should send from."; return null; }
        if (!IsValidAddress(from)) { problem = $"\"{from}\" is not a valid email address."; return null; }
        if (host.Length == 0) { problem = "Enter the mail server, for example smtp.gmail.com."; return null; }

        problem = null;
        return new EmailSettings
        {
            ToAddress = to.Length > 0 ? to : null,
            ModisoftToAddress = modisoftTo.Length > 0 ? modisoftTo : null,
            FromAddress = from,
            SmtpHost = host,
            SmtpPort = (int)_port.Value,
            // Empty means "keep what is saved", which is what makes the box write-only.
            ProtectedPassword = _password.Text.Length > 0
                ? SecretProtector.Protect(_password.Text)
                : _original.ProtectedPassword,
            AdminPinHash = NewPinHash ?? _original.AdminPinHash,
        };
    }

    private bool MayClose()
    {
        var composed = Compose(out string? problem);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (string.IsNullOrWhiteSpace(composed!.ProtectedPassword))
        {
            MessageBox.Show(this,
                _password.Text.Length > 0
                    ? "The password could not be encrypted on this Windows account, so it was not saved."
                    : "Enter the app password for the sending mailbox.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        Result = composed;
        return true;
    }

    private static bool IsValidAddress(string address)
    {
        try
        {
            var parsed = new MailAddress(address);
            // MailAddress accepts a bare "name" as a display name with no host, so check the host too.
            return parsed.Host.Contains('.') && parsed.Address == address;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 3),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(500, 0),
        Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(110, 118, 138),
        Margin = new Padding(0, 0, 0, 2),
    };
}
