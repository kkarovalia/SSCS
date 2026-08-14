using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdiToSscs.Core;

/// <summary>
/// Reads an invoice off the picture using Google's Gemini API.
///
/// Confirmation reads use one request per page. Picture-primary Bimbo, Dr Pepper and Red Bull reads
/// send all pages of one ticket in a single request so one model answer owns the complete invoice.
///
/// The model is asked for what is **printed**, never for what follows from it. It is told in as many
/// words to leave a genuinely illegible figure out rather than invent it. Money comes back as text
/// for the same reason a cent is never held as a float.
///
/// The established confirmation path fails empty. Each picture-primary contract instead reports
/// unavailable separately from unreadable: unavailable can offer the operator-approved OCR fallback,
/// while a model that reached the page but could not produce a postable invoice requires a rescan.
/// </summary>
public sealed class GeminiVisionReader
    : IInvoiceVisionReader, IBimboVisionReader, IDrPepperVisionReader, IRedBullVisionReader,
      IBlueBellVisionReader, IChubiwinosVisionReader, IVisionProgressReporter
{
    /// <summary>
    /// What the operator is told is happening, in the words of the job rather than the protocol.
    /// </summary>
    private const string ReadingPhase = "Reading the invoice";

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
    private enum AttemptOutcome
    {
        Read, Incomplete,
        /// <summary>HTTP 429, and Google named a **per-day** quota. Gone until tomorrow.</summary>
        RateLimited,
        /// <summary>
        /// HTTP 429, and Google named a **per-minute** quota — or would not say which. A different
        /// fault from <see cref="RateLimited"/> wearing the same status code, and the opposite
        /// response is correct: this one clears in under a minute.
        ///
        /// The store's dashboard read 6 of 20 requests for the whole day while every invoice was
        /// being pushed off gemini-3.7-flash by 429s. Nothing was exhausted. The ladder was
        /// rate-limiting itself: a refused key and a throttled key both answer instantly, so five
        /// or six requests left in about a second, tripped the five-per-minute limit, and every
        /// rung after that got a 429 caused by the rung before it. Nine invoices in a row were read
        /// by a worse model than the store had available, which is where bad barcodes come from.
        /// </summary>
        TooFast,
        Overloaded, Refused, Network, Canceled,
    }

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
            Add(AttemptOutcome.RateLimited, "out of free allowance (HTTP 429 per-day)");
            Add(AttemptOutcome.TooFast, "sent too fast (HTTP 429 per-minute)");
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
        AttemptOutcome.TooFast => "that key has sent too many requests this minute",
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
    /// Reads the complete Bimbo ticket in one picture request. This deliberately uses its own
    /// prompt, schema and result type: changing it must not change one byte of the established Dr
    /// Pepper or Red Bull requests.
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

            string body = BuildBimboPrimaryBody(pages);
            var reading = ReadSpecialPage(
                body, tally, ParseBimboReply, out reachedApi, ReadingPhase);
            string note = tally.Describe(clock.ElapsedMilliseconds, reading?.Rows.Count ?? 0);
            if (reading is null)
                return reachedApi
                    ? BimboVisionReading.Unreadable(note)
                    : BimboVisionReading.Unavailable(note);

            return reading with { Status = BimboVisionStatus.Success, Note = note };
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
    /// Reads every page of one Blue Bell ticket in one picture request. The response has no
    /// invoice-number or date field, and no OCR or product catalogue contributes a row value.
    /// </summary>
    public BlueBellVisionReading ReadBlueBell(byte[] pdfBytes)
    {
        if (!IsConfigured) return BlueBellVisionReading.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return BlueBellVisionReading.Unreadable("The PDF had no page image to read.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return BlueBellVisionReading.Unreadable("The PDF had no page image to read.");
            tally.Pages = pages.Count;

            string body = BuildBlueBellPrimaryBody(pages);
            var reading = ReadSpecialPage(
                body, tally, ParseBlueBellReply, out reachedApi, ReadingPhase);
            string note = tally.Describe(clock.ElapsedMilliseconds, reading?.Rows.Count ?? 0);
            if (reading is null)
                return reachedApi
                    ? BlueBellVisionReading.Unreadable(note)
                    : BlueBellVisionReading.Unavailable(note);

            return reading with { Status = BlueBellVisionStatus.Success, Note = note };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? BlueBellVisionReading.Unreadable(note)
                : BlueBellVisionReading.Unavailable(note);
        }
    }

    /// <summary>
    /// Reads every page of one Chubiwinos invoice in one picture request. Its response deliberately
    /// has no invoice-number or date field, and neither OCR nor the legacy catalogue contributes a
    /// row value.
    /// </summary>
    public ChubiwinosVisionReading ReadChubiwinos(byte[] pdfBytes)
    {
        if (!IsConfigured)
            return ChubiwinosVisionReading.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return ChubiwinosVisionReading.Unreadable("The PDF had no page image to read.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return ChubiwinosVisionReading.Unreadable("The PDF had no page image to read.");
            tally.Pages = pages.Count;

            string body = BuildChubiwinosPrimaryBody(pages);
            var reading = ReadSpecialPage(
                body, tally, ParseChubiwinosReply, out reachedApi, ReadingPhase);
            string note = tally.Describe(clock.ElapsedMilliseconds, reading?.Rows.Count ?? 0);
            if (reading is null)
                return reachedApi
                    ? ChubiwinosVisionReading.Unreadable(note)
                    : ChubiwinosVisionReading.Unavailable(note);

            return reading with { Status = ChubiwinosVisionStatus.Success, Note = note };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? ChubiwinosVisionReading.Unreadable(note)
                : ChubiwinosVisionReading.Unavailable(note);
        }
    }

    /// <summary>
    /// Reads every page of one Red Bull ticket in one picture request. The response deliberately
    /// has no invoice-number or date field, and the legacy OCR/barcode readers are not consulted.
    /// </summary>
    public RedBullVisionReading ReadRedBull(byte[] pdfBytes)
    {
        if (!IsConfigured) return RedBullVisionReading.Unavailable("AI reading is not configured.");
        if (pdfBytes is null || pdfBytes.Length == 0)
            return RedBullVisionReading.Unreadable("The PDF had no page image to read.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tally = new Tally();
        bool reachedApi = false;
        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0)
                return RedBullVisionReading.Unreadable("The PDF had no page image to read.");
            tally.Pages = pages.Count;

            string body = BuildRedBullPrimaryBody(pages);
            var reading = ReadSpecialPage(
                body, tally, ParseRedBullReply, out reachedApi, ReadingPhase);
            string note = tally.Describe(clock.ElapsedMilliseconds, reading?.Rows.Count ?? 0);
            if (reading is null)
                return reachedApi
                    ? RedBullVisionReading.Unreadable(note)
                    : RedBullVisionReading.Unavailable(note);

            return reading with { Status = RedBullVisionStatus.Success, Note = note };
        }
        catch
        {
            string note = tally.Describe(clock.ElapsedMilliseconds, 0);
            return reachedApi
                ? RedBullVisionReading.Unreadable(note)
                : RedBullVisionReading.Unavailable(note);
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
                    out _, out AttemptOutcome outcome, out _);
                if (reading is not null)
                {
                    _at = index;          // start here next page rather than re-probing empty quotas
                    return reading;
                }
                anythingWorthWaitingFor |= outcome
                    is AttemptOutcome.RateLimited or AttemptOutcome.TooFast
                    or AttemptOutcome.Overloaded
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
            answeredAt, body, tally, parse, phase, Combinations + 1, Combinations + 1,
            out _, out _, out _);
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
    /// The same waits serve a **per-minute** 429, which is the other fault that clears on its own.
    /// **A per-day 429 is still never retried.** An exhausted allowance does not come back before
    /// tomorrow, and sleeping on one is exactly what used to leave an invoice at "Converting..."
    /// for minutes; <see cref="IsAPerMinuteLimit"/> is what keeps the two apart, and an unclassified
    /// 429 counts as the per-day one.
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
                out bool answered, out AttemptOutcome outcome, out TimeSpan? askedToWait);
            if (answered)
            {
                apiAnswered = true;
                if (answeredAt < 0) answeredAt = index;
            }
            if (reading is not null) return reading;

            // Two faults earn another go on this rung, and only while there are goes left: Google's
            // server being busy, and this key having sent too many requests this minute. Both clear
            // on their own. Anything else - the day's allowance gone, refused, unreachable, or
            // answering badly - is not fixed by waiting.
            bool worthWaitingFor = outcome is AttemptOutcome.Overloaded or AttemptOutcome.TooFast;
            if (!worthWaitingFor || go >= _busyWaits.Length) return null;

            // Google's own retryDelay when it gave one, since it knows when the window reopens.
            TimeSpan wait = askedToWait ?? _busyWaits[go];
            Report(phase, outcome == AttemptOutcome.TooFast
                ? "That key has sent too many requests this minute - not your daily allowance. "
                  + $"Trying the same model again in {wait.TotalSeconds:0} seconds."
                : "Google's AI is busy - their server, not your allowance. Trying the same "
                  + $"model again in {wait.TotalSeconds:0} seconds.");
            if (wait > TimeSpan.Zero) { Thread.Sleep(wait); tally.Waited += wait.TotalSeconds; }
        }
    }

    /// <summary>One attempt, tallied and announced. Never throws.</summary>
    private T? Attempt<T>(
        int index, string body, Tally tally, Func<Stream, T?> parse, string phase,
        int number, int of, out bool apiAnswered, out AttemptOutcome outcome,
        out TimeSpan? askedToWait) where T : class
    {
        var (key, model) = Pair(index);
        int keyNumber = index % _keys.Length + 1;

        Report(phase, $"try {number} of {of} sent (key {keyNumber}, {model}). No reply yet.");
        var reading = TryOnce(
            body, key, model, parse, out apiAnswered, out outcome, out string? failure,
            out askedToWait);
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
        out bool apiAnswered, out AttemptOutcome outcome, out string? failure,
        out TimeSpan? askedToWait) where T : class
    {
        apiAnswered = false;
        outcome = AttemptOutcome.Read;
        failure = null;
        askedToWait = null;
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
            if (status == 429)
            {
                // Ask Google *which* 429 this is before deciding it is hopeless. A per-day quota is
                // gone until tomorrow and waiting on it is the bug that used to leave an invoice at
                // "Converting..." for minutes; a per-minute quota clears on its own and dropping the
                // best model over one is how nine invoices in a row got read by a worse one.
                using var refusal = new MemoryStream();
                using (var stream = response.Content.ReadAsStream(CancellationToken.None))
                    stream.CopyTo(refusal);
                refusal.Position = 0;
                bool perMinute = IsAPerMinuteLimit(refusal, out askedToWait);
                outcome = perMinute ? AttemptOutcome.TooFast : AttemptOutcome.RateLimited;
                failure = perMinute ? "HTTP 429 (per-minute limit)" : "HTTP 429 (daily allowance)";
                return null;
            }

            outcome = status >= 500 ? AttemptOutcome.Overloaded : AttemptOutcome.Refused;
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

    private static string BuildBimboPrimaryBody(IReadOnlyList<byte[]> pages)
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
                w.WriteString("text", $"Ticket page {index + 1} of {pages.Count}:");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteStartObject("inline_data");
                w.WriteString("mime_type", "image/png");
                w.WriteBase64String("data", pages[index]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("text", BimboPrimaryPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteBimboPrimarySchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Bimbo's one and only request. The final checks deliberately tell the model where to look
    /// again while it still has the page; they never authorize changing a printed value to make the
    /// arithmetic work. Bimbo's ten-digit body rule is intentionally unlike Dr Pepper's UPC-A rule.
    /// </summary>
    private const string BimboPrimaryPrompt = """
        You are the only reader of this scanned Bimbo Bakeries delivery ticket. Nothing downstream
        will re-read a product row or ask a person about it, so your answer is what reaches the
        store's system. Read only the physical print visible in the supplied page pictures. Never
        use an OCR transcription, product catalogue, remembered product, manufacturer prefix,
        arithmetic inference, or a plausible repair. Inspect every printed character twice.

        Return every actual product exactly once and in physical ticket order across all pages.
        Exclude headings, DELIVERY TOTAL, RETURNS TOTAL, TICKET TOTALS, PRIOR BALANCE and CASH DUE.
        One product row prints:
          UPC | ITEM No | DESCRIPTION | QTY | SUGG RETAIL | RETAIL AMOUNT | PRICE | AMOUNT

        barcode is exactly the TEN digits Bimbo prints in the UPC column, without its hyphen. Count
        the ten printed digits one at a time. Do not add a leading zero, do not compute a check digit,
        and do not pad or expand the value to twelve digits. Nothing printed on this ticket can
        validate the UPC, so inspect the cell especially carefully and return the digits as read.
        Return null only when the printed cell is genuinely illegible; never repair or replace a
        digit to make the code look plausible.

        itemNumber is the six printed ITEM No digits. units is QTY and is already the count of
        individual sellable units; there are no cases. price is the second-to-last figure and has
        four decimal places. amount is the rightmost wholesale AMOUNT, not the earlier RETAIL
        AMOUNT. Never return SUGG RETAIL or RETAIL AMOUNT in either field. Set isCredit true only for
        a row under RETURNS. Return numeric magnitudes without minus signs; isCredit owns the sign.

        Read deliveryUnits and deliveryAmount from DELIVERY TOTAL, and returnUnits and returnAmount
        from RETURNS TOTAL, when those rows exist. Read ticketUnits and ticketTotal from TICKET
        TOTALS, and cashDue from CASH DUE. ticketTotal is the last money figure on TICKET TOTALS.
        CASH DUE is an account-balance figure, not the invoice total; it can include PRIOR BALANCE.

        Before you answer, work through every one of these checks and go back to the picture for any
        row or total that fails one:

        1. Count every UPC body. It must be exactly ten printed digits, with no leading zero or check
           digit added. Re-read any nine- or eleven-digit result character by character.
        2. On every delivery and return row, QTY x PRICE must equal the rightmost wholesale AMOUNT.
           Make sure PRICE is the four-decimal figure and AMOUNT is the final figure on the row.
        3. Add the QTY and wholesale AMOUNT values in DELIVERIES and compare them with DELIVERY TOTAL.
           Separately add RETURNS and compare them with RETURNS TOTAL.
        4. Subtract the return units and money from the delivery units and money, then compare those
           signed results with TICKET TOTALS. Copy CASH DUE independently. Never change ticketTotal
           or any product row to make CASH DUE match; they agree only when PRIOR BALANCE is zero.

        These checks tell you where to look again. They never tell you what to write. Never change,
        fill, reject or derive a value merely to make a check pass. If a non-barcode field is still
        uncertain after looking again, return null for it. For a barcode, return a legible value as
        read even when you remain unsure; return null only when the cell is genuinely illegible.

        Ignore the printed invoice number and date completely. They are always typed into empty
        boxes, and the response schema intentionally has no field capable of carrying either value.
        """;

    private static string BuildBlueBellPrimaryBody(IReadOnlyList<byte[]> pages)
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
                w.WriteString("text", $"Ticket page {index + 1} of {pages.Count}:");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteStartObject("inline_data");
                w.WriteString("mime_type", "image/png");
                w.WriteBase64String("data", pages[index]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("text", BlueBellPrimaryPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteBlueBellPrimarySchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Blue Bell's one and only read. Its printed UPC is a padded body rather than a barcode, and
    /// every handwriting instruction is explicit because these tickets come back marked in pen.
    /// </summary>
    private const string BlueBellPrimaryPrompt = """
        You are the only reader of this scanned Blue Bell delivery ticket. Nothing downstream will
        re-read a product row or ask a person about it, so your answer is what reaches the store's
        system. Read only the physical printed print visible in the supplied page pictures. Never
        use an OCR transcription, a product catalogue, a remembered product, a manufacturer prefix,
        arithmetic inference, or a plausible repair. Inspect every printed character twice.

        Ignore handwriting, pen and marker of every kind. A handwritten number, tick, circle,
        cross, signature, initials, arrow or underline is a person's note and is never a value on
        this invoice. A handwritten number can be the correct net total or quantity and is still
        not the answer. Where ink sits over a printed figure, read the physical print underneath.
        If that printed figure is genuinely unreadable, return null rather than the handwritten
        version. Never use handwriting to resolve a disagreement between two printed figures.

        Return every actual invoice product exactly once, in physical order across all pages. A
        PICK LIST page headed PROD DESCRIPTION SELLBOX QTY is a packing slip, not an invoice: return
        no rows or totals from it. Blue Bell's invoice columns are:

          PROD | UPC | EA/COST | RETAIL | BX CNT | QTY | PRICE | EXTENDED

        EA/COST and RETAIL are always blank. Do not return either field and never shift a neighbouring
        value into either column. productNumber is PROD. description is the printed description.
        upc is exactly the twelve characters physically printed in the UPC column, including its
        leading zero. These characters are not a UPC-A and carry no check digit. Do not compute or
        append a check digit, drop the leading zero, pad, expand, shorten, validate, repair or replace
        the value. Core alone restores the barcode later.

        quantity is the QTY cell exactly as printed, preserving its minus sign and a fraction such
        as -11/12. Do not turn the fraction into a decimal, carton count or unit count. packSize is
        the string "12" when the physically printed description begins DZ, otherwise "1". Read DZ
        from the picture; never use PROD, a catalogue or remembered packaging to choose it. price
        is PRICE and extended is EXTENDED. Return row money as printed numeric magnitudes and let
        isCredit own the booking direction. Set isCredit true for every row in a Returns block and
        false for every row in Sales. All Sales and Returns blocks in this PDF belong to one payable.

        For each Sales or Returns block, return one pageSummaries entry with its physical page and
        isCredit direction. Copy the TOTAL row's QTY as totalQuantity and money as totalAmount. Copy
        UNITS RETURNED as unitsReturned or BARS RETURNED as barsReturned only where that exact label
        and count are physically printed. Preserve every sign on these summary figures. Copy
        BALANCE DUE into balanceDue only when legible, but never let its large-font reading override
        the TOTAL row. A physical page can contain both Sales and Returns, so return two summaries
        with the same page when it does.

        Before you answer, work through every one of these checks and go back to the picture for any
        cell that fails one:

        1. UPC column: count exactly twelve physically printed characters including the leading
           zero. Do not calculate a check digit or change a character to make a barcode valid.
        2. Columns and pack: verify QTY, PRICE and EXTENDED came from their named columns, that blank
           EA/COST and RETAIL stole nothing, and that packSize follows the printed leading DZ only.
        3. Row figures: compare QTY x PRICE with EXTENDED, including a printed n/12 quantity on a DZ
           row. If they disagree, re-read those physical cells without deriving or changing one.
        4. Section checks: compare rows separately with that block's TOTAL QTY and money, and compare
           loose returns with the printed UNITS RETURNED or BARS RETURNED count.
        5. Source check: confirm every returned character is vendor print, never handwriting, a PICK
           LIST value, OCR text, remembered product data or a value inferred from arithmetic.

        These checks only tell you where to look again. They never tell you what to write and never
        authorize changing, filling, rejecting, deriving or repairing a printed value to make
        arithmetic work. Return null for a genuinely unreadable field.

        Ignore every printed invoice number and invoice date completely, including a Returns block's
        separate printed number. They are always typed into empty boxes, one number and one date for
        the whole PDF, and the response schema intentionally has no field capable of carrying either.
        """;

    private static string BuildChubiwinosPrimaryBody(IReadOnlyList<byte[]> pages)
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
            w.WriteString("text", ChubiwinosPrimaryPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteChubiwinosPrimarySchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Chubiwinos' one and only read. Printed UPC-A bodies, whole UPC-A and EAN-13 values, DUMP
    /// credits and handwriting rules are vendor-specific and do not share another prompt block.
    /// </summary>
    private const string ChubiwinosPrimaryPrompt = """
        You are the only reader of this scanned Chubiwinos S&G invoice. Nothing downstream will
        re-read a product row or ask a person about it, so your answer is what reaches the store's
        system. Read only the physical printed print visible in the supplied page pictures. Never
        use an OCR transcription, a product catalogue, a remembered product, a manufacturer prefix,
        arithmetic inference, or a plausible repair. Inspect every printed character twice.

        Ignore handwriting, pen and marker of every kind. A handwritten number, tick, circle,
        cross, signature, initials, arrow or underline is a person's note and is never a value on
        this invoice. A handwritten number can be the correct figure and is still not the answer:
        it is not what the vendor printed. Where ink sits on top of a printed figure, read the
        physical print underneath. If the ink makes that print genuinely unreadable, return null
        rather than the handwritten version. Never use handwriting to resolve a disagreement
        between two printed figures.

        Return every actual product exactly once, in physical order across all supplied pages. A
        product row starts with a three-digit item number at the very start of the printed line,
        followed by a space, a description, QTY, PRICE and the rightmost TOTAL. Its barcode is
        printed on the line BELOW it. A long description may wrap onto one line between the priced
        row and the barcode. The shape is:

          3-digit item number | description | QTY | PRICE | TOTAL
                              whole printed barcode on the line below

        Do not return the store street address, phone numbers, a Totals row, a barcode line by
        itself, headings or summary figures as product rows. Stop product parsing at the summary
        band beginning SALES: or SALES;. Anything below that band is not a product row. A second,
        truncated copy of the invoice header on a later page is not a second invoice and contributes
        no product rows. Every page in this PDF belongs to one payable.

        itemNumber is the three printed digits at the start of the row. description is the printed
        product wording, including a wrapped continuation where present. quantity is QTY and is
        already the number of individual sellable units; there is no case or pack conversion on
        this vendor. price is PRICE, the cost of one unit. amount is the rightmost TOTAL. Return
        quantities and money as exact text and preserve their printed decimals.

        A row under SALES SECTION sets isCredit false. A row under DUMP SECTION sets isCredit true.
        DUMP money is printed in brackets. Return the numeric magnitudes for quantity, price and
        amount and let isCredit own the sign. A DUMP row stays on this same payable.

        barcode is the digits physically printed below this row, as printed, with hyphens removed.
        Some products print an eleven-digit UPC-A body with its check digit omitted, some print a
        whole twelve-digit UPC-A, and others print a whole thirteen-digit EAN-13. Return whichever
        length is physically there. Never pad, truncate, normalize to twelve, add a leading zero,
        remove a leading zero, or compute or replace a check digit yourself. Core will append the
        one mathematically determined check digit when the paper contains an eleven-digit body.
        For a printed twelve- or thirteen-digit code, verify its check digit and re-read the cell if
        it fails, but return what you can actually see even if it still fails. A flagged code can be
        corrected later; a null code loses the whole product line. Return null only when the printed
        digit cell is genuinely illegible. Never use a catalogue, expected prefix or product memory.

        For each SALES SECTION and DUMP SECTION, return one sectionTotals entry. section is SALES or
        DUMP, quantity is the count printed after Totals:, and amount is that Totals row's money.
        Copy each of these summary-band figures independently where physically printed, preserving
        brackets and signs: SALES, CREDITS, NET AMOUNT, SALES TAX, TOTAL DUE, TOTAL PAYMENT and
        INVOICE BALANCE. A total is a check only. Never use it to fill or change a product row.

        In vendorMarkers, name only Chubiwinos markers you can physically see: the CHUBIWINOS S&G
        vendor name, a SALES SECTION or DUMP SECTION banner, Route, Created, INVOICE BALANCE, or a
        Totals row. A faint or damaged marker may be omitted. Markers help identify the selected
        vendor but never authorize inventing a product or figure.

        Before you answer, work through every one of these checks and go back to the picture for any
        cell that fails one:

        1. Row shape: every returned row begins with its physical three-digit item number and owns
           the barcode printed below it. Exclude addresses, headings, Totals rows, the summary band
           and any repeated header.
        2. Barcode: count the physically printed digits one at a time. Keep eleven as eleven,
           twelve as twelve and thirteen as thirteen. Verify a physically printed check digit and
           re-read failures, but never change a visible digit merely to make the check pass.
        3. Columns: verify quantity came from QTY, price from PRICE and amount from the rightmost
           TOTAL. There is no case count and no retail column to return.
        4. Direction: verify every SALES row has isCredit false and every DUMP row has isCredit true,
           with bracketed DUMP money returned as a magnitude.
        5. Row figures: compare QTY x PRICE with TOTAL and re-read any differing physical cells.
           Never derive or change one merely to make the arithmetic work.
        6. Section checks: separately add SALES and DUMP quantities and money and compare them with
           their own printed Totals rows. Then compare the signed rows with each printed summary
           figure, keeping SALES, CREDITS, NET AMOUNT, tax, due, payment and balance distinct.
        7. Source check: confirm every returned character is vendor print, never handwriting, OCR
           text, remembered product data, a catalogue value or a value inferred from arithmetic.

        These checks tell you where to look again. They never tell you what to write and never
        authorize changing, filling, rejecting, deriving or repairing a printed value to make
        arithmetic work. Return null for a genuinely unreadable non-barcode field. For a legible
        barcode, return the printed digits even when its check digit still fails.

        Ignore the printed invoice number and date completely. They are always typed into empty
        boxes, and the response schema intentionally has no field capable of carrying either value.
        """;

    private static string BuildRedBullPrimaryBody(IReadOnlyList<byte[]> pages)
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
                w.WriteString("text", $"Ticket page {index + 1} of {pages.Count}:");
                w.WriteEndObject();
                w.WriteStartObject();
                w.WriteStartObject("inline_data");
                w.WriteString("mime_type", "image/png");
                w.WriteBase64String("data", pages[index]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteStartObject();
            w.WriteString("text", RedBullPrimaryPrompt);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("generationConfig");
            w.WriteNumber("temperature", 0);
            w.WriteNumber("maxOutputTokens", OutputRoom);
            w.WriteString("responseMimeType", "application/json");
            WriteRedBullPrimarySchema(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Red Bull's one and only primary read. The self-checks send the model back to the physical
    /// cell while it can still look at it; none authorizes changing a value to make arithmetic work.
    /// </summary>
    private const string RedBullPrimaryPrompt = """
        You are the only reader of this scanned Red Bull delivery ticket. Nothing downstream will
        re-read a product row or ask a person about it, so your answer is what reaches the store's
        system. Read only the physical print visible in the supplied page pictures. Never use an
        OCR transcription, a product catalogue, a remembered product, a manufacturer prefix,
        arithmetic inference, or a plausible repair. Inspect every printed character twice.

        Return every actual product exactly once, in physical ticket order across all pages.
        Exclude headings, totals, Units Delivered, Units Picked Up, ROA and per-page INVOICE figures
        from rows. One product is a priced row followed by its printed barcode line:

          ID | QTY | UNITS | DESCRIPTION | PRICE | DEP | DISC | SUGAR | TOTAL
                               12 printed barcode digits on the line BELOW the description

        The barcode is the twelve digits printed under the product description, including the
        check digit. Count them one at a time. Verify the last digit against the other eleven with
        the UPC-A rule and re-read the printed digit cell if it fails. Return the digits exactly as
        read even if it still fails: a flagged code can be corrected, while a null code loses the
        whole product line. Return null only when the printed digits are genuinely illegible. Never
        repair, pad, replace or infer a digit, and never use a remembered 611269 prefix.

        Do not read the bars. The printed bar pattern sits to the right of TOTAL and OCR often
        renders it as a run of ones such as 1111'1"III511111. Those bars are not the digit cell and
        are never a barcode value. Read only the twelve printed digits below the description.

        These barcode digits are printed small and this scanner fills in the closed loops, so one
        digit in particular is unreliable and it is always the same one. A printed 8 whose loops
        have filled in reads as 6, as 0, or as 9. Every wrong code taken off these tickets so far
        has been exactly that: 611269818994 read as 611269816994 and as 611269810994, 611269002867
        read as 611269002667, 611269991000 read as 611269891000. The reverse also happens, less
        often, where a 6 or a 0 sits next to heavy ink.

        So when the check digit does not come out, look again at the digit cells specifically and
        ask whether a character you read as 6, 0 or 9 is actually a filled-in 8. Judge it by the
        shape you can actually see - an 8 has two closed loops stacked, a 6 has one loop with an
        open tail above it, a 0 is a single loop, a 9 is one loop with a tail below. Compare the
        suspect character against the other digits printed on the same line, which are the same
        typeface at the same size and show you what a clean 8 and a clean 6 look like on this page.

        Change a digit only when the second look at the printed shape genuinely shows a different
        character. Never adjust one merely to make the check digit come out, and never fall back on
        a code you remember or a prefix you expect. A wrong code that satisfies its check digit is
        worse than one that fails it, because failing is the only way anyone finds out.

        cases is QTY, the number of cases. units is UNITS, the number of individual sellable cans
        or bottles. They are different columns and both must be returned when legible. price is the
        printed PRICE: a case price on a delivery row and a truncated per-unit rate on a credit row.
        amount is TOTAL, the rightmost money figure. deposit, discount and sugar are the DEP, DISC
        and SUGAR figures between PRICE and TOTAL; every current sample prints $0.00 there. Never
        return one of those middle figures as amount. Return every number and money figure as exact
        text, preserving its printed decimals.

        A credit is printed on its own page with fractional QTY, UNITS in brackets, and money in
        brackets. Return magnitudes without signs and set isCredit true. A delivery must set
        isCredit false. isCredit owns the direction; do not turn a routine return into an error.

        For each physical page, also return one pageSummaries entry. Copy Units Delivered, Units
        Picked Up, INVOICE and ROA only where that exact label and figure are physically printed on
        that page. Preserve brackets or minus signs. These figures have different scopes: do not
        replace one with another, and do not manufacture a net figure that is not printed. Every
        page in this PDF belongs to one payable; a credit page's own printed INVOICE number does not
        start a second invoice.

        Ignore the printed invoice number and date completely. They are always typed into empty
        boxes, and the response schema intentionally has no field capable of carrying either value.

        Before you answer, work through every one of these checks and go back to the picture for any
        row or footer cell that fails one:

        1. Barcode: count exactly twelve printed digits under the description. Calculate the UPC-A
           check digit from the other eleven. Do this for every single row, not only the ones that
           looked hard. Where it fails, go back to that row's digit cells and read them one at a
           time, longest look at any 6, 0 or 9, because on this scanner those are where a filled-in
           8 hides. Do not use the bars or a known prefix.
        2. Columns: verify that cases came from QTY, units came from UNITS, and amount came from the
           rightmost TOTAL after DEP, DISC and SUGAR.
        3. Delivery row: QTY x PRICE, less DEP, DISC and SUGAR, should equal TOTAL. This does not
           hold on a credit row: credit PRICE is a truncated per-unit rate, so never "correct" a
           credit from QTY x PRICE.
        4. Page scope: compare delivery rows only with delivery-page figures and credit rows only
           with credit-page figures. Never force the net invoice to equal a delivery subtotal, a
           credit subtotal, ROA, or one page's INVOICE figure merely because it is nearby.
        5. Counts: compare the physical product rows with the separately printed Units Delivered or
           Units Picked Up on the same page, then re-read any differing cell.

        These checks tell you where to look again. They never tell you what to write and never
        authorize changing a printed value to make arithmetic work. Never fill, reject, derive or
        repair a field merely to satisfy a check. If a non-barcode field is still uncertain after
        looking again, return null. For a barcode, return legible digits as read even when the check
        still fails; return null only when the printed digit cell is genuinely illegible.
        """;

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
        w.WriteStringValue("ticketTotal");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteChubiwinosPrimarySchema(Utf8JsonWriter w)
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
                     "itemNumber", "description", "barcode", "quantity", "price", "amount",
                 })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "itemNumber", "description", "barcode", "quantity", "price", "amount",
                     "isCredit",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("isCredit");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("sectionTotals");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (string name in new[] { "section", "quantity", "amount" })
            WriteField(w, name, "string");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[] { "section", "quantity", "amount" })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        foreach (string name in new[]
                 {
                     "sales", "credits", "netAmount", "salesTax", "totalDue", "totalPayment",
                     "invoiceBalance",
                 })
            WriteField(w, name, "string");

        w.WriteStartObject("vendorMarkers");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "string");
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("rows");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteRedBullPrimarySchema(Utf8JsonWriter w)
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
                     "itemNumber", "description", "barcode", "cases", "units", "price",
                     "deposit", "discount", "sugar", "amount",
                 })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "itemNumber", "description", "barcode", "cases", "units", "price",
                     "deposit", "discount", "sugar", "amount", "isCredit",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("isCredit");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("pageSummaries");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (string name in new[]
                 {
                     "page", "unitsDelivered", "unitsPickedUp", "invoice", "roa",
                 })
            WriteField(w, name, "string");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "page", "unitsDelivered", "unitsPickedUp", "invoice", "roa",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteEndObject();
        w.WriteStartArray("required");
        w.WriteStringValue("rows");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteBlueBellPrimarySchema(Utf8JsonWriter w)
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
                     "page", "productNumber", "description", "upc", "quantity", "price",
                     "extended", "packSize",
                 })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "page", "productNumber", "description", "upc", "quantity", "price",
                     "extended", "packSize", "isCredit",
                 })
            w.WriteStringValue(name);
        w.WriteEndArray();
        w.WriteStartArray("required");
        w.WriteStringValue("isCredit");
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("pageSummaries");
        w.WriteString("type", "array");
        w.WriteStartObject("items");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (string name in new[]
                 {
                     "page", "totalQuantity", "totalAmount", "unitsReturned", "barsReturned",
                     "balanceDue",
                 })
            WriteField(w, name, "string");
        WriteField(w, "isCredit", "boolean");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in new[]
                 {
                     "page", "isCredit", "totalQuantity", "totalAmount", "unitsReturned",
                     "barsReturned", "balanceDue",
                 })
            w.WriteStringValue(name);
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

    private static ChubiwinosVisionReading? ParseChubiwinosReply(Stream stream)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        var read = new List<ChubiwinosVisionRow>();
        foreach (var row in rows.EnumerateArray())
        {
            string item = Text(row, "itemNumber") ?? "";
            string description = Text(row, "description") ?? "";
            string? barcode = Text(row, "barcode");
            string? quantity = Text(row, "quantity");
            string? price = Text(row, "price");
            string? amount = Text(row, "amount");
            bool? credit = NullableFlag(row, "isCredit");

            if (item.Length == 0 && description.Length == 0 && barcode is null
                && quantity is null && price is null && amount is null)
                continue;

            read.Add(new ChubiwinosVisionRow
            {
                ItemNumber = item,
                Description = description,
                Barcode = barcode,
                Quantity = quantity,
                Price = price,
                Amount = amount,
                IsCredit = credit,
            });
        }

        // Shape only. A footer, Totals count, valid check digit and matching arithmetic are all
        // diagnostics in Core. They must never spend another request and turn a readable invoice
        // into a refusal.
        bool hasPostableRow = read.Any(row =>
            ChubiwinosCode(row.Barcode) is not null
            && Int(row.Quantity) is > 0
            && (Money(row.Price) is > 0m || Money(row.Amount) is > 0m));
        if (!hasPostableRow) return null;

        var sectionTotals = new List<ChubiwinosVisionSectionTotal>();
        if (root.TryGetProperty("sectionTotals", out var totals)
            && totals.ValueKind == JsonValueKind.Array)
        {
            foreach (var total in totals.EnumerateArray())
            {
                string section = Text(total, "section") ?? "";
                string? quantity = Text(total, "quantity");
                string? amount = Text(total, "amount");
                if (section.Length == 0 && quantity is null && amount is null) continue;
                sectionTotals.Add(new ChubiwinosVisionSectionTotal
                {
                    Section = section,
                    Quantity = quantity,
                    Amount = amount,
                });
            }
        }

        var markers = new List<string>();
        if (root.TryGetProperty("vendorMarkers", out var markerValues)
            && markerValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var marker in markerValues.EnumerateArray())
                if (marker.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(marker.GetString()))
                    markers.Add(marker.GetString()!.Trim());
        }

        return new ChubiwinosVisionReading
        {
            Status = ChubiwinosVisionStatus.Success,
            Rows = read,
            SectionTotals = sectionTotals,
            Sales = Text(root, "sales"),
            Credits = Text(root, "credits"),
            NetAmount = Text(root, "netAmount"),
            SalesTax = Text(root, "salesTax"),
            TotalDue = Text(root, "totalDue"),
            TotalPayment = Text(root, "totalPayment"),
            InvoiceBalance = Text(root, "invoiceBalance"),
            VendorMarkers = markers,
        };
    }

    private static RedBullVisionReading? ParseRedBullReply(Stream stream)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        var read = new List<RedBullVisionRow>();
        foreach (var row in rows.EnumerateArray())
        {
            string item = Text(row, "itemNumber") ?? "";
            string description = Text(row, "description") ?? "";
            string? barcode = Text(row, "barcode");
            string? cases = Text(row, "cases");
            string? units = Text(row, "units");
            string? price = Text(row, "price");
            string? deposit = Text(row, "deposit");
            string? discount = Text(row, "discount");
            string? sugar = Text(row, "sugar");
            string? amount = Text(row, "amount");
            bool? credit = NullableFlag(row, "isCredit");

            if (item.Length == 0 && description.Length == 0 && barcode is null
                && cases is null && units is null && price is null && amount is null)
                continue;

            read.Add(new RedBullVisionRow
            {
                ItemNumber = item,
                Description = description,
                Barcode = barcode,
                Cases = cases,
                Units = units,
                Price = price,
                Deposit = deposit,
                Discount = discount,
                Sugar = sugar,
                Amount = amount,
                IsCredit = credit,
            });
        }

        // Shape only: one row carrying exactly what Core can post. A missing footer or arithmetic
        // disagreement is a warning on a written file, not a second request followed by a refusal.
        bool hasPostableRow = read.Any(row =>
            RedBullCode(row.Barcode) is not null
            && Int(row.Units) is > 0
            && Money(row.Amount) is > 0m);
        if (!hasPostableRow) return null;

        var summaries = new List<RedBullVisionPageSummary>();
        if (root.TryGetProperty("pageSummaries", out var pageSummaries)
            && pageSummaries.ValueKind == JsonValueKind.Array)
        {
            foreach (var summary in pageSummaries.EnumerateArray())
            {
                string page = Text(summary, "page") ?? "";
                string? unitsDelivered = Text(summary, "unitsDelivered");
                string? unitsPickedUp = Text(summary, "unitsPickedUp");
                string? invoice = Text(summary, "invoice");
                string? roa = Text(summary, "roa");
                if (page.Length == 0 && unitsDelivered is null && unitsPickedUp is null
                    && invoice is null && roa is null)
                    continue;
                summaries.Add(new RedBullVisionPageSummary
                {
                    Page = page,
                    UnitsDelivered = unitsDelivered,
                    UnitsPickedUp = unitsPickedUp,
                    Invoice = invoice,
                    Roa = roa,
                });
            }
        }

        return new RedBullVisionReading
        {
            Status = RedBullVisionStatus.Success,
            Rows = read,
            PageSummaries = summaries,
        };
    }

    private static BlueBellVisionReading? ParseBlueBellReply(Stream stream)
    {
        string? json = ReplyPayload(stream);
        if (json is null) return null;
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return null;

        var read = new List<BlueBellVisionRow>();
        foreach (var row in rows.EnumerateArray())
        {
            string page = Text(row, "page") ?? "";
            string productNumber = Text(row, "productNumber") ?? "";
            string description = Text(row, "description") ?? "";
            string? upc = Text(row, "upc");
            string? quantity = Text(row, "quantity");
            string? price = Text(row, "price");
            string? extended = Text(row, "extended");
            string? packSize = Text(row, "packSize");
            bool? credit = NullableFlag(row, "isCredit");

            if (productNumber.Length == 0 && description.Length == 0 && upc is null
                && quantity is null && price is null && extended is null)
                continue;

            read.Add(new BlueBellVisionRow
            {
                Page = page,
                ProductNumber = productNumber,
                Description = description,
                Upc = upc,
                Quantity = quantity,
                Price = price,
                Extended = extended,
                PackSize = packSize,
                IsCredit = credit,
            });
        }

        // Shape only. Totals, returned counts, pack agreement and row arithmetic are deliberately
        // absent from this gate; Core can post a row from its printed UPC, quantity, and either
        // money figure. Anything else is a warning on the written file.
        bool hasPostableRow = read.Any(row =>
            BlueBellCode(row.Upc) is not null
            && BlueBellQuantity(row.Quantity)
            && (Money(row.Price) is > 0m || Money(row.Extended) is > 0m));
        if (!hasPostableRow) return null;

        var summaries = new List<BlueBellVisionPageSummary>();
        if (root.TryGetProperty("pageSummaries", out var pageSummaries)
            && pageSummaries.ValueKind == JsonValueKind.Array)
        {
            foreach (var summary in pageSummaries.EnumerateArray())
            {
                string page = Text(summary, "page") ?? "";
                bool? credit = NullableFlag(summary, "isCredit");
                string? totalQuantity = Text(summary, "totalQuantity");
                string? totalAmount = Text(summary, "totalAmount");
                string? unitsReturned = Text(summary, "unitsReturned");
                string? barsReturned = Text(summary, "barsReturned");
                string? balanceDue = Text(summary, "balanceDue");
                if (page.Length == 0 && credit is null && totalQuantity is null
                    && totalAmount is null && unitsReturned is null && barsReturned is null
                    && balanceDue is null)
                    continue;
                summaries.Add(new BlueBellVisionPageSummary
                {
                    Page = page,
                    IsCredit = credit,
                    TotalQuantity = totalQuantity,
                    TotalAmount = totalAmount,
                    UnitsReturned = unitsReturned,
                    BarsReturned = barsReturned,
                    BalanceDue = balanceDue,
                });
            }
        }

        return new BlueBellVisionReading
        {
            Status = BlueBellVisionStatus.Success,
            Rows = read,
            PageSummaries = summaries,
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

        // This is the whole gate: one row carrying the three values Core can actually post.
        // Arithmetic disagreements are useful notes on a written file, not reasons to spend another
        // request and then refuse a readable delivery. Do not add count, band or row-math gates.
        //
        // TICKET TOTALS is deliberately **not** required. It used to be, which made this reader
        // stricter than the parser it feeds: the parser has always been willing to invoice a ticket
        // whose totals line was destroyed, paying it for the sum of its own rows and saying so, and
        // that path could never run. A smudged summary line is not a reason to send a delivery of
        // legible product rows back to the scanner.
        bool hasPostableRow = read.Any(row =>
            BimboBody(row.Barcode) is not null
            && Int(row.Units) is > 0
            && Money(row.Amount) is > 0m);
        if (!hasPostableRow) return null;

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

    /// <summary>The longest a named per-minute limit may hold one rung. Beyond this, move down.</summary>
    private static readonly TimeSpan LongestThrottleWait = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Whether a 429 is the per-minute limit rather than the daily allowance, and how long Google
    /// asked us to wait.
    ///
    /// Google returns both under the same status code and says which in the body: a `QuotaFailure`
    /// detail carries a `quotaId` such as `GenerateRequestsPerMinutePerProjectPerModel-FreeTier` or
    /// `GenerateRequestsPerDayPerProjectPerModel-FreeTier`, and a `RetryInfo` detail may carry a
    /// `retryDelay` like `"38s"`.
    ///
    /// **A 429 nobody can classify stays the daily one.** That is the conservative reading: getting
    /// it wrong that way costs a worse model on one invoice, while getting it wrong the other way
    /// puts the operator back in front of a window that says "Converting..." for minutes on a quota
    /// that will not return today. A per-day violation always wins over a per-minute one when both
    /// appear.
    /// </summary>
    private static bool IsAPerMinuteLimit(Stream body, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array) return false;

            bool perMinute = false, perDay = false;
            foreach (var detail in details.EnumerateArray())
            {
                if (detail.TryGetProperty("retryDelay", out var delay)
                    && delay.ValueKind == JsonValueKind.String
                    && ParseSeconds(delay.GetString()) is TimeSpan asked)
                    retryAfter = asked;

                if (!detail.TryGetProperty("violations", out var violations)
                    || violations.ValueKind != JsonValueKind.Array) continue;

                foreach (var violation in violations.EnumerateArray())
                    foreach (string field in new[] { "quotaId", "quotaMetric" })
                    {
                        if (!violation.TryGetProperty(field, out var named)
                            || named.ValueKind != JsonValueKind.String) continue;
                        string text = named.GetString() ?? "";
                        perMinute |= text.Contains("PerMinute", StringComparison.OrdinalIgnoreCase)
                                     || text.Contains("per_minute", StringComparison.OrdinalIgnoreCase);
                        perDay |= text.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
                                  || text.Contains("per_day", StringComparison.OrdinalIgnoreCase);
                    }
            }

            if (perDay || !perMinute) { retryAfter = null; return false; }
            if (retryAfter > LongestThrottleWait) retryAfter = LongestThrottleWait;
            return true;
        }
        catch
        {
            retryAfter = null;
            return false;
        }
    }

    /// <summary>Google's `"38s"` / `"1.5s"` duration form.</summary>
    private static TimeSpan? ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.EndsWith('s')) return null;
        return double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture,
            out double seconds) && seconds is >= 0 and < 3600
            ? TimeSpan.FromSeconds(seconds) : null;
    }

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

    /// <summary>
    /// An eleven-digit UPC-A body or complete twelve- or thirteen-digit Chubiwinos code, with
    /// printed hyphens removed. A printed check digit is deliberately not a gate: Core sends a
    /// failed one as read and flags it. Core restores the omitted check digit on an eleven-digit
    /// body before writing XML.
    /// </summary>
    private static string? ChubiwinosCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(trimmed);
        return digits.Length is 11 or 12 or 13 ? digits : null;
    }

    /// <summary>
    /// The twelve Red Bull digits the model copied, or null for malformed text. The check digit is
    /// deliberately not part of this gate: a failed one is postable and Core ships it flagged.
    /// </summary>
    private static string? RedBullCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(trimmed);
        return digits.Length == 12 ? digits : null;
    }

    /// <summary>The twelve padded characters Blue Bell physically prints, not a validated UPC.</summary>
    private static string? BlueBellCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        return trimmed.Length == 12 && trimmed[0] == '0' && trimmed.All(char.IsAsciiDigit)
            ? trimmed : null;
    }

    private static bool BlueBellQuantity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string cleaned = text.Trim();
        if (cleaned.StartsWith('(') && cleaned.EndsWith(')')) cleaned = cleaned[1..^1].Trim();
        else if (cleaned.StartsWith('+') || cleaned.StartsWith('-')) cleaned = cleaned[1..].Trim();
        else if (cleaned.EndsWith('+') || cleaned.EndsWith('-')) cleaned = cleaned[..^1].Trim();
        if (cleaned.Contains('+') || cleaned.Contains('-')) return false;
        if (cleaned.Contains('/'))
        {
            string[] pieces = cleaned.Split('/');
            return pieces.Length == 2
                   && decimal.TryParse(pieces[0], NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out decimal numerator)
                   && decimal.TryParse(pieces[1], NumberStyles.AllowDecimalPoint,
                       CultureInfo.InvariantCulture, out decimal denominator)
                   && numerator > 0m && denominator == 12m;
        }
        return decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                   out decimal quantity)
               && quantity > 0m;
    }

    /// <summary>The exact ten-digit body Bimbo prints, or null for a padded or repaired value.</summary>
    private static string? BimboBody(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim();
        if (trimmed.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-'))) return null;
        string digits = Upc.DigitsOnly(trimmed);
        return digits.Length == 10 ? digits : null;
    }

    /// <summary>The digits of a barcode, or null where the reply holds anything else.</summary>
    private static string? Digits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string digits = new(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 8 and <= 14 ? digits : null;
    }
}
