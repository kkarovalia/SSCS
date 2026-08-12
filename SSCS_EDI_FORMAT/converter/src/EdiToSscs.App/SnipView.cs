using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using EdiToSscs.Core;

namespace EdiToSscs.App;

/// <summary>
/// Shows one printed row cut out of the scanned invoice, with the row being talked about marked.
///
/// This is the point of the whole exercise: the scan's picture has never been wrong, only the
/// machine's transcription of it, so the operator is shown the paper rather than our reading of it.
/// The marker matters as much as the picture — a snip carries the row above for context, and reading
/// the wrong row is exactly the mistake being guarded against.
/// </summary>
internal static class SnipView
{
    /// <summary>How wide the row is drawn. Wide enough to read at arm's length on a shop PC.</summary>
    private const int DisplayWidth = 860;

    /// <summary>
    /// How tall the picture may get before it is put in a scroller instead of shown whole.
    ///
    /// A cut-out row is a couple of hundred pixels and never reaches this. A whole page does — one of
    /// these receipts is 602 x 3652, which at reading width is over five thousand pixels tall — and a
    /// dialog that size does not fit on a shop monitor. Scrolling keeps the page legible; shrinking
    /// it to fit would make it a picture of an invoice rather than a readable one, which defeats the
    /// entire point of showing it.
    /// </summary>
    private const int MaxHeight = 460;

    /// <summary>
    /// How tall the frame is when it is showing a page rather than a cut-out row.
    ///
    /// Sized to read as a band of the invoice — six or eight printed rows — rather than as a whole
    /// document, because being handed an entire invoice to hunt through is barely better than being
    /// handed nothing. The rest of the page stays behind it and reachable: the model's idea of where
    /// the row is is a guess, and a window small enough to look tidy is a window that can miss.
    /// Scrolling out of a band recovers; a band cut out of the page and the rest thrown away does not.
    /// </summary>
    private const int BandHeight = 320;

    /// <summary>Builds the control, or null when there is no picture to show.</summary>
    public static Control? TryCreate(PdfRowSnip? snip)
    {
        var picture = Render(snip);
        if (picture is null) return null;

        var box = new PictureBox
        {
            Image = picture,
            Width = picture.Width,
            Height = picture.Height,
            SizeMode = PictureBoxSizeMode.Normal,
            BorderStyle = BorderStyle.None,
            Margin = new Padding(0),
        };

        if (picture.Height <= MaxHeight)
        {
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Margin = new Padding(0, 0, 0, 10);
            return box;
        }

        // Tall enough to need scrolling: the page goes inside a fixed-height frame. AutoScroll adds
        // the bar, and the extra width keeps it beside the picture rather than over the right-hand
        // AMOUNT column - which is regularly the very figure being asked about.
        var frame = new Panel
        {
            AutoScroll = true,
            Width = picture.Width + SystemInformation.VerticalScrollBarWidth + 2,
            Height = BandHeight,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 10),
        };
        frame.Controls.Add(box);

        // Open at the row rather than at the top of the page. Being asked to type one figure and
        // handed a whole invoice to scroll through is barely better than being handed nothing, so
        // the model says roughly how far down its row is and the frame starts there — the row a
        // third of the way in, with what comes above it for context.
        //
        // A hint, never a claim: nothing is marked, so a position that is out costs a scroll, which
        // is what the operator faced anyway. That is why this is allowed to be a guess when marking
        // a figure from a guess would not be.
        int target = (int)Math.Round(snip!.ScrollTo * ((double)picture.Height / Math.Max(1, snip.Height)));
        int top = Math.Clamp(target - BandHeight / 3, 0, Math.Max(0, picture.Height - BandHeight));
        if (top > 0) frame.AutoScrollPosition = new Point(0, top);

        // And a way to turn it over.
        //
        // Some of these sheets go through the scanner upside down, and the only thing that can say so
        // is a reader looking at the picture — the text layer is debris precisely *because* the page
        // is inverted. That reader gets it right most of the time and not all of the time, and a page
        // shown the wrong way up is no use at all. Rather than keep pushing on the guess, the operator
        // gets the control: one click, no consequences, and it is obvious from looking whether it is
        // needed. Cheap to offer and it ends the problem outright.
        var turn = new LinkLabel
        {
            Text = "Turn the page over",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        turn.LinkClicked += (_, _) =>
        {
            var shown = box.Image;
            if (shown is null) return;
            int was = frame.VerticalScroll.Value;
            shown.RotateFlip(RotateFlipType.Rotate180FlipNone);
            box.Image = shown;                                   // reassign so the box repaints
            // The row that was in view is now the same distance from the other end.
            frame.AutoScrollPosition = new Point(
                0, Math.Clamp(picture.Height - was - frame.ClientSize.Height, 0,
                    Math.Max(0, picture.Height - frame.ClientSize.Height)));
        };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 4),
            WrapContents = false,
        };
        stack.Controls.Add(frame);
        stack.Controls.Add(turn);
        return stack;
    }

    private static Bitmap? Render(PdfRowSnip? snip)
    {
        if (snip is null || snip.Width <= 0 || snip.Height <= 0) return null;

        try
        {
            using var page = new Bitmap(new MemoryStream(snip.PagePng));
            var cut = Rectangle.Intersect(
                new Rectangle(0, 0, page.Width, page.Height),
                new Rectangle(snip.Left, snip.Top, snip.Width, snip.Height));
            if (cut.Width <= 0 || cut.Height <= 0) return null;

            // Scans are one bit per pixel. Going to full colour first lets the resize smooth the
            // strokes instead of dropping every other one, which is the difference between a
            // readable 8 and a readable 3.
            using var row = page.Clone(cut, PixelFormat.Format24bppRgb);

            double scale = (double)DisplayWidth / row.Width;
            int height = Math.Max(1, (int)Math.Round(row.Height * scale));
            var canvas = new Bitmap(DisplayWidth, height);

            using (var g = Graphics.FromImage(canvas))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(row, new Rectangle(0, 0, canvas.Width, canvas.Height));

                // The figure itself where the snipper could place it, the whole row where it could
                // not. Drawn as a wash under a hard edge: the wash says "here" from across the
                // counter, and the edge says exactly how much of it is meant — which is the whole
                // point on a barcode, where the digits after the slash are not part of the answer.
                int top = Math.Max(0, (int)Math.Round(snip.FocusTop * scale));
                int band = Math.Min((int)Math.Round(snip.FocusHeight * scale), canvas.Height - top);
                int left = Math.Max(0, (int)Math.Round(snip.FocusLeft * scale));
                int span = Math.Min((int)Math.Round(snip.FocusWidth * scale), canvas.Width - left - 1);
                if (band > 0 && span > 0 && top < canvas.Height)
                {
                    using var wash = new SolidBrush(Color.FromArgb(46, 255, 193, 7));
                    using var edge = new Pen(Color.FromArgb(190, 198, 138, 0), 2f);
                    var marked = new Rectangle(left, top, span, band);
                    g.FillRectangle(wash, marked);
                    g.DrawRectangle(edge, marked);
                }
            }

            return canvas;
        }
        catch
        {
            return null;   // a picture is a courtesy; never let it cost the operator their invoice
        }
    }
}
