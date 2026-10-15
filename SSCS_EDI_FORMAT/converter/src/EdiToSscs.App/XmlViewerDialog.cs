using System.Text;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Shows what a file converted to, exactly as it was sent or saved: the NAXML for SSCS or the EDI
/// text for Modisoft. These are the bytes themselves, decoded rather than reformatted, so what is
/// on screen is what the back office receives.
///
/// It reads from the documents held in memory rather than from the file, so it still shows what was
/// converted even if the saved copy has since been moved or deleted. A ticket that produced two
/// invoices gets a dropdown to switch between them.
///
/// "Save a copy" exists for the case where somebody needs a second copy somewhere else — to attach
/// by hand, or to keep. It writes where the operator chooses and touches nothing already saved.
/// </summary>
internal sealed class XmlViewerDialog : Form
{
    private readonly IReadOnlyList<ConvertedDocument> _documents;
    private readonly ComboBox? _picker;
    private readonly TextBox _text;
    private readonly Label _caption;

    public XmlViewerDialog(IReadOnlyList<ConvertedDocument> documents, string sourceFileName, string? savedPath)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0) throw new ArgumentException("There is nothing to show.", nameof(documents));
        _documents = documents;

        Text = IsXml(documents[0])
            ? $"Converted XML - {sourceFileName}"
            : $"Converted EDI file - {sourceFileName}";
        AppIcon.ApplyTo(this);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10f);
        MinimumSize = new Size(700, 480);
        Size = new Size(900, 640);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // caption + picker
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // the XML
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons
        Controls.Add(layout);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 8),
        };

        if (documents.Count > 1)
        {
            top.Controls.Add(new Label
            {
                Text = "Invoice:", AutoSize = true, Margin = new Padding(0, 8, 8, 0),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            });
            _picker = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280, Margin = new Padding(0, 4, 12, 0) };
            foreach (var doc in documents) _picker.Items.Add(doc.FileName);
            _picker.SelectedIndexChanged += (_, _) => ShowSelected();
            top.Controls.Add(_picker);
        }

        _caption = new Label
        {
            AutoSize = true, Margin = new Padding(0, 8, 0, 0), ForeColor = Color.FromArgb(95, 105, 130),
        };
        top.Controls.Add(_caption);
        layout.Controls.Add(top, 0, 0);

        _text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10f),
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        };
        layout.Controls.Add(_text, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 8, 0, 0),
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var saveCopy = new Button { Text = "Save a copy...", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var copy = new Button { Text = "Copy", AutoSize = true };
        saveCopy.Click += OnSaveCopy;
        copy.Click += OnCopy;
        buttons.Controls.Add(close);
        buttons.Controls.Add(saveCopy);
        buttons.Controls.Add(copy);

        var delivery = new Label
        {
            Text = savedPath is not null
                ? $"Saved in {Path.GetDirectoryName(savedPath)}"
                : "Not saved to a file.",
            AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 0, 0),
            ForeColor = Color.FromArgb(95, 105, 130),
        };

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(delivery, 0, 0);
        bottom.Controls.Add(buttons, 1, 0);
        layout.Controls.Add(bottom, 0, 2);

        CancelButton = close;
        if (_picker is not null) _picker.SelectedIndex = 0; else ShowSelected();
    }

    private ConvertedDocument Selected =>
        _documents[_picker is null || _picker.SelectedIndex < 0 ? 0 : _picker.SelectedIndex];

    private static bool IsXml(ConvertedDocument doc) =>
        doc.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    private void ShowSelected()
    {
        var doc = Selected;
        // The output is ASCII by construction, so decoding is exact; the box shows the file, not a
        // re-serialised version of it. Windows line endings are what the TextBox needs anyway.
        _text.Text = Encoding.UTF8.GetString(doc.Bytes).Replace("\r\n", "\n").Replace("\n", "\r\n");
        _text.SelectionStart = 0;
        _text.SelectionLength = 0;
        _caption.Text = $"{doc.FileName}  -  {doc.Bytes.Length:N0} bytes";
    }

    private void OnCopy(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(_text.Text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            MessageBox.Show(this, "The clipboard is being used by another program. Try again in a moment.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OnSaveCopy(object? sender, EventArgs e)
    {
        var doc = Selected;
        bool xml = IsXml(doc);
        using var dialog = new SaveFileDialog
        {
            Title = xml ? "Save a copy of this XML" : "Save a copy of this EDI file",
            FileName = doc.FileName,
            Filter = xml
                ? "SSCS XML files (*.xml)|*.xml|All files (*.*)|*.*"
                : "Modisoft EDI files (*.txt)|*.txt|All files (*.*)|*.*",
            OverwritePrompt = true,
            AddExtension = true,
            DefaultExt = xml ? "xml" : "txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, doc.Bytes);
            MessageBox.Show(this, $"Saved {Path.GetFileName(dialog.FileName)}.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, $"The copy could not be saved: {ex.Message}",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
