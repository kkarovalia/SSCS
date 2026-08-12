using System.Globalization;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Asks the operator to type the fields a scanned Blue Bell ticket cannot supply, showing the total
/// and line count that were read from it so they can find the matching paper before typing.
///
/// The invoice number is always asked for, and the box always starts empty. The scanners mis-read
/// it without any sign of trouble — a Blue Bell ticket's text layer says 033105035263 where the
/// paper reads 033103035263, and Chubiwinos numbers came back as "0V9293" and "6)9141" — and the
/// number carries no check digit, so nothing in the file can catch the error. Everything else on the
/// ticket reconciles against the printed TOTAL; this one field does not, so it is typed off the
/// paper. Offering the scan's reading as a starting point would defeat the point of asking.
///
/// **The date is the same field wearing a different hat, and is now treated identically**
/// (2026-08-12). It carries no check digit either, it is printed once, and the scanner eats its last
/// digit: 07/13/2016 and 07/20/2020 both for 2026, which books a delivery into the wrong period in
/// complete silence. Two things were wrong with asking-but-offering. The box arrived pre-filled with
/// the scan's guess, so Enter accepted it — and worse, whatever was typed **was then discarded**:
/// this class held the scan's date and returned it in preference to the box (`_knownDate ?? typed`),
/// so correcting a wrong year on screen changed nothing at all. Both are gone. Empty box, typed
/// every time, and what is typed is what is used.
/// </summary>
internal sealed class InvoiceNumberDialog : Form
{
    private const string DateFormat = TypedDate.Format;

    private readonly TextBox? _input;
    private readonly TextBox? _dateInput;
    private readonly TextBox? _totalInput;

    public string InvoiceNumber => _input?.Text.Trim() ?? "";

    /// <summary>
    /// The date the operator typed. Nothing else — the scan's own reading is not a fallback here and
    /// is not held by this class at all, which is what stops it becoming one again.
    /// </summary>
    public DateOnly Date => ParseDate(_dateInput?.Text) ?? default;

    /// <summary>The grand total typed, when the scan lost the ticket's own. Zero otherwise.</summary>
    public decimal Total => ParseMoney(_totalInput?.Text) ?? 0m;

    public InvoiceNumberDialog(InvoiceDetailsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Says whose paper to pick up, in the words the operator would use. The window title is the
        // first thing read and often the only thing read, so it carries the instruction rather than
        // a category name.
        Text = request.Vendor.Length > 0 ? $"Type invoice for {request.Vendor}" : "Invoice details";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 10f);
        // Sized from its contents, because the date box is only present on some tickets.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(500, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16),
        };
        Controls.Add(layout);

        // The Kind is only worth naming when it says something the title does not: a Blue Bell ticket
        // carries a Sales invoice and a Returns invoice, and the operator has to know which one this
        // box is asking about. For a one-invoice vendor it would just repeat the title.
        string kind = string.Equals(request.Kind, request.Vendor, StringComparison.OrdinalIgnoreCase)
            ? "" : $" ({request.Kind})";
        string heading = request.SectionCount > 1
            ? $"{request.FileName} - invoice {request.SectionIndex} of {request.SectionCount}{kind}"
            : $"{request.FileName}{kind}";

        layout.Controls.Add(Heading(heading));

        // What was read off the ticket, so the operator knows they have the right piece of paper.
        // The line count is left out when the invoice has not been read that far yet.
        string counted = request.LineCount > 0 ? $"      {Items(request.LineCount)}" : "";
        string read = $"Total {request.Total:0.00}{counted}";
        layout.Controls.Add(new Label
        {
            Text = read, AutoSize = true, MaximumSize = new Size(430, 0), Margin = new Padding(0, 0, 0, 10),
        });

        layout.Controls.Add(FieldLabel("Invoice #"));
        // Deliberately empty, for every vendor. The scan's own reading is never put in this box: it
        // is wrong often enough to matter (033105035263 for 033103035263; "0V9293" for 6V9293) and
        // nothing in the file can catch it, so a pre-filled box would turn the one field that needs
        // a person's eyes into a keypress. The operator reads it off the paper.
        _input = new TextBox
        {
            Width = 260, Font = new Font("Consolas", 12f), Margin = new Padding(0, 0, 0, 2),
        };
        layout.Controls.Add(_input);
        layout.Controls.Add(Hint("Copy it from the top of the ticket."));

        if (request.NeedsDate)
        {
            layout.Controls.Add(FieldLabel("Date  (month first)"));
            // Empty, every time, for the same reason the invoice number is. A date that is already
            // in the box is a date nobody reads off the paper.
            _dateInput = new TextBox
            {
                Width = 160, Font = new Font("Consolas", 12f), Margin = new Padding(0, 0, 0, 2),
            };
            layout.Controls.Add(_dateInput);
            layout.Controls.Add(Hint("Copy it from the top of the ticket."));
        }

        if (request.NeedsTotal)
        {
            layout.Controls.Add(FieldLabel("Amount due"));
            _totalInput = new TextBox
            {
                Width = 160, Font = new Font("Consolas", 12f), Margin = new Padding(0, 0, 0, 2),
            };
            layout.Controls.Add(_totalInput);
            layout.Controls.Add(Hint("The total at the bottom of the invoice, for example 1080.22."));
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var cancel = new Button { Text = "Skip this file", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var ok = new Button { Text = "Convert", DialogResult = DialogResult.OK, AutoSize = true };
        ok.Click += (_, _) =>
        {
            string? problem = WhatIsMissing();
            if (problem is null) return;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.None;
        };

        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            var first = (Control?)_input ?? _dateInput;
            first?.Focus();
            (first as TextBox)?.SelectAll();
        };
    }

    private static string Items(int count) => $"{count} line item{(count == 1 ? "" : "s")}";

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(430, 0),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8),
    };

    /// <summary>The name of a box, directly above it. Short — the operator is holding the paper.</summary>
    private static Label FieldLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 3),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold),
    };

    /// <summary>A footnote under a box. One short line, or it stops being read at all.</summary>
    private static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(430, 0),
        Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(110, 118, 138),
        Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>The message to show, or null when the dialog may close.</summary>
    private string? WhatIsMissing()
    {
        if (_input is not null && InvoiceNumber.Length == 0)
            return "Type the invoice number, or Skip this file.";
        if (_dateInput is not null && ParseDate(_dateInput.Text) is null)
            return "That date could not be read. Month first, so 8/11/26 or 08-11-2026.";
        if (_totalInput is not null && ParseMoney(_totalInput.Text) is not > 0m)
            return "Type the amount due like 1080.22.";
        return null;
    }

    /// <summary>Parses the typed grand total, ignoring a "$" or thousands separators.</summary>
    private static decimal? ParseMoney(string? text)
    {
        string cleaned = new((text ?? "").Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d)
            ? d
            : null;
    }

    /// <summary>
    /// Month, day, year in whatever punctuation the operator used. The rules live in
    /// <see cref="TypedDate"/> in Core, where they can be tested.
    /// </summary>
    private static DateOnly? ParseDate(string? text) => TypedDate.TryParse(text);
}
