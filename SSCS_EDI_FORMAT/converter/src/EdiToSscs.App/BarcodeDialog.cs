using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Asks the operator to read one product's barcode off the paper, for the rare line where the scan
/// destroyed it and the vendor's catalogue has never recorded a code for that product.
///
/// It names the line the way the operator will find it — its position on the invoice, the vendor's
/// product number, the description, and the quantity and cost so they can check they are looking at
/// the right row — and shows what the scan made of the barcode, which is usually close enough to
/// confirm the right row without being trustworthy enough to send.
///
/// The typed value is checked against its own check digit before the dialog will close. That digit
/// is arithmetic on the others, so it catches most single-digit typos for free; an 11-digit body is
/// completed rather than rejected. A code that still fails can be sent anyway, but only after the
/// operator says so — a wrong barcode is invisible downstream, since the money balances either way.
/// </summary>
internal sealed class BarcodeDialog : Form
{
    private readonly TextBox _input;

    /// <summary>The digits the operator typed, with any dashes and spaces removed.</summary>
    public string Barcode => Upc.DigitsOnly(_input.Text);

    public BarcodeDialog(BarcodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Text = "Barcode needed";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
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

        layout.Controls.Add(Heading(
            $"{request.FileName} - product {request.Position} of {request.LineCount}"));

        layout.Controls.Add(new Label
        {
            Text = $"{request.ItemNumber}  {request.Description}",
            AutoSize = true, MaximumSize = new Size(450, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 4),
        });

        layout.Controls.Add(new Label
        {
            Text = $"{Math.Abs(request.Units)} @ {request.UnitCost:0.00}"
                   + (request.Units < 0 ? "   (a credit - DUMP SECTION)" : ""),
            AutoSize = true, MaximumSize = new Size(450, 0), Margin = new Padding(0, 0, 0, 10),
        });

        layout.Controls.Add(new Label
        {
            Text = request.PrintedCode.Length > 0
                ? $"The scan read this line's barcode as {request.PrintedCode}, which is not a valid "
                  + "barcode, and this vendor has no code on record for the product. Please type it "
                  + "from the invoice."
                : "This line's barcode could not be read at all, and this vendor has no code on "
                  + "record for the product. Please type it from the invoice.",
            AutoSize = true, MaximumSize = new Size(450, 0), Margin = new Padding(0, 0, 0, 10),
        });

        layout.Controls.Add(FieldLabel("Barcode"));
        _input = new TextBox { Width = 260, Font = new Font("Consolas", 12f), Margin = new Padding(0, 0, 0, 2) };
        layout.Controls.Add(_input);
        layout.Controls.Add(Hint("All the digits under the bars, including the last one. Dashes are fine."));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var cancel = new Button { Text = "Skip this file", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var ok = new Button { Text = "Use this barcode", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) => { if (!MayClose()) DialogResult = DialogResult.None; };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>
    /// Whether the dialog may close: a real barcode, or one the operator has confirmed despite its
    /// check digit. An 11-digit body is accepted because its missing digit can be computed exactly.
    /// </summary>
    private bool MayClose()
    {
        string digits = Barcode;

        if (digits.Length == 0)
        {
            MessageBox.Show(this, "Type the barcode from the invoice, or Skip this file.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (digits.Length is < 11 or > 13)
        {
            MessageBox.Show(this,
                $"A barcode is 12 or 13 digits; you typed {digits.Length}. Please check the number on the invoice.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (digits.Length == 11 || Upc.IsValidGtin(digits)) return true;

        // The operator has usually typed exactly what is printed, so "you made a typo" would be the
        // wrong thing to say: the number under the bars can itself be wrong, and on this vendor it
        // demonstrably is. Name the real fix - scan the product - and let them proceed either way.
        return MessageBox.Show(this,
            $"{digits} does not pass its own check digit.\n\n"
            + "That means either a digit was mistyped, or the number printed under the barcode on the "
            + "invoice is itself wrong. The surest fix is to scan the product with the store's scanner "
            + "and use the number that comes up.\n\n"
            + "Use this one anyway?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(450, 0),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8),
    };

    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 3),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(450, 0),
        Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(110, 118, 138),
        Margin = new Padding(0, 0, 0, 6),
    };
}
