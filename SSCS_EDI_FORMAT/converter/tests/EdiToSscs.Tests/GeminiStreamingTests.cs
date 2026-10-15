using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// Invoice reads stream, so a read Google is working on can be told apart from one it has stopped
/// working on. Pinned on 2026-10-05, the night two Dr Pepper conversions sat at "Converting..." for
/// over five minutes each: 3.7 and 3.8 Flash were answering "high demand", 3.6 Flash closed its
/// stream four seconds in with no answer, and the one-shot request of the day could not tell any of
/// that from a slow read.
/// </summary>
public class GeminiStreamingTests
{
    private const string Scan = @"Fixtures\PDF\DRPEPPER_4643519283_with_credits.pdf";
    private const string Recorded = @"Fixtures\Gemini\DRPEPPER_primary_recorded.json";

    /// <summary>One retry of a busy model, as in production, without the real wait.</summary>
    private static readonly TimeSpan[] NoBusyWaits = [TimeSpan.Zero];

    /// <summary>Answers by key and model, and records what was asked.</summary>
    private sealed class Router(Func<string, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Tried { get; } = [];
        public List<string> Urls { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.Headers.GetValues("x-goog-api-key").Single();
            string model = request.RequestUri!.AbsolutePath.Split('/')[^1].Split(':')[0];
            Tried.Add($"{key}/{model}");
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return respond(key, model);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    /// <summary>
    /// A reply body that arrives on a timetable: each piece after its pause, and then - if told
    /// to - nothing at all, for as long as the reader cares to wait.
    /// </summary>
    private sealed class Timetable(IEnumerable<(TimeSpan Pause, byte[] Bytes)> pieces, bool thenNothing)
        : Stream
    {
        private readonly Queue<(TimeSpan Pause, byte[] Bytes)> _pieces = new(pieces);
        private byte[] _current = [];
        private int _at;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_at >= _current.Length)
            {
                if (_pieces.Count == 0)
                {
                    if (thenNothing) await Task.Delay(Timeout.Infinite, cancellationToken);
                    return 0;
                }
                var (pause, bytes) = _pieces.Dequeue();
                // Slept on the reading thread rather than awaited, so a busy test run cannot
                // stretch a pause: the only thing left on the thread pool is the reader's own
                // silence clock, and that running late can only make it more patient.
                if (pause > TimeSpan.Zero) Thread.Sleep(pause);
                cancellationToken.ThrowIfCancellationRequested();
                _current = bytes;
                _at = 0;
            }
            int count = Math.Min(buffer.Length, _current.Length - _at);
            _current.AsMemory(_at, count).CopyTo(buffer);
            _at += count;
            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Events(params object[] events) =>
        Events(new MemoryStream(Sse(events)));

    private static HttpResponseMessage Events(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Server-sent events the way Google sends them: one JSON object per data line.</summary>
    private static byte[] Sse(IEnumerable<object> events) => Encoding.UTF8.GetBytes(string.Concat(
        events.Select(e => "data: " + JsonSerializer.Serialize(e) + "\r\n\r\n")));

    private static object Thought(string text) => new
    {
        candidates = new[] { new { content = new { parts = new[] { new { text, thought = true } }, role = "model" } } },
    };

    private static object Text(string text) => new
    {
        candidates = new[] { new { content = new { parts = new[] { new { text } }, role = "model" } } },
    };

    private static object Finish(string reason = "STOP") => new
    {
        candidates = new[] { new { content = new { parts = new[] { new { text = "" } }, role = "model" }, finishReason = reason } },
    };

    /// <summary>
    /// The recorded answer streamed the way Google streams it: thinking first - including a summary
    /// that looks like JSON, which must never be mistaken for the answer - then the answer in pieces.
    /// </summary>
    private static object[] StreamedAnswer(string answer)
    {
        int third = answer.Length / 3;
        return
        [
            Thought("**Reading the invoice**\n\n{\"rows\": [], \"amountDue\": \"0.00\"}"),
            Thought("**Checking the totals**"),
            Text(answer[..third]),
            Text(answer[third..(2 * third)]),
            Text(answer[(2 * third)..]),
            Finish(),
        ];
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

    [Fact]
    public void AStreamedReplyReadsExactlyLikeTheOneShotReply()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var oneShot = new GeminiVisionReader(
                ["key-1"], ["gemini-test"], new HttpClient(new Router((_, _) => Answered(recorded))), [])
            .ReadDrPepper(File.ReadAllBytes(Scan));
        var said = new List<string>();
        var handler = new Router((_, _) => Events(StreamedAnswer(recorded)));
        var reader = new GeminiVisionReader(["key-1"], ["gemini-test"], new HttpClient(handler), [])
        {
            Progress = said.Add,
        };

        var streamed = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, oneShot.Status);
        Assert.Equal(DrPepperVisionStatus.Success, streamed.Status);
        Assert.Equal(JsonSerializer.Serialize(oneShot.Rows), JsonSerializer.Serialize(streamed.Rows));
        Assert.Equal(JsonSerializer.Serialize(oneShot.Summary), JsonSerializer.Serialize(streamed.Summary));
        Assert.Equal(oneShot.AmountDue, streamed.AmountDue);
        Assert.Single(handler.Tried);
        // Said only because the reply was actually arriving.
        Assert.Contains(said, line => line.Contains("Google is answering", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryVendorsReadStreamsAndAsksForThoughtSummaries()
    {
        var handler = new Router((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var reader = new GeminiVisionReader(
            ["key-1"], ["gemini-test"], new HttpClient(handler), [], NoBusyWaits);
        byte[] pdf = File.ReadAllBytes(Scan);

        _ = reader.ReadDrPepper(pdf);
        _ = reader.ReadBimbo(pdf);
        _ = reader.ReadRedBull(pdf);
        _ = reader.ReadBlueBell(pdf);
        _ = reader.ReadChubiwinos(pdf);
        _ = reader.TryRead(pdf, VisionVendor.RedBull);

        Assert.True(handler.Bodies.Count >= 6, $"only {handler.Bodies.Count} requests were made");
        Assert.All(handler.Urls, url =>
            Assert.Contains(":streamGenerateContent?alt=sse", url, StringComparison.Ordinal));
        Assert.All(handler.Bodies, body =>
        {
            var config = JsonNode.Parse(body)!["generationConfig"]!;
            Assert.True(config["thinkingConfig"]!["includeThoughts"]!.GetValue<bool>());
            // Nothing else about the request moved.
            Assert.Equal(0, config["temperature"]!.GetValue<int>());
            Assert.Equal("application/json", config["responseMimeType"]!.GetValue<string>());
        });
    }

    [Fact]
    public void AStreamGoogleCutsOffIsABusyModelAndTheNextModelIsAsked()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Router((_, model) => model == "gemini-3.7-flash"
            ? Events(Thought("**Initiating Invoice Analysis**"))   // and then the stream just ends
            : Events(StreamedAnswer(recorded)));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels, new HttpClient(handler), [],
            NoBusyWaits);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            ["key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash", "key-1/gemini-3.6-flash"],
            handler.Tried);
        Assert.Contains("cut off by Google", reading.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream that was cut off never reached an answer, so it is Google's side and never a
    /// verdict on the scan: the operator may still be offered the other reader, not told to rescan.
    /// </summary>
    [Fact]
    public void ReadsGoogleCutsOffAreUnavailableNotUnreadable()
    {
        var handler = new Router((_, _) => Events(Thought("**Initiating Invoice Analysis**")));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels, new HttpClient(handler), [],
            NoBusyWaits);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Unavailable, reading.Status);
        Assert.Equal(GeminiVisionReader.DefaultModels.Length * 2, handler.Tried.Count);
    }

    [Fact]
    public void AReadThatGoesQuietIsGivenUpOnAndTheNextModelAsked()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Router((_, model) => model == "gemini-3.7-flash"
            ? Events(new Timetable(
                [(TimeSpan.Zero, Sse([Thought("**Initiating Invoice Analysis**")]))], thenNothing: true))
            : Events(StreamedAnswer(recorded)));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels, new HttpClient(handler), [],
            NoBusyWaits, silence: TimeSpan.FromSeconds(1));

        var clock = Stopwatch.StartNew();
        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        // Given up on once: not asked again, and not asked on the other key either.
        Assert.Equal(["key-1/gemini-3.7-flash", "key-1/gemini-3.6-flash"], handler.Tried);
        Assert.Contains("went quiet", reading.Note, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"waited {clock.Elapsed}");
    }

    /// <summary>
    /// The silence rule is not a deadline. A read that keeps talking runs as long as it takes -
    /// the old 90-second deadline went because it cut off slow reads that would have finished.
    /// </summary>
    [Fact]
    public void AReadThatKeepsTalkingIsNeverCutOffHoweverLongItTakes()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var pieces = Enumerable.Range(1, 35)
            .Select(n => (TimeSpan.FromMilliseconds(100), Sse([Thought($"**Step {n}**")])))
            .Append((TimeSpan.FromMilliseconds(100), Sse(StreamedAnswer(recorded))));
        var handler = new Router((_, _) => Events(new Timetable(pieces, thenNothing: false)));
        var reader = new GeminiVisionReader(
            ["key-1"], ["gemini-test"], new HttpClient(handler), [], NoBusyWaits,
            silence: TimeSpan.FromSeconds(3));

        var clock = Stopwatch.StartNew();
        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));
        clock.Stop();

        Assert.True(reading.Status == DrPepperVisionStatus.Success, reading.Note);
        Assert.Single(handler.Tried);
        // Well past the silence limit, with every gap a small fraction of it.
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(3.2), $"finished in {clock.Elapsed}");
    }

    [Fact]
    public void AnErrorAfterTheReplyStartedIsTheSameFaultAsItsStatus()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        var handler = new Router((_, model) => model == "gemini-3.7-flash"
            ? Events(
                Thought("**Initiating Invoice Analysis**"),
                new { error = new { code = 503, message = "This model is currently experiencing high demand.", status = "UNAVAILABLE" } })
            : Events(StreamedAnswer(recorded)));
        var reader = new GeminiVisionReader(
            ["key-1", "key-2"], GeminiVisionReader.DefaultModels, new HttpClient(handler), [],
            NoBusyWaits);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Equal(
            ["key-1/gemini-3.7-flash", "key-1/gemini-3.7-flash", "key-1/gemini-3.6-flash"],
            handler.Tried);
        Assert.Contains("HTTP 503 after the reply had started", reading.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AThoughtSummaryIsNeverReadAsTheAnswer()
    {
        string recorded = File.ReadAllText(Recorded, Encoding.UTF8);
        string envelope = JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new object[]
                        {
                            new { text = "{\"rows\": [], \"amountDue\": \"0.00\"}", thought = true },
                            new { text = recorded },
                        },
                    },
                    finishReason = "STOP",
                },
            },
        });
        var handler = new Router((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
        });
        var reader = new GeminiVisionReader(["key-1"], ["gemini-test"], new HttpClient(handler), []);

        var reading = reader.ReadDrPepper(File.ReadAllBytes(Scan));

        Assert.Equal(DrPepperVisionStatus.Success, reading.Status);
        Assert.Single(handler.Tried);
    }
}
