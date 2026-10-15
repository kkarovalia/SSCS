using System.Diagnostics;
using System.Reflection;
using EdiToSscs.Core;

namespace EdiToSscs.App;

public sealed class MainForm : Form, IDestinationProvider, IInvoiceDetailsPrompt, IInvoiceVisionReader,
    IBimboVisionReader, IDrPepperVisionReader, IRedBullVisionReader, IBlueBellVisionReader,
    IChubiwinosVisionReader
{
    // Option A — navy primary, light surfaces, red used sparingly for brand accent.
    private static readonly Color ThemeBg = Color.FromArgb(248, 249, 252);
    private static readonly Color ThemePanel = Color.White;
    private static readonly Color ThemeNavy = Color.FromArgb(42, 56, 145);
    private static readonly Color ThemeNavySoft = Color.FromArgb(232, 236, 248);
    private static readonly Color ThemeBorder = Color.FromArgb(120, 135, 185);
    private static readonly Color ThemeMuted = Color.FromArgb(95, 105, 130);
    private static readonly Color ThemeText = Color.FromArgb(35, 42, 65);
    private static readonly Color ThemeRedAccent = Color.FromArgb(209, 40, 47);
    private static readonly Color ThemeButtonText = Color.White;

    private readonly SettingsStore _settingsStore = new();
    private readonly ConversionLogger _logger = new();
    private readonly Image? _logoImage;

    private ConvertTab _ediTab = null!;
    private ConvertTab _pdfTab = null!;
    private ConvertTab _modisoftTab = null!;

    private string? _currentFolder;
    private bool _firstShownHandled;

    /// <summary>
    /// The email configuration as last saved. Guarded because Send Email runs on a background
    /// thread while Settings can be edited on the UI thread.
    /// </summary>
    private EmailSettings _email = new();
    private readonly object _emailLock = new();
    private readonly SmtpEmailSender _emailSender;

    /// <summary>
    /// Sends PDF to Modisoft EDI files to the Modisoft address. Its own sender, from the same
    /// settings, so nothing a tab does can reach the other system's address.
    /// </summary>
    private readonly SmtpEmailSender _modisoftEmailSender;

    /// <summary>
    /// The AI reader's configuration as last saved, guarded for the same reason as the email
    /// settings: a conversion runs on a background thread while Settings can be edited on the UI one.
    /// </summary>
    private VisionSettings _vision = new();
    private readonly object _visionLock = new();

    private Label _folderValueLabel = null!;
    private Label _emailValueLabel = null!;
    private Label _visionValueLabel = null!;

    public MainForm()
    {
        _ = _logger.CleanupOldLogs(DateTime.UtcNow);
        var settings = _settingsStore.Load();
        _currentFolder = settings.OutputFolder;
        _email = settings.Email;
        _vision = settings.Vision;
        _logoImage = LoadEmbeddedLogo();

        // Reads the live settings on every send, so a change in Settings takes effect immediately.
        _emailSender = new SmtpEmailSender(() => { lock (_emailLock) return _email.Clone(); });
        _modisoftEmailSender = new SmtpEmailSender(
            () => { lock (_emailLock) return _email.Clone(); }, target: OutputTarget.Modisoft);

        BuildLayout();
        UpdateFolderLabel();
        UpdateEmailLabel();
        UpdateVisionLabel();

        Shown += OnFormShown;
        FormClosing += OnFormClosingConfirm;
        FormClosed += (_, _) => _logoImage?.Dispose();
    }

    /// <summary>Marshals a UI update without throwing once the window has started closing.</summary>
    private void RunOnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
        else
        {
            action();
        }
    }

    private void OnFormClosingConfirm(object? sender, FormClosingEventArgs e)
    {
        if (!_ediTab.IsBusy && !_pdfTab.IsBusy && !_modisoftTab.IsBusy) return;
        var answer = MessageBox.Show(
            this,
            "Files are still being converted. If you close now, unfinished conversions will stop.\n\nClose anyway?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            e.Cancel = true;
    }

    // ---------------------------------------------------------------- IDestinationProvider

    public string? CurrentFolder => _currentFolder;

    public DestinationStatus ValidateCurrent() => DestinationValidator.Validate(_currentFolder);

    public string? RequestNewFolder(string reasonMessage)
    {
        if (IsDisposed || !IsHandleCreated) return null;
        if (InvokeRequired)
        {
            try { return (string?)Invoke(() => RequestNewFolder(reasonMessage)); }
            catch (InvalidOperationException) { return null; }
        }

        MessageBox.Show(this, reasonMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            dialog.SelectedPath = _currentFolder;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    // ---------------------------------------------------------------- IInvoiceDetailsPrompt

    /// <summary>
    /// Asks the operator to key the invoice number and date into empty boxes, showing only the safe
    /// totals and line count parsed from the ticket. Called from the conversion
    /// worker, so it marshals to the UI thread. Returns null when the operator cancels the file.
    /// </summary>
    public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsDisposed || !IsHandleCreated) return null;
        if (InvokeRequired)
        {
            try { return (InvoiceDetails?)Invoke(() => AskInvoiceDetails(request)); }
            catch (InvalidOperationException) { return null; }
        }

        try
        {
            using var dialog = new InvoiceNumberDialog(request);
            return dialog.ShowDialog(this) == DialogResult.OK
                ? new InvoiceDetails(dialog.InvoiceNumber, dialog.Date, dialog.Total)
                : null;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    /// <summary>
    /// Asks the operator to type one line's barcode, for the rare product whose code the scan
    /// destroyed and no independent reading can confirm. Called from the conversion worker, so it
    /// marshals to the UI thread. Returns null when the operator skips the file.
    /// </summary>
    public string? AskBarcode(BarcodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsDisposed || !IsHandleCreated) return null;
        if (InvokeRequired)
        {
            try { return (string?)Invoke(() => AskBarcode(request)); }
            catch (InvalidOperationException) { return null; }
        }

        try
        {
            using var dialog = new BarcodeDialog(request);
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Barcode : null;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    /// <summary>
    /// Asks the operator for one figure on one product row — barcode, units or amount — that the
    /// scan destroyed and the ticket's own arithmetic could not rebuild. Called from the conversion
    /// worker, so it marshals to the UI thread. Returns null when the operator skips the file.
    /// </summary>
    public string? AskLineField(LineFieldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsDisposed || !IsHandleCreated) return null;
        if (InvokeRequired)
        {
            try { return (string?)Invoke(() => AskLineField(request)); }
            catch (InvalidOperationException) { return null; }
        }

        try
        {
            using var dialog = new LineFieldDialog(request);
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Value : null;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    public bool ConfirmBimboOcrFallback(string fileName)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        if (InvokeRequired)
        {
            try { return (bool)Invoke(() => ConfirmBimboOcrFallback(fileName)); }
            catch (InvalidOperationException) { return false; }
        }

        try
        {
            return MessageBox.Show(this,
                $"AI reading is unavailable for {fileName}.\n\nUse the legacy PDF text reader for "
                + "this Bimbo batch? It is less reliable on scanned tickets. This question is shown "
                + "only once for the current batch.",
                "Bimbo AI reading unavailable", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    public bool ConfirmDrPepperOcrFallback(string fileName)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        if (InvokeRequired)
        {
            try { return (bool)Invoke(() => ConfirmDrPepperOcrFallback(fileName)); }
            catch (InvalidOperationException) { return false; }
        }

        try
        {
            return MessageBox.Show(this,
                $"Gemini is unavailable for {fileName}.\n\nUse the legacy OCR parser for this "
                + "Dr Pepper batch? It is less reliable and will run only if you approve it here. "
                + "This question is shown once for the current batch.",
                "Dr Pepper AI reading unavailable", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    public bool ConfirmRedBullOcrFallback(string fileName)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        if (InvokeRequired)
        {
            try { return (bool)Invoke(() => ConfirmRedBullOcrFallback(fileName)); }
            catch (InvalidOperationException) { return false; }
        }

        try
        {
            return MessageBox.Show(this,
                $"AI reading is unavailable for {fileName}.\n\nUse the legacy OCR parser for this "
                + "Red Bull batch? It is less reliable and may ask about product rows. This "
                + "question is shown only once for the current batch.",
                "Red Bull AI reading unavailable", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    public bool ConfirmBlueBellOcrFallback(string fileName)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        if (InvokeRequired)
        {
            try { return (bool)Invoke(() => ConfirmBlueBellOcrFallback(fileName)); }
            catch (InvalidOperationException) { return false; }
        }

        try
        {
            return MessageBox.Show(this,
                $"AI reading is unavailable for {fileName}.\n\nUse the legacy OCR parser for this "
                + "Blue Bell batch? It is less reliable on the ticket's watermark and will run "
                + "only if you approve it here. This question is shown once for the current batch.",
                "Blue Bell AI reading unavailable", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    public bool ConfirmChubiwinosOcrFallback(string fileName)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        if (InvokeRequired)
        {
            try { return (bool)Invoke(() => ConfirmChubiwinosOcrFallback(fileName)); }
            catch (InvalidOperationException) { return false; }
        }

        try
        {
            return MessageBox.Show(this,
                $"AI reading is unavailable for {fileName}.\n\nUse the legacy OCR parser for this "
                + "Chubiwinos batch? It is less reliable on the money columns and may ask for a "
                + "barcode. This question is shown only once for the current batch.",
                "Chubiwinos AI reading unavailable", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        finally
        {
            RepaintConversionQueues();
        }
    }

    private void RepaintConversionQueues()
    {
        QueueRepaintPolicy.AfterPrompt(
            () => _ediTab?.ForceRepaint(),
            () => _pdfTab?.ForceRepaint(),
            () => _modisoftTab?.ForceRepaint());
    }

    public void SaveFolder(string folder)
    {
        _currentFolder = folder;
        try { _settingsStore.SaveOutputFolder(folder); }
        catch
        {
            RunOnUi(() => MessageBox.Show(this,
                "Your folder choice could not be saved and will be asked again next time.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        RunOnUi(UpdateFolderLabel);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        Text = "EDI & PDF to SSCS Converter";
        AppIcon.ApplyTo(this);
        MinimumSize = new Size(820, 600);
        Size = new Size(940, 700);
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ThemeBg;

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = ThemeBg,
            Padding = new Padding(12, 10, 12, 12),
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(shell);

        shell.Controls.Add(BuildHeader(), 0, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };

        var settingsPage = new TabPage("Settings") { BackColor = ThemeBg, Padding = new Padding(16), UseVisualStyleBackColor = false };
        var ediPage = new TabPage("EDI to SSCS XML") { BackColor = ThemeBg, Padding = new Padding(10), UseVisualStyleBackColor = false };
        var pdfPage = new TabPage("PDF to SSCS XML") { BackColor = ThemeBg, Padding = new Padding(10), UseVisualStyleBackColor = false };
        var modisoftPage = new TabPage("PDF to Modisoft EDI") { BackColor = ThemeBg, Padding = new Padding(10), UseVisualStyleBackColor = false };

        // PDF sits ahead of EDI and opens first: the PDF vendors are the everyday job, and the tab
        // that is already in front is the one nobody converts on the wrong tab by accident. The
        // Modisoft tab sits beside it because it takes the same PDFs; only the file it writes and
        // the address it sends to differ.
        tabs.TabPages.Add(settingsPage);
        tabs.TabPages.Add(pdfPage);
        tabs.TabPages.Add(modisoftPage);
        tabs.TabPages.Add(ediPage);

        _ediTab = new ConvertTab(this, ConversionKind.Edi, OutputTarget.Sscs,
            "Click or drag and drop EDI files here",
            "Accepted: .txt, .edi, .edi13  —  the data file a vendor emails you");
        _pdfTab = new ConvertTab(this, ConversionKind.Pdf, OutputTarget.Sscs,
            "Click or drag and drop PDF invoices here",
            "Accepted: .pdf (searchable)  —  choose the vendor above before dropping");
        // No vendor dropdown here (2026-10-15): Modisoft takes only the lines from the file and the
        // vendor is picked on the purchase in Modisoft, so any vendor's invoice goes straight in.
        _modisoftTab = new ConvertTab(this, ConversionKind.Pdf, OutputTarget.Modisoft,
            "Click or drag and drop any invoice here",
            "Accepted: .pdf, or a photo (.jpg, .png)  —  any vendor, no need to pick one. Makes a Modisoft EDI file.");
        _ediTab.Build(ediPage);
        _pdfTab.Build(pdfPage);
        _modisoftTab.Build(modisoftPage);

        tabs.SelectedTab = pdfPage;
        shell.Controls.Add(tabs, 0, 1);

        BuildSettingsPage(settingsPage);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = ThemePanel, Padding = new Padding(14, 10, 14, 12) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(ThemeRedAccent, 2f);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoSize = false, BackColor = Color.Transparent,
        };

        if (_logoImage is not null)
        {
            row.Controls.Add(new PictureBox
            {
                Image = _logoImage, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(80, 80),
                Margin = new Padding(0, 0, 14, 0), BackColor = Color.Transparent,
            });
        }

        row.Controls.Add(new Label
        {
            Text = "EDI & PDF to SSCS Converter", AutoSize = true,
            // Without this a Label eats the "&" as a keyboard accelerator and the title renders as
            // "EDI _PDF to SSCS Converter" with the space underlined.
            UseMnemonic = false,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = ThemeNavy,
            BackColor = Color.Transparent, Margin = new Padding(0, 24, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        });

        header.Controls.Add(row);
        return header;
    }

    // ---------------------------------------------------------------- Settings page

    private void BuildSettingsPage(TabPage page)
    {
        // Scrollable: the page carries two sections, and at 150% display scaling on a short laptop
        // screen the help text would otherwise be cut off with no way to reach it.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10, BackColor = ThemeBg,
            AutoScroll = true,
        };
        for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // help text / filler
        page.Controls.Add(layout);

        // --- Email ---
        layout.Controls.Add(SectionHeading("Email"), 0, 0);

        _emailValueLabel = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = ThemeText, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_emailValueLabel, 0, 1);

        var emailButton = MakePrimaryButton("Email Settings...");
        emailButton.Margin = new Padding(0, 0, 0, 20);
        emailButton.Click += OnOpenEmailSettings;
        layout.Controls.Add(emailButton, 0, 2);

        // --- AI reading ---
        layout.Controls.Add(SectionHeading("AI reading"), 0, 3);

        _visionValueLabel = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = ThemeText, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_visionValueLabel, 0, 4);

        var visionButton = MakePrimaryButton("AI Reading Settings...");
        visionButton.Margin = new Padding(0, 0, 0, 20);
        visionButton.Click += OnOpenVisionSettings;
        layout.Controls.Add(visionButton, 0, 5);

        // --- Output folder ---
        layout.Controls.Add(SectionHeading("Output folder"), 0, 6);

        _folderValueLabel = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = ThemeText, Margin = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(_folderValueLabel, 0, 7);

        var changeFolderButton = MakePrimaryButton("Change Output Folder");
        changeFolderButton.Margin = new Padding(0, 0, 0, 16);
        changeFolderButton.Click += OnChangeOutputFolder;
        layout.Controls.Add(changeFolderButton, 0, 8);

        layout.Controls.Add(new Label
        {
            Text = "How sending works\n\n" +
                   "Converted files are always saved to the output folder. Press Send Email on a convert\n" +
                   "tab to email everything that has just been converted — one email per invoice, each\n" +
                   "with its own XML attached. Nothing is sent until you press it.\n\n" +
                   "An invoice already sent is not sent again unless you say so, so pressing the button\n" +
                   "twice cannot double-book a delivery.\n\n" +
                   "• EDI to SSCS XML — converts the data files vendors email you (.txt, .edi, .edi13).\n" +
                   "• PDF to SSCS XML — converts Dr Pepper, Blue Bell, Chubiwinos, Bimbo and Red Bull PDF\n" +
                   "  invoices. Pick the vendor first.\n" +
                   "• PDF to Modisoft EDI — any vendor's invoice (a PDF or a photo), read by AI and made into\n" +
                   "  an EDI file for Modisoft, emailed to the Modisoft address. No vendor to pick. In\n" +
                   "  Modisoft, create the purchase and pick the vendor, press Import Invoice, choose EDI and\n" +
                   "  then Xpress Wholesale (for every vendor), and upload the file.\n\n" +
                   "A Blue Bell ticket that also has a Returns section produces two files, one per\n" +
                   "invoice number, and each goes out as its own email.\n\n" +
                   "Use View XML to read what a file converted to.",
            AutoSize = true, Font = new Font("Segoe UI", 10.5f), ForeColor = ThemeMuted, Margin = new Padding(0, 4, 0, 0),
        }, 0, 9);
    }

    private static Label SectionHeading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold),
        ForeColor = ThemeNavy, Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>
    /// Opens the email settings, behind the admin PIN. The first time through, the operator chooses
    /// the PIN — which is saved even if they then cancel the settings themselves, so that the gate
    /// is in place from that moment on rather than only once email is configured.
    /// </summary>
    private void OnOpenEmailSettings(object? sender, EventArgs e)
    {
        EmailSettings current;
        lock (_emailLock) current = _email.Clone();

        var (allowed, newPinHash) = PinDialog.Authorize(this, current.AdminPinHash);
        if (!allowed) return;

        if (newPinHash is not null)
        {
            current.AdminPinHash = newPinHash;
            ApplyEmailSettings(current);
        }

        using var dialog = new EmailSettingsDialog(current);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyEmailSettings(dialog.Result);
    }

    /// <summary>Saves the email settings and makes them live for the next send.</summary>
    private void ApplyEmailSettings(EmailSettings settings)
    {
        lock (_emailLock) _email = settings;
        try
        {
            _settingsStore.SaveEmail(settings);
        }
        catch
        {
            MessageBox.Show(this,
                "Your email settings could not be saved and will be asked again next time.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateEmailLabel();
    }

    private void UpdateEmailLabel()
    {
        EmailSettings s;
        lock (_emailLock) s = _email;

        if (!s.IsCompleteFor(OutputTarget.Sscs) && !s.IsCompleteFor(OutputTarget.Modisoft))
        {
            _emailValueLabel.Text = "Email is not set up yet, so Send Email has nothing to send to.\n"
                + "Open Email Settings to set the store's address and the sending mailbox.";
            return;
        }

        // Both addresses are always named, set or not, so a store that only uses one back office
        // can see at a glance that the other tab has nowhere to send.
        _emailValueLabel.Text =
            (string.IsNullOrWhiteSpace(s.ToAddress)
                ? "SSCS files: no address set."
                : $"SSCS files go to: {s.ToAddress}") + "\n"
            + (string.IsNullOrWhiteSpace(s.ModisoftToAddress)
                ? "Modisoft files: no address set."
                : $"Modisoft files go to: {s.ModisoftToAddress}") + "\n"
            + $"Sent from {s.FromAddress}. Files are saved to the output folder as well.";
    }

    /// <summary>
    /// Opens the AI reading settings, behind the same PIN as email. It holds a key and it sends the
    /// store's invoices outside the building, which is the same class of decision.
    /// </summary>
    private void OnOpenVisionSettings(object? sender, EventArgs e)
    {
        EmailSettings email;
        lock (_emailLock) email = _email.Clone();

        var (allowed, newPinHash) = PinDialog.Authorize(this, email.AdminPinHash);
        if (!allowed) return;

        if (newPinHash is not null)
        {
            email.AdminPinHash = newPinHash;
            ApplyEmailSettings(email);
        }

        VisionSettings current;
        lock (_visionLock) current = _vision.Clone();

        using var dialog = new VisionSettingsDialog(current);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyVisionSettings(dialog.Result);
    }

    /// <summary>Saves the AI reader's settings and makes them live for the next conversion.</summary>
    private void ApplyVisionSettings(VisionSettings settings)
    {
        lock (_visionLock) _vision = settings;
        try
        {
            _settingsStore.SaveVision(settings);
        }
        catch
        {
            MessageBox.Show(this,
                "Your AI reading settings could not be saved and will be asked again next time.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateVisionLabel();
    }

    private void UpdateVisionLabel()
    {
        VisionSettings s;
        lock (_visionLock) s = _vision;

        // Every key configured, not just the one field an older build wrote. The settings screen
        // saves into the list and clears that field, so asking it whether a key exists answered "no"
        // for a store that had just added one — and then told them to go and get a free Google key.
        _visionValueLabel.Text = s.IsActive
            ? "On. Blue Bell, Bimbo, Chubiwinos, Dr Pepper and Red Bull are read once from the complete ticket pictures.\n"
              + "Their AI paths ask only for the invoice number and date. The Modisoft tab reads any vendor's invoice."
            : s.AllProtectedKeys.Count == 0
                ? "Off. Invoices are read from the PDF only, which means more questions on a bad scan,\n"
                  + "and the Modisoft tab cannot read anything. Open AI Reading Settings to switch it on with a free Google key."
                : "Off. A key is saved but not being used - open AI Reading Settings to switch it back on.";
    }

    /// <summary>
    /// The second reading of a scanned invoice, built from whatever is saved right now — so turning
    /// it on or off in Settings takes effect on the very next file, with no restart.
    ///
    /// Never throws and never blocks a conversion: no key, no network, a bad reply, all give an
    /// empty reading and the invoice converts the way it always has.
    /// </summary>
    public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
    {
        try
        {
            return CreateVisionReader()?.TryRead(pdfBytes, vendor) ?? VisionReading.Empty;
        }
        catch
        {
            return VisionReading.Empty;
        }
    }

    public BimboVisionReading ReadBimbo(byte[] pdfBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadBimbo(pdfBytes)
                   ?? BimboVisionReading.Unavailable("AI reading is switched off or has no usable key.");
        }
        catch
        {
            return BimboVisionReading.Unavailable();
        }
    }

    public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadDrPepper(pdfBytes)
                   ?? DrPepperVisionReading.Unavailable(
                       "AI reading is switched off or has no usable key.");
        }
        catch
        {
            return DrPepperVisionReading.Unavailable();
        }
    }

    public RedBullVisionReading ReadRedBull(byte[] pdfBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadRedBull(pdfBytes)
                   ?? RedBullVisionReading.Unavailable(
                       "AI reading is switched off or has no usable key.");
        }
        catch
        {
            return RedBullVisionReading.Unavailable();
        }
    }

    public BlueBellVisionReading ReadBlueBell(byte[] pdfBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadBlueBell(pdfBytes)
                   ?? BlueBellVisionReading.Unavailable(
                       "AI reading is switched off or has no usable key.");
        }
        catch
        {
            return BlueBellVisionReading.Unavailable();
        }
    }

    public ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadChubiwinos(pdfBytes)
                   ?? ChubiwinosVisionReading.Unavailable(
                       "AI reading is switched off or has no usable key.");
        }
        catch
        {
            return ChubiwinosVisionReading.Unavailable();
        }
    }

    public AnyInvoiceVisionReading ReadAnyInvoice(byte[] fileBytes)
    {
        try
        {
            return CreateVisionReader()?.ReadAnyInvoice(fileBytes)
                   ?? AnyInvoiceVisionReading.Off("AI reading is switched off or has no usable key.");
        }
        catch
        {
            return AnyInvoiceVisionReading.Unavailable();
        }
    }

    /// <summary>
    /// The reader in use, and the settings it was built from.
    ///
    /// It is kept because <see cref="GeminiVisionReader"/> remembers which key and model last
    /// answered and starts the next invoice there. Building a new one per file threw that away, so
    /// every invoice walked back up to the best model, collected a 429 on each dead rung and paid
    /// for the walk in requests: on 2026-08-14 one Red Bull ticket spent seven requests to convert,
    /// six of them on rungs the file before it had already found empty. Against a free allowance
    /// counted in tens of requests a day, that is most of the allowance.
    ///
    /// Rebuilt whenever the saved keys or models change, so switching a refused key mid-queue still
    /// takes effect on the next file — which is exactly what the store did on the run that found
    /// this.
    /// </summary>
    private GeminiVisionReader? _visionReaderInUse;
    private string _visionReaderBuiltFrom = "";

    private GeminiVisionReader? CreateVisionReader()
    {
        VisionSettings s;
        lock (_visionLock) s = _vision.Clone();
        if (!s.IsActive) return null;

        var keys = s.AllProtectedKeys
            .Select(SecretProtector.Unprotect)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToList();

        // The single legacy Model setting still counts, ahead of the list, so a store that pinned
        // one model keeps it. Gemini's verifier moves to the next configured combination first.
        var models = new[] { s.Model }.Concat(s.Models)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0) return null;

        // The keys themselves never leave this method; the fingerprint is their count and order
        // plus the models, which is enough to notice a change without holding a decrypted key.
        string builtFrom = $"{keys.Count}|{string.Join(",", keys.Select(k => k.GetHashCode()))}"
                           + $"|{string.Join(",", models)}";
        lock (_visionLock)
        {
            if (_visionReaderInUse is not null && _visionReaderBuiltFrom == builtFrom)
                return _visionReaderInUse;
            _visionReaderBuiltFrom = builtFrom;
            // The app keeps asking while Google is busy; the reader's own default is to give up.
            _visionReaderInUse = new GeminiVisionReader(
                keys, models, patience: GeminiVisionReader.BusyPatience) { Progress = ForwardAiProgress };
            return _visionReaderInUse;
        }
    }

    /// <summary>
    /// One AI read at a time, across every tab, and where its progress lines go while it runs.
    ///
    /// The reader is shared - see <see cref="_visionReaderInUse"/> - so it has one place to say what
    /// it is waiting on, and that has to be the row of the file being read. Until 2026-10-05 it
    /// said nothing at all: the window was never wired to it, so a read that sat for five minutes
    /// showed "Converting..." the whole time. Taking turns is also what the free allowance wants,
    /// at five requests a minute per model.
    /// </summary>
    private readonly object _aiReadGate = new();
    private Action<string>? _aiProgress;

    private void ForwardAiProgress(string message) => Volatile.Read(ref _aiProgress)?.Invoke(message);

    private T ReadWithProgress<T>(Action<string>? progress, Func<T> read)
    {
        if (!Monitor.TryEnter(_aiReadGate))
        {
            progress?.Invoke("Waiting for the invoice on the other tab to finish its AI reading.");
            Monitor.Enter(_aiReadGate);
        }
        try
        {
            Volatile.Write(ref _aiProgress, progress);
            return read();
        }
        finally
        {
            Volatile.Write(ref _aiProgress, null);
            Monitor.Exit(_aiReadGate);
        }
    }

    /// <summary>
    /// One tab's view of the AI reader: the same reads, with progress going to that tab's queue.
    /// Each tab's <see cref="ConversionService"/> sets <see cref="Progress"/> to its own queue.
    /// </summary>
    private sealed class TabVisionReader(MainForm owner)
        : IInvoiceVisionReader, IBimboVisionReader, IDrPepperVisionReader, IRedBullVisionReader,
          IBlueBellVisionReader, IChubiwinosVisionReader, IAnyInvoiceVisionReader, IVisionProgressReporter
    {
        public Action<string>? Progress { get; set; }

        public AnyInvoiceVisionReading ReadAnyInvoice(byte[] fileBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadAnyInvoice(fileBytes));

        public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor) =>
            owner.ReadWithProgress(Progress, () => owner.TryRead(pdfBytes, vendor));

        public BimboVisionReading ReadBimbo(byte[] pdfBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadBimbo(pdfBytes));

        public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadDrPepper(pdfBytes));

        public RedBullVisionReading ReadRedBull(byte[] pdfBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadRedBull(pdfBytes));

        public BlueBellVisionReading ReadBlueBell(byte[] pdfBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadBlueBell(pdfBytes));

        public ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes) =>
            owner.ReadWithProgress(Progress, () => owner.ReadChubiwinos(pdfBytes));
    }

    // ---------------------------------------------------------------- shared helpers

    private static Button MakePrimaryButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = ThemeNavy, ForeColor = ThemeButtonText, Padding = new Padding(12, 4, 12, 4),
        Cursor = Cursors.Hand, FlatAppearance = { BorderSize = 0 },
    };

    /// <summary>An underlined text link, for the places that only open a window to look at.</summary>
    private static LinkLabel MakeLink(string text) => new()
    {
        Text = text, AutoSize = true, LinkColor = ThemeNavy, ActiveLinkColor = ThemeRedAccent,
        LinkBehavior = LinkBehavior.AlwaysUnderline, Cursor = Cursors.Hand,
        Margin = new Padding(2, 10, 16, 0),
    };

    private static Image? LoadEmbeddedLogo()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("best-stop-logo.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var temp = Image.FromStream(stream);
        return new Bitmap(temp);
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        ClampToWorkingArea();

        if (_firstShownHandled) return;
        _firstShownHandled = true;

        if (DestinationValidator.Validate(_currentFolder) != DestinationStatus.NotConfigured)
            return;

        MessageBox.Show(this, "Welcome! Please choose the folder where converted SSCS files should be saved.",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            SaveFolder(dialog.SelectedPath);
    }

    /// <summary>
    /// Keeps the window within the screen's working area so its bottom (the action buttons) is never
    /// pushed off-screen — which can happen at 125–150% display scaling on shorter laptop screens.
    /// </summary>
    private void ClampToWorkingArea()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int w = Math.Min(Width, wa.Width);
        int h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height)
            Size = new Size(w, h);

        int x = Math.Max(wa.Left, Math.Min(Location.X, wa.Right - Width));
        int y = Math.Max(wa.Top, Math.Min(Location.Y, wa.Bottom - Height));
        if (x != Location.X || y != Location.Y)
            Location = new Point(x, y);
    }

    private void OnChangeOutputFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder where converted SSCS files should be saved.",
            UseDescriptionForTitle = true,
        };
        if (!string.IsNullOrWhiteSpace(_currentFolder))
            dialog.SelectedPath = _currentFolder;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var status = DestinationValidator.Validate(dialog.SelectedPath);
        if (status != DestinationStatus.Ok)
        {
            MessageBox.Show(this, DestinationValidator.Describe(status), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        SaveFolder(dialog.SelectedPath);
    }

    private void UpdateFolderLabel()
    {
        _folderValueLabel.Text = "Current folder: " + (string.IsNullOrWhiteSpace(_currentFolder) ? "Not set" : _currentFolder);
    }

    private static string StatusText(FileStatus status) => status switch
    {
        FileStatus.Waiting => "Waiting",
        FileStatus.Converting => "Converting",
        FileStatus.Converted => "Converted",
        FileStatus.Warning => "Warning",
        FileStatus.Failed => "Failed",
        FileStatus.Canceled => "Canceled",
        _ => status.ToString(),
    };

    private static Color StatusColor(FileStatus status) => status switch
    {
        FileStatus.Converted => Color.DarkGreen,
        FileStatus.Warning => Color.DarkOrange,
        FileStatus.Failed => Color.DarkRed,
        FileStatus.Canceled => Color.Gray,
        _ => SystemColors.ControlText,
    };

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text : text[..idx];
    }

    // ---------------------------------------------------------------- per-tab convert UI

    /// <summary>
    /// One convert section (EDI or PDF): a click/drag drop area, a per-file status list and summary.
    /// The PDF sections also show a vendor dropdown; the EDI section needs no vendor. Each tab owns
    /// an independent conversion queue and its own email sender; all share the destination folder.
    /// </summary>
    private sealed class ConvertTab
    {
        private readonly MainForm _owner;
        private readonly ConversionKind _kind;
        private readonly OutputTarget _target;
        private readonly SmtpEmailSender _sender;
        private readonly string[] _extensions;
        private readonly string _dropPrimaryText;
        private readonly string _dropSecondaryText;
        private readonly ConversionCoordinator _coordinator;
        private readonly Dictionary<QueuedFile, ListViewItem> _rowMap = new();

        private ListView _fileList = null!;
        private Label _summaryLabel = null!;
        private Panel _dropPanel = null!;
        private ComboBox? _vendorCombo;
        private Button _sendEmailButton = null!;
        private bool _sending;

        public ConvertTab(
            MainForm owner, ConversionKind kind, OutputTarget target, string dropPrimary, string dropSecondary)
        {
            _owner = owner;
            _kind = kind;
            _target = target;
            _sender = target == OutputTarget.Modisoft ? owner._modisoftEmailSender : owner._emailSender;
            _extensions = kind != ConversionKind.Pdf ? [".txt", ".edi", ".edi13"]
                : PicksVendor ? [".pdf"]
                : [".pdf", ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif"];
            _dropPrimaryText = dropPrimary;
            _dropSecondaryText = dropSecondary;

            var service = new ConversionService(
                owner._logger, Application.ProductVersion, owner,
                visionReader: new TabVisionReader(owner));
            _coordinator = new ConversionCoordinator(service, owner);
            _coordinator.FileChanged += OnFileChanged;
            _coordinator.BatchCompleted += summary => _owner.RunOnUi(() =>
            {
                _summaryLabel.Text = summary;
                RepaintFileList();
            });
        }

        public bool IsBusy => _coordinator.IsBusy;

        /// <summary>
        /// Only PDF to SSCS XML picks a vendor: SSCS needs to know who sent it, and each vendor
        /// there has its own parser. The Modisoft tab reads any vendor's invoice the same way.
        /// </summary>
        private bool PicksVendor => _kind == ConversionKind.Pdf && _target == OutputTarget.Sscs;

        /// <summary>What the viewer button says: SSCS files are XML, Modisoft files are EDI text.</summary>
        private string ViewAction => _target == OutputTarget.Modisoft ? "View File" : "View XML";

        /// <summary>
        /// Forces the native list view to repaint after a modal prompt closes. Its backing model can
        /// update while the dialog owns the UI thread; without this explicit invalidation a later
        /// queued file can appear as a blank white list until the operator clicks it.
        /// </summary>
        public void ForceRepaint() => RepaintFileList();

        public void Build(TabPage page)
        {
            // The PDF vendor row (if any) is pinned to the top and the status + action buttons to the
            // bottom (both docked), so a short window — e.g. at 125–150% display scaling — never clips
            // them. Only the drop area and file list in the middle compress. Docked controls are laid
            // out in reverse add order, so the Fill panel is added FIRST and the docked edges after it.
            var vendorRow = PicksVendor ? BuildVendorRow() : null;

            // --- Middle: drop area + file list (fills remaining space, compresses when short) ---
            // The drop area gets a fixed height rather than a share of what is left. As a percentage
            // it was squeezed by the caption row above the list and clipped both of its lines; 132px
            // is what the 14pt heading plus the "Accepted:" line actually need. The list takes
            // everything else, which is the part worth growing anyway.
            var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = ThemeBg };
            middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 132)); // drop area
            middle.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // list caption + View XML
            middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // list

            _dropPanel = new Panel
            {
                Dock = DockStyle.Fill, BackColor = ThemeNavySoft, BorderStyle = BorderStyle.FixedSingle,
                AllowDrop = true, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(16),
            };
            _dropPanel.Paint += (_, e) =>
            {
                var rect = _dropPanel.ClientRectangle;
                rect.Inflate(-4, -4);
                using var pen = new Pen(ThemeBorder, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                e.Graphics.DrawRectangle(pen, rect);
            };
            WireDropTarget(_dropPanel);
            _dropPanel.Click += OnChooseFiles;

            var dropInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, AllowDrop = true };
            dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 60f));
            dropInner.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            WireDropTarget(dropInner);
            dropInner.Click += OnChooseFiles;

            var dropPrimary = new Label
            {
                Text = _dropPrimaryText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomCenter,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = ThemeNavy, BackColor = Color.Transparent,
                Cursor = Cursors.Hand, AllowDrop = true,
            };
            var dropSecondary = new Label
            {
                Text = _dropSecondaryText, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopCenter,
                ForeColor = ThemeMuted, BackColor = Color.Transparent, Cursor = Cursors.Hand, AllowDrop = true,
            };
            WireDropTarget(dropPrimary);
            WireDropTarget(dropSecondary);
            dropPrimary.Click += OnChooseFiles;
            dropSecondary.Click += OnChooseFiles;
            dropInner.Controls.Add(dropPrimary, 0, 0);
            dropInner.Controls.Add(dropSecondary, 0, 1);
            _dropPanel.Controls.Add(dropInner);
            middle.Controls.Add(_dropPanel, 0, 0);

            _fileList = new BufferedListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
                ShowItemToolTips = true, HideSelection = false, BackColor = ThemePanel,
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 8),
            };
            _fileList.Columns.Add("File", 260);
            _fileList.Columns.Add("Status", 90);
            _fileList.Columns.Add("Details", 380);
            _fileList.DoubleClick += (_, _) => ViewSelectedXml();
            _fileList.Resize += (_, _) =>
            {
                int used = 0;
                for (int i = 0; i < _fileList.Columns.Count - 1; i++) used += _fileList.Columns[i].Width;
                int remaining = _fileList.ClientSize.Width - used - 4;
                if (remaining > 80) _fileList.Columns[^1].Width = remaining;
            };

            // View XML sits on the list's own line rather than down with the action buttons: it acts
            // on whichever row is highlighted, so it belongs beside the rows, not beside Send Email
            // which acts on the whole batch.
            var listHeaderRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = ThemeBg,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            listHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            listHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            listHeaderRow.Controls.Add(new Label
            {
                // What the buttons actually do. It read "click one to view its XML", and a single
                // click does nothing — the viewer is on double-click, because opening a modal window
                // every time an arrow key moves the highlight would be unusable.
                Text = $"Converted files - double-click one, or pick it and press {ViewAction}",
                AutoSize = true, ForeColor = ThemeMuted,
                Anchor = AnchorStyles.Left, Margin = new Padding(2, 8, 0, 0),
            }, 0, 0);

            var viewXmlButton = MakePrimaryButton(ViewAction);
            viewXmlButton.Margin = new Padding(8, 0, 2, 2);
            viewXmlButton.Anchor = AnchorStyles.Right;
            viewXmlButton.Click += (_, _) => ViewSelectedXml();
            listHeaderRow.Controls.Add(viewXmlButton, 1, 0);

            middle.Controls.Add(listHeaderRow, 0, 1);
            middle.Controls.Add(_fileList, 0, 2);

            // --- Bottom stack: status summary + action buttons (pinned to the bottom) ---
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, ColumnCount = 1, RowCount = 2, BackColor = ThemeBg,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); // summary
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // footer

            _summaryLabel = new Label
            {
                Dock = DockStyle.Fill, AutoSize = false, BorderStyle = BorderStyle.FixedSingle, BackColor = ThemePanel,
                ForeColor = ThemeText, Padding = new Padding(6), TextAlign = ContentAlignment.TopLeft, Margin = new Padding(0, 0, 0, 6),
            };
            bottom.Controls.Add(_summaryLabel, 0, 0);

            // Bottom row: the two "go and look at something" links on the left, and the one action
            // that actually does something — Send Email — on the right, where it reads as the end of
            // the job and is hard to hit by accident on the way to a link.
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = ThemeBg,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var links = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = ThemeBg,
                Margin = new Padding(0), Anchor = AnchorStyles.Left,
            };

            var savedFolderLink = MakeLink("Saved Folder");
            savedFolderLink.LinkClicked += (_, _) => OnOpenOutputFolder(null, EventArgs.Empty);

            var logsLink = MakeLink("View Logs");
            logsLink.LinkClicked += (_, _) => OpenFolderInExplorer(_owner._logger.Directory, "logs folder", ensureExists: true);

            links.Controls.Add(savedFolderLink);
            links.Controls.Add(logsLink);

            _sendEmailButton = MakePrimaryButton("Send Email");
            _sendEmailButton.Margin = new Padding(12, 2, 2, 2);
            _sendEmailButton.Anchor = AnchorStyles.Right;
            _sendEmailButton.Click += (_, _) => SendConvertedByEmail();

            footer.Controls.Add(links, 0, 0);
            footer.Controls.Add(_sendEmailButton, 1, 0);
            bottom.Controls.Add(footer, 0, 1);

            // Add the Fill panel first (it takes the leftover center), then the docked edges.
            page.Controls.Add(middle);
            page.Controls.Add(bottom);
            if (vendorRow is not null) page.Controls.Add(vendorRow);
        }

        /// <summary>
        /// Builds the PDF section's vendor row. Each PDF vendor has its own parser, so the choice
        /// here selects which one runs — an invoice dropped under the wrong vendor is rejected by
        /// that parser rather than mis-read.
        /// </summary>
        private FlowLayoutPanel BuildVendorRow()
        {
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = ThemeBg,
                Padding = new Padding(0, 0, 0, 6),
            };
            row.Controls.Add(new Label
            {
                Text = "Vendor:", AutoSize = true, ForeColor = ThemeText,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(2, 10, 8, 0),
            });

            var hint = new Label
            {
                AutoSize = true, ForeColor = ThemeMuted, Margin = new Padding(4, 10, 0, 0),
            };

            _vendorCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Margin = new Padding(0, 6, 0, 0) };
            _vendorCombo.Items.Add("Dr Pepper");
            _vendorCombo.Items.Add("Blue Bell");
            _vendorCombo.Items.Add("Chubiwinos");
            _vendorCombo.Items.Add("Bimbo");
            _vendorCombo.Items.Add("Red Bull");
            _vendorCombo.SelectedIndexChanged += (_, _) => hint.Text = SelectedVendor switch
            {
                PdfVendor.BlueBell => "  (Blue Bell delivery tickets - you will be asked for each invoice number)",
                PdfVendor.Chubiwinos => "  (Chubiwinos S&G invoices - you will be asked for the invoice number)",
                PdfVendor.Bimbo => "  (Bimbo delivery tickets - you will be asked for invoice number and date)",
                PdfVendor.RedBull => "  (Red Bull delivery tickets - all pages are one invoice)",
                _ => "  (Dr Pepper searchable PDF invoices)",
            };
            _vendorCombo.SelectedIndex = 0;

            row.Controls.Add(_vendorCombo);
            row.Controls.Add(hint);
            return row;
        }

        private PdfVendor SelectedVendor => !PicksVendor ? PdfVendor.Any : _vendorCombo?.SelectedIndex switch
        {
            1 => PdfVendor.BlueBell,
            2 => PdfVendor.Chubiwinos,
            3 => PdfVendor.Bimbo,
            4 => PdfVendor.RedBull,
            _ => PdfVendor.DrPepper,
        };

        private void WireDropTarget(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += OnDropDragEnter;
            control.DragOver += OnDropDragEnter;
            control.DragDrop += OnDropDragDrop;
        }

        private void OnDropDragEnter(object? sender, DragEventArgs e)
        {
            e.Effect = HasAllowedFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDropDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                EnqueuePaths(paths);
        }

        private void OnChooseFiles(object? sender, EventArgs e)
        {
            string filter = _kind != ConversionKind.Pdf
                ? "EDI files (*.txt;*.edi;*.edi13)|*.txt;*.edi;*.edi13"
                : PicksVendor
                    ? "PDF invoices (*.pdf)|*.pdf"
                    : "Invoices (*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif)|*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.heic;*.heif";
            using var dialog = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "Choose files to convert", CheckFileExists = true };
            if (dialog.ShowDialog(_owner) == DialogResult.OK)
                EnqueuePaths(dialog.FileNames);
        }

        private void EnqueuePaths(IEnumerable<string> paths)
        {
            var pathList = paths.ToList();
            var allowed = pathList.Where(IsAllowedPath).ToList();
            int rejected = pathList.Count - allowed.Count;
            if (rejected > 0)
            {
                string accepted = string.Join(", ", _extensions);
                string message = rejected == pathList.Count
                    ? $"Only {accepted} files can be converted here. No matching files were found."
                    : $"{rejected} file(s) were skipped because they are not {accepted} files.";
                MessageBox.Show(_owner, message, _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            if (allowed.Count == 0) return;

            foreach (var file in _coordinator.Enqueue(allowed, _kind, SelectedVendor, _target))
                EnsureRow(file);
        }

        private bool HasAllowedFiles(IDataObject? data) =>
            data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Any(IsAllowedPath);

        private bool IsAllowedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) return false;
            string ext = Path.GetExtension(path);
            return _extensions.Any(a => string.Equals(a, ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Emails every converted invoice on this tab, one message each, without asking which — the
        /// operator has just converted a delivery and wants it filed, not a row-picking exercise.
        ///
        /// An invoice already sent is skipped, so pressing the button a second time after adding one
        /// more file sends only the new one. That guard is the reason this can be a single click: a
        /// duplicate invoice email books the stock twice in SSCS, and nothing downstream catches it.
        ///
        /// The send runs on a background thread because a dead network costs 45 seconds per message,
        /// and a frozen window during that is indistinguishable from a crash.
        /// </summary>
        private async void SendConvertedByEmail()
        {
            if (_sending) return;

            if (!_sender.IsConfigured)
            {
                MessageBox.Show(_owner, _target == OutputTarget.Modisoft
                        ? "There is no address for Modisoft files yet. Open the Settings tab, press Email Settings, and fill in the Modisoft address and the sending mailbox."
                        : "Email is not set up yet. Open the Settings tab, press Email Settings, and fill in the store's address and the sending mailbox.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var pending = PendingForEmail();
            if (pending.Count == 0)
            {
                MessageBox.Show(_owner, DescribeNothingToSend(),
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string to = _sender.ToAddress ?? "the store";
            _sending = true;
            _sendEmailButton.Enabled = false;
            _sendEmailButton.Text = "Sending...";
            _summaryLabel.Text = $"Sending {pending.Count} invoice{(pending.Count == 1 ? "" : "s")} to {to}...";

            try
            {
                var report = await Task.Run(() => SendAll(pending)).ConfigureAwait(true);
                _summaryLabel.Text = report.Summary;

                foreach (var (file, _) in pending)
                    RefreshRow(file);

                if (report.Failures.Count > 0)
                {
                    MessageBox.Show(_owner,
                        $"{report.Failures.Count} invoice{(report.Failures.Count == 1 ? "" : "s")} could not be sent:\n\n"
                        + string.Join("\n\n", report.Failures)
                        + "\n\nThe converted files are still in the output folder, so nothing was lost. "
                        + "Fix the problem and press Send Email again - the ones that did send will not go twice.",
                        _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _sending = false;
                _sendEmailButton.Enabled = true;
                _sendEmailButton.Text = "Send Email";
            }
        }

        /// <summary>Every converted invoice on this tab that has not already been emailed.</summary>
        private List<(QueuedFile File, ConvertedDocument Document)> PendingForEmail()
        {
            var pending = new List<(QueuedFile, ConvertedDocument)>();
            foreach (ListViewItem row in _fileList.Items)
            {
                if (row.Tag is not QueuedFile file) continue;
                if (file.Status is not (FileStatus.Converted or FileStatus.Warning)) continue;
                foreach (var doc in file.Documents)
                    if (!file.EmailedFileNames.Contains(doc.FileName))
                        pending.Add((file, doc));
            }
            return pending;
        }

        /// <summary>
        /// Says why the button did nothing, distinguishing "nothing converted yet" from "all of it
        /// has already gone" — those need opposite responses from the operator.
        /// </summary>
        private string DescribeNothingToSend()
        {
            bool anyConverted = _fileList.Items.Cast<ListViewItem>()
                .Any(r => r.Tag is QueuedFile f && f.Documents.Count > 0);

            return anyConverted
                ? "Every converted invoice on this tab has already been emailed. Convert another file to send more."
                : "There is nothing to send yet. Convert an invoice first.";
        }

        private sealed record SendReport(string Summary, List<string> Failures);

        /// <summary>
        /// Sends each invoice in turn on the worker thread. One failure never stops the rest: the
        /// remaining invoices are independent deliveries and a network blip on one is not a reason
        /// to hold the others back.
        /// </summary>
        private SendReport SendAll(List<(QueuedFile File, ConvertedDocument Document)> pending)
        {
            int sent = 0;
            var failures = new List<string>();

            foreach (var (file, doc) in pending)
            {
                var result = _sender.Send(doc, file.FileName);
                if (result.Sent)
                {
                    file.MarkEmailed(doc.FileName);
                    sent++;
                }
                else
                {
                    failures.Add($"{doc.FileName}: {result.ErrorMessage}");
                }
            }

            string to = _sender.ToAddress ?? "the store";
            string summary = failures.Count == 0
                ? $"Sent {sent} invoice{(sent == 1 ? "" : "s")} to {to}."
                : $"Sent {sent} of {pending.Count} to {to}. {failures.Count} could not be sent - see the message for why.";
            return new SendReport(summary, failures);
        }

        /// <summary>Repaints one row after a send, so the Details column shows what happened.</summary>
        private void RefreshRow(QueuedFile file)
        {
            if (!_rowMap.TryGetValue(file, out var item)) return;
            int emailed = file.EmailedFileNames.Count;
            if (emailed == 0) return;

            string note = emailed == file.Documents.Count
                ? $"Emailed to {_sender.ToAddress}"
                : $"Emailed {emailed} of {file.Documents.Count} to {_sender.ToAddress}";
            item.SubItems[2].Text = note;
            item.ToolTipText = note + "\n" + file.Detail;
            RepaintFileList();
        }


        /// <summary>
        /// The queue item behind the selected row, once it has actually converted. Returns null and
        /// explains why not, naming <paramref name="action"/> so the message fits the button pressed.
        /// </summary>
        private QueuedFile? SelectedConvertedFile(string action)
        {
            if (_fileList.SelectedItems.Count == 0)
            {
                MessageBox.Show(_owner, $"Click a converted file in the list first, then try {action} again.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            if (_fileList.SelectedItems[0].Tag is not QueuedFile file) return null;

            if (file.Documents.Count == 0)
            {
                string reason = file.Status switch
                {
                    FileStatus.Waiting or FileStatus.Converting => "That file has not finished converting yet.",
                    FileStatus.Failed => "That file did not convert, so there is nothing to send.",
                    FileStatus.Canceled => "That file was canceled, so there is nothing to send.",
                    _ => "There is nothing to send for that file.",
                };
                MessageBox.Show(_owner, reason, _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            return file;
        }

        /// <summary>
        /// Opens the converted XML for the selected row. The documents live on the queue item rather
        /// than being re-read from disk, so the viewer works even if the saved file has since moved.
        /// </summary>
        private void ViewSelectedXml()
        {
            var file = SelectedConvertedFile(ViewAction);
            if (file is null) return;

            using var viewer = new XmlViewerDialog(file.Documents, file.FileName, file.OutputPath);
            viewer.ShowDialog(_owner);
        }

        private void OnOpenOutputFolder(object? sender, EventArgs e)
        {
            if (DestinationValidator.Validate(_owner._currentFolder) is DestinationStatus.NotConfigured
                or DestinationStatus.Missing or DestinationStatus.Unreachable or DestinationStatus.NotADirectory)
            {
                MessageBox.Show(_owner,
                    "No output folder is set yet, or the folder is no longer available. Please choose an output folder first.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenFolderInExplorer(_owner._currentFolder!, "output folder", ensureExists: false);
        }

        private void OpenFolderInExplorer(string path, string label, bool ensureExists)
        {
            try
            {
                if (ensureExists) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show(_owner, $"Could not open the {label} in File Explorer.",
                    _owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnFileChanged(QueuedFile file)
        {
            _owner.RunOnUi(() =>
            {
                var item = EnsureRow(file);
                item.SubItems[1].Text = StatusText(file.Status);
                item.SubItems[2].Text = FirstLine(file.Detail);
                item.ToolTipText = file.Detail;
                item.ForeColor = StatusColor(file.Status);
                if (file.Status is FileStatus.Converted or FileStatus.Warning or FileStatus.Failed or FileStatus.Canceled)
                    _summaryLabel.Text = file.Detail;

                // ListView is a native Windows control. During a fast queue, changing several
                // subitems and their colour while a modal invoice prompt is opening/closing can
                // update its model without scheduling a complete paint. The symptom is an empty
                // white list whose rows reappear as soon as the operator clicks it. Nothing was
                // lost; the click merely invalidated the control. Do that explicitly after every
                // model change so the queue is always visible without operator intervention.
                RepaintFileList();

                // Nothing opens on its own here. A conversion that finished interrupts nobody: the
                // only window this app puts in front of an operator is a question it cannot answer
                // itself. A window that appears after the work is done is a window that gets clicked
                // through, and one that appears on a file that converted perfectly well teaches
                // people to click through the ones that matter.
            });
        }

        private ListViewItem EnsureRow(QueuedFile file)
        {
            if (_rowMap.TryGetValue(file, out var existing)) return existing;
            var item = new ListViewItem(file.FileName) { Tag = file };
            item.SubItems.Add(StatusText(file.Status));
            item.SubItems.Add(FirstLine(file.Detail));
            item.ToolTipText = file.Detail;
            item.ForeColor = StatusColor(file.Status);
            _fileList.Items.Add(item);
            _rowMap[file] = item;
            return item;
        }

        /// <summary>
        /// Forces the native list surface to catch up with its already-updated items. Invalidate
        /// schedules the full details view (including subitems) and Update performs it before the
        /// next worker notification or modal prompt can cover the window again.
        /// </summary>
        private void RepaintFileList()
        {
            if (_fileList.IsDisposed || !_fileList.IsHandleCreated) return;
            _fileList.Invalidate(invalidateChildren: true);
            _fileList.Update();
        }

        /// <summary>
        /// WinForms ListView wraps the native common control, which otherwise erases its background
        /// between paints. Buffering prevents that white intermediate frame from becoming the last
        /// frame left on screen while a conversion prompt owns the UI thread.
        /// </summary>
        private sealed class BufferedListView : ListView
        {
            public BufferedListView() => DoubleBuffered = true;
        }
    }
}
