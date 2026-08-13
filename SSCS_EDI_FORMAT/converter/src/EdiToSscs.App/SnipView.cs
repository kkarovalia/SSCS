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
    /// document, because a picture scaled to fit a shop monitor is a picture of an invoice rather
    /// than a readable one. The whole page is behind it and reachable by scrolling, which is the
    /// point: the row is somewhere on this page and the caption above says which one to look for.
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

        // A PictureBox does not own its Image, so closing the dialog frees the control and leaves the
        // bitmap behind. That is not a rounding error here: a whole page drawn at reading width is
        // 860 x 5216, about 18 MB, and a bad Red Bull batch asks sixty-odd questions in a row. Tie
        // the picture's life to the control that shows it.
        box.Disposed += (_, _) => picture.Dispose();

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

        // The page opens at the top. For one day it opened at the row, from the vision model's
        // estimate of how far down the page that row sits, and two separate things were wrong with
        // it. The estimate is not good enough — measured across the store's thirty scans on
        // 2026-08-12, it put the view on the right row 6 times out of 11 on Red Bull and 0 out of 8
        // on Dr Pepper, twice landing on the address block and twice on the totals. And the scroll
        // never happened at all: AutoScrollPosition set before the panel is laid out is clamped to
        // zero, so every one of those runs silently opened at the top regardless. The estimate is
        // gone; the top is where a page opens, and the caption above says which row to look for.
        //
        // There was a "Turn the page over" link here for a day, and it is gone (2026-08-12, at the
        // user's request: "wtf is flip the page, that shit's useless"). It was for the sheet that
        // went through the scanner upside down — but a page the operator cannot read is a page to
        // re-feed at the scanner in five seconds, not one to read sideways off a screen and type
        // from. The picture is still turned automatically where the reading says it is inverted,
        // which costs nobody a click.
        return frame;
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
