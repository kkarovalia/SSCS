using System.Globalization;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Asks the operator for one figure on one product row that the scan destroyed — its barcode, its
/// unit count, or its dollar amount.
///
/// The wording is deliberately plain and says which product it means the way a person reading the
/// paper would find it: "The 5th product's UPC is blurry." It also shows what the scan made of the
/// figure, which is usually close enough to confirm the right row without being trustworthy enough
/// to send.
///
/// There is no prompt for a description. SSCS keeps its own wording for any item it already knows,
/// so a garbled one costs nothing and the operator can fix it on the rare new item.
/// </summary>
internal sealed class LineFieldDialog : Form
{
    private readonly TextBox _input;
    private readonly LineFieldRequest _request;

    /// <summary>What the operator typed.</summary>
    public string Value => _input.Text.Trim();

    public LineFieldDialog(LineFieldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        string what = request.Field switch
        {
            LineField.Barcode => "UPC",
            LineField.Units => "units",
            _ => "amount",
        };

        Text = $"{request.Vendor} invoice - {what} needed";
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

        layout.Controls.Add(new Label
        {
            Text = $"{request.FileName} - line {request.Position} of {request.LineCount}",
            AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = $"The {Ordinal(request.Position)} product's {what} is blurry - please type it here.",
            AutoSize = true, MaximumSize = new Size(450, 0),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = Describe(request),
            AutoSize = true, MaximumSize = new Size(450, 0), Margin = new Padding(0, 0, 0, 10),
        });

        layout.Controls.Add(new Label
        {
            Text = what switch
            {
                "UPC" => "UPC",
                "units" => "Units",
                _ => "Amount",
            },
            AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 2),
        });

        _input = new TextBox { Width = 260, Font = new Font("Consolas", 12f), Margin = new Padding(0, 0, 0, 2) };
        layout.Controls.Add(_input);

        layout.Controls.Add(new Label
        {
            Text = request.Field switch
            {
                LineField.Barcode => "All the digits under the bars, including the last one. Dashes are fine.",
                LineField.Units => "The number in the UNITS column - individual cans, not cases.",
                _ => "The dollar figure in the TOTAL column, at the right-hand end of the row.",
            },
            AutoSize = true, MaximumSize = new Size(450, 0), ForeColor = SystemColors.GrayText,
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
        Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Names the row the way it is printed, plus whatever else about it did read.</summary>
    private static string Describe(LineFieldRequest request)
    {
        var parts = new List<string>();
        if (request.Description.Length > 0) parts.Add(request.Description);
        if (request.Field != LineField.Units && request.Units is int u)
            parts.Add($"{Math.Abs(u)} units");
        if (request.Field != LineField.Amount && request.Amount is decimal a)
            parts.Add(a.ToString("C2", CultureInfo.GetCultureInfo("en-US")));

        string row = parts.Count > 0 ? string.Join("  -  ", parts) : "this row";
        string scanned = request.PrintedValue.Length > 0
            ? $"\n\nThe scan read it as \"{request.PrintedValue}\", which does not add up against the "
              + "rest of the ticket."
            : "\n\nThe scan could not read it at all.";
        return row + scanned;
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        21 => "21st", 22 => "22nd", 23 => "23rd",
        31 => "31st", 32 => "32nd", 33 => "33rd",
        _ => $"{n}th",
    };

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
                if (digits.Length is < 11 or > 13)
                {
                    MessageBox.Show(this,
                        $"A barcode is 12 digits; you typed {digits.Length}. Please check the number on "
                        + "the invoice.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
