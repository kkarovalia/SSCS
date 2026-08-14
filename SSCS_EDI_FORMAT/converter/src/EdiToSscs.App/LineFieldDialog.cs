using System.Globalization;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Asks the operator for one figure on one product row that the scan destroyed — its barcode, its
/// unit count, unit price, or dollar amount.
///
/// The window shows the printed row itself, cut out of the scan and marked. That is nearly all of
/// the design: the answer is on the screen, so there is nothing to describe, nothing to look up on
/// the paper, and nothing to explain. One question, the row, a box, one hint.
///
/// Where the picture cannot be had — a PDF that was never a scan — it falls back to naming the row
/// in words, which is what this window used to do for every case.
///
/// There is no prompt for a description. SSCS keeps its own wording for any item it already knows,
/// so a garbled one costs nothing and the operator can fix it on the rare new item.
/// </summary>
internal sealed class LineFieldDialog : Form
{
    private readonly TextBox _input;
    private readonly LineFieldRequest _request;

    /// <summary>
    /// What the operator typed, with the SKU dropped if they copied the whole printed token.
    ///
    /// The invoice prints the code as "078000200287/20020468", and where the scan mangled it badly
    /// enough that the marker cannot be trusted around the barcode alone, the whole row is marked and
    /// copying both halves is the obvious thing to do. The slash says exactly where one ends and the
    /// other begins, so this is not a guess — and refusing it would be telling someone off for
    /// reading the paper correctly.
    /// </summary>
    public string Value
    {
        get
        {
            string typed = _input.Text.Trim();
            int slash = typed.IndexOf('/');
            return _request.Field == LineField.Barcode && slash > 0 ? typed[..slash].Trim() : typed;
        }
    }

    public LineFieldDialog(LineFieldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        var row = SnipView.TryCreate(request.Snip);
        int textWidth = row?.Width ?? 450;

        Text = $"{request.Vendor} - item {request.Position} of {request.LineCount}";
        AppIcon.ApplyTo(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScroll = true;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(520, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
        };
        Controls.Add(layout);

        // The question, in five words, naming the column the vendor prints. This is the only line
        // that has to be read: the row is on screen underneath it with the figure marked, so there
        // is nothing to describe and nothing to look up.
        layout.Controls.Add(new Label
        {
            Text = request.Field == LineField.Barcode || request.ColumnName.Length == 0
                ? "Type the barcode"
                : $"Type the number under {request.ColumnName}",
            AutoSize = true, MaximumSize = new Size(textWidth, 0),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10),
        });

        // Which row is meant, in words — whenever the picture does not say it for itself.
        //
        // A cut-out row needs no caption: it is the row, on screen, with the figure boxed. A whole
        // page is the opposite. It is the fallback for a scan whose text layer read nothing, so
        // there are no coordinates, so nothing can be marked — and a page of an invoice with no
        // marker on it answers nothing at all unless the operator is told what they are looking for.
        // Without this the dialog reads "Type the barcode", shows small print, and says "find the
        // row by its name" with no name anywhere on it.
        if (row is null || request.WholePage)
            layout.Controls.Add(new Label
            {
                Text = Describe(request, brief: row is not null),
                AutoSize = true, MaximumSize = new Size(textWidth, 0),
                Font = row is not null ? new Font("Segoe UI", 10f, FontStyle.Bold) : Font,
                Margin = new Padding(0, 0, 0, 10),
            });

        if (row is not null)
        {
            // The invoice's own column names, cut from the same page at the same width, so they sit
            // directly over the figures they name — and directly against the row, with nothing
            // between them, because a legend two paragraphs above a picture is a legend nobody
            // connects to it. This is what answers "which number do you mean" on a row whose marker
            // could not be trusted to a single figure.
            //
            // Where the printed one cannot be had, the names are typed out instead rather than left
            // off. A snip is two printed lines from the middle of a ticket, and by item 16 the
            // header is a foot further up the page — so a row with no strip over it is a line of
            // bare figures with nothing to say which is the NET and which the AMOUNT.
            var legend = SnipView.TryCreate(request.Header) ?? TypedLegend(request, textWidth);
            if (legend is not null)
            {
                legend.Margin = new Padding(legend.Margin.Left, legend.Margin.Top, legend.Margin.Right, 0);
                layout.Controls.Add(legend);
            }

            layout.Controls.Add(row);
        }

        _input = new TextBox
        {
            Width = 260, Font = new Font("Consolas", 14f),
            // The gap under the box belongs to the hint when there is one, and to the box when
            // there is not, so the buttons never end up jammed against it.
            Margin = new Padding(0, 10, 0, request.Hint.Length > 0 ? 2 : 12),
        };
        layout.Controls.Add(_input);

        // A hint only where something can actually go wrong. Anything the picture already answers is
        // a line that gets skipped, and skipping becomes a habit that skips the line that mattered.
        if (request.Hint.Length > 0)
            layout.Controls.Add(new Label
            {
                Text = request.Hint,
                AutoSize = true, MaximumSize = new Size(textWidth, 0), ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 0, 0, 10),
            });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var cancel = new Button
        {
            Text = "Skip this file", DialogResult = DialogResult.Cancel,
            AutoSize = true, Margin = new Padding(8, 0, 0, 0),
        };
        var ok = new Button { Text = "Use this", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) => { if (!MayClose()) DialogResult = DialogResult.None; };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            Rectangle working = Screen.FromControl(this).WorkingArea;
            MaximumSize = new Size(Math.Max(520, working.Width - 32), Math.Max(420, working.Height - 32));
            if (Width > MaximumSize.Width || Height > MaximumSize.Height)
                Size = new Size(Math.Min(Width, MaximumSize.Width), Math.Min(Height, MaximumSize.Height));
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>
    /// The vendor's column names typed out, for a row whose printed header could not be cut.
    ///
    /// Second best on purpose, and it is worth being clear about why. The cut header is taken off
    /// the same page at the same width, so UNITS lands over the units and the operator reads
    /// straight down. Typed names cannot line up with a scan of somebody's typewriter, so they are
    /// an ordered list and claim nothing more than the order — which is still the difference between
    /// a readable row and six numbers in a line.
    ///
    /// Set apart from the picture deliberately: grey ground, our own font. A strip that looked cut
    /// from the invoice while not lining up with it would invite exactly the mistake of reading down
    /// from a column name to the figure beneath it, and land on the one next door.
    ///
    /// Nothing here is ever marked. The box goes on the figure being typed.
    /// </summary>
    private static Control? TypedLegend(LineFieldRequest request, int width)
    {
        if (request.ColumnLegend.Length == 0) return null;

        var font = new Font("Consolas", 9.5f, FontStyle.Bold);
        int lines = request.ColumnLegend.Count(c => c == '\n') + 1;
        return new Label
        {
            Text = request.ColumnLegend,
            Font = font,
            BackColor = Color.FromArgb(242, 242, 242),
            ForeColor = Color.FromArgb(48, 48, 48),
            BorderStyle = BorderStyle.FixedSingle,
            AutoSize = false,
            Padding = new Padding(6, 4, 6, 4),
            Margin = new Padding(0),
            Width = width,
            Height = (font.Height * lines) + 10,
        };
    }

    /// <summary>
    /// Points at the row on the paper: where to find it, what it says, and what the scan made of the
    /// figure being asked about. Two short lines — a paragraph here does not get read.
    /// </summary>
    /// <param name="brief">
    /// Drop the line about what the scan read. Set when a picture is being shown as well: over a
    /// whole page this is a caption saying which row to look for, and it has to be short enough to
    /// be read at a glance before the eye goes to the paper. What the scan made of the figure is
    /// exactly the thing not to dwell on there — it is wrong, that is why the question exists.
    /// </param>
    private static string Describe(LineFieldRequest request, bool brief = false)
    {
        var parts = new List<string>();
        if (request.LineCount > 0) parts.Add($"Item {request.Position} of {request.LineCount}");
        if (request.Description.Length > 0) parts.Add(request.Description);
        if (request.Field != LineField.Units && request.Units is int u)
            parts.Add($"{Math.Abs(u)} units");
        if (request.Field != LineField.Amount && request.Amount is decimal a)
            parts.Add(a.ToString("C2", CultureInfo.GetCultureInfo("en-US")));

        string row = parts.Count > 0 ? string.Join("  -  ", parts) : "this row";
        if (brief) return row;

        string scanned = request.PrintedValue.Length > 0
            ? $"\n\nThe scan read \"{request.PrintedValue}\", which does not match the rest of the invoice."
            : "\n\nThe scan could not read it at all.";
        return row + scanned;
    }

    /// <summary>Rejects something that plainly is not the figure being asked for, before it can
    /// reach the invoice. Anything that gets past here still has to survive the parser.</summary>
    private bool MayClose()
    {
        string typed = Value;
        if (typed.Length == 0)
        {
            MessageBox.Show(this, "Type the figure from the invoice, or Skip this file.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        switch (_request.Field)
        {
            case LineField.Barcode:
            {
                string digits = Upc.DigitsOnly(typed);
                // Bimbo prints the ten-digit body, not a complete UPC-A. Its Core parser restores
                // the leading zero and check digit after the operator has copied exactly what is on
                // the ticket. Applying the other vendors' 12-digit rule here made the Bimbo hint
                // impossible to follow.
                if (string.Equals(_request.Vendor, "Bimbo", StringComparison.OrdinalIgnoreCase))
                {
                    if (digits.Length == 10
                        || digits.Length == 12 && digits[0] == '0' && Upc.IsValidUpcA12(digits))
                        return true;
                    MessageBox.Show(this,
                        $"Bimbo prints 10 UPC digits; that is {digits.Length}. Type the digits under "
                        + "UPC, or a complete valid 12-digit UPC-A.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                if (string.Equals(_request.Vendor, "Dr Pepper", StringComparison.OrdinalIgnoreCase))
                {
                    if (digits.Length == 12 && Upc.IsValidUpcA12(digits)) return true;
                    MessageBox.Show(this,
                        "Dr Pepper requires the complete valid 12-digit UPC-A printed on the "
                        + $"invoice; that entry has {digits.Length} digits. It will not be padded, "
                        + "repaired, or accepted with a bad check digit.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                if (digits.Length is < 11 or > 13)
                {
                    MessageBox.Show(this,
                        $"A barcode is 12 digits; that is {digits.Length}. Type the number before the "
                        + "slash on the invoice.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                if (digits.Length == 11 || Upc.IsValidGtin(digits)) return true;
                return MessageBox.Show(this,
                    $"{digits} does not pass its own check digit, so either a digit was mistyped or the "
                    + "number printed under the bars is itself wrong.\n\nUse it anyway?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            }

            case LineField.Units:
            {
                if (int.TryParse(new string(typed.Where(char.IsAsciiDigit).ToArray()), out int units)
                    && units > 0)
                    return true;
                MessageBox.Show(this, "Type the whole number of units, for example 24.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            default:
            {
                string cleaned = new(typed.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
                    && amount > 0m)
                    return true;
                MessageBox.Show(this, "Type the dollar amount, for example 53.45.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }
    }
}
