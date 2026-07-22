using System.Security.Cryptography;
using System.Text;

namespace EdiToSscs.Tests;

/// <summary>Shared helpers: fixture access, byte-precise assertions, synthetic record builders.</summary>
public static class TestUtil
{
    public static string FixturePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", relative);

    public static byte[] Fixture(string relative) => File.ReadAllBytes(FixturePath(relative));

    /// <summary>
    /// Asserts byte-for-byte equality. On failure reports the first differing byte offset,
    /// 1-based line and column, and the expected/actual byte values, then also asserts
    /// length and SHA-256 so the standard failure data is complete.
    /// </summary>
    public static void AssertBytesEqual(byte[] expected, byte[] actual)
    {
        int min = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < min; i++)
        {
            if (expected[i] != actual[i])
            {
                (int line, int col) = OffsetToLineCol(expected, i);
                Assert.Fail(
                    $"First differing byte at offset {i} (line {line}, column {col}): " +
                    $"expected 0x{expected[i]:X2} '{Printable(expected[i])}', " +
                    $"actual 0x{actual[i]:X2} '{Printable(actual[i])}'. " +
                    $"Expected length {expected.Length}, actual length {actual.Length}.");
            }
        }
        if (expected.Length != actual.Length)
        {
            (int line, int col) = OffsetToLineCol(expected.Length > actual.Length ? expected : actual, min);
            Assert.Fail(
                $"Files are identical up to offset {min} (line {line}, column {col}) but lengths differ: " +
                $"expected {expected.Length} bytes, actual {actual.Length} bytes.");
        }
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), Convert.ToHexString(SHA256.HashData(actual)));
        Assert.True(expected.AsSpan().SequenceEqual(actual));
    }

    private static (int Line, int Col) OffsetToLineCol(byte[] data, int offset)
    {
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset && i < data.Length; i++)
        {
            if (data[i] == (byte)'\n') { line++; lineStart = i + 1; }
        }
        return (line, offset - lineStart + 1);
    }

    private static string Printable(byte b) => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"\\x{b:X2}";

    // ------------------------------------------------------------- builders

    public const string GhraHeader = "GHRA   00001314540813180000950902";                 // 33 bytes
    public const string Ghra13Header = "GHRA13 00001314540813180000950902";               // 33 bytes
    public const string PepsiHeader = "PEPSI  00804719060000134805";                      // 27 bytes
    public const string CRecord = "CSERSERVICE CHARGE           ";                        // 29 bytes

    public static readonly string RubyHeader =
        "GHRA13" + "Ruby Red Sales & Services".PadRight(30) + "IN-3738".PadRight(10) + "20260408" + "0000018144"; // 64 bytes

    private static readonly string Filler64 = new('X', 64);
    private static readonly string Filler55 = new('Y', 55);

    /// <summary>Standard EDI B record (76 bytes): 'B' + 11-char code field + 64 preserved bytes.</summary>
    public static string B(string code11, string? remainder64 = null)
    {
        if (code11.Length != 11) throw new ArgumentException("code must be 11 chars");
        string rest = remainder64 ?? Filler64;
        if (rest.Length != 64) throw new ArgumentException("remainder must be 64 chars");
        return "B" + code11 + rest;
    }

    /// <summary>EDI13 B record (78 bytes): 'B' + 13-char code field + 64 preserved bytes.</summary>
    public static string B13(string field13, string? remainder64 = null)
    {
        if (field13.Length != 13) throw new ArgumentException("field must be 13 chars");
        string rest = remainder64 ?? Filler64;
        if (rest.Length != 64) throw new ArgumentException("remainder must be 64 chars");
        return "B" + field13 + rest;
    }

    /// <summary>Ruby Red B record (79 bytes): 'B' + 13-char code field + 55 preserved bytes + '00000000  '.</summary>
    public static string BRuby(string field13, string? middle55 = null)
    {
        if (field13.Length != 13) throw new ArgumentException("field must be 13 chars");
        string mid = middle55 ?? Filler55;
        if (mid.Length != 55) throw new ArgumentException("middle must be 55 chars");
        return "B" + field13 + mid + "00000000  ";
    }

    /// <summary>Joins lines with CRLF, no final newline (the convention of all CRLF fixtures).</summary>
    public static byte[] CrlfFile(params string[] lines) =>
        Encoding.Latin1.GetBytes(string.Join("\r\n", lines));

    /// <summary>Joins lines with LF and a final LF (the convention of the Ruby Red fixture).</summary>
    public static byte[] LfFile(params string[] lines) =>
        Encoding.Latin1.GetBytes(string.Concat(lines.Select(l => l + "\n")));

    public static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}

/// <summary>Creates a unique temp directory and removes it (with retries) on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sscs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Sub(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path)) return;
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }
    }
}
