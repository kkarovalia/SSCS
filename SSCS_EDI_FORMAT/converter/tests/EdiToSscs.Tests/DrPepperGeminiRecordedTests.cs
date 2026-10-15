using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>Recorded Gemini payloads pin Dr Pepper's request and response contracts.</summary>
public class DrPepperGeminiRecordedTests
{
    private const string Scan = @"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf";
    private const string Recorded = @"Fixtures\Gemini\DRPEPPER_primary_recorded.json";

    private sealed class Stub(params string[] payloads) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<bool> CancellableRequests { get; } = [];

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CancellableRequests.Add(cancellationToken.CanBeCanceled);
            Requests.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            string payload = payloads[Math.Min(Requests.Count - 1, payloads.Length - 1)];
            string envelope = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                              + JsonSerializer.Serialize(payload) + "}]}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class FallbackStub(
        HttpStatusCode unavailableStatus, string successfulPayload) : HttpMessageHandler
    {
        public List<string> Tried { get; } = [];

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.Headers.GetValues("x-goog-api-key").Single();
            string model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            Tried.Add($"{key}/{model}");
            if (model == "gemini-3.7-flash")
                return new HttpResponseMessage(unavailableStatus)
                {
                    Content = new StringContent("overloaded", Encoding.UTF8, "text/plain"),
                };

            string envelope = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                              + JsonSerializer.Serialize(successfulPayload) + "}]}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class TimeoutThenFallbackStub(string successfulPayload) : HttpMessageHandler
    {
        public List<string> Tried { get; } = [];

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.Headers.GetValues("x-goog-api-key").Single();
            string model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            Tried.Add($"{key}/{model}");
            if (model == "gemini-3.7-flash") throw new TaskCanceledException("simulated timeout");

            string envelope = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                              + JsonSerializer.Serialize(successfulPayload) + "}]}}]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>Answers per attempt number, key and model, and records the order they were tried in.</summary>
    private sealed class ProgrammableStub(
        Func<int, string, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<string> _tried = [];
        private readonly object _gate = new();

        /// <summary>Locked: Dr Pepper runs its two barcode passes at the same time.</summary>
        public IReadOnlyList<string> Tried
        {
            get { lock (_gate) return _tried.ToArray(); }
        }

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.Headers.GetValues("x-goog-api-key").Single();
            string model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            int number;
            lock (_gate)
            {
                _tried.Add($"{key}/{model}");
                number = _tried.Count;
            }
            return respond(number, key, model);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static HttpResponseMessage Unavailable(HttpStatusCode status) => new(status);

    /// <summary>
    /// A 429 shaped the way Google actually sends one, naming which quota ran out.
    ///
    /// Both limits arrive under the same status code and want opposite handling, so the body is the
    /// only thing that separates them. `retryDelay` is what Google says about when the window
    /// reopens.
    /// </summary>
    private static HttpResponseMessage Throttled(string quotaId, string? retryDelay = null)
    {
        var details = new List<object>
        {
            new
            {
                type = "type.googleapis.com/google.rpc.QuotaFailure",
                violations = new[] { new { quotaId } },
            },
        };
        if (retryDelay is not null)
            details.Add(new { type = "type.googleapis.com/google.rpc.RetryInfo", retryDelay });

        string body = JsonSerializer.Serialize(new { error = new { code = 429, details } })
            .Replace("\"type\":", "\"@type\":", StringComparison.Ordinal);
        return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Answered(string payload)
    {
        string envelope = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":"
                          + JsonSerializer.Serialize(payload) + "}]}}]}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>A backoff that would be impossible to miss if the reader ever slept on this path.</summary>
    private static readonly TimeSpan[] UnusedBackoff =
        [TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50)];

    /// <summary>
    /// Retry a busy model immediately rather than on the real two-minute schedule. The retrying
    /// itself is the behaviour under test; the length of the waits is not.
    /// </summary>
    private static readonly TimeSpan[] NoBusyWaits = [TimeSpan.Zero, TimeSpan.Zero];

    [Fact]
    public void RecordedPrimaryResponseKeepsStringsAndMapsStableLocalRowIds()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Stub(recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        var row = Assert.Single(reading.Rows);
        Assert.Equal("p1-sales-r1-o1", row.RowId);
        Assert.Equal("12", row.Units);
        Assert.Equal("20.66", row.Amount);
        Assert.Equal("20.66", reading.AmountDue);
        Assert.Equal(row.RowId, Assert.Single(reading.PackageSubtotals).RowIds.Single());
        Assert.Single(handler.Requests); // all pages are one primary invoice read
    }

    [Fact]
    public void ProductionInvoiceClientHasNoTimeout()
    {
        var reader = new GeminiVisionReader("test-key");
        var field = typeof(GeminiVisionReader).GetField(
            "_http", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var http = Assert.IsType<HttpClient>(field!.GetValue(reader));

        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Fact]
    public void AskableNullFieldsDoNotRejectTheWholeAiInvoice()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8)
            .Replace("\"barcode\": \"078000200287\"", "\"barcode\": null", StringComparison.Ordinal)
            .Replace("\"barcodeLeft\": \"520\"", "\"barcodeLeft\": null", StringComparison.Ordinal)
            .Replace("\"barcodeTop\": \"310\"", "\"barcodeTop\": null", StringComparison.Ordinal)
            .Replace("\"barcodeWidth\": \"240\"", "\"barcodeWidth\": null", StringComparison.Ordinal)
            .Replace("\"barcodeHeight\": \"35\"", "\"barcodeHeight\": null", StringComparison.Ordinal);
        recorded = new System.Text.RegularExpressions.Regex("\"units\": \"12\"")
            .Replace(recorded, "\"units\": null", 1);
        recorded = new System.Text.RegularExpressions.Regex("\"amount\": \"20\\.66\"")
            .Replace(recorded, "\"amount\": null", 1);
        var handler = new Stub(recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        var row = Assert.Single(reading.Rows);
        Assert.Null(row.Barcode);
        Assert.Null(row.Units);
        Assert.Null(row.Amount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void SalesOnlyInvoiceDoesNotRequireANonexistentCreditSummary()
    {
        var payload = JsonNode.Parse(File.ReadAllText(Recorded, Encoding.UTF8))!.AsObject();
        payload.Remove("creditCases");
        payload.Remove("creditUnits");
        payload.Remove("creditAmount");
        var handler = new Stub(payload.ToJsonString());
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Null(reading.Summary!.CreditCases);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A missing printed credit summary means one check cannot be run. It does not mean the
    /// invoice is unreadable — that reading is thrown away twice and the operator is sent back to
    /// the scanner for a block of figures the file never needed.
    /// </summary>
    [Fact]
    public void InvoiceWithCreditRowsConvertsWithoutAPrintedCreditSummary()
    {
        var payload = JsonNode.Parse(File.ReadAllText(Recorded, Encoding.UTF8))!.AsObject();
        var row = payload["rows"]!.AsArray()[0]!.AsObject();
        row["section"] = "CREDITS";
        row["isCredit"] = true;
        payload.Remove("creditCases");
        payload.Remove("creditUnits");
        payload.Remove("creditAmount");
        var handler = new Stub(payload.ToJsonString());
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Single(handler.Requests);
        Assert.True(Assert.Single(reading.Rows).IsCredit);
    }

    /// <summary>
    /// A Dr Pepper ticket carrying returns prints a SALES SUMMARY block **instead of** a TOT SALE
    /// line, so `totalSale*` comes back empty and the model is right to leave it so.
    ///
    /// Requiring it refused a flawless reading of the store's 49-row ticket on 2026-08-13 — fifty
    /// rows, thirteen package subtotals, AMOUNT DUE to the cent — twice over, and then asked for a
    /// rescan of a page that had been read perfectly.
    /// </summary>
    [Fact]
    public void ATicketPrintingASalesSummaryInsteadOfTotSaleIsNotIncomplete()
    {
        var payload = JsonNode.Parse(File.ReadAllText(Recorded, Encoding.UTF8))!.AsObject();
        payload.Remove("totalSaleCases");
        payload.Remove("totalSaleUnits");
        payload.Remove("totalSaleAmount");
        var handler = new Stub(payload.ToJsonString());
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Single(handler.Requests);
        Assert.Equal("12", Assert.Single(reading.Rows).Units);
        Assert.Equal("12", reading.Summary!.SalesUnits);
    }

    /// <summary>
    /// A reply with no AMOUNT DUE is the one still treated as incomplete. It is the figure that
    /// makes a half-read page visible: without it nothing can notice that the lines do not come to
    /// what the invoice is billing.
    /// </summary>
    [Fact]
    public void AReplyWithoutAmountDueIsStillIncomplete()
    {
        var payload = JsonNode.Parse(File.ReadAllText(Recorded, Encoding.UTF8))!.AsObject();
        payload.Remove("amountDue");
        var handler = new Stub(payload.ToJsonString());
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Unreadable, reading.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// An exhausted allowance drops to the next rung at once. There is nothing to wait for: the
    /// free quota does not come back before tomorrow.
    /// </summary>
    [Fact]
    public void DrPepperImmediatelyFallsBackWhenTheTopModelsAllowanceIsGone()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new FallbackStub(HttpStatusCode.TooManyRequests, recorded);
        // Real busy waits: if a 429 ever earned a retry this would take seconds and show up here.
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), []);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            [
                "key-1/gemini-3.7-flash",
                "key-2/gemini-3.7-flash",
                "key-1/gemini-3.6-flash",
            ],
            handler.Tried);
    }

    /// <summary>
    /// A busy model is asked again on the same key, and then not on the other keys at all.
    ///
    /// The rung worth pressing is the best one the ladder has reached, because a blip that lasts
    /// seconds should not decide that an invoice gets read by a weaker model. But "this model is
    /// currently experiencing high demand" is about the model: the second key gets the same answer,
    /// and every busy reply counts against the day's 20. On 2026-10-05 the dashboard showed
    /// gemini-3.7-flash 6 used for two conversions it never answered, which is what this stopped.
    /// </summary>
    [Fact]
    public void ABusyModelIsPressedOnOneKeyAndSkippedOnTheOthers()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new FallbackStub(HttpStatusCode.ServiceUnavailable, recorded);
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), [], NoBusyWaits);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            [
                "key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash",
                "key-1/gemini-3.6-flash",
            ],
            handler.Tried);
    }

    /// <summary>
    /// The store's own busy-model retry is one more go, not two. A second quick retry mostly spent
    /// a request out of the day's 20: Google's "spikes in demand are usually temporary" is minutes,
    /// not the seconds these waits cover.
    /// </summary>
    [Fact]
    public void ABusyModelGetsOneMoreGoInProduction()
    {
        var waits = (TimeSpan[])typeof(GeminiVisionReader)
            .GetField("BusyModelWaits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        var wait = Assert.Single(waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void DrPepperTimeoutDoesNotPreventTheNextModelFromReadingTheInvoice()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new TimeoutThenFallbackStub(recorded);
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), []);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            [
                "key-1/gemini-3.7-flash",
                "key-2/gemini-3.7-flash",
                "key-1/gemini-3.6-flash",
            ],
            handler.Tried);
    }

    /// <summary>
    /// An exhausted allowance is one sweep, in order, and then it stops — and it is never waited on.
    ///
    /// This replaces a test that pinned the opposite. Until 2026-08-13 an all-unavailable ladder ran
    /// **three** complete sweeps with a 20-second and then a 50-second sleep between them, so one
    /// Dr Pepper invoice could sit at "Converting..." for minutes on end and the store's 12:38 run
    /// did. Nothing is learned by asking a dead ladder a second time, and the operator is standing
    /// at the counter while it happens.
    ///
    /// Deliberately built with the **real** busy-model waits, so if a 429 ever reached the retry
    /// path this would take two minutes and fail rather than passing quietly.
    /// </summary>
    [Fact]
    public void AnExhaustedAllowanceIsOneSweepWithNoSleep()
    {
        var handler = new ProgrammableStub((_, _, _) => Unavailable(HttpStatusCode.TooManyRequests));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.Equal(
            [
                "key-1/gemini-3.7-flash", "key-2/gemini-3.7-flash",
                "key-1/gemini-3.6-flash", "key-2/gemini-3.6-flash",
                "key-1/gemini-3.5-flash", "key-2/gemini-3.5-flash",
            ],
            handler.Tried);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"slept for {clock.Elapsed}");
    }

    /// <summary>
    /// A per-minute 429 is waited out on the rung it happened on, exactly like a busy model.
    ///
    /// This is the fault that pushed nine invoices in a row off gemini-3.7-flash on 2026-08-14
    /// while the store's dashboard showed 6 of 20 requests used for the whole day. Nothing was
    /// exhausted: a refused key and a throttled key both answer instantly, so the ladder emptied
    /// five or six requests into one second and tripped the five-per-minute limit itself. Treating
    /// that as "gone until tomorrow" spent the invoice on a worse model, which is where bad
    /// barcodes come from.
    /// </summary>
    [Fact]
    public void AKeyThatSentTooManyRequestsThisMinuteIsAskedAgainOnTheSameModel()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new ProgrammableStub((number, _, _) => number <= 2
            ? Throttled("GenerateRequestsPerMinutePerProjectPerModel-FreeTier")
            : Answered(recorded));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        // Asked the *same* rung again rather than dropping a model over a limit that clears itself.
        Assert.Equal(
            ["key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash"],
            handler.Tried);
    }

    /// <summary>
    /// The per-day allowance keeps its old handling: one sweep, no sleeping. It does not come back
    /// before tomorrow, and waiting on it is what used to leave an invoice at "Converting..." for
    /// minutes. Built with the real waits so a sleep here would fail loudly.
    /// </summary>
    [Fact]
    public void AnExhaustedDailyAllowanceIsStillNeverWaitedOn()
    {
        var handler = new ProgrammableStub((_, _, _) =>
            Throttled("GenerateRequestsPerDayPerProjectPerModel-FreeTier", "38s"));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.Equal(6, handler.Tried.Count);
        Assert.Equal(handler.Tried.Distinct(), handler.Tried);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"slept for {clock.Elapsed}");
    }

    /// <summary>
    /// A 429 that does not say which quota it is counts as the daily one. Guessing the other way
    /// puts the operator in front of a window that says "Converting..." for minutes on an allowance
    /// that will not return today; guessing this way costs one invoice a worse model.
    /// </summary>
    [Fact]
    public void AnUnexplained429IsTreatedAsTheDailyAllowance()
    {
        var handler = new ProgrammableStub((_, _, _) =>
            Unavailable(HttpStatusCode.TooManyRequests));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.Equal(6, handler.Tried.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"slept for {clock.Elapsed}");
    }

    /// <summary>
    /// A busy model is asked again instead of refusing a readable invoice.
    ///
    /// 503 is Google's own server, not the store's allowance, and it clears on its own — so unlike
    /// a 429 it is worth waiting out. On 2026-08-13 at 17:20:33 both keys got a 503 on the top model
    /// while the two rungs below were out of allowance for the day, and a perfectly readable invoice
    /// was refused outright. The same file went through in a single request two minutes later.
    /// </summary>
    [Fact]
    public void ABusyModelIsAskedAgainInsteadOfRefusingTheInvoice()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        // Busy right across the first sweep, then Google's side frees up.
        var handler = new ProgrammableStub((number, _, _) => number <= 6
            ? Unavailable(HttpStatusCode.ServiceUnavailable)
            : Answered(recorded));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.True(handler.Tried.Count > 6, "the busy rungs were never asked again");
    }

    /// <summary>
    /// Waiting it out is bounded. The app has no Stop button, so a retry loop that never ends could
    /// only be escaped by killing the converter — and a queue that cannot be stopped is worse than
    /// a file that has to be dropped in again.
    /// </summary>
    [Fact]
    public void AModelThatStaysBusyGivesUpRatherThanLoopingForever()
    {
        var handler = new ProgrammableStub(
            (_, _, _) => Unavailable(HttpStatusCode.ServiceUnavailable));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        // Unavailable, not Unreadable: nothing ever answered, so the legacy reader may still be
        // offered. Each model once plus its retries, on the first key only - a busy model is busy
        // for every key.
        Assert.Equal(DrPepperVisionStatus.Unavailable, reading.Status);
        Assert.Equal(
            GeminiVisionReader.DefaultModels.Length * (1 + NoBusyWaits.Length), handler.Tried.Count);
        Assert.DoesNotContain(handler.Tried, tried => tried.StartsWith("key-2/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The store asked for this on 2026-10-06, after three Dr Pepper attempts in four minutes were
    /// each given up after one round of 503s on every model: when Google is busy, wait and ask
    /// again. A later round asks each model once - no quick retry, since every busy reply is one of
    /// the day's 20 - and still never on the other key.
    /// </summary>
    [Fact]
    public void ABusyGoogleIsAskedAgainUntilItAnswers()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new ProgrammableStub((number, _, _) => number <= 12
            ? Unavailable(HttpStatusCode.ServiceUnavailable)
            : Answered(recorded));
        var progress = new List<string>();
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits,
            patience: TimeSpan.FromMinutes(1), roundWaits: [TimeSpan.Zero])
        {
            Progress = message => { lock (progress) progress.Add(message); },
        };

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        // Round one: each model three times (two quick retries). Round two: each model once. Round
        // three: the first model answers.
        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            [
                "key-1/gemini-3.7-flash", "key-1/gemini-3.6-flash", "key-1/gemini-3.5-flash",
                "key-1/gemini-3.7-flash",
            ],
            handler.Tried.Skip(9));
        Assert.DoesNotContain(handler.Tried, tried => tried.StartsWith("key-2/", StringComparison.Ordinal));
        Assert.Contains(progress, message => message.Contains("keeps trying until", StringComparison.Ordinal));
        Assert.Contains("2 more rounds while Google was busy", reading.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// Patience is not forever. When it runs out the read is unavailable exactly as before, so the
    /// operator is offered the legacy reader rather than left at "Converting...".
    /// </summary>
    [Fact]
    public void APatientReadStillGivesUpWhenItsPatienceRunsOut()
    {
        var handler = new ProgrammableStub((_, _, _) => Unavailable(HttpStatusCode.ServiceUnavailable));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits,
            patience: TimeSpan.FromSeconds(1), roundWaits: [TimeSpan.FromMilliseconds(300)]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        int firstRound = GeminiVisionReader.DefaultModels.Length * (1 + NoBusyWaits.Length);
        Assert.Equal(DrPepperVisionStatus.Unavailable, reading.Status);
        Assert.True(handler.Tried.Count > firstRound, $"only {handler.Tried.Count} requests");
        Assert.Equal(0, (handler.Tried.Count - firstRound) % GeminiVisionReader.DefaultModels.Length);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.DoesNotContain(handler.Tried, tried => tried.StartsWith("key-2/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only "busy" is waited on. An allowance used up for the day is still one sweep and done, even
    /// with all the patience in the world - it does not come back this afternoon.
    /// </summary>
    [Fact]
    public void AnExhaustedAllowanceIsNotWaitedOnEvenByAPatientRead()
    {
        var handler = new ProgrammableStub((_, _, _) => Unavailable(HttpStatusCode.TooManyRequests));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits,
            patience: TimeSpan.FromMinutes(15), roundWaits: [TimeSpan.FromMinutes(1)]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.Equal(DrPepperVisionStatus.Unavailable, reading.Status);
        Assert.Equal(6, handler.Tried.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"slept for {clock.Elapsed}");
    }

    /// <summary>
    /// The production schedule: minutes of patience, and pauses of at least half a minute between
    /// rounds, so a busy morning cannot burn the day's 20 per model on one invoice.
    /// </summary>
    [Fact]
    public void TheAppWaitsMinutesForABusyGoogleAndPacesItsRounds()
    {
        Assert.InRange(GeminiVisionReader.BusyPatience, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

        var waits = (TimeSpan[])typeof(GeminiVisionReader)
            .GetField("BusyRoundWaits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        Assert.NotEmpty(waits);
        Assert.All(waits, wait => Assert.True(wait >= TimeSpan.FromSeconds(30), $"{wait} between rounds"));
    }

    /// <summary>
    /// Nothing answered at all means AI was unavailable, which is the only state that may offer the
    /// legacy OCR reader. This is the whole of that distinction, so it is pinned on its own.
    /// </summary>
    [Fact]
    public void NoCombinationAnsweringAtAllIsReportedAsAiUnavailable()
    {
        var handler = new ProgrammableStub((_, _, _) => Unavailable(HttpStatusCode.ServiceUnavailable));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Unavailable, reading.Status);
    }

    /// <summary>
    /// An HTTP 200 that will not validate is the service answering badly, not the service being
    /// down. It buys exactly one more request — at the combination that proved it can reply — and
    /// the file is then a rescan, never a silent OCR read.
    /// </summary>
    [Fact]
    public void AnIncompleteReplyBuysOneFreshAttemptAndNotAnotherSweep()
    {
        var handler = new ProgrammableStub((_, _, model) => model == "gemini-3.5-flash"
            ? Answered("{\"rows\":[]}")
            : Unavailable(HttpStatusCode.TooManyRequests));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Unreadable, reading.Status);
        Assert.Equal(GeminiVisionReader.DefaultModels.Length * 2 + 1, handler.Tried.Count);
        // The one fresh attempt goes back to the first combination that answered, not to the top.
        Assert.Equal("key-1/gemini-3.5-flash", handler.Tried[^1]);
    }

    [Fact]
    public void TheFreshAttemptIsUsedWhenItReturnsACompleteInvoice()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new ProgrammableStub((number, _, model) => model switch
        {
            "gemini-3.5-flash" when number <= GeminiVisionReader.DefaultModels.Length * 2
                => Answered("{\"rows\":[]}"),
            "gemini-3.5-flash" => Answered(recorded),
            _ => Unavailable(HttpStatusCode.TooManyRequests),
        });
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);

        DrPepperVisionReading reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(GeminiVisionReader.DefaultModels.Length * 2 + 1, handler.Tried.Count);
    }

    /// <summary>
    /// Every failure kind reaches the log as itself.
    ///
    /// The 12:38 run logged "8 throttled" for six 503s and two 429s. Those are different faults
    /// wanting opposite responses — a 429 comes back tomorrow, a 503 in a minute — and a network
    /// error is neither, so calling all of them throttling sends whoever reads the log to the wrong
    /// fix.
    /// </summary>
    [Fact]
    public void TheLogSaysWhichFailureEachAttemptActuallyWas()
    {
        // Keyed by rung rather than by attempt number, so the busy one answers the same way when
        // it is asked again. Key 2 is never asked about the busy model, so its faults sit lower.
        var handler = new ProgrammableStub((_, key, model) => (key, model) switch
        {
            ("key-1", "gemini-3.7-flash") => Unavailable(HttpStatusCode.ServiceUnavailable),
            ("key-1", "gemini-3.6-flash") => Unavailable(HttpStatusCode.TooManyRequests),
            ("key-2", "gemini-3.6-flash") => Unavailable(HttpStatusCode.Forbidden),
            _ => throw new HttpRequestException("the line dropped"),
        });
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits);

        string note = reader.ReadDrPepper(File.ReadAllBytes(Scan)).Note;

        // A bare 429 says nothing about which quota it was, so it reads as the daily allowance.
        Assert.Contains("1 out of free allowance (HTTP 429 per-day)", note, StringComparison.Ordinal);
        // One in the opening sweep, then once per retry round - it stayed busy every time.
        Assert.Contains($"{1 + NoBusyWaits.Length} model busy (HTTP 5xx)", note, StringComparison.Ordinal);
        Assert.Contains("1 refused (other HTTP error)", note, StringComparison.Ordinal);
        Assert.Contains("2 could not reach Google", note, StringComparison.Ordinal);
        Assert.Contains("key 1/gemini-3.6-flash: HTTP 429", note, StringComparison.Ordinal);
        Assert.Contains("key 1/gemini-3.7-flash: HTTP 503", note, StringComparison.Ordinal);
        Assert.DoesNotContain("key 2/gemini-3.7-flash", note, StringComparison.Ordinal);
        Assert.DoesNotContain("throttled", note, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The queue is told which attempt is outstanding, and told nothing the app cannot know. Google
    /// may only be said to be answering while its reply is actually arriving; a request that has
    /// sent nothing back is "no reply yet", never "processing".
    /// </summary>
    [Fact]
    public void ProgressNamesTheAttemptAndNeverClaimsGoogleIsWorking()
    {
        var said = new List<string>();
        var handler = new ProgrammableStub((_, _, _) => Unavailable(HttpStatusCode.ServiceUnavailable));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff, NoBusyWaits)
        {
            Progress = said.Add,
        };

        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Contains(said, line => line.Contains("try 1 of 6 sent", StringComparison.Ordinal));
        Assert.Contains(said, line => line.Contains("No reply yet", StringComparison.Ordinal));
        Assert.Contains(said, line => line.Contains("Google's AI is busy", StringComparison.Ordinal));
        Assert.All(said, line => Assert.DoesNotContain(
            "processing", line, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Every request asks for enough room to answer in.
    ///
    /// Leaving this unset is not "no limit", it is the model's own default — and on a thinking model
    /// the reasoning comes out of the same budget before the answer starts. A full Dr Pepper page is
    /// nineteen rows of twenty fields plus eleven subtotals, so the reply got cut off mid-JSON and
    /// arrived as a 200 with nothing usable in it. That was indistinguishable from the model simply
    /// being bad at the job, and it is why `gemini-3.6-flash` spent 35 to 78 seconds a go on
    /// 2026-08-13 and returned nothing three runs running.
    /// </summary>
    [Fact]
    public void EveryRequestAsksForEnoughRoomToAnswerIn()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Stub(recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.NotEmpty(handler.Requests);
        foreach (string body in handler.Requests)
        {
            using var request = JsonDocument.Parse(body);
            var config = request.RootElement.GetProperty("generationConfig");
            Assert.True(config.TryGetProperty("maxOutputTokens", out var room),
                "every request must say how much room the reply has");
            Assert.True(room.GetInt32() >= 32768, $"only {room.GetInt32()} tokens of room");
        }
    }

    /// <summary>
    /// A reply Google cut short says so in the log, instead of looking like a bad answer. They have
    /// opposite fixes: one is ours to give more room, the other is the model's to do better.
    /// </summary>
    [Fact]
    public void AReplyCutShortIsLoggedAsCutShortAndNotAsABadAnswer()
    {
        var handler = new ProgrammableStub((_, _, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"role":"model"}}]}""",
                Encoding.UTF8, "application/json"),
        });
        var reader = new GeminiVisionReader(
            ["key-1"], ["gemini-test"], new HttpClient(handler), UnusedBackoff);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Unreadable, reading.Status);
        Assert.Contains("cut short", reading.Note, StringComparison.Ordinal);
        Assert.Contains("ran out of output room", reading.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimarySchemaCannotReturnInvoiceNumberOrDate()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Stub(recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        var properties = request.RootElement.GetProperty("generationConfig")
            .GetProperty("responseSchema").GetProperty("properties");
        Assert.False(properties.TryGetProperty("invoiceNumber", out _));
        Assert.False(properties.TryGetProperty("date", out _));
        Assert.False(properties.TryGetProperty("invoiceDate", out _));
    }

    [Fact]
    public void PrimarySchemaRequiresEveryInvoiceLevelCrossCheck()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Stub(recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        _ = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        var schema = request.RootElement.GetProperty("generationConfig")
            .GetProperty("responseSchema");
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[]
                 {
                     "rows", "packageSubtotals", "totalSaleCases", "totalSaleUnits",
                     "totalSaleAmount", "salesCases", "salesUnits", "salesAmount",
                     "creditCases", "creditUnits", "creditAmount", "amountDue",
                 })
            Assert.Contains(name, required);

        var rowRequired = schema.GetProperty("properties").GetProperty("rows")
            .GetProperty("items").GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("cases", rowRequired);
        Assert.Contains("net", rowRequired);
        Assert.Contains("tax", rowRequired);
        Assert.DoesNotContain("barcode", rowRequired);
        Assert.DoesNotContain("units", rowRequired);
        Assert.DoesNotContain("amount", rowRequired);
    }

    [Fact]
    public void IncompleteSuccessfulResponseGetsOneFreshRetry()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Stub("{\"rows\":[]}", recorded);
        var reader = new GeminiVisionReader(
            ["test-key"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class EndToEndPrompt : IInvoiceDetailsPrompt
    {
        public List<InvoiceDetailsRequest> Details { get; } = [];
        public List<LineFieldRequest> LineFields { get; } = [];
        public int FallbackQuestions { get; private set; }

        public InvoiceDetails? AskInvoiceDetails(InvoiceDetailsRequest request)
        {
            Details.Add(request);
            return new InvoiceDetails("4643519283", new DateOnly(2026, 8, 13));
        }

        public string? AskLineField(LineFieldRequest request)
        {
            LineFields.Add(request);
            return null;
        }

        public bool ConfirmDrPepperOcrFallback(string fileName)
        {
            FallbackQuestions++;
            return false;
        }
    }

    /// <summary>
    /// The whole conversion, through the real reader: primary read, both barcode passes, the
    /// parser's own checks, the operator prompt and the XML on disk.
    ///
    /// Reading the invoice on its own proves the request contract and nothing else. The three passes
    /// run against one reader — two of them concurrently — feed each other row ids and geometry, and
    /// only the finished file shows whether the barcode the model read three times reached SSCS.
    /// </summary>
    [Fact]
    public void TheCompleteConversionIsOneRequestAndWritesTheInvoice()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new ProgrammableStub((_, _, _) => Answered(recorded));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels,
            new HttpClient(handler), UnusedBackoff);
        var prompt = new EndToEndPrompt();
        using var temp = new TempDir();
        var service = new ConversionService(
            new ConversionLogger(Path.Combine(temp.Path, "logs")), "test", prompt, reader);
        service.BeginBatch();
        string input = Path.Combine(temp.Path, "drpepper.pdf");
        File.Copy(Scan, input);

        var result = service.ConvertFile(new QueuedFile
        {
            InputPath = input, Kind = ConversionKind.Pdf, Vendor = PdfVendor.DrPepper,
        }, temp.Sub("out"));

        Assert.True(result.Success, result.UserMessage);
        Assert.Equal(0, prompt.FallbackQuestions);
        Assert.Empty(prompt.LineFields);      // Dr Pepper never asks about a line any more
        Assert.Single(handler.Tried);         // and it costs exactly one request

        var details = Assert.Single(prompt.Details);
        Assert.Equal(20.66m, details.Total);
        Assert.False(details.NeedsTotal);

        var xml = System.Xml.Linq.XDocument.Load(Assert.Single(result.FinalOutputPaths));
        Assert.Equal("4643519283", xml.Descendants("InvoiceNumber").Single().Value);
        Assert.Equal("078000200287", Assert.Single(xml.Descendants("InvoiceUnitId")).Value);
        Assert.Equal("12", Assert.Single(xml.Descendants("InvoiceUnitQty")).Value);
    }

}
