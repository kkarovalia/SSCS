using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdiToSscs.Core;

/// <summary>
/// Reads an invoice off the picture using Google's Gemini API.
///
/// One request per page, rather than one for the whole ticket. A page is the unit the scanner
/// produced, the requests stay small enough to go inline, and a page that fails — a bad image, a
/// refused request — costs only its own rows instead of the ticket's.
///
/// The model is asked for what is **printed**, never for what follows from it. It is told in as many
/// words to leave a figure out when it cannot read it, because a plausible invented amount is far
/// worse here than a missing one: a missing amount asks a person, and an invented one that happens
/// to reconcile is booked in silence. Money comes back as text for the same reason a cent is never
/// held as a float.
///
/// The established confirmation path fails empty. Bimbo's picture-primary contract instead reports
/// unavailable separately from unreadable: unavailable can offer the operator-approved OCR fallback,
/// while a model that reached the page but could not produce a consistent invoice requires a rescan.
/// </summary>
public sealed class GeminiVisionReader
    : IInvoiceVisionReader, IBimboVisionReader, IDrPepperVisionReader, IVisionProgressReporter
{
    /// <summary>
    /// What the operator is told is happening, in the words of the job rather than the protocol.
    /// Three passes read one invoice, and "Converting..." for all three of them is what made a
    /// hundred-second read look like a hung window.
    /// </summary>
    private const string ReadingPhase = "Reading the invoice";
    private const string BarcodePhase = "Checking the barcodes";
    private const string CloseUpPhase = "Checking each barcode close up";

    /// <summary>
    /// The model to read with.
    ///
    /// The store's proven primary reader. Newer and lighter stable multimodal models remain behind
    /// it as separate quota pools; the model is also a setting, so the store can change the order.
    /// </summary>
    public const string DefaultModel = "gemini-3.7-flash";

    /// <summary>
    /// Stable image/PDF readers, quality-first. Every configured key is tried on one model before
    /// the reader advances to the next.
    ///
    /// **Full Flash only — no Flash-Lite rung, deliberately** (2026-08-13). Which rung answered
    /// decides how good the reading is, and the day this was settled the allowance ran out on both
    /// Flash models by early afternoon: every conversion after that came off Flash-Lite, and that
    /// is where the day's transposed and duplicated barcode digits came from. One ticket read on
    /// Lite silently dropped a whole $24.85 row; the same file on full Flash came back complete and
    /// exact to the cent in 21 seconds.
    ///
    /// Removing the rung means that when every Flash model is exhausted the reader reports itself
    /// unavailable instead of quietly answering badly — which offers the legacy parser with an
    /// explicit approval rather than shipping a worse reading nobody was told about. That is the
    /// intended trade and it is the store's call, not a limitation.
    ///
    /// **Every name here was checked against the account's own `models.list` on 2026-08-13.** A
    /// model that does not exist is not a slower fall-through, it is a 404 on every request against
    /// that rung. There is no `gemini-3.1-flash` or `gemini-3-flash` — those ship only as `-lite`,
    /// `-image` or `-preview` variants — and `gemini-3.7-flash` does exist, which is why it is the
    /// top rung.
    /// </summary>
    public static readonly string[] DefaultModels =
    [
        DefaultModel,
        "gemini-3.6-flash",
        "gemini-3.5-flash",
    ];

    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models";

    /// <summary>The settings-page key check is tiny and should not wait forever on a dead connection.</summary>
    private static readonly TimeSpan KeyCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How much room the reply is given, in tokens.
    ///
    /// This used to be unset, which is not "no limit" — it is the model's own default, and on the
    /// newer thinking models the reasoning is spent out of the same budget before a single character
    /// of the answer is written. A full Dr Pepper page is nineteen rows of twenty fields plus eleven
    /// package subtotals, so the JSON alone is thousands of tokens; add thinking and the reply gets
    /// cut off mid-object. What comes back then is a 200 with a truncated or empty candidate, which
    /// this class could only call "incomplete" — and it looked for all the world like the model
    /// being bad at reading invoices. On 2026-08-13 `gemini-3.6-flash` did exactly that three runs
    /// running, spending 35 to 78 seconds each time to return nothing usable, while the smaller
    /// models answered the same page completely in fifteen.
    ///
    /// Sized for a page far bigger than any the store has sent. It costs nothing when unused: this
    /// is a ceiling, not an allocation.
    /// </summary>
    private const int OutputRoom = 65536;

    // Invoice reads have no client-side deadline. If Gemini accepted a request and is still working,
    // let it finish. Explicit HTTP quota/overload responses still advance to the next key/model at
    // once; only the optional settings-page key check has a short diagnostic timeout.
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// How long to wait after being throttled, per attempt.
    ///
    /// The free tier allows **20 requests a minute**, and a page is one request — so a stack of
    /// invoices dropped on the converter at once will hit that, and did on the store's own thirty-five
    /// (measured 2026-08-11: the first dozen went through and every one after came back 429). Google
    /// says how long to wait and it is around three quarters of a minute, so the waits are sized to
    /// clear the window rather than to be polite. Without this the reading is simply lost and the
    /// operator is asked about rows the model could have read perfectly well.
    /// </summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50)];

    private readonly string[] _keys;
    private readonly string[] _models;
    private readonly HttpClient _http;
    private readonly TimeSpan[] _backoff;

    /// <summary>
    /// How long to keep going back to a model that says it is busy. Injectable so the suite can
    /// prove the behaviour without spending two real minutes doing it.
    /// </summary>
    private readonly TimeSpan[] _busyWaits;

    /// <summary>
    /// Where the last successful request came from, so the next page starts there instead of
    /// re-probing combinations already known to be empty. An invoice is several pages and a quota
    /// does not refill between them.
    /// </summary>
    private int _at;

    public GeminiVisionReader(
        string apiKey, string? model = null, HttpClient? http = null, TimeSpan[]? backoff = null,
        TimeSpan[]? busyWaits = null)
        : this([apiKey], string.IsNullOrWhiteSpace(model) ? null : [model], http, backoff,
            busyWaits)
    {
    }

    /// <param name="apiKeys">
    /// Every key to try, in order. More than one is supported because a free-tier key runs out — but
    /// note that spreading one workload over several accounts to get past a quota is what Google
    /// calls circumvention, and the account it suspends is the one currently working.
    /// </param>
    /// <param name="models">Models to fall through after every key; null for <see cref="DefaultModels"/>.</param>
    /// <param name="busyWaits">
    /// Waits between retries of a model that answered 503; null for <see cref="BusyModelWaits"/>.
    /// An empty array retries without waiting, which is only ever what a test wants.
    /// </param>
    public GeminiVisionReader(
        IReadOnlyList<string>? apiKeys, IReadOnlyList<string>? models = null,
        HttpClient? http = null, TimeSpan[]? backoff = null, TimeSpan[]? busyWaits = null)
    {
        _keys = [.. (apiKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())];
        _models = [.. (models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim())];
        if (_models.Length == 0) _models = DefaultModels;
        _http = http ?? Shared;
        _backoff = backoff ?? Backoff;
        _busyWaits = busyWaits ?? BusyModelWaits;
    }

    /// <summary>True when a key is configured at all. No key is not an error, just no second reader.</summary>
    public bool IsConfigured => _keys.Length > 0;

    /// <summary>How many key-and-model pairs there are to work through.</summary>
    private int Combinations => _keys.Length * _models.Length;

    /// <summary>
    /// The n-th pair. Keys cycle fastest, so the preferred model is tried on every configured
    /// account before any account falls back to the lower-quality model.
    /// </summary>
    private (string Key, string Model) Pair(int index) =>
        (_keys[index % _keys.Length], _models[index / _keys.Length]);

    /// <summary>
    /// What one attempt against one key and model came back as.
    ///
    /// These are kept apart because until 2026-08-13 they all reached the log as the single word
    /// **throttled**, and they are five different faults. A 429 is the free allowance gone and it
    /// comes back tomorrow. A 503 is Google's model busy and it comes back in a minute. A 400 or a
    /// 403 says this request or this key is wrong and no later attempt fixes it. A dropped
    /// connection never reached Google at all, so the store's network is the thing to look at. And
    /// an HTTP 200 that does not validate is the model answering badly, which is the only one of
    /// them worth asking the same combination again about. A log that calls all five the same word
    /// cannot tell anybody which happened — the 12:38 run read "8 throttled" for six 503s and two
    /// 429s, and those want opposite responses.
    /// </summary>
    private enum AttemptOutcome { Read, Incomplete, RateLimited, Overloaded, Refused, Network, Canceled }

    /// <summary>
    /// What one read cost, tallied as it happens so <see cref="VisionReading.Note"/> can say where
    /// the time went. Per call rather than per instance: one reader serves every file in the queue.
    /// </summary>
    private sealed class Tally
    {
        public int Pages;
        public int Requests { get; private set; }
        public double Waited;
        private readonly Dictionary<AttemptOutcome, int> _counts = [];
        public List<string> Failures { get; } = [];

        public int Count(AttemptOutcome outcome) => _counts.GetValueOrDefault(outcome);

        public void Record(AttemptOutcome outcome, int keyNumber, string model, string? reason)
        {
            Requests++;
            _counts[outcome] = Count(outcome) + 1;
            if (reason is not null) Failures.Add($"key {keyNumber}/{model}: {reason}");
        }

        public string Describe(long ms, int rows)
        {
            var parts = new List<string>
            {
                $"{Pages} page{(Pages == 1 ? "" : "s")}",
                $"{Requests} request{(Requests == 1 ? "" : "s")}",
                $"{rows} row{(rows == 1 ? "" : "s")} read",
            };
            Add(AttemptOutcome.RateLimited, "out of free allowance (HTTP 429)");
            Add(AttemptOutcome.Overloaded, "model busy (HTTP 5xx)");
            Add(AttemptOutcome.Refused, "refused (other HTTP error)");
            Add(AttemptOutcome.Network, "could not reach Google");
            Add(AttemptOutcome.Canceled, "no reply came back");
            Add(AttemptOutcome.Incomplete, "answered but incomplete (HTTP 200)");
            if (Waited > 0) parts.Add($"waited {Waited:0}s on the free allowance");
            if (Failures.Count > 0) parts.Add($"failures [{string.Join("; ", Failures)}]");
            parts.Add($"{ms / 1000.0:0.0}s total");
            return string.Join(", ", parts);

            void Add(AttemptOutcome outcome, string label)
            {
                int count = Count(outcome);
                if (count > 0) parts.Add($"{count} {label}");
            }
        }
    }

    /// <summary>
    /// Where to say which attempt is outstanding, so a slow read is not a silent one. Optional;
    /// null is the silence this had before 2026-08-13, when a conversion that spent minutes inside
    /// one HTTPS call was indistinguishable from a hung application.
    ///
    /// **Only what is actually known is said.** A synchronous HTTPS call either returns or it does
    /// not; nothing in it reports whether the far end is working. So this says which try is out and
    /// what the finished ones came back as, and never that Google is "processing".
    /// </summary>
    public Action<string>? Progress { get; set; }

    private void Report(string phase, string what)
    {
        Action<string>? sink = Progress;
        if (sink is null || phase.Length == 0) return;
        try { sink($"{phase} - {what}"); }
        catch
        {
            // Progress is decoration. A subscriber throwing must never fail the conversion it is
            // describing.
        }
    }

    private static string Describe(AttemptOutcome outcome) => outcome switch
    {
        AttemptOutcome.RateLimited => "today's free AI allowance is used up",
        AttemptOutcome.Overloaded => "Google's AI is busy",
        AttemptOutcome.Refused => "Google refused the request",
        AttemptOutcome.Network => "could not reach Google",
        AttemptOutcome.Canceled => "no reply came back",
        AttemptOutcome.Incomplete => "the reply was incomplete",
        _ => "read",
    };

    public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
    {
        if (!IsConfigured || pdfBytes is null || pdfBytes.Length == 0) return VisionReading.Empty;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0) return VisionReading.Empty;
            tally.Pages = pages.Count;

            var rows = new List<VisionRow>();
            decimal? total = null;
            int? units = null;

            for (int number = 1; number <= pages.Count; number++)
            {
                var page = ReadPage(pages[number - 1], vendor, tally);
                if (page is null) continue;

                // Stamped here because this is the only place that knows: one request is one page,
                // and once the rows are concatenated there is no telling them apart again.
                rows.AddRange(page.Rows.Select(r => r with { Page = number }));
                // A ticket prints its total on the last page that has one; its unit counts add up
                // across pages, the same way the parser adds the printed ones up.
                if (page.PrintedTotal is not null) total = page.PrintedTotal;
                if (page.PrintedUnits is not null) units = (units ?? 0) + page.PrintedUnits.Value;
            }

            // A read that found nothing still says how long it spent finding nothing, which is the
            // case most worth seeing in the log: two minutes and an empty reading is a different
            // problem from two minutes and forty rows.
            string note = tally.Describe(clock.ElapsedMilliseconds, rows.Count);
            return rows.Count == 0
                ? VisionReading.Empty with { Note = note }
                : new VisionReading
                {
                    Rows = rows, PrintedTotal = total, PrintedUnits = units, Note = note,
                };
        }
        catch
        {
            return VisionReading.Empty;
        }
    }

    /// <summary>
    /// Reads the complete Dr Pepper invoice in one picture request. The response has no invoice
    /// number or date fields, and OCR is not consulted for any value.
    /// </summary>
    public DrPepperVisionReading ReadDrPepper(byte[] pdfBytes)
    {
        if (!IsConfigured) return DrPepperVisionReading.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return DrPepperVisionReading.Unreadable("The PDF had no page image to read.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return DrPepperVisionReading.Unreadable("The PDF had no page image to read.");
            tally.Pages = pages.Count;

            string body = BuildDrPepperPrimaryBody(pages);
            var reading = ReadSpecialPage(
                body, tally, ParseDrPepperReply, out reachedApi, ReadingPhase);
            string note = tally.Describe(clock.ElapsedMilliseconds, reading?.Rows.Count ?? 0);
            if (reading is null || reading.Rows.Count == 0
                || string.IsNullOrWhiteSpace(reading.AmountDue))
                return reachedApi
                    ? DrPepperVisionReading.Unreadable(note)
                    : DrPepperVisionReading.Unavailable(note);

            return reading with { Status = DrPepperVisionStatus.Success, Note = note };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? DrPepperVisionReading.Unreadable(note)
                : DrPepperVisionReading.Unavailable(note);
        }
    }


    /// <summary>
    /// Reads Bimbo as the source invoice rather than as a confirmation of OCR. This deliberately
    /// uses its own prompt, schema and result type: changing it must not change one byte of the
    /// established Dr Pepper or Red Bull requests.
    /// </summary>
    public BimboVisionReading ReadBimbo(byte[] pdfBytes)
    {
        if (!IsConfigured) return BimboVisionReading.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return BimboVisionReading.Unreadable("The PDF had no page image to read.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return BimboVisionReading.Unreadable("The PDF had no page image to read.");
            tally.Pages = pages.Count;

            var rows = new List<BimboVisionRow>();
            BimboVisionBandTotal? deliveries = null, returns = null;
            string? ticketUnits = null, ticketTotal = null, cashDue = null;

            for (int number = 1; number <= pages.Count; number++)
            {
                string body = BuildBimboPrimaryBody(pages[number - 1]);
                var page = ReadSpecialPage(
                    body, tally, ParseBimboReply, out bool answered, ReadingPhase);
                reachedApi |= answered;
                if (page is null) continue;

                rows.AddRange(page.Rows.Select(r => r with { Page = number }));
                deliveries ??= page.Deliveries;
                returns ??= page.Returns;
                ticketUnits ??= page.TicketUnits;
                ticketTotal ??= page.TicketTotal;
                cashDue ??= page.CashDue;
            }

            string note = tally.Describe(clock.ElapsedMilliseconds, rows.Count);
            if (rows.Count == 0 || string.IsNullOrWhiteSpace(ticketUnits)
                                || string.IsNullOrWhiteSpace(ticketTotal)
                                || rows.Any(r => r.IsCredit is null))
                return reachedApi
                    ? BimboVisionReading.Unreadable(note)
                    : BimboVisionReading.Unavailable(note);

            return new BimboVisionReading
            {
                Status = BimboVisionStatus.Success,
                Rows = rows,
                Deliveries = deliveries,
                Returns = returns,
                TicketUnits = ticketUnits,
                TicketTotal = ticketTotal,
                CashDue = cashDue,
                Note = note,
            };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? BimboVisionReading.Unreadable(note)
                : BimboVisionReading.Unavailable(note);
        }
    }

    /// <summary>
    /// A focused second read of Bimbo's check-digit-less UPC column. The target locators contain no
    /// primary barcode, so agreement means the picture was actually read twice rather than one
    /// answer being echoed back.
    /// </summary>
    public BimboBarcodeVerification VerifyBimboBarcodes(
        byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!IsConfigured) return BimboBarcodeVerification.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return BimboBarcodeVerification.Unreadable("The PDF had no page image to read.");
        if (targets.Count == 0)
            return new BimboBarcodeVerification { Status = BimboVisionStatus.Success };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return BimboBarcodeVerification.Unreadable("The PDF had no page image to read.");

            // Start the verification on another configured model/key where one exists. A separate
            // focused request on the same model is still useful; a different model is better.
            if (Combinations > 1) _at = (_at + 1) % Combinations;

            var rows = new List<BimboBarcodeRow>();
            foreach (var group in targets.GroupBy(t => Math.Clamp(t.Page, 1, pages.Count)))
            {
                tally.Pages++;
                var wanted = group.ToList();
                var ids = wanted.Select(t => t.RowId).ToHashSet(StringComparer.Ordinal);
                string body = BuildBimboBarcodeBody(pages[group.Key - 1], wanted);
                var page = ReadSpecialPage(
                    body, tally, s => ParseBimboBarcodeReply(s, ids), out bool answered,
                    BarcodePhase);
                reachedApi |= answered;
                if (page is not null) rows.AddRange(page.Rows);
            }

            string note = tally.Describe(clock.ElapsedMilliseconds, rows.Count);
            if (rows.Count != targets.Count)
                return reachedApi
                    ? BimboBarcodeVerification.Unreadable(note)
                    : BimboBarcodeVerification.Unavailable(note);

            return new BimboBarcodeVerification
            {
                Status = BimboVisionStatus.Success,
                Rows = rows,
                Note = note,
            };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? BimboBarcodeVerification.Unreadable(note)
                : BimboBarcodeVerification.Unavailable(note);
        }
    }

    /// <summary>
    /// A third UPC read whose visual input is materially different from both full-page reads. Each
    /// image contains only one enlarged UPC cell; no description, item number, quantity, money, or
    /// earlier barcode is present for the model to echo or infer from.
    /// </summary>
    public BimboBarcodeVerification VerifyBimboBarcodeCrops(
        byte[] pdfBytes, IReadOnlyList<BimboBarcodeTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!IsConfigured) return BimboBarcodeVerification.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return BimboBarcodeVerification.Unreadable("The PDF had no page image to read.");
        if (targets.Count == 0)
            return new BimboBarcodeVerification { Status = BimboVisionStatus.Success };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally { Pages = targets.Select(t => t.Page).Distinct().Count() };
        bool reachedApi = false;
        try
        {
            var crops = targets
                .Select(t => new BimboUpcCrop(t.RowId, TryMakeBimboUpcCrop(pdfBytes, t)))
                .Where(c => c.Png is not null)
                .Select(c => c with { Png = c.Png! })
                .ToList();
            if (crops.Count == 0)
                return BimboBarcodeVerification.Unreadable(tally.Describe(clock.ElapsedMilliseconds, 0));

            // The second pass starts one combination on; the third starts two on. With fewer than
            // three combinations a model can repeat, but the third visual input remains independent:
            // it contains only the isolated cell and none of the context or prior answers.
            if (Combinations > 1) _at = (_at + 2) % Combinations;

            var ids = crops.Select(c => c.RowId).ToHashSet(StringComparer.Ordinal);
            string body = BuildBimboBarcodeCropBody(crops);
            var reply = ReadSpecialPage(
                body, tally, s => ParseBimboBarcodeReply(s, ids), out bool answered,
                CloseUpPhase);
            reachedApi |= answered;

            string note = tally.Describe(clock.ElapsedMilliseconds, reply?.Rows.Count ?? 0);
            if (reply is null || reply.Rows.Count != crops.Count)
                return reachedApi
                    ? BimboBarcodeVerification.Unreadable(note)
                    : BimboBarcodeVerification.Unavailable(note);

            var read = reply.Rows.ToDictionary(r => r.RowId, StringComparer.Ordinal);
            return new BimboBarcodeVerification
            {
                Status = BimboVisionStatus.Success,
                // A row whose visual cell could not be isolated remains null and therefore goes to
                // the operator; it does not make readable sibling rows lose their third opinion.
                Rows = targets.Select(t => read.TryGetValue(t.RowId, out var row)
                    ? row : new BimboBarcodeRow(t.RowId, null)).ToList(),
                Note = note,
            };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? BimboBarcodeVerification.Unreadable(note)
                : BimboBarcodeVerification.Unavailable(note);
        }
    }

    private sealed record BimboUpcCrop(string RowId, byte[]? Png);

    private static byte[]? TryMakeBimboUpcCrop(byte[] pdfBytes, BimboBarcodeTarget target)
    {
        if (target.LineNumber < 1) return null;

        var snip = PdfRowSnipper.TryCut(
            pdfBytes, target.LineNumber, target.LineNumber,
            focusLineNo: target.LineNumber,
            fallbackColumn: new PdfColumnBand(0.015, 0.185));
        if (snip is null || snip.FocusWidth < 1 || snip.FocusHeight < 1) return null;

        try
        {
            using var page = new System.Drawing.Bitmap(new MemoryStream(snip.PagePng));
            int x = Math.Clamp(snip.FocusLeft, 0, page.Width - 1);
            int y = Math.Clamp(snip.Top + snip.FocusTop, 0, page.Height - 1);
            int horizontalAir = Math.Max(2, snip.FocusHeight / 3);
            int verticalAir = Math.Max(1, snip.FocusHeight / 5);
            x = Math.Max(0, x - horizontalAir);
            y = Math.Max(0, y - verticalAir);
            int width = Math.Min(page.Width - x, snip.FocusWidth + horizontalAir * 2);
            int height = Math.Min(page.Height - y, snip.FocusHeight + verticalAir * 2);
            if (width < 2 || height < 2) return null;

            using var cell = page.Clone(
                new System.Drawing.Rectangle(x, y, width, height), page.PixelFormat);
            if (target.PageUpsideDown)
                cell.RotateFlip(System.Drawing.RotateFlipType.Rotate180FlipNone);

            double scale = Math.Max(3.0, 96.0 / Math.Max(1, cell.Height));
            int enlargedWidth = Math.Max(1, (int)Math.Round(cell.Width * scale));
            int enlargedHeight = Math.Max(1, (int)Math.Round(cell.Height * scale));
            using var enlarged = new System.Drawing.Bitmap(enlargedWidth, enlargedHeight);
            using (var graphics = System.Drawing.Graphics.FromImage(enlarged))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(cell, 0, 0, enlargedWidth, enlargedHeight);
            }

            using var output = new MemoryStream();
            enlarged.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that the key works, and says so in words an operator can act on.
    ///
    /// The alternative is finding out on a live delivery, which is the same argument the Test Email
    /// button rests on. Deliberately tiny — a few words in, a few words out — so it costs nothing
    /// against the daily allowance.
    /// </summary>
    public string CheckTheKeyWorks()
    {
        if (!IsConfigured) return "There is no key saved yet, so there is nothing to test.";

        var refused = new List<string>();
        var empty = new List<string>();
        bool offline = false;

        for (int i = 0; i < Combinations; i++)
        {
            var (key, model) = Pair(i);
            int which = (i % _keys.Length) + 1;
            string named = _keys.Length > 1 ? $"key {which} on {model}" : model;

            int status = Ping(key, model, ref offline);
            switch (status)
            {
                case >= 200 and < 300:
                    return _keys.Length > 1 || _models.Length > 1
                        ? $"Working, using {named}. Invoices will be read with AI help from now on."
                        : "The key works. Invoices will be read with AI help from now on.";
                case 400 or 401 or 403: refused.Add(named); break;
                case 404: break;                    // that model is gone; the next one may not be
                case 429: empty.Add(named); break;
            }
        }

        if (offline) return "Could not reach Google. Check this PC is online, then try again.";
        if (empty.Count > 0)
            return $"The key works, but today's free allowance is used up ({string.Join(", ", empty)}). "
                   + "It resets overnight. Invoices still convert in the meantime, with more questions.";
        if (refused.Count > 0)
            return "That key was refused. Check it was copied whole, with no spaces.";

        return "None of those models answered. Clear the model box to go back to the default.";
    }

    /// <summary>One tiny request, for the Test button. Returns the HTTP status, or 0 if it never got there.</summary>
    private int Ping(string key, string model, ref bool offline)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{Endpoint}/{model}:generateContent");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            request.Content = new StringContent(
                """{"contents":[{"parts":[{"text":"Reply with the word ready."}]}]}""",
                Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

            using var cancel = new CancellationTokenSource(KeyCheckTimeout);
            using var response = _http.Send(request, cancel.Token);
            return (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            offline = true;
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// One page, read — trying every key and model before waiting on any of them.
    ///
    /// The order matters. A throttled request means *this* key has nothing left on *this* model, and
    /// the next model has a quota of its own, so moving across costs nothing and the operator waits
    /// for nobody. Only when every combination is empty is there anything to wait for, and then the
    /// whole chain is tried again after a pause.
    ///
    /// A 400 or a 403 is different: it says the request or the key is wrong, not early. That
    /// combination is skipped and never waited on.
    ///
    /// This is Red Bull's optional **second** reader, and it keeps its quota wait for the reason the
    /// wait was measured for: thirty-five invoices dropped on the converter at once will exhaust the
    /// free minute, and without the wait every file after the first dozen silently loses its second
    /// opinion. Nothing here is the source of an invoice, so a slow one costs time and no accuracy.
    /// The picture-primary vendors do not go through this method — see
    /// <see cref="ReadSpecialPage{T}"/>, which never sleeps.
    /// </summary>
    private VisionReading? ReadPage(byte[] png, VisionVendor vendor, Tally tally)
    {
        if (Combinations == 0) return null;
        string body = BuildBody(png, vendor);
        const string phase = "Reading the picture";

        for (int round = 0; ; round++)
        {
            bool anythingWorthWaitingFor = false;

            for (int tried = 0; tried < Combinations; tried++)
            {
                int index = (_at + tried) % Combinations;
                var reading = Attempt(
                    index, body, tally, ParseReply, phase, tried + 1, Combinations,
                    out _, out AttemptOutcome outcome);
                if (reading is not null)
                {
                    _at = index;          // start here next page rather than re-probing empty quotas
                    return reading;
                }
                anythingWorthWaitingFor |= outcome
                    is AttemptOutcome.RateLimited or AttemptOutcome.Overloaded
                    or AttemptOutcome.Network or AttemptOutcome.Canceled;
            }

            // Every key on every model is empty. Waiting is the only thing left, and only for as
            // long as the quota window - beyond that the conversion goes on without a second reader.
            if (!anythingWorthWaitingFor || round >= _backoff.Length) return null;
            tally.Waited += _backoff[round].TotalSeconds;
            Report(phase, $"nothing answered; waiting {_backoff[round].TotalSeconds:0} seconds for "
                          + "the free allowance");
            Thread.Sleep(_backoff[round]);
        }
    }

    // ───────────────────────────────────────────────────────────────────────── the request

    /// <summary>
    /// One AI pass for a picture-primary vendor: **every configured key and model, once each, in
    /// order, with nothing in between** — and then at most one more request.
    ///
    /// The shape is what it is because of what each answer means. A 429, a 503, a refusal or a
    /// dropped line all say *this combination cannot serve this request now*, and the next
    /// combination is a different quota pool on a different model, so the only sensible response is
    /// to move to it immediately. Sleeping first helps nothing and the operator is standing at the
    /// counter. An HTTP 200 that fails validation is a different animal — the service is up and
    /// answering, it simply answered badly — and that is the one thing worth asking again, once, of
    /// the combination that proved it can reply.
    ///
    /// So the cost is bounded and knowable: at most one request per combination, plus one. It used
    /// to be three full sweeps with 20- and 50-second sleeps between them, which on 2026-08-13 took
    /// a single Dr Pepper invoice past four minutes with the window saying only "Converting...".
    /// There is no sleep on this path at all, and no client-side deadline that would guess at
    /// whether Google is still working — see <see cref="Shared"/>.
    ///
    /// <paramref name="apiAnswered"/> is the whole of the unavailable/unreadable distinction. True
    /// means some combination returned HTTP 200, so the service reached the invoice and the page
    /// needs re-scanning; false means nothing did, and only then may the operator be offered the
    /// legacy OCR reader.
    /// </summary>
    private T? ReadSpecialPage<T>(
        string body, Tally tally, Func<Stream, T?> parse, out bool apiAnswered,
        string phase = "") where T : class
    {
        apiAnswered = false;
        if (Combinations == 0) return null;

        int startingAt = Math.Abs(Volatile.Read(ref _at) % Combinations);
        int answeredAt = -1;

        for (int tried = 0; tried < Combinations; tried++)
        {
            int index = (startingAt + tried) % Combinations;
            var reading = AttemptRung(
                index, body, tally, parse, phase, tried + 1, Combinations,
                ref apiAnswered, ref answeredAt);
            if (reading is not null)
            {
                Interlocked.Exchange(ref _at, index);
                return reading;
            }
        }

        // Nothing answered at all. Every rung has now had its say, including the extra goes a busy
        // one gets, so asking the ladder again would only make the operator wait a second time for
        // the same silence.
        if (answeredAt < 0) return null;

        // Something answered, and answered badly. One fresh attempt, at the combination that proved
        // it can reply — not another sweep.
        var again = Attempt(
            answeredAt, body, tally, parse, phase, Combinations + 1, Combinations + 1, out _, out _);
        if (again is not null) Interlocked.Exchange(ref _at, answeredAt);
        return again;
    }

    /// <summary>
    /// How long to wait before giving a busy model another go, and — by its length — how many extra
    /// goes it gets. Two, a few seconds apart.
    ///
    /// A 503 says Google's own server is busy, which is nobody's allowance and clears on its own,
    /// so the rung that is worth insisting on is the one already in hand: the best model the ladder
    /// has reached. Dropping to a worse model over a blip that lasts seconds is how an invoice ends
    /// up read by something that transposes digits — and dropping off the ladder entirely is how a
    /// readable invoice gets refused, which is what happened to the store at 17:20 on 2026-08-13
    /// while the same file went through in one request two minutes later.
    ///
    /// **Nothing here retries a 429.** An exhausted allowance does not come back before tomorrow,
    /// and sleeping on one is exactly what used to leave an invoice at "Converting..." for minutes.
    /// </summary>
    private static readonly TimeSpan[] BusyModelWaits =
        [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)];

    /// <summary>
    /// One rung of the ladder, including the extra goes it gets if it comes back busy.
    ///
    /// The retry happens **here**, while this key and model are still the current rung, rather than
    /// after the whole ladder has been walked. That is what "try the best model a couple of times
    /// before dropping" means: 3.7 on key 1 three times over nine seconds, then 3.7 on key 2, then
    /// 3.6 on key 1, and so on down. Any number of keys works — the ladder is every configured key
    /// on one model before the next model, whether that is one key or six.
    /// </summary>
    private T? AttemptRung<T>(
        int index, string body, Tally tally, Func<Stream, T?> parse, string phase,
        int number, int of, ref bool apiAnswered, ref int answeredAt) where T : class
    {
        for (int go = 0; ; go++)
        {
            var reading = Attempt(
                index, body, tally, parse, phase, number, of,
                out bool answered, out AttemptOutcome outcome);
            if (answered)
            {
                apiAnswered = true;
                if (answeredAt < 0) answeredAt = index;
            }
            if (reading is not null) return reading;

            // Only busy earns another go, and only while there are goes left. Anything else - out
            // of allowance, refused, unreachable, or answering badly - is not fixed by waiting.
            if (outcome != AttemptOutcome.Overloaded || go >= _busyWaits.Length) return null;

            TimeSpan wait = _busyWaits[go];
            Report(phase, "Google's AI is busy - their server, not your allowance. Trying the same "
                          + $"model again in {wait.TotalSeconds:0} seconds.");
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
    }

    /// <summary>One attempt, tallied and announced. Never throws.</summary>
    private T? Attempt<T>(
        int index, string body, Tally tally, Func<Stream, T?> parse, string phase,
        int number, int of, out bool apiAnswered, out AttemptOutcome outcome) where T : class
    {
        var (key, model) = Pair(index);
        int keyNumber = index % _keys.Length + 1;

        Report(phase, $"try {number} of {of} sent (key {keyNumber}, {model}). No reply yet.");
        var reading = TryOnce(
            body, key, model, parse, out apiAnswered, out outcome, out string? failure);
        tally.Record(outcome, keyNumber, model, failure);
        if (reading is null) Report(phase, $"try {number} of {of}: {Describe(outcome)}.");
        return reading;
    }

    /// <summary>
    /// One request against one key and model, classified by what actually came back.
    ///
    /// There is no way from here to tell a request Google is working on from one it has quietly
    /// stopped working on: a synchronous call either returns or it does not, and nothing in the
    /// protocol reports progress. Nothing in this class claims otherwise.
    /// </summary>
    private T? TryOnce<T>(
        string body, string key, string model, Func<Stream, T?> parse,
        out bool apiAnswered, out AttemptOutcome outcome, out string? failure) where T : class
    {
        apiAnswered = false;
        outcome = AttemptOutcome.Read;
        failure = null;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{Endpoint}/{model}:generateContent");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            request.Content = new StringContent(
                body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

            using var response = _http.Send(request, CancellationToken.None);
            if (response.IsSuccessStatusCode)
            {
                apiAnswered = true;

                // Buffered so that a reply which failed to parse can still be asked *why*. One page
                // of JSON; the copy costs nothing next to the request that fetched it.
                using var buffer = new MemoryStream();
                using (var stream = response.Content.ReadAsStream(CancellationToken.None))
                    stream.CopyTo(buffer);
                buffer.Position = 0;
                var reading = parse(buffer);
                if (reading is null)
                {
                    outcome = AttemptOutcome.Incomplete;
                    buffer.Position = 0;
                    failure = RanOutOfRoom(buffer)
                        ? "HTTP 200 reply cut short (ran out of output room)"
                        : "HTTP 200 incomplete response";
                }
                return reading;
            }

            int status = (int)response.StatusCode;
            outcome = status == 429 ? AttemptOutcome.RateLimited
                : status >= 500 ? AttemptOutcome.Overloaded
                : AttemptOutcome.Refused;
            failure = $"HTTP {status}";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Production invoice reads have no client deadline, so a cancellation here is the
            // connection or the caller, never a timer this class set. It is counted apart from a
            // real 429: "throttled" was the wrong word for it and sent people to the wrong fix.
            bool canceled = ex is TaskCanceledException or OperationCanceledException;
            outcome = canceled ? AttemptOutcome.Canceled : AttemptOutcome.Network;
            failure = ex is HttpRequestException http && http.StatusCode is not null
                ? $"HTTP {(int)http.StatusCode.Value} transport error"
                : canceled ? "canceled before a reply" : "network error";
            return null;
        }
        catch
        {
            // A reply that arrived and could not be read at all. If the status said 200 it belongs
            // with the other incomplete replies, and gets the one fresh attempt they get.
            outcome = apiAnswered ? AttemptOutcome.Incomplete : AttemptOutcome.Refused;
            failure = "invalid response";
            return null;
        }
    }

    private static string BuildBody(byte[] png, VisionVendor vendor)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();

            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");

            w.WriteStartObject();
            w.WriteStartObject("inline_data");
            w.WriteString("mime_type", "image/png");
            w.WriteBase64String("data", png);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject();
            w.WriteString("text", PromptFor(vendor));
            w.WriteEndObject();

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartObject("generationConfig");
            // Reading printed digits is not a creative task; nothing here should vary run to run.
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteSchema(w, vendor);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildDrPepperPrimaryBody(IReadOnlyList<byte[]> pages)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");
            for (int index = 0; index < pages.Count; index++)
            {
                w.WriteStartObject();
                w.WriteString("text", $"Invoice page {index + 1} of {pages.Count}:");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteStartObject("inline_data");
                w.WriteString("mime_type", "image/png");
                w.WriteBase64String("data", pages[index]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("text", DrPepperPrimaryPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteDrPepperPrimarySchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Dr Pepper's one and only request.
    ///
    /// Since 2026-08-13 nothing downstream re-reads this invoice, so this prompt is where a misread
    /// figure is caught or not caught at all. That is the right place for it: the model is looking
    /// at the page and can look again, which no amount of arithmetic on this side can do. Hence the
    /// explicit final pass — count the digits, verify the check digit, multiply the row out, add the
    /// subtotals up — stated as work to do before answering rather than as background advice.
    ///
    /// The one deliberate asymmetry is the barcode. Everywhere else an uncertain figure is null,
    /// because a null unit count drops a row and says so, which is safe. A null barcode drops the
    /// row too — and a barcode that merely fails its check digit is caught here, flagged, and fixed
    /// in SSCS in seconds. So a legible-but-doubtful code is worth more than nothing, and the
    /// prompt says exactly that. It must never be read as licence to repair one: adjusting a digit
    /// to satisfy the check produces a code that looks perfect and posts the delivery to the wrong
    /// item, which is the one failure nothing downstream can see.
    /// </summary>
    private const string DrPepperPrimaryPrompt = """
        You are the only reader of this scanned Dr Pepper invoice. Nothing downstream will re-read
        the page, so your answer is what reaches the store's system. Read only the physical print
        visible in the supplied page pictures. Never use an OCR transcription, product catalogue,
        remembered product, manufacturer prefix, arithmetic inference, or a plausible repair.
        Inspect every printed character twice. A pen stroke may damage a digit; it does not replace
        it. If a value is still uncertain, return null for that field.

        Return every actual product exactly once and in physical invoice order. Exclude equipment,
        deposits, PACKAGE SUBTOTAL rows, TOTAL SALE rows, summary rows, and barcode scan sheets.
        A product is normally two visual lines: description plus a 12-digit UPC/SKU on the product
        line, then the money line headed WHSLE CASES UNITS NET TAX AMOUNT. Credit rows are under the
        CREDITS heading, omit WHSLE, may print CASES as zero, and must set isCredit true. Return
        numeric values as exact strings, preserving every decimal and printed sign. isCredit owns
        the output sign; do not turn a routine credit into an error.

        For each row return its page, section (SALES or CREDITS), order on that page within that section,
        occurrence of the same item within that section, item number, description, barcode,
        wholesale where physically printed, cases, units, net, tax and amount. Also return every
        PACKAGE SUBTOTAL with the rowIds it covers and its cases, units and amount; TOTAL SALE cases,
        units and amount; the separate sales/credit summary counts and amounts; and AMOUNT DUE.
        Generate each rowId from page, section and page-local row order and reuse it exactly in subtotal rowIds.

        Before you answer, work through every one of these checks and go back to the picture for any
        row that fails one:

        1. Barcodes. Dr Pepper prints a full 12-digit UPC-A. Count the digits: exactly twelve, and
           the twelfth is calculated from the other eleven. Verify it — add digits 1, 3, 5, 7, 9 and
           11, multiply that sum by three, add digits 2, 4, 6, 8 and 10, and the twelfth digit is
           whatever brings the result up to a multiple of ten. If it does not come out, you have
           misread a digit: look at the printed cell again, character by character. Transposed pairs
           and a duplicated digit inside a run of zeros are the two mistakes to look for.
        2. Delivery rows. CASES x NET must equal AMOUNT. This does not hold on a credit and must not
           be checked there.
        3. Every PACKAGE SUBTOTAL against the rows above it, in cases, units and amount.
        4. The printed unit counts against the rows in each section, and the signed row money
           against AMOUNT DUE.

        These checks tell you where to look again. They never tell you what to write. Never change,
        fill, reject, or derive a value merely to make one pass, and never adjust a barcode digit to
        satisfy its check digit — a repaired code looks perfect and sends the delivery to the wrong
        product. If a figure is still uncertain after looking again, return null for it.

        The barcode is the one exception to returning null. A code that fails its check digit is
        still worth returning, because it gets flagged for a person to correct; a null barcode
        loses the whole line. Return null for a barcode only when the cell is genuinely illegible.

        Ignore invoice number and invoice date completely. They are always typed into empty boxes,
        and the response schema intentionally has no field capable of carrying either value.
        """;

    private static string BuildBimboPrimaryBody(byte[] png) => BuildSpecialBody(
        png,
        """
        You are the primary reader of a scanned Bimbo Bakeries delivery ticket. Copy what is
        physically printed. Never use an OCR transcription and never invent or repair a digit.

        Return every product row in page order. One row prints:
          UPC | ITEM No | DESCRIPTION | QTY | SUGG RETAIL | RETAIL AMOUNT | PRICE | AMOUNT

        barcode is exactly the ten UPC digits Bimbo prints, without its hyphen. Do not add a leading
        zero, compute a check digit, or pad it to twelve digits. itemNumber is the six printed digits.
        units is QTY and is already individual sellable units; there are no cases. price is the
        second-to-last figure and has four decimal places. amount is the rightmost wholesale AMOUNT,
        not the earlier retail amount. Set isCredit true only for a row under **RETURNS**. Return
        magnitudes without minus signs; isCredit owns the sign.

        Read deliveryUnits/deliveryAmount from DELIVERY TOTAL and returnUnits/returnAmount from
        RETURNS TOTAL when those rows exist. Read ticketUnits and ticketTotal from TICKET TOTALS,
        and cashDue from CASH DUE. ticketTotal is the last money figure on TICKET TOTALS.

        Cross-check the page before answering: inspect each row twice, verify QTY x PRICE against the
        rightmost AMOUNT, sum each band against its own total, then compare the bands with TICKET
        TOTALS and CASH DUE. A failed check means look at the printed characters again; it never means
        change a printed value to make the arithmetic work. If a row field is still unclear, return
        null for that field so the operator can read it.

        Ignore the printed invoice number and date completely. They are always typed by the operator
        into empty boxes and there is intentionally no response field capable of carrying either one.
        """,
        WriteBimboPrimarySchema);

    private static string BuildBimboBarcodeBody(
        byte[] png, IReadOnlyList<BimboBarcodeTarget> targets)
    {
        string locators = JsonSerializer.Serialize(targets.Select(t => new
        {
            t.RowId,
            t.Position,
            t.ItemNumber,
            t.Description,
            t.Units,
            t.Amount,
        }));

        return BuildSpecialBody(
            png,
            """
            Independently re-read only the UPC column on this Bimbo Bakeries ticket. Bimbo prints a
            ten-digit body with a hyphen after the fifth digit. Return exactly those ten printed
            digits without the hyphen. Do not add a leading zero, compute a check digit, pad to twelve
            digits, or infer a UPC from the item number or description. If any digit is unclear,
            return null for that barcode.

            The rows below are locators, not answers. They deliberately contain no UPC from the first
            reading. Find each row on the picture using its position and the other printed fields,
            then return the same rowId with your independent UPC reading. Return every rowId exactly
            once. Ignore the invoice number and date.

            Row locators:
            """ + locators,
            WriteBimboBarcodeSchema);
    }

    private static string BuildBimboBarcodeCropBody(IReadOnlyList<BimboUpcCrop> crops)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteString("text",
                "Read each isolated Bimbo UPC cell independently, digit by digit. Each image contains "
                + "only the printed UPC cell for the rowId immediately before it. Return exactly the "
                + "ten printed digits without the hyphen. Do not add a leading zero, compute a check "
                + "digit, or guess an unclear digit; return null for an unclear barcode. Return every "
                + "rowId exactly once. You are not being shown either earlier reading.");
            w.WriteEndObject();

            foreach (var crop in crops)
            {
                w.WriteStartObject();
                w.WriteString("text", $"rowId: {crop.RowId}");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteStartObject("inline_data");
                w.WriteString("mime_type", "image/png");
                w.WriteBase64String("data", crop.Png!);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteBimboBarcodeSchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildSpecialBody(
        byte[] png, string prompt, Action<Utf8JsonWriter> writeSchema)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("contents");
            w.WriteStartObject();
            w.WriteStartArray("parts");
            w.WriteStartObject();
            w.WriteStartObject("inline_data");
            w.WriteString("mime_type", "image/png");
            w.WriteBase64String("data", png);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("text", prompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            writeSchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteDrPepperPrimarySchema(Utf8JsonWriter w)
    {
        w.WriteStartObject("responseSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");

        w.WriteStartObject("rows");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (string name in new[]
                 {
                     "rowId", "page", "section", "rowOrder", "occurrence", "itemNumber",
                     "description", "barcode", "wholesale", "cases", "units", "net", "tax",
                     "amount",
                 })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        WriteField(w, "pageUpsideDown", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "rowId", "page", "section", "rowOrder", "occurrence", "itemNumber",
                     "description", "barcode", "wholesale", "cases", "units", "net", "tax",
                     "amount", "isCredit", "pageUpsideDown",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        foreach (string name in new[]
                 {
                     "rowId", "page", "section", "rowOrder", "description", "wholesale",
                     "cases", "net", "tax", "isCredit",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("packageSubtotals");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        WriteField(w, "groupId", "string");
        w.WriteStartObject("rowIds");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteEndObject();
        foreach (string name in new[] { "cases", "units", "amount" })
            WriteField(w, name, "string");
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("groupId");
        w.WriteStringValue("rowIds");
        w.WriteStringValue("cases");
        w.WriteStringValue("units");
        w.WriteStringValue("amount");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        foreach (string name in new[]
                 {
                     "totalSaleCases", "totalSaleUnits", "totalSaleAmount", "salesCases",
                     "salesUnits", "salesAmount", "creditCases", "creditUnits", "creditAmount",
                     "amountDue",
                 })
            WriteField(w, name, "string");
        w.WriteEndObject();
        w.WriteStartArray("required");
        foreach (string name in new[]
                 {
                     "rows", "packageSubtotals", "totalSaleCases", "totalSaleUnits",
                     "totalSaleAmount", "salesCases", "salesUnits", "salesAmount",
                     "creditCases", "creditUnits", "creditAmount", "amountDue",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBimboPrimarySchema(Utf8JsonWriter w)
    {
        w.WriteStartObject("responseSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        w.WriteStartObject("rows");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (string name in new[] { "itemNumber", "description", "barcode", "units", "price", "amount" })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[] { "itemNumber", "description", "barcode", "units", "price", "amount", "isCredit" })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("itemNumber");
        w.WriteStringValue("isCredit");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        foreach (string name in new[]
                 {
                     "deliveryUnits", "deliveryAmount", "returnUnits", "returnAmount",
                     "ticketUnits", "ticketTotal", "cashDue",
                 })
            WriteField(w, name, "string");
        WriteField(w, "pageUpsideDown", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("rows");
        w.WriteStringValue("ticketUnits");
        w.WriteStringValue("ticketTotal");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBimboBarcodeSchema(Utf8JsonWriter w)
    {
        w.WriteStartObject("responseSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        w.WriteStartObject("rows");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        WriteField(w, "rowId", "string");
        WriteField(w, "barcode", "string");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        w.WriteStringValue("rowId");
        w.WriteStringValue("barcode");
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("rowId");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("rows");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>
    /// The reply's shape. Vendor-aware for exactly one reason, and it is not efficiency.
    ///
    /// **Bimbo must not be offered a `qty` field.** Red Bull and Dr Pepper print a case count beside
    /// a unit count and need both. Bimbo has neither cases nor case prices — its one count column is
    /// headed **QTY** and holds individual units, which is `units` here. Offer a model a field
    /// spelled `qty` and a column headed QTY and it will put one in the other, whatever the prompt
    /// says; `units` then comes back null, every row arrives with no count, and the operator is asked
    /// about all of them. Measured on ticket 84294890013049: eleven rows, eleven questions, each one
    /// already carrying the description and the amount the model had read perfectly well.
    ///
    /// One name for the count is the fix. The prompt naming the field is not, on its own — this is
    /// the schema contradicting the prompt, and the schema is what the reply is validated against.
    /// </summary>
    private static void WriteSchema(Utf8JsonWriter w, VisionVendor vendor)
    {
        bool cases = vendor is not VisionVendor.Bimbo;

        w.WriteStartObject("responseSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");

        w.WriteStartObject("rows");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        WriteField(w, "itemNumber", "string");
        WriteField(w, "description", "string");
        WriteField(w, "barcode", "string");
        if (cases) WriteField(w, "qty", "string");
        WriteField(w, "units", "string");
        WriteField(w, "price", "string");
        WriteField(w, "amount", "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in
                 cases
                     ? ["itemNumber", "description", "barcode", "qty", "units", "price", "amount",
                        "isCredit"]
                     : new[] { "itemNumber", "description", "barcode", "units", "price", "amount",
                               "isCredit" })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("itemNumber");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        WriteField(w, "printedTotal", "string");
        WriteField(w, "printedUnits", "string");
        WriteField(w, "pageUpsideDown", "boolean");

        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("rows");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter w, string name, string type)
    {
        w.WriteStartObject(name);
        w.WriteString("type", type);
        w.WriteBoolean("nullable", true);
        w.WriteEndObject();
    }

    /// <summary>
    /// What the model is asked to do. Two things matter more than the column list.
    ///
    /// It must copy digits rather than reconstruct them: these tickets are scanned at 200-300 dpi and
    /// a model that "corrects" $54.70 to the $64.70 it expects from the rows around it defeats the
    /// entire point of having a second reader. And it must be willing to return nothing for a figure,
    /// because a blank sends the row to a person and a guess sends it to the register.
    /// </summary>
    private static string PromptFor(VisionVendor vendor)
    {
        const string Common = """
            You are reading a scanned delivery invoice. Report only what is physically printed on
            this page.

            Rules, in order of importance:
            1. Never guess. If a figure is smudged, cut off, obscured by pen marks, or you are not
               certain of every character, leave that field out. A missing figure is checked by a
               person; a wrong one is not caught by anything.
            2. Do not correct, infer, or reconcile. Do not work a figure out from the other figures
               on the row, and do not adjust a number because it looks inconsistent with the rest of
               the page. Copy the characters as printed even when they look wrong.
            3. Report money exactly as printed, digits and decimal point only, with no currency
               symbol, no thousands separator and no sign - for example 1234.50.
            4. Return one entry per product row, in the order they appear down the page. Do not
               include subtotal, total, tax, deposit or summary lines as rows.
            5. Set isCredit true for a returned line - printed in brackets, or under a CREDITS
               heading - and report its figures without the brackets. Set it false for a normal
               delivery row. This is the only thing that says which way the goods went, so do not
               leave it out.

            Which way up the page is. Some of these sheets go through the scanner rotated 180
            degrees, so the printing runs bottom-to-top and every character is upside down. You can
            still read them; say so by setting pageUpsideDown true. Set it false for a page the
            right way up. This does not change anything you report - read the page as it is meant to
            be read either way, and report the figures normally.
            """;

        string columns = vendor switch
        {
            VisionVendor.RedBull => """

                THIS IS A RED BULL DISTRIBUTION COMPANY DELIVERY TICKET.

                Layout. Each product takes two printed lines - the priced row, and its barcode on
                the line below, indented under the description with the bars off to the right:

                  ID        QTY  UNITS  DESCRIPTION        PRICE   DEP    DISC   SUGAR    TOTAL
                  RB1718     1     24   RED BULL 8.4OZ LS  $40.75  $0.00  $0.00  $0.00   $40.75
                                          611269991000                                 ||||||||

                Columns, left to right:
                  itemNumber  - ID: the code in the first column, RB and 4 to 6 digits
                  qty         - QTY: the number of CASES
                  units       - UNITS: individual cans or bottles. This is NOT the QTY column.
                  description - the product name
                  price       - PRICE: the cost of ONE CASE, not one can
                  (DEP, DISC and SUGAR follow, always 0.00 on these tickets - do not report them)
                  amount      - TOTAL: the last money column on the row
                  barcode     - the 12 digits on the line below, printed above the bars

                The money is printed with dollar signs. Report it without them.

                How a DELIVERY row adds up: QTY x PRICE = TOTAL, less DEP/DISC/SUGAR where those
                are not zero. So 2 x $35.11 = $70.22. If your reading does not add up, look at the
                row again - but if it still does not, report what is printed.
                Never adjust a figure to make the row work.

                Returns. A returns page or section prints the same columns with UNITS and every
                money figure in brackets. Set isCredit true on those rows and report the figures
                without the brackets:

                  RB247584  0.08   (2)   PINK 12OZ LS       $2.22  $0.00  $0.00  $0.00  ($4.45)
                                          611269003123                                 ||||||||

                A returns row is priced per UNIT rather than per case, so PRICE is a single can's
                price and QTY is a fraction of a case. THE DELIVERY ARITHMETIC ABOVE DOES NOT APPLY
                TO A RETURNS ROW - 0.08 x $2.22 is nothing like $4.45, and that is correct. Do not
                "fix" a returns row to make QTY x PRICE reach TOTAL. Copy what is printed.

                Foot of the page: printedTotal is the INVOICE or ROA figure, and printedUnits is
                Units Delivered. Ignore DISCOUNT, DEPOSIT, TAX and TOTAL DUE.

                The column headings on these scans are usually unreadable - PRICE comes out as
                PflICE and TOTAL as IDEAL. Go by position, not by the heading text.
                """,
            VisionVendor.DrPepper => """

                THIS IS A DR PEPPER / KEURIG DR PEPPER INVOICE.

                Layout. Each product takes two printed lines - the product line carrying the name
                and then the code, and the money row beneath it:

                  DESCRIPTION          UPC/SKU
                  WHSLE  CASES UNITS   NET     TAX     AMOUNT
                  20z24 DrPep          078000082401/10001195
                   56.00    4    96    32.50   0.00    130.00

                The name comes FIRST on the product line and the code after it. The code is one
                token: 12 digits, a slash, then the vendor's own item number.

                Columns:
                  description - the product name, at the START of the product line
                  barcode     - the 12 digits BEFORE the slash
                  itemNumber  - the digits AFTER the slash
                  qty         - CASES
                  units       - UNITS: individual bottles or cans. This is NOT the CASES column.
                  price       - NET: the cost of ONE CASE
                  amount      - AMOUNT: the last money column
                  (WHSLE is the first money column and TAX the second to last - do not report them)

                How a DELIVERY row adds up: CASES x NET = AMOUNT. So 4 x $32.50 = $130.00. If your
                reading does not add up, look at the row again - but if it still does not, report
                what is printed.
                Never adjust a figure to make the row work.

                Groups. Rows are grouped by package type, and each group is closed by its own
                subtotal - "PEG 2 24 40.00", meaning 2 cases, 24 units, $40.00. The invoice ends
                with TOT SALE (or a SALES SUMMARY block) and AMOUNT DUE. Do NOT report any of those
                as product rows.

                Pallets and shells. Below AMOUNT DUE the ticket lists the crates and pallets that
                went back on the truck - "PALLET PLASTIC 37X37 75001490 1", "SHELL PET 20Z
                75000014 5". They have a name, a number and a count but no price and no amount.
                They are not products and must NOT be reported as rows.

                Returns. A CREDITS banner starts a section with a SHORTER COLUMN SET - the WHSLE
                column is gone - and the money in brackets. Set isCredit true on those rows and
                report the figures without the brackets:

                  CREDITS
                  DESCRIPTION          UPC/SKU
                  CASES UNITS   NET     TAX     AMOUNT
                  12z12p Zsgr DP       078000035261/10136728
                      0     1    6.37   0.00    (6.37)

                Count the columns on a credit row before reading it. There are five money and count
                columns, not six, so the first figure is CASES and not WHSLE - reading it as a sales
                row shifts every figure one place left. AND THE DELIVERY ARITHMETIC ABOVE DOES NOT
                APPLY: a credit's CASES is routinely 0 while its AMOUNT is not, as here. Do not
                "fix" a credit row to make CASES x NET reach AMOUNT. Copy what is printed.

                Pen marks. These invoices come back marked up by hand. A pen stroke is not a digit
                and not a decimal point, and a stroke through a figure is not a minus sign. Read the
                printed figure underneath it, or leave the field out.

                Foot of the page: printedTotal is AMOUNT DUE, printedUnits is the TOT SALE unit
                count.
                """,
            VisionVendor.Bimbo => """

                THIS IS A BIMBO BAKERIES DELIVERY TICKET - a narrow till-roll receipt headed
                "BIMBO BAKERIES USA, INC. - CASH STOPS".

                Layout. One printed line per product, under a "**DELIVERIES**" banner whose letters
                are spaced far apart:

                              ITEM                               SUGG  RETAIL    WHOLESALE
                      UPC     No      DESCRIPTION           QTY RETAIL AMOUNT  PRICE   AMOUNT
                  74323-09183 008788  BIM PANQUE NUE FS FC    4   3.19  12.76 3.0100    12.04

                Columns, left to right:
                  barcode     - UPC: the ten digits in the first column, with a hyphen after five
                  itemNumber  - ITEM No: Bimbo's own six-digit product number, the second column
                  description - the product name
                  units       - the QTY column. Note the field is called "units" and the column is
                                headed QTY: they are the same number. There is no separate case
                                count on this vendor and no field to put one in.
                  price       - PRICE: the second-to-last column, printed to FOUR decimal places
                  amount      - the LAST column on the row, the one under WHOLESALE

                THERE ARE TWO COLUMNS HEADED "AMOUNT" and you want the right-hand one. The heading
                spans two printed lines and reads, across the row:

                  ... QTY | SUGG RETAIL | RETAIL AMOUNT | WHOLESALE PRICE | WHOLESALE AMOUNT

                So there are FOUR money figures after QTY. Counting them left to right: the first
                (SUGG RETAIL) and the second (RETAIL AMOUNT) are reported nowhere, the third is
                `price` and the fourth and last is `amount`. On the example row above that is
                price 3.0100 and amount 12.04 - not 3.19 and not 12.76, which are the retail pair
                and belong to no field here.

                The tell, if you are unsure which pair you are looking at: `price` is the only figure
                on the row printed to four decimal places, and `amount` is the one immediately to its
                right at the very end of the line.

                There are no cases on this vendor and no case prices. QTY is already the number of
                individual packets the store sells, and PRICE is already the cost of one of them.
                "SS2P" in a description means a single serve two-pack, which is ONE thing the store
                sells and not two, so it changes nothing about QTY. Report QTY exactly as printed.

                PRICE carries four decimal places and the last two are not padding - 3.0100, 1.9400,
                2.3900. Report all four digits after the point.

                How a row adds up: QTY x PRICE = AMOUNT. So 4 x 3.0100 = 12.04. If your reading does
                not add up, look at the row again - but if it still does not, report what is
                printed. Never adjust a figure to make the row work.

                Returns. Lower down, a "**RETURNS**" banner starts a second block with the same
                columns and its own header, and every figure on those rows carries a MINUS SIGN:

                  74323-09145 008783  BIM CONCHAS SS2P FC    -2   1.99  -3.98 1.9400    -3.88

                Set isCredit true on those rows and report their figures without the minus signs.
                The row arithmetic above still holds on a returns row - 2 x 1.9400 = 3.88 - so check
                it the same way you would a delivery.

                Foot of the page: printedUnits is the count on the "TICKET TOTALS" row and
                printedTotal is the LAST money figure on that same row, which is the figure repeated
                below as "CASH DUE". The ticket also prints a "DELIVERY TOTAL" row and a "RETURNS
                TOTAL" row; those are each one band's own total, they are not the ticket's, and none
                of these lines is a product row.
                """,
            _ => "",
        };

        // No separator here on purpose. Both pieces below open with their own blank line, the way the
        // column blocks always have, so every line ending in the finished prompt comes from the
        // source file and they are all the same. A "\n\n" joined onto CRLF literals would leave one
        // odd seam in the middle of a prompt that was tuned character by character against the
        // store's own scans.
        return Common + BarcodeRules(vendor) + columns;
    }

    /// <summary>
    /// What the model is told about the one field that identifies the product.
    ///
    /// Per vendor rather than shared, because the vendors do not print the same thing. Red Bull and
    /// Dr Pepper print a whole UPC-A whose last digit is arithmetic on the other eleven, so the code
    /// can be checked before it is reported and a code that will not check out can be dropped.
    ///
    /// **Bimbo prints neither the leading zero nor the check digit** — `74323-09183` for the UPC-A
    /// `074323091830` — so there is no arithmetic to do and nothing to check. Handing it the UPC-A
    /// instructions would be worse than saying nothing at all: told to count to twelve and make the
    /// digits add up, a model pads a ten-digit body into a plausible twelve-digit code, and on this
    /// vendor nothing downstream can catch that. The money reconciles either way and SSCS opens a new
    /// item under an id belonging to nothing.
    /// </summary>
    private static string BarcodeRules(VisionVendor vendor) => vendor switch
    {
        VisionVendor.Bimbo => """


            The barcode, which on this vendor cannot check itself.

            The UPC column prints TEN digits with a hyphen after the fifth: 74323-09183. That is not
            a whole barcode - this vendor leaves off both the leading zero and the final check digit
            - so there is no sum you can do on it and nothing about it that tells you whether you
            read it correctly.

            Report exactly the ten printed digits, without the hyphen: 7432309183.

              a. Count them. There must be exactly ten. Nine or eleven means you have dropped or
                 doubled one - look at the printed digits again and count them one at a time.
              b. Do NOT add a leading zero, do NOT work out a check digit, and do NOT pad the code
                 to twelve digits. Ten digits, exactly as they are printed.
              c. If any digit is unclear, leave the barcode field empty.

            An empty barcode is fine - a person reads it off the paper in a couple of seconds, and
            the item number in the next column identifies the product on its own.

            What you must NOT do is tidy a code up into something that looks more like a barcode.
            Nothing on this ticket can catch a wrong one: the money adds up the same either way, and
            the delivery is quietly booked onto a product that was never delivered. So report the
            digits you can actually see, or report nothing.
            """,
        _ => """


            The barcode, which is the one field that can check itself.

            It is a UPC-A: exactly 12 digits, and the last one is arithmetic on the other eleven.
            Add the digits in positions 1, 3, 5, 7, 9 and 11, multiply that sum by 3, then add the
            digits in positions 2, 4, 6, 8 and 10. The check digit is whatever number takes that
            grand total up to a multiple of 10.

            So, for every barcode, before you report it:

              a. Count the digits. There must be exactly 12. Eleven means you have dropped one -
                 almost always a zero out of a run of zeros, so 078000082166 comes back as
                 07800082166. Look at the printed digits again and count them one at a time.
              b. Work out the check digit and compare it with the last digit printed. If they
                 match, report the code.
              c. If they do not match, you have misread a digit somewhere. Look at the code again,
                 character by character.
              d. If it still does not come out, leave the barcode field empty.

            An empty barcode is fine - a person reads it off the paper in a couple of seconds.

            What you must NOT do is change a digit so that the sum works. A code you have adjusted
            into adding up is far worse than no code at all: it looks correct to everything
            downstream and quietly books the delivery onto a product that was never delivered.
            Nothing else on the invoice can catch that - the money adds up the same either way. So
            report the digits you can actually see, or report nothing.
            """,
    };

    // ───────────────────────────────────────────────────────────────────────── the reply

    /// <summary>
    /// Pulls the reading out of Gemini's envelope. Anything unexpected anywhere gives null, which
    /// the caller treats as a page that could not be read.
    /// </summary>
    private static DrPepperVisionReading? ParseDrPepperReply(Stream stream)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        var read = new List<DrPepperVisionRow>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            index++;
            string page = Text(row, "page") ?? "1";
            string section = (Text(row, "section") ?? "SALES").Trim().ToUpperInvariant();
            string order = Text(row, "rowOrder") ?? index.ToString(CultureInfo.InvariantCulture);
            string occurrence = Text(row, "occurrence") ?? "1";
            string modelId = Text(row, "rowId") ?? $"row-{index}";
            string baseId = $"p{page}-{section.ToLowerInvariant()}-r{order}-o{occurrence}";
            string localId = baseId;
            for (int suffix = 2; !localIds.Add(localId); suffix++) localId = $"{baseId}-{suffix}";
            ids.TryAdd(modelId, localId);

            var found = new DrPepperVisionRow
            {
                RowId = localId,
                Page = page,
                Section = section,
                RowOrder = order,
                Occurrence = occurrence,
                ItemNumber = Text(row, "itemNumber") ?? "",
                Description = Text(row, "description") ?? "",
                Barcode = Text(row, "barcode"),
                Wholesale = Text(row, "wholesale"),
                Cases = Text(row, "cases"),
                Units = Text(row, "units"),
                Net = Text(row, "net"),
                Tax = Text(row, "tax"),
                Amount = Text(row, "amount"),
                // The sign of the line. Where the model left the flag off, the section it read off
                // the printed banner says the same thing — that is still its own reading of the
                // page, not a guess of ours, and it beats refusing the invoice over a missing
                // boolean.
                IsCredit = NullableFlag(row, "isCredit") ?? section == "CREDITS",
                PageUpsideDown = Flag(row, "pageUpsideDown"),
            };
            if (found.ItemNumber.Length == 0 && found.Description.Length == 0
                && found.Barcode is null && found.Units is null && found.Amount is null)
                continue;
            read.Add(found);
        }
        // What "incomplete" is allowed to mean.
        //
        // This gate decides whether a reply is usable at all, and it used to demand a filled CASES,
        // NET, TAX and WHSLE on every row plus seven top-level figures — so one blank cell anywhere
        // threw the whole invoice away, retried, threw it away again and asked for a rescan. On
        // 2026-08-13 it did exactly that to a flawless reading of a 49-row ticket: 50 rows, 13
        // package subtotals, every figure present and correct, refused because `totalSale*` came
        // back empty. That invoice carries returns, so it prints a SALES SUMMARY block **instead
        // of** a TOT SALE line — the figure is not on the paper, and the model was right to leave
        // it null.
        //
        // So the bar is now what a file actually needs: at least one row worth posting, and the
        // invoice's own AMOUNT DUE. AMOUNT DUE is what makes a half-read page visible — the lines
        // will not come to it and the converted file says so. Everything else is a note, and a
        // column that never reaches the XML at all is not grounds to refuse a delivery.
        if (read.Count == 0 || string.IsNullOrWhiteSpace(Text(root, "amountDue"))) return null;

        var subtotals = new List<DrPepperVisionPackageSubtotal>();
        if (root.TryGetProperty("packageSubtotals", out var groups)
            && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var rowIds = new List<string>();
                var groupIds = new HashSet<string>(StringComparer.Ordinal);
                if (group.TryGetProperty("rowIds", out var groupRows)
                    && groupRows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idElement in groupRows.EnumerateArray())
                    {
                        string? modelId = idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() : null;
                        if (modelId is null || !ids.TryGetValue(modelId, out string? localId)
                            || !groupIds.Add(localId)) return null;
                        rowIds.Add(localId);
                    }
                }
                if (rowIds.Count == 0
                    || string.IsNullOrWhiteSpace(Text(group, "cases"))
                    || string.IsNullOrWhiteSpace(Text(group, "units"))
                    || string.IsNullOrWhiteSpace(Text(group, "amount"))) return null;
                subtotals.Add(new DrPepperVisionPackageSubtotal
                {
                    GroupId = Text(group, "groupId") ?? $"group-{subtotals.Count + 1}",
                    RowIds = rowIds,
                    Cases = Text(group, "cases"),
                    Units = Text(group, "units"),
                    Amount = Text(group, "amount"),
                });
            }
        }

        var summary = new DrPepperVisionSummary
        {
            SalesCases = Text(root, "salesCases"),
            SalesUnits = Text(root, "salesUnits"),
            SalesAmount = Text(root, "salesAmount"),
            CreditCases = Text(root, "creditCases"),
            CreditUnits = Text(root, "creditUnits"),
            CreditAmount = Text(root, "creditAmount"),
        };
        return new DrPepperVisionReading
        {
            Status = DrPepperVisionStatus.Success,
            Rows = read,
            PackageSubtotals = subtotals,
            TotalSaleCases = Text(root, "totalSaleCases"),
            TotalSaleUnits = Text(root, "totalSaleUnits"),
            TotalSaleAmount = Text(root, "totalSaleAmount"),
            Summary = summary,
            AmountDue = Text(root, "amountDue"),
        };
    }

    private static BimboVisionReading? ParseBimboReply(Stream stream)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        bool upsideDown = Flag(root, "pageUpsideDown");
        var read = new List<BimboVisionRow>();
        foreach (var row in rows.EnumerateArray())
        {
            string item = Text(row, "itemNumber") ?? "";
            string description = Text(row, "description") ?? "";
            string? barcode = Text(row, "barcode");
            string? units = Text(row, "units");
            string? price = Text(row, "price");
            string? amount = Text(row, "amount");
            bool? credit = NullableFlag(row, "isCredit");

            if (item.Length == 0 && description.Length == 0 && barcode is null
                && units is null && price is null && amount is null)
                continue;

            read.Add(new BimboVisionRow
            {
                ItemNumber = item,
                Description = description,
                Barcode = barcode,
                Units = units,
                Price = price,
                Amount = amount,
                IsCredit = credit,
                PageUpsideDown = upsideDown,
            });
        }

        if (read.Count == 0) return null;
        int? ticketUnitNumber = Int(Text(root, "ticketUnits"));
        decimal? ticketMoney = Money(Text(root, "ticketTotal"));
        if (ticketUnitNumber is int printedUnits && read.All(r => Int(r.Units) is not null))
        {
            int rowUnits = read.Sum(r => (r.IsCredit == true ? -1 : 1) * Int(r.Units)!.Value);
            if (rowUnits != printedUnits) return null;
        }
        if (ticketMoney is decimal printedTotal && read.All(r => Money(r.Amount) is not null))
        {
            decimal rowTotal = read.Sum(r => (r.IsCredit == true ? -1m : 1m) * Money(r.Amount)!.Value);
            if (Math.Abs(rowTotal - printedTotal) > 0.01m * Math.Max(1, read.Count)) return null;
        }

        string? deliveryUnits = Text(root, "deliveryUnits");
        string? deliveryAmount = Text(root, "deliveryAmount");
        string? returnUnits = Text(root, "returnUnits");
        string? returnAmount = Text(root, "returnAmount");
        return new BimboVisionReading
        {
            Status = BimboVisionStatus.Success,
            Rows = read,
            Deliveries = deliveryUnits is null && deliveryAmount is null
                ? null : new BimboVisionBandTotal(deliveryUnits, deliveryAmount),
            Returns = returnUnits is null && returnAmount is null
                ? null : new BimboVisionBandTotal(returnUnits, returnAmount),
            TicketUnits = Text(root, "ticketUnits"),
            TicketTotal = Text(root, "ticketTotal"),
            CashDue = Text(root, "cashDue"),
        };
    }

    private static BimboBarcodeVerification? ParseBimboBarcodeReply(
        Stream stream, IReadOnlySet<string> expectedIds)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        if (!payload.RootElement.TryGetProperty("rows", out var rows)
            || rows.ValueKind != JsonValueKind.Array) return null;

        var read = new List<BimboBarcodeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            string id = Text(row, "rowId") ?? "";
            if (!expectedIds.Contains(id) || !seen.Add(id)) return null;
            read.Add(new BimboBarcodeRow(id, Text(row, "barcode")));
        }
        if (seen.Count != expectedIds.Count) return null;
        return new BimboBarcodeVerification
        {
            Status = BimboVisionStatus.Success,
            Rows = read,
        };
    }

    /// <summary>
    /// The JSON the model actually wrote, or null if it did not write usable JSON.
    ///
    /// A reply that ran out of room comes back as a 200 with `finishReason` MAX_TOKENS and either a
    /// truncated string or no `parts` at all — so this reads defensively rather than indexing
    /// straight into `content`, and reports *why* through <paramref name="cutShort"/> instead of
    /// letting a truncation look the same as a model that answered badly. They have different
    /// fixes, and for most of 2026-08-13 the first was wearing the second's clothes.
    /// </summary>
    private static string? ReplyPayload(Stream stream, out bool cutShort)
    {
        cutShort = false;
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0) return null;

        var candidate = candidates[0];
        cutShort = candidate.TryGetProperty("finishReason", out var reason)
                   && reason.ValueKind == JsonValueKind.String
                   && reason.GetString() is "MAX_TOKENS" or "LENGTH";

        if (!candidate.TryGetProperty("content", out var content)
            || !content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array) return null;

        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        return text.Length == 0 ? null : text.ToString();
    }

    private static string? ReplyPayload(Stream stream) => ReplyPayload(stream, out _);

    /// <summary>True when Google says it stopped because it ran out of output room.</summary>
    private static bool RanOutOfRoom(Stream stream)
    {
        try
        {
            ReplyPayload(stream, out bool cutShort);
            return cutShort;
        }
        catch
        {
            return false;
        }
    }

    private static VisionReading? ParseReply(Stream stream)
    {
        // Through the same defensive reader as every other vendor: a candidate that ran out of room
        // has no `parts` to index into, and reaching for one threw where it should have returned
        // "nothing usable came back".
        string? json = ReplyPayload(stream);
        if (json is null) return null;

        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        var read = new List<VisionRow>();
        foreach (var row in rows.EnumerateArray())
        {
            string item = Text(row, "itemNumber") ?? "";
            string? barcode = Digits(Text(row, "barcode"));
            int? units = Int(Text(row, "units"));
            decimal? qty = Money(Text(row, "qty"));
            decimal? price = Money(Text(row, "price"));
            decimal? amount = Money(Text(row, "amount"));

            // A row with no item number and no figures says nothing at all.
            if (item.Length == 0 && barcode is null && units is null && price is null && amount is null)
                continue;

            read.Add(new VisionRow
            {
                ItemNumber = item,
                Description = Text(row, "description") ?? "",
                Barcode = barcode,
                Units = units,
                Qty = qty,
                Price = price,
                Amount = amount,
                IsCredit = Flag(row, "isCredit"),
            });
        }

        bool upsideDown = Flag(root, "pageUpsideDown");
        return new VisionReading
        {
            Rows = [.. read.Select(r => r with { PageUpsideDown = upsideDown })],
            PrintedTotal = Money(Text(root, "printedTotal")),
            PrintedUnits = Int(Text(root, "printedUnits")),
        };
    }

    /// <summary>
    /// A yes/no the model set. False whenever it is missing or malformed, which is the safe way
    /// round: a delivery booked as a return would subtract stock that actually arrived, and the
    /// invoice total is what catches a row facing the wrong way.
    /// </summary>
    private static bool Flag(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True
            || (v.ValueKind == JsonValueKind.String
                && bool.TryParse(v.GetString(), out bool parsed) && parsed));

    private static bool? NullableFlag(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool parsed)
            ? parsed
            : null;
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// A money figure, or null for anything that is not plainly one.
    ///
    /// Deliberately narrow. The model is asked for bare digits, so a reply carrying anything else has
    /// not done what it was told, and the safe reading of a field nobody understands is no reading.
    /// </summary>
    private static decimal? Money(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = text.Trim().Trim('(', ')', '$', '-', '+', ' ');
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? Math.Abs(value)
            : null;
    }

    private static int? Int(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string cleaned = text.Trim().Trim('(', ')', ' ');
        return int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Abs(value)
            : null;
    }

    /// <summary>The digits of a barcode, or null where the reply holds anything else.</summary>
    private static string? Digits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string digits = new(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 8 and <= 14 ? digits : null;
    }
}
