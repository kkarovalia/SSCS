using System.Net;
using System.Text;
using System.Text.Json;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Proofs for the reader that talks to Google: what it sends, what it makes of a reply, and — the
/// part that matters most on a store counter — that every way this can fail is silent and empty.
///
/// A conversion must never be lost because a key expired or the line was down.
/// </summary>
public class GeminiVisionReaderTests
{
    private const string Scan = @"Fixtures\PDF\REDBULL_2030211863_sscs_verified.pdf";
    private const string BimboScan = @"Fixtures\PDF\BIMBO_84294890013239_sscs_verified.pdf";

    /// <summary>
    /// Answers each request from a script and records what it was sent.
    ///
    /// The replies are captured as status-and-text and rebuilt for every send, because a single
    /// <see cref="HttpResponseMessage"/> can only be read once — reusing one makes the second
    /// request look like a parse failure and sends the reader off down its fallback chain.
    /// </summary>
    private sealed class Stub : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _replies;

        public Stub(params HttpResponseMessage[] replies) =>
            _replies = [.. replies.Select(r =>
                (r.StatusCode, r.Content.ReadAsStringAsync().GetAwaiter().GetResult()))];

        public List<string> Sent { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            Sent.Add(request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult());
            var (status, body) = _replies[Math.Min(Sent.Count - 1, _replies.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Send(request, token));
    }

    /// <summary>Gemini's reply envelope, carrying the model's JSON as the text of one part.</summary>
    private static HttpResponseMessage Reply(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                + JsonSerializer.Serialize(payload) + "}]}}]}",
                Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Raw(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>No backoff in tests — the waits are real seconds and nothing here is measuring them.</summary>
    private static readonly TimeSpan[] NoWait = [];

    /// <summary>One key, one model — so these tests are about the reply and not the fallback chain.</summary>
    private static readonly string[] OneModel = ["gemini-test"];

    /// <summary>
    /// The ladder, best first, and **no Flash-Lite rung at all**.
    ///
    /// This is not an oversight to be tidied up later. Once both Flash models had run out of free
    /// allowance on 2026-08-13 every remaining invoice was read by Flash-Lite, and that is where
    /// the day's transposed and duplicated barcode digits came from — one ticket lost a whole
    /// $24.85 row that way while reporting itself fine. Running out of Flash now reports the reader
    /// unavailable, which asks before falling back, rather than answering badly without saying so.
    ///
    /// Every name here was checked against the account's own models.list on 2026-08-13. A model
    /// that does not exist is not a slower rung, it is a 404 on every request against it — there is
    /// no `gemini-3.1-flash` or `gemini-3-flash`, and `gemini-3.7-flash` does exist.
    /// </summary>
    [Fact]
    public void SharedDefaultsAreFullFlashOnly()
    {
        Assert.Equal(
            [
                "gemini-3.7-flash",
                "gemini-3.6-flash",
                "gemini-3.5-flash",
            ],
            GeminiVisionReader.DefaultModels);
        Assert.Equal("gemini-3.7-flash", GeminiVisionReader.DefaultModel);
        Assert.DoesNotContain(GeminiVisionReader.DefaultModels,
            m => m.Contains("lite", StringComparison.OrdinalIgnoreCase));
    }

    private static (VisionReading Reading, Stub Handler) Read(params HttpResponseMessage[] replies)
    {
        var handler = new Stub(replies);
        var reader = new GeminiVisionReader(
            ["test-key"], OneModel, new HttpClient(handler), NoWait);
        return (reader.TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull), handler);
    }

    /// <summary>
    /// No key configured is not an error and not a prompt — it is simply no second reader, and
    /// nothing is sent anywhere. This is the state the converter ships in.
    /// </summary>
    [Fact]
    public void WithNoKeyItReadsNothingAndSendsNothing()
    {
        var handler = new Stub(Reply("""{"rows":[{"itemNumber":"X","amount":"1.00"}]}"""));
        var reader = new GeminiVisionReader("", http: new HttpClient(handler));

        var reading = reader.TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.False(reader.IsConfigured);
        Assert.Empty(reading.Rows);
        Assert.Empty(handler.Sent);
    }

    /// <summary>A reply the model actually produces, turned into rows the parser can use.</summary>
    [Fact]
    public void ReadsRowsOutOfAReply()
    {
        var (reading, _) = Read(
            Reply("""
                {"rows":[
                  {"itemNumber":"0861718","description":"RED BULL 8.4OZ LS","barcode":"611269991000",
                   "units":"24","price":"40.75","amount":"40.75"},
                  {"itemNumber":"8633049","units":"24","price":"70.22","amount":"70.22"}
                ],"printedTotal":"342.50","printedUnits":"156"}
                """),
            Reply("""{"rows":[]}"""));

        Assert.Equal(2, reading.Rows.Count);
        Assert.Equal("0861718", reading.Rows[0].ItemNumber);
        Assert.Equal("611269991000", reading.Rows[0].Barcode);
        Assert.Equal(24, reading.Rows[0].Units);
        Assert.Equal(40.75m, reading.Rows[0].Amount);
        Assert.Equal(342.50m, reading.PrintedTotal);
        Assert.Equal(156, reading.PrintedUnits);
    }

    /// <summary>
    /// A figure the model would not commit to comes back as nothing, and stays nothing. This is the
    /// whole safety property of the prompt: a blank sends the row to a person, and a guess sends it
    /// to the register.
    /// </summary>
    [Fact]
    public void AFigureTheModelLeftOutStaysMissing()
    {
        var (reading, _) = Read(
            Reply("""{"rows":[{"itemNumber":"X","units":"24","amount":null,"price":""}]}"""),
            Reply("""{"rows":[]}"""));

        var row = Assert.Single(reading.Rows);
        Assert.Equal(24, row.Units);
        Assert.Null(row.Amount);
        Assert.Null(row.Price);
    }

    /// <summary>
    /// Money is read as printed. Brackets mean a credit and the sign is the parser's business, a
    /// stray currency symbol is not a reason to lose the row, and anything that is not plainly a
    /// number is no reading at all rather than a zero.
    /// </summary>
    [Theory]
    [InlineData("1234.50", "1234.50")]
    [InlineData("$1,234.50", "1234.50")]
    [InlineData("(4.45)", "4.45")]
    [InlineData("40.75-", "40.75")]
    [InlineData("illegible", null)]
    [InlineData("n/a", null)]
    [InlineData("", null)]
    public void ReadsMoneyAsPrintedOrNotAtAll(string printed, string? expected)
    {
        var (reading, _) = Read(
            Reply("{\"rows\":[{\"itemNumber\":\"X\",\"amount\":"
                  + JsonSerializer.Serialize(printed) + "}]}"),
            Reply("""{"rows":[]}"""));

        Assert.Equal(
            expected is null ? null : decimal.Parse(expected),
            Assert.Single(reading.Rows).Amount);
    }

    /// <summary>A barcode that is not a barcode is discarded rather than passed on as a product id.</summary>
    [Theory]
    [InlineData("611269991000", "611269991000")]
    [InlineData("6112 6999 1000", "611269991000")]
    [InlineData("611", null)]
    [InlineData("not printed", null)]
    public void KeepsOnlySomethingShapedLikeABarcode(string printed, string? expected)
    {
        var (reading, _) = Read(
            Reply("{\"rows\":[{\"itemNumber\":\"X\",\"barcode\":"
                  + JsonSerializer.Serialize(printed) + "}]}"),
            Reply("""{"rows":[]}"""));

        Assert.Equal(expected, Assert.Single(reading.Rows).Barcode);
    }

    /// <summary>Every way the far end can fail gives the same thing: no reading, no exception.</summary>
    [Fact]
    public void AnyFailureFromTheApiIsSimplyNoReading()
    {
        Assert.Empty(Read(Raw("rate limited", HttpStatusCode.TooManyRequests)).Reading.Rows);
        Assert.Empty(Read(Raw("no", HttpStatusCode.Unauthorized)).Reading.Rows);
        Assert.Empty(Read(Raw("boom", HttpStatusCode.InternalServerError)).Reading.Rows);
        Assert.Empty(Read(Raw("this is not json")).Reading.Rows);
        Assert.Empty(Read(Raw("""{"candidates":[]}""")).Reading.Rows);
        Assert.Empty(Read(Reply("not json either")).Reading.Rows);
        Assert.Empty(Read(Reply("""{"somethingElse":true}""")).Reading.Rows);
    }

    /// <summary>
    /// Being throttled is not a failure, it is "ask again in a moment" — so it does.
    ///
    /// The free tier allows twenty requests a minute and a page is one request, so a stack of
    /// invoices dropped at once will hit it. Measured on the store's own thirty-five on 2026-08-11:
    /// the first dozen went through and every one after came back 429. Giving up there loses the
    /// reading silently and puts the operator back to answering questions the model could have
    /// answered.
    /// </summary>
    [Fact]
    public void AThrottledRequestIsRetriedRatherThanLost()
    {
        var handler = new Stub(
            Raw("slow down", HttpStatusCode.TooManyRequests),
            Reply("""{"rows":[{"itemNumber":"X","units":"24","amount":"40.75"}]}"""),
            Reply("""{"rows":[]}"""));

        var reading = new GeminiVisionReader(
                "test-key", http: new HttpClient(handler), backoff: [TimeSpan.Zero, TimeSpan.Zero])
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.Equal(40.75m, Assert.Single(reading.Rows).Amount);
        Assert.Equal(3, handler.Sent.Count);        // throttled once, retried, then the second page
    }

    /// <summary>
    /// A refused key or a malformed request is **not** retried. Those say the request is wrong, not
    /// early, and asking again only burns the allowance the operator is waiting on.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void APermanentRefusalIsNotRetried(HttpStatusCode status)
    {
        var handler = new Stub(Raw("no", status));

        new GeminiVisionReader(
                ["test-key"], OneModel, new HttpClient(handler), [TimeSpan.Zero, TimeSpan.Zero])
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.Equal(2, handler.Sent.Count);        // one attempt per page, no more
    }

    /// <summary>Answers by which key and model was asked, and records the order they were tried in.</summary>
    private sealed class Router(Func<string, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Tried { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken token)
        {
            string key = request.Headers.TryGetValues("x-goog-api-key", out var v) ? v.First() : "";
            string model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            Tried.Add($"{key}/{model}");
            return respond(key, model);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(Send(request, token));
    }

    /// <summary>A fresh success every call — a response body can only be read once.</summary>
    private static HttpResponseMessage Rows() =>
        Reply("""{"rows":[{"itemNumber":"X","units":"24","amount":"40.75"}]}""");

    /// <summary>
    /// A key with nothing left on one model still has a quota on the next, so a throttled request
    /// moves across immediately rather than waiting. Nobody stands at the counter while a timer runs
    /// down if there is another door to try.
    /// </summary>
    [Fact]
    public void AnExhaustedModelFallsThroughToTheNextWithoutWaiting()
    {
        var handler = new Router((_, model) =>
            model == "m1" ? Raw("empty", HttpStatusCode.TooManyRequests) : Rows());

        var reading = new GeminiVisionReader(
                ["k1"], ["m1", "m2"], new HttpClient(handler), [TimeSpan.FromSeconds(30)])
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.NotEmpty(reading.Rows);

        // Page one probes m1 then m2; page two starts at m2, because a quota does not refill between
        // the pages of one invoice and re-probing it is a wasted request every time.
        Assert.Equal(["k1/m1", "k1/m2", "k1/m2"], handler.Tried);
    }

    /// <summary>
    /// Every key gets the preferred model before any key gets the lower-quality fallback model.
    /// </summary>
    [Fact]
    public void EveryKeyGetsThePreferredModelBeforeAnyFallbackModel()
    {
        var handler = new Router((key, _) =>
            key == "k1" ? Raw("empty", HttpStatusCode.TooManyRequests) : Rows());

        var reading = new GeminiVisionReader(
                ["k1", "k2"], ["m1", "m2"], new HttpClient(handler), [TimeSpan.FromSeconds(30)])
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.NotEmpty(reading.Rows);
        Assert.Equal(["k1/m1", "k2/m1", "k2/m1"], handler.Tried);
    }

    /// <summary>
    /// A key the API refuses outright does not stop the others. One bad paste should cost the
    /// operator nothing when a working key is sitting underneath it.
    /// </summary>
    [Fact]
    public void ARefusedKeyDoesNotStopTheRest()
    {
        var handler = new Router((key, _) =>
            key == "bad" ? Raw("no", HttpStatusCode.Forbidden) : Rows());

        var reading = new GeminiVisionReader(
                ["bad", "good"], ["m1"], new HttpClient(handler), NoWait)
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.NotEmpty(reading.Rows);
        Assert.Equal(["bad/m1", "good/m1", "good/m1"], handler.Tried);
    }

    /// <summary>
    /// Everything empty is still not a failure — it is simply no second reader, and the invoice
    /// converts the way it does with no key at all. Every combination is tried before giving up.
    /// </summary>
    [Fact]
    public void WhenEveryKeyAndModelIsEmptyThereIsSimplyNoReading()
    {
        var handler = new Router((_, _) => Raw("empty", HttpStatusCode.TooManyRequests));

        var reading = new GeminiVisionReader(
                ["k1", "k2"], ["m1", "m2"], new HttpClient(handler), NoWait)
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.Empty(reading.Rows);
        Assert.Equal(["k1/m1", "k2/m1", "k1/m2", "k2/m2"], handler.Tried.Take(4));
    }

    [Fact]
    public void AnyNumberOfKeysIsExhaustedOnThePreferredModelBeforeTheFallbackModel()
    {
        var handler = new Router((_, _) => Raw("empty", HttpStatusCode.TooManyRequests));

        new GeminiVisionReader(
                ["k1", "k2", "k3", "k4", "k5"], ["flash", "flash-lite"],
                new HttpClient(handler), NoWait)
            .TryRead(File.ReadAllBytes(Scan), VisionVendor.RedBull);

        Assert.Equal(
            [
                "k1/flash", "k2/flash", "k3/flash", "k4/flash", "k5/flash",
                "k1/flash-lite", "k2/flash-lite", "k3/flash-lite", "k4/flash-lite",
                "k5/flash-lite",
            ],
            handler.Tried.Take(10));
    }

    /// <summary>A PDF that was never a scan has no picture to read, and nothing is sent.</summary>
    [Fact]
    public void ADocumentWithNoPictureInItIsNeverSent()
    {
        var handler = new Stub(Reply("""{"rows":[]}"""));
        var reader = new GeminiVisionReader("test-key", http: new HttpClient(handler));

        Assert.Empty(reader.TryRead([1, 2, 3], VisionVendor.RedBull).Rows);
        Assert.Empty(reader.TryRead([], VisionVendor.RedBull).Rows);
        Assert.Empty(handler.Sent);
    }

    /// <summary>
    /// One request per page, each carrying that page's picture — and the instruction not to guess,
    /// which is the only thing standing between a smudged figure and a confident wrong answer.
    /// </summary>
    [Fact]
    public void SendsOneRequestPerPageCarryingThePictureAndTheRules()
    {
        var (_, handler) = Read(Reply("""{"rows":[]}"""));

        Assert.Equal(2, handler.Sent.Count);          // this ticket is a delivery page and a credit page
        Assert.All(handler.Sent, body =>
        {
            Assert.Contains("\"inline_data\"", body, StringComparison.Ordinal);
            Assert.Contains("image/png", body, StringComparison.Ordinal);
            Assert.Contains("Never guess", body, StringComparison.Ordinal);
            Assert.Contains("Do not correct, infer, or reconcile", body, StringComparison.Ordinal);
            Assert.Contains("\"temperature\":0", body, StringComparison.Ordinal);
        });

        // The vendor's own column names, so UNITS is not confused with the case count.
        Assert.All(handler.Sent, body =>
            Assert.Contains("individual cans or bottles", body, StringComparison.Ordinal));
    }

    /// <summary>
    /// Each vendor gets its own briefing, because they do not agree about anything that matters: the
    /// last money column is TOTAL on one and AMOUNT on the other, the barcode sits below the row on
    /// one and before a slash on the other, and only one of them comes back covered in pen.
    ///
    /// Each also has to say how its own rows add up, so the model has something to check itself
    /// against, and how that vendor prints a return — the sign is the one thing no arithmetic on the
    /// page can recover.
    /// </summary>
    [Fact]
    public void EachVendorIsToldItsOwnLayoutArithmeticAndReturns()
    {
        string Ask(VisionVendor vendor)
        {
            var h = new Stub(Reply("""{"rows":[]}"""));
            new GeminiVisionReader(["k"], OneModel, new HttpClient(h), NoWait)
                .TryRead(File.ReadAllBytes(Scan), vendor);
            return h.Sent[0];
        }

        string RedBull() => Ask(VisionVendor.RedBull);
        string DrPepper() => Ask(VisionVendor.DrPepper);
        string Bimbo() => Ask(VisionVendor.Bimbo);

        string rb = RedBull(), dp = DrPepper(), bb = Bimbo();
        Assert.NotEqual(rb, dp);
        Assert.NotEqual(rb, bb);
        Assert.NotEqual(dp, bb);

        // Where the figures are, how the row proves itself, and how a return is printed.
        Assert.Contains("QTY x PRICE = TOTAL", rb, StringComparison.Ordinal);
        Assert.Contains("above the bars", rb, StringComparison.Ordinal);
        Assert.Contains("INVOICE or ROA", rb, StringComparison.Ordinal);
        Assert.Contains("isCredit true", rb, StringComparison.Ordinal);

        Assert.Contains("CASES x NET = AMOUNT", dp, StringComparison.Ordinal);
        Assert.Contains("BEFORE the slash", dp, StringComparison.Ordinal);
        Assert.Contains("AMOUNT DUE", dp, StringComparison.Ordinal);
        Assert.Contains("CREDITS banner", dp, StringComparison.Ordinal);
        Assert.Contains("pen stroke is not a digit", dp, StringComparison.Ordinal);

        Assert.Contains("QTY x PRICE = AMOUNT", bb, StringComparison.Ordinal);
        Assert.Contains("TICKET TOTALS", bb, StringComparison.Ordinal);
        Assert.Contains("MINUS SIGN", bb, StringComparison.Ordinal);
        // The one thing about this vendor a model would otherwise get exactly backwards.
        Assert.Contains("no cases on this vendor", bb, StringComparison.Ordinal);

        // None of them is told to make a row work. That instruction is what turns a second reader
        // into a machine for producing plausible wrong answers.
        Assert.All(new[] { rb, dp, bb }, p =>
            Assert.Contains("Never adjust a figure", p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Bimbo does not get the UPC-A instructions, and that is not a detail.
    ///
    /// Red Bull and Dr Pepper print a whole twelve-digit code whose last digit is arithmetic on the
    /// other eleven, so the model is told to count to twelve, work the check digit out, and drop any
    /// code that will not add up. Bimbo prints `74323-09183` — the body, with both the leading zero
    /// and the check digit left off. Told to reach twelve digits and make them sum, a model pads a
    /// ten-digit body into a plausible code, and on this vendor **nothing downstream can catch that**:
    /// the money reconciles either way and SSCS opens a new item under an id belonging to nothing.
    /// </summary>
    [Fact]
    public void OnlyTheVendorsThatPrintACheckDigitAreToldToUseOne()
    {
        string Ask(VisionVendor vendor)
        {
            var h = new Stub(Reply("""{"rows":[]}"""));
            new GeminiVisionReader(["k"], OneModel, new HttpClient(h), NoWait)
                .TryRead(File.ReadAllBytes(Scan), vendor);
            return h.Sent[0];
        }

        string bb = Ask(VisionVendor.Bimbo);
        Assert.DoesNotContain("exactly 12 digits", bb, StringComparison.Ordinal);
        Assert.DoesNotContain("check digit and compare", bb, StringComparison.Ordinal);
        Assert.Contains("There must be exactly ten", bb, StringComparison.Ordinal);
        Assert.Contains("do NOT pad the code", bb, StringComparison.Ordinal);

        Assert.All(new[] { VisionVendor.RedBull, VisionVendor.DrPepper }, v =>
        {
            string p = Ask(v);
            Assert.Contains("It is a UPC-A: exactly 12 digits", p, StringComparison.Ordinal);
            Assert.DoesNotContain("There must be exactly ten", p, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Splitting the barcode rules out of the shared preamble left the two shipped vendors' briefings
    /// **exactly** as they were. Both were tuned against the store's own scans, and a prompt that
    /// drifts while nobody is looking is a reader that quietly starts making different mistakes.
    ///
    /// The seam is what this guards: the common rules, then the barcode block, then the vendor's
    /// columns, joined the way they were when the three were one string.
    /// </summary>
    [Fact]
    public void SplittingTheBarcodeRulesOutLeftTheOtherTwoPromptsUntouched()
    {
        // The prompt itself, out of the request envelope, so the seam is checked as the model sees
        // it rather than as JSON happens to escape it.
        static string Prompt(VisionVendor vendor)
        {
            var h = new Stub(Reply("""{"rows":[]}"""));
            new GeminiVisionReader(["k"], OneModel, new HttpClient(h), NoWait)
                .TryRead(File.ReadAllBytes(Scan), vendor);

            using var doc = System.Text.Json.JsonDocument.Parse(h.Sent[0]);
            string text = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[1]
                .GetProperty("text").GetString()!;

            // The literals carry the source file's CRLFs. Which they are is not the point here; that
            // the prompt uses one kind throughout is, and the assertion below would pass on a mixed
            // one without this line, so it is checked separately.
            Assert.DoesNotContain("\n\n\r", text, StringComparison.Ordinal);
            return text.Replace("\r\n", "\n");
        }

        const string Preamble =
            "read the page as it is meant to\nbe read either way, and report the figures normally.\n\n";

        Assert.All(new[] { VisionVendor.RedBull, VisionVendor.DrPepper }, v =>
            Assert.Contains(
                Preamble + "The barcode, which is the one field that can check itself.",
                Prompt(v), StringComparison.Ordinal));

        Assert.Contains(
            Preamble + "The barcode, which on this vendor cannot check itself.",
            Prompt(VisionVendor.Bimbo), StringComparison.Ordinal);

        // And the vendor's own columns follow the barcode rules, not the other way round.
        Assert.Contains("or report nothing.\nTHIS IS A RED BULL",
            Prompt(VisionVendor.RedBull), StringComparison.Ordinal);
        Assert.Contains("or report nothing.\nTHIS IS A DR PEPPER",
            Prompt(VisionVendor.DrPepper), StringComparison.Ordinal);
        Assert.Contains("or report nothing.\nTHIS IS A BIMBO",
            Prompt(VisionVendor.Bimbo), StringComparison.Ordinal);
    }

    /// <summary>
    /// The reply's fields, per vendor — and the reason this test exists rather than being obvious.
    ///
    /// A schema field whose name matches a **column heading the vendor does not mean by it** silently
    /// steals that column. Bimbo's one count column is headed QTY and holds individual units, which
    /// this contract calls `units`; while a field spelled `qty` was also on offer the model filled
    /// that instead, `units` came back null on every row, and nothing settled. Ticket 84294890013049:
    /// eleven rows, eleven questions, each already carrying the description and amount the model had
    /// read perfectly well. The prompt said which field to use and lost to the schema, which is the
    /// part worth remembering — the schema is what the reply is validated against.
    ///
    /// So Bimbo is offered exactly one count field. Red Bull and Dr Pepper genuinely bill in cases
    /// and keep both, and are pinned here so that fixing Bimbo cannot quietly cost them a column.
    /// </summary>
    [Fact]
    public void OnlyTheVendorsThatBillInCasesAreOfferedACaseCountField()
    {
        static IReadOnlyList<string> Fields(VisionVendor vendor)
        {
            var h = new Stub(Reply("""{"rows":[]}"""));
            new GeminiVisionReader(["k"], OneModel, new HttpClient(h), NoWait)
                .TryRead(File.ReadAllBytes(Scan), vendor);

            using var doc = System.Text.Json.JsonDocument.Parse(h.Sent[0]);
            return [.. doc.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("rows")
                .GetProperty("items").GetProperty("properties")
                .EnumerateObject().Select(p => p.Name)];
        }

        string[] withCases =
            ["itemNumber", "description", "barcode", "qty", "units", "price", "amount", "isCredit"];

        Assert.Equal(withCases, Fields(VisionVendor.RedBull));
        Assert.Equal(withCases, Fields(VisionVendor.DrPepper));

        Assert.Equal(
            ["itemNumber", "description", "barcode", "units", "price", "amount", "isCredit"],
            Fields(VisionVendor.Bimbo));

        // The ordering hint has to agree with the properties, or the model is told to emit a field
        // that is not in the contract.
        static IReadOnlyList<string> Ordering(VisionVendor vendor)
        {
            var h = new Stub(Reply("""{"rows":[]}"""));
            new GeminiVisionReader(["k"], OneModel, new HttpClient(h), NoWait)
                .TryRead(File.ReadAllBytes(Scan), vendor);

            using var doc = System.Text.Json.JsonDocument.Parse(h.Sent[0]);
            return [.. doc.RootElement
                .GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("properties").GetProperty("rows")
                .GetProperty("items").GetProperty("propertyOrdering")
                .EnumerateArray().Select(e => e.GetString()!)];
        }

        Assert.All(Enum.GetValues<VisionVendor>(), v => Assert.Equal(Fields(v), Ordering(v)));
    }

    [Fact]
    public void BimboThreePassesAreIndependentAndCannotCarryHeaderSuggestions()
    {
        const string primaryUpc = "7432309183";
        var handler = new Stub(
            Reply("""
                {"rows":[{"itemNumber":"008788","description":"BIM PANQUE NUE FS FC",
                "barcode":"7432309183","units":"4","price":"3.0100","amount":"12.04",
                "isCredit":false}],"deliveryUnits":"4","deliveryAmount":"12.04",
                "ticketUnits":"4","ticketTotal":"12.04","cashDue":"12.04"}
                """),
            Reply("""{"rows":[{"rowId":"p1-r1","barcode":"7432309183"}]}"""),
            Reply("""{"rows":[{"rowId":"p1-r1","barcode":"7432309183"}]}"""));
        var reader = new GeminiVisionReader(
            ["k"], OneModel, new HttpClient(handler), NoWait);

        byte[] pdf = File.ReadAllBytes(BimboScan);
        var reading = reader.ReadBimbo(pdf);
        int lineNumber = PdfTextExtractor.ExtractPositionedLines(pdf)
            .First(l => l.Text.Contains("008788", StringComparison.Ordinal)).LineNumber;
        BimboBarcodeTarget[] targets =
        [
            new BimboBarcodeTarget(
                "p1-r1", 1, 1, "008788", "BIM PANQUE NUE FS FC", "4", "12.04", lineNumber),
        ];
        var verified = reader.VerifyBimboBarcodes(pdf, targets);
        var isolated = reader.VerifyBimboBarcodeCrops(pdf, targets);

        Assert.Equal(BimboVisionStatus.Success, reading.Status);
        Assert.Equal(BimboVisionStatus.Success, verified.Status);
        Assert.Equal(BimboVisionStatus.Success, isolated.Status);
        Assert.Equal(3, handler.Sent.Count);

        using var first = JsonDocument.Parse(handler.Sent[0]);
        string primaryPrompt = first.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[1].GetProperty("text").GetString()!;
        Assert.Contains("inspect each row twice", primaryPrompt, StringComparison.Ordinal);
        Assert.Contains("Ignore the printed invoice number and date", primaryPrompt, StringComparison.Ordinal);

        using var second = JsonDocument.Parse(handler.Sent[1]);
        string barcodePrompt = second.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[1].GetProperty("text").GetString()!;
        Assert.DoesNotContain(primaryUpc, barcodePrompt, StringComparison.Ordinal);
        Assert.Contains("p1-r1", barcodePrompt, StringComparison.Ordinal);

        using var third = JsonDocument.Parse(handler.Sent[2]);
        var parts = third.RootElement.GetProperty("contents")[0].GetProperty("parts");
        string cropPrompt = parts[0].GetProperty("text").GetString()!;
        Assert.Contains("digit by digit", cropPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(primaryUpc, cropPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("008788", handler.Sent[2], StringComparison.Ordinal);
        Assert.DoesNotContain("PANQUE", handler.Sent[2], StringComparison.Ordinal);
        Assert.DoesNotContain("12.04", handler.Sent[2], StringComparison.Ordinal);

        byte[] cropBytes = Convert.FromBase64String(parts[2]
            .GetProperty("inline_data").GetProperty("data").GetString()!);
        using var crop = new System.Drawing.Bitmap(new MemoryStream(cropBytes));
        Assert.True(crop.Width > crop.Height * 3,
            $"isolated UPC crop was unexpectedly {crop.Width}x{crop.Height}");

        string combined = string.Concat(handler.Sent);
        Assert.DoesNotContain("suggestedInvoice", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suggestedDate", combined, StringComparison.OrdinalIgnoreCase);
    }
}
