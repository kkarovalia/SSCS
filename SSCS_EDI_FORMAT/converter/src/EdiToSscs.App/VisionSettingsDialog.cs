using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Turns on the AI reader that looks at the picture of a scanned invoice, and holds its keys.
///
/// Keys are a **list you can see and edit**, not a box you overwrite. The first version of this
/// screen was a single text box where whatever you typed became the whole list — so pasting a
/// second account's key silently deleted the first, and the screen then had no way of telling you
/// how many were left. A destructive save with no visible state is the worst combination there is.
///
/// The keys themselves are still write-only: they are stored encrypted and never shown back. What
/// the list shows is how many there are and the last four characters of each, which is enough to
/// tell two accounts apart without putting a credential on screen.
///
/// The wording is doing real work here too. Turning this on sends pictures of the store's invoices
/// to Google, and an operator has to understand that from the screen without being told about
/// tokens or models — so it says what leaves the building, what it costs, and what happens when it
/// is off.
/// </summary>
internal sealed class VisionSettingsDialog : Form
{
    private readonly VisionSettings _original;

    /// <summary>The working list, held **encrypted** exactly as it will be saved.</summary>
    private readonly List<string> _protectedKeys;

    private readonly ListBox _keyList;
    private readonly Label _keyCount;
    private readonly Button _removeKey;
    private readonly TextBox _models;
    private readonly CheckBox _enabled;

    /// <summary>What to save. Only meaningful once the dialog closed with OK.</summary>
    public VisionSettings Result { get; private set; } = new();

    public VisionSettingsDialog(VisionSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _original = current.Clone();
        _protectedKeys = [.. current.AllProtectedKeys];

        Text = "AI reading";
        AppIcon.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10f);
        MinimumSize = new Size(620, 420);
        Size = new Size(640, 760);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16), AutoScroll = true,
        };
        var buttonStrip = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 8, 16, 12) };
        Controls.Add(layout);
        Controls.Add(buttonStrip);

        Shown += (_, _) => ClampToWorkingArea();

        _enabled = new CheckBox
        {
            Text = "Use AI reading on scanned invoices", AutoSize = true, Checked = current.Enabled,
        };
        layout.Controls.Add(WithHelp(_enabled,
            "A scanned invoice is read twice: from the text inside the PDF, and by an AI looking at "
            + "the picture of the page.\n\n"
            + "Where the two agree, the figure goes in. Where they disagree, whichever one the "
            + "invoice's own totals back is used. Where nothing can settle it, you are asked.\n\n"
            + "Off: works exactly as it does today, with more questions."));

        layout.Controls.Add(WithHelp(
            new Label
            {
                Text = "Invoices are sent to Google to be read.", AutoSize = true,
                ForeColor = Color.FromArgb(95, 105, 130), Margin = new Padding(0, 6, 0, 0),
            },
            "Vendor delivery tickets - products, counts and prices. No customer or card details.\n\n"
            + "They do leave the building, and on the free plan Google may use them to improve their "
            + "own systems. Paid billing stops that."));

        // --- keys ---------------------------------------------------------------------------
        layout.Controls.Add(WithHelp(FieldLabel("Google AI Studio keys"),
            "Free from aistudio.google.com - press Get API key. No card needed.\n\n"
            + "Stored encrypted and never shown back, so only the last four characters appear here.\n\n"
            + "When a key runs out of free requests, the next one in the list is used. Note that "
            + "spreading one shop's work across several Google accounts is against Google's terms, "
            + "and the account they suspend is whichever one is currently working."));

        _keyCount = new Label
        {
            AutoSize = true, Margin = new Padding(0, 0, 0, 4), ForeColor = Color.FromArgb(35, 42, 65),
        };
        layout.Controls.Add(_keyCount);

        _keyList = new ListBox { Width = 460, Height = 92, IntegralHeight = false };
        _keyList.SelectedIndexChanged += (_, _) => UpdateKeyList(keepSelection: true);
        layout.Controls.Add(_keyList);

        var keyButtons = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };
        var addKey = new Button { Text = "Add key...", AutoSize = true };
        addKey.Click += OnAddKey;
        _removeKey = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        _removeKey.Click += OnRemoveKey;
        keyButtons.Controls.Add(addKey);
        keyButtons.Controls.Add(_removeKey);
        layout.Controls.Add(keyButtons);

        // --- models -------------------------------------------------------------------------
        layout.Controls.Add(WithHelp(FieldLabel("Models (optional, one per line)"),
            $"Leave empty to use {string.Join(", then ", GeminiVisionReader.DefaultModels)}.\n\n"
            + "Each model has its own daily allowance, so running out on one does not stop the next "
            + "- which is cheaper and simpler than a second account."));

        _models = new TextBox
        {
            Width = 460, Height = 52, Multiline = true,
            Text = string.Join(Environment.NewLine,
                new[] { current.Model }.Concat(current.Models).Where(m => !string.IsNullOrWhiteSpace(m))),
        };
        layout.Controls.Add(_models);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        save.Click += (_, _) => Result = Collect();

        var test = new Button { Text = "Test the keys", AutoSize = true, Margin = new Padding(16, 0, 0, 0) };
        test.Click += OnTest;

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(test);
        buttonStrip.Controls.Add(buttons);

        AcceptButton = save;
        CancelButton = cancel;

        UpdateKeyList();
    }

    // ─────────────────────────────────────────────────────────────────────────────── the keys

    /// <summary>
    /// Redraws the list from the working set. Shows the position and the last four characters of
    /// each key — enough to tell two accounts apart, and not enough to be a credential.
    /// </summary>
    private void UpdateKeyList(bool keepSelection = false)
    {
        int selected = keepSelection ? _keyList.SelectedIndex : -1;

        _keyList.BeginUpdate();
        _keyList.Items.Clear();
        for (int i = 0; i < _protectedKeys.Count; i++)
        {
            string? plain = SecretProtector.Unprotect(_protectedKeys[i]);
            string tail = plain is { Length: >= 4 }
                ? "ends " + plain[^4..]
                : plain is null ? "saved on another PC - cannot be used here" : "saved";
            _keyList.Items.Add($"Key {i + 1}   ({tail})");
        }
        _keyList.EndUpdate();

        if (selected >= 0 && selected < _keyList.Items.Count) _keyList.SelectedIndex = selected;

        _keyCount.Text = _protectedKeys.Count switch
        {
            0 => "No keys saved yet. AI reading stays off until you add one.",
            1 => "1 key saved. Add a second and it will be used when the first runs out.",
            _ => $"{_protectedKeys.Count} keys saved. They are tried in this order.",
        };
        _removeKey.Enabled = _keyList.SelectedIndex >= 0;
    }

    /// <summary>
    /// Adds a key without touching the ones already there — which is the whole point of this screen
    /// existing. The typed value is encrypted immediately and the plain text is not kept.
    /// </summary>
    private void OnAddKey(object? sender, EventArgs e)
    {
        string? typed = PromptForKey();
        if (string.IsNullOrWhiteSpace(typed)) return;

        string? encrypted = SecretProtector.Protect(typed.Trim());
        if (encrypted is null)
        {
            MessageBox.Show(this, "That key could not be saved securely on this PC, so it was not added.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Same key twice is a wasted attempt every time a quota runs out, not a second allowance.
        if (_protectedKeys.Any(k => string.Equals(SecretProtector.Unprotect(k), typed.Trim(), StringComparison.Ordinal)))
        {
            MessageBox.Show(this, "That key is already in the list.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _protectedKeys.Add(encrypted);
        UpdateKeyList();
    }

    private void OnRemoveKey(object? sender, EventArgs e)
    {
        int index = _keyList.SelectedIndex;
        if (index < 0 || index >= _protectedKeys.Count) return;

        _protectedKeys.RemoveAt(index);
        UpdateKeyList();
    }

    /// <summary>A single-field prompt for one key, masked as it is typed.</summary>
    private string? PromptForKey()
    {
        using var dialog = new Form
        {
            Text = "Add a key",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = Font,
            ClientSize = new Size(460, 132),
        };
        AppIcon.ApplyTo(dialog);

        var label = new Label
        {
            Text = "Paste the key from aistudio.google.com:",
            AutoSize = true, Location = new Point(16, 16),
        };
        var box = new TextBox
        {
            Location = new Point(16, 44), Width = 424, UseSystemPasswordChar = true,
        };
        var ok = new Button
        {
            Text = "Add", DialogResult = DialogResult.OK, AutoSize = true, Location = new Point(284, 84),
        };
        var cancel = new Button
        {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Location = new Point(360, 84),
        };

        dialog.Controls.AddRange([label, box, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    // ───────────────────────────────────────────────────────────────────────────────── the rest

    /// <summary>
    /// Asks Google whether the keys work, rather than letting the store find out on a live delivery.
    /// Reports which key and model answered, so a cascade can be seen working.
    /// </summary>
    private void OnTest(object? sender, EventArgs e)
    {
        var keys = _protectedKeys
            .Select(SecretProtector.Unprotect)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToList();

        if (keys.Count == 0)
        {
            MessageBox.Show(this, "Add a key first, then test it.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Cursor = Cursors.WaitCursor;
        try
        {
            string result = new GeminiVisionReader(keys, Lines(_models)).CheckTheKeyWorks();
            MessageBox.Show(this, result, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    /// <summary>Non-blank lines of a box, trimmed.</summary>
    private static List<string> Lines(TextBox box) =>
        [.. box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)];

    private VisionSettings Collect() => new()
    {
        Enabled = _enabled.Checked,
        Models = Lines(_models),
        // The list is the setting now. The old single-key field is cleared rather than left behind,
        // so there is exactly one place a key can live and no chance of a stale one being tried.
        Model = null,
        ProtectedApiKey = null,
        ProtectedApiKeys = [.. _protectedKeys],
    };

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

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 0, 0, 0),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    /// <summary>
    /// The explanations, which used to be four paragraphs of text nobody read on a screen that gets
    /// opened once. They live behind a marker beside the thing they describe instead: an operator
    /// setting this up hovers and reads, and everyone after that sees a short screen.
    ///
    /// Long enough to stay up while it is being read, and it follows the mouse off the marker onto
    /// the control itself so a slow reader does not lose it.
    /// </summary>
    private readonly ToolTip _tips = new()
    {
        AutoPopDelay = 30000, InitialDelay = 150, ReshowDelay = 80, ShowAlways = true,
    };

    /// <summary>Puts a control and its help marker on one row, with the tip on both.</summary>
    private Control WithHelp(Control control, string help)
    {
        var marker = new Label
        {
            Text = "  ⓘ", AutoSize = true, Cursor = Cursors.Help,
            ForeColor = Color.FromArgb(42, 56, 145),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Margin = new Padding(2, control is CheckBox ? 3 : 1, 0, 0),
        };

        _tips.SetToolTip(marker, help);
        _tips.SetToolTip(control, help);

        var row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0, 10, 0, 3),
        };
        row.Controls.Add(control);
        row.Controls.Add(marker);
        return row;
    }
}
