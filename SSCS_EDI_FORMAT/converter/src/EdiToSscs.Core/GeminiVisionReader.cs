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
/// Failure is always silent and always empty. No key, no network, a refused or throttled request, a
/// reply that will not parse — every one of them gives <see cref="VisionReading.Empty"/> and the
/// conversion carries on exactly as it does without this reader. The store cannot lose a delivery
/// because Google was slow.
/// </summary>
public sealed class GeminiVisionReader : IInvoiceVisionReader
{
    /// <summary>
    /// The model to read with.
    ///
    /// **Not the newest one.** Measured against the store's own key on 2026-08-11: `gemini-3.6-flash`
    /// allows **twenty requests a day** on the free tier, and a page is one request — so a single
    /// period's invoices exhaust it before they are half done, and every file after that silently
    /// loses its second reader. `gemini-3.5-flash` answers on the same key with quota to spare.
    /// `gemini-3.5-flash-lite` also answers and is the fallback if this one is ever capped too;
    /// the model is a setting, so the store can change it without a new build.
    /// </summary>
    public const string DefaultModel = "gemini-3.5-flash";

    /// <summary>
    /// The models to fall through, best first. Each carries **its own quota**, so a key with nothing
    /// left on one will often still answer on the next — which is why this list exists and why it is
    /// the first thing tried when a request comes back throttled. `flash-lite` is a smaller model and
    /// a slightly worse reader, which is the right way round: a second opinion from the small model
    /// still has to be confirmed by the paper before any figure is taken.
    /// </summary>
    public static readonly string[] DefaultModels = [DefaultModel, "gemini-3.5-flash-lite"];

    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/models";

    /// <summary>
    /// Long enough for a full-page scan on a slow line, short enough that a hung request does not
    /// look like a hung application. A timeout is just an empty reading.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private static readonly HttpClient Shared = new() { Timeout = RequestTimeout };

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
    /// Where the last successful request came from, so the next page starts there instead of
    /// re-probing combinations already known to be empty. An invoice is several pages and a quota
    /// does not refill between them.
    /// </summary>
    private int _at;

    public GeminiVisionReader(
        string apiKey, string? model = null, HttpClient? http = null, TimeSpan[]? backoff = null)
        : this([apiKey], string.IsNullOrWhiteSpace(model) ? null : [model], http, backoff)
    {
    }

    /// <param name="apiKeys">
    /// Every key to try, in order. More than one is supported because a free-tier key runs out — but
    /// note that spreading one workload over several accounts to get past a quota is what Google
    /// calls circumvention, and the account it suspends is the one currently working.
    /// </param>
    /// <param name="models">Models to fall through per key; null for <see cref="DefaultModels"/>.</param>
    public GeminiVisionReader(
        IReadOnlyList<string>? apiKeys, IReadOnlyList<string>? models = null,
        HttpClient? http = null, TimeSpan[]? backoff = null)
    {
        _keys = [.. (apiKeys ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())];
        _models = [.. (models ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim())];
        if (_models.Length == 0) _models = DefaultModels;
        _http = http ?? Shared;
        _backoff = backoff ?? Backoff;
    }

    /// <summary>True when a key is configured at all. No key is not an error, just no second reader.</summary>
    public bool IsConfigured => _keys.Length > 0;

    /// <summary>How many key-and-model pairs there are to work through.</summary>
    private int Combinations => _keys.Length * _models.Length;

    /// <summary>
    /// The n-th pair. Models cycle fastest, so a key is exhausted across every model before the next
    /// key is touched — which is the order that keeps one account's work on one account.
    /// </summary>
    private (string Key, string Model) Pair(int index) =>
        (_keys[index / _models.Length], _models[index % _models.Length]);

    public VisionReading TryRead(byte[] pdfBytes, VisionVendor vendor)
    {
        if (!IsConfigured || pdfBytes is null || pdfBytes.Length == 0) return VisionReading.Empty;

        try
        {
            var pages = PdfPageImages.TryReadAll(pdfBytes);
            if (pages.Count == 0) return VisionReading.Empty;

            var rows = new List<VisionRow>();
            decimal? total = null;
            int? units = null;

            for (int number = 1; number <= pages.Count; number++)
            {
                var page = ReadPage(pages[number - 1], vendor);
                if (page is null) continue;

                // Stamped here because this is the only place that knows: one request is one page,
                // and once the rows are concatenated there is no telling them apart again.
                rows.AddRange(page.Rows.Select(r => r with { Page = number }));
                // A ticket prints its total on the last page that has one; its unit counts add up
                // across pages, the same way the parser adds the printed ones up.
                if (page.PrintedTotal is not null) total = page.PrintedTotal;
                if (page.PrintedUnits is not null) units = (units ?? 0) + page.PrintedUnits.Value;
            }

            return rows.Count == 0
                ? VisionReading.Empty
                : new VisionReading { Rows = rows, PrintedTotal = total, PrintedUnits = units };
        }
        catch
        {
            return VisionReading.Empty;
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
            int which = (i / _models.Length) + 1;
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

            using var cancel = new CancellationTokenSource(RequestTimeout);
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
    /// One page, read — waiting out a throttle rather than giving up on it.
    ///
    /// Only a 429 and a 5xx are worth retrying: those say "ask again later". A 400 or a 403 says the
    /// request or the key is wrong and asking again just wastes the allowance the operator is
    /// waiting on.
    /// </summary>
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
    /// </summary>
    private VisionReading? ReadPage(byte[] png, VisionVendor vendor)
    {
        if (Combinations == 0) return null;
        string body = BuildBody(png, vendor);

        for (int round = 0; ; round++)
        {
            bool anythingWorthWaitingFor = false;

            for (int tried = 0; tried < Combinations; tried++)
            {
                int index = (_at + tried) % Combinations;
                var (key, model) = Pair(index);

                var reading = TryOnce(body, key, model, out bool throttled);
                if (reading is not null)
                {
                    _at = index;          // start here next page rather than re-probing empty quotas
                    return reading;
                }
                anythingWorthWaitingFor |= throttled;
            }

            // Every key on every model is empty. Waiting is the only thing left, and only for as
            // long as the quota window - beyond that the conversion goes on without a second reader.
            if (!anythingWorthWaitingFor || round >= _backoff.Length) return null;
            Thread.Sleep(_backoff[round]);
        }
    }

    /// <summary>
    /// One request against one key and model. Null for any failure; <paramref name="throttled"/>
    /// says whether asking again later could plausibly work.
    /// </summary>
    private VisionReading? TryOnce(string body, string key, string model, out bool throttled)
    {
        throttled = false;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{Endpoint}/{model}:generateContent");
            request.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            request.Content = new StringContent(
                body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

            using var cancel = new CancellationTokenSource(RequestTimeout);
            using var response = _http.Send(request, cancel.Token);

            if (response.IsSuccessStatusCode)
            {
                using var stream = response.Content.ReadAsStream(cancel.Token);
                return ParseReply(stream);
            }

            throttled = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // A dropped line is worth another go; the next key is on the same wire, so this is the
            // one case where moving on will not help and waiting might.
            throttled = true;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ───────────────────────────────────────────────────────────────────────── the request

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
            w.WriteString("responseMimeType", "application/json");
            WriteSchema(w);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteSchema(Utf8JsonWriter w)
    {
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
        WriteField(w, "qty", "string");
        WriteField(w, "units", "string");
        WriteField(w, "price", "string");
        WriteField(w, "amount", "string");
        WriteField(w, "isCredit", "boolean");
        WriteField(w, "verticalPosition", "string");
        w.WriteEndObject();
        w.WriteStartArray("propertyOrdering");
        foreach (string name in
                 new[] { "itemNumber", "description", "barcode", "qty", "units", "price", "amount",
                         "isCredit", "verticalPosition" })
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

            Where each row sits. Give verticalPosition as a whole number from 0 to 1000 saying how
            far down the page the row is printed - 0 is the very top edge, 1000 the very bottom,
            500 halfway. It only has to be close. It is used to scroll a picture of the page to
            that row when a person is asked about it, and for nothing else: no figure comes from
            it.

            Which way up the page is. Some of these sheets go through the scanner rotated 180
            degrees, so the printing runs bottom-to-top and every character is upside down. You can
            still read them; say so by setting pageUpsideDown true. Set it false for a page the
            right way up. This does not change anything you report - read the page as it is meant to
            be read either way, and report the figures normally.

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
            """;

        string columns = vendor switch
        {
            VisionVendor.RedBull => """

                THIS IS A RED BULL DISTRIBUTION COMPANY DELIVERY TICKET.

                Layout. Each product takes two printed lines - the priced row, and its barcode
                directly underneath, printed above the bars:

                  RB1718   1   24   RED BULL 8.4OZ LS   40.75  0.00  0.00  0.00   40.75
                                      611269991000                            |||||||||||

                Columns, left to right:
                  itemNumber  - the code in the first column
                  qty         - QTY: the number of CASES
                  units       - UNITS: individual cans or bottles. This is NOT the QTY column.
                  description - the product name
                  price       - PRICE: the cost of ONE CASE, not one can
                  (DEP, DISC and SUGAR follow, usually 0.00 - do not report them)
                  amount      - TOTAL: the last money column on the row
                  barcode     - the 12 digits on the line below, above the bars

                How a row adds up: QTY x PRICE = TOTAL, less DEP/DISC/SUGAR where those are not
                zero. If your reading does not add up, look at the row again - but if it still does
                not, report what is printed.
                Never adjust a figure to make the row work.

                Returns. A returns page or section prints the same columns with UNITS and every
                money figure in brackets - (2) and (4.45). Set isCredit true on those rows and
                report the figures without the brackets. A returns row is priced per UNIT rather
                than per case, so its PRICE is a single can's price and its QTY may be a fraction.

                Foot of the page: printedTotal is the INVOICE or ROA figure, and printedUnits is
                Units Delivered. Ignore DISCOUNT, DEPOSIT and TAX.

                The column headings on these scans are usually unreadable. Go by position, not by
                the heading text.
                """,
            VisionVendor.DrPepper => """

                THIS IS A DR PEPPER / KEURIG DR PEPPER INVOICE.

                Layout. Each product takes two printed lines - the product line carrying the code
                and name, then the money row beneath it:

                  078000200287/20020468  12z12 Cn DrPep
                       8.99    2    24    21.38   0.00   21.38

                Columns:
                  barcode     - the 12 digits BEFORE the slash on the product line
                  itemNumber  - the digits AFTER the slash
                  description - the product name
                  qty         - CASES
                  units       - UNITS: individual bottles or cans. This is NOT the CASES column.
                  price       - NET: the cost of ONE CASE
                  amount      - AMOUNT: the last money column
                  (WHSLE is the first money column and TAX the second to last - do not report them)

                How a row adds up: CASES x NET = AMOUNT. If your reading does not add up, look at
                the row again - but if it still does not, report what is printed.
                Never adjust a figure to make the row work.

                Groups. Rows are grouped by package type, and each group is closed by its own
                subtotal - "PEG 2 24 40.00", meaning 2 cases, 24 units, $40.00. The invoice ends
                with TOT SALE (or a SALES SUMMARY block) and AMOUNT DUE. Do NOT report any of those
                as product rows.

                Pallets and shells. Below AMOUNT DUE the ticket lists the crates and pallets that
                went back on the truck - "PALLET PLASTIC 37X37 75001490 1", "SHELL PET 20Z
                75000014 5". They have a name, a number and a count but no price and no amount.
                They are not products and must NOT be reported as rows.

                Returns. A CREDITS banner starts a section with a shorter column set - no WHSLE -
                and the money in brackets. Set isCredit true on those rows and report the figures
                without the brackets.

                Pen marks. These invoices come back marked up by hand. A pen stroke is not a digit
                and not a decimal point, and a stroke through a figure is not a minus sign. Read the
                printed figure underneath it, or leave the field out.

                Foot of the page: printedTotal is AMOUNT DUE, printedUnits is the TOT SALE unit
                count.
                """,
            _ => "",
        };

        return Common + columns;
    }

    // ───────────────────────────────────────────────────────────────────────── the reply

    /// <summary>
    /// Pulls the reading out of Gemini's envelope. Anything unexpected anywhere gives null, which
    /// the caller treats as a page that could not be read.
    /// </summary>
    private static VisionReading? ParseReply(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0) return null;

        var parts = candidates[0].GetProperty("content").GetProperty("parts");
        var text = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                text.Append(t.GetString());
        if (text.Length == 0) return null;

        using var payload = JsonDocument.Parse(text.ToString());
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
                VerticalPosition = Int(Text(row, "verticalPosition")),
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
