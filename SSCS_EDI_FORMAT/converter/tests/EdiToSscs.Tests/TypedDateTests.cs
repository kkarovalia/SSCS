using EdiToSscs.Core;

namespace EdiToSscs.Tests;

/// <summary>
/// The date box, which used to accept one punctuation and reject every other.
///
/// Typing `08/11/2026` — a real date, and the format the tickets themselves print — came back as
/// "Type the date like 12-14-2023". Somebody keying twenty invoices should not be told how to
/// punctuate, so every reasonable way of writing the same day is now the same day.
/// </summary>
public class TypedDateTests
{
    /// <summary>All of these are the eleventh of August 2026, however they are written.</summary>
    [Theory]
    [InlineData("08-11-2026")]
    [InlineData("08/11/2026")]
    [InlineData("8/11/2026")]
    [InlineData("8-11-26")]
    [InlineData("08/11/26")]
    [InlineData("8.11.26")]
    [InlineData("08.11.2026")]
    [InlineData("8 11 2026")]
    [InlineData("08112026")]
    [InlineData("081126")]
    [InlineData("  08/11/2026  ")]
    public void EveryWayOfWritingTheSameDayReadsTheSame(string typed) =>
        Assert.Equal(new DateOnly(2026, 8, 11), TypedDate.TryParse(typed));

    /// <summary>
    /// Month first, always. This is the one part a person cannot check by looking — 08-11-2026 is a
    /// real date read either way round — so a day-first reading would book the delivery into the
    /// wrong month and say nothing. Never inferred from the machine's locale.
    /// </summary>
    [Fact]
    public void TheMonthComesFirst()
    {
        var read = TypedDate.TryParse("08-11-2026");
        Assert.Equal(8, read!.Value.Month);
        Assert.Equal(11, read.Value.Day);
    }

    /// <summary>
    /// A date that is not a date is rejected rather than guessed at. Rejecting is loud - the operator
    /// is asked again - and guessing is silent, which is the wrong way round for the one field
    /// nothing downstream can check.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("today")]
    [InlineData("13-11-2026")]   // no thirteenth month
    [InlineData("08-32-2026")]   // no thirty-second day
    [InlineData("02-30-2026")]   // February has no thirtieth
    [InlineData("8/11")]         // no year at all
    [InlineData("0811260")]      // seven digits is not a date in any reading
    public void AnythingThatIsNotADateIsRefused(string? typed) =>
        Assert.Null(TypedDate.TryParse(typed));

    /// <summary>A two-digit year is this century; these are delivery tickets, not archives.</summary>
    [Fact]
    public void ATwoDigitYearIsThisCentury() =>
        Assert.Equal(new DateOnly(2026, 12, 14), TypedDate.TryParse("12-14-26"));

    /// <summary>What the box is pre-filled with still reads back as the same day.</summary>
    [Fact]
    public void TheFormatTheBoxOffersIsOneItAccepts()
    {
        var day = new DateOnly(2026, 8, 11);
        Assert.Equal(day, TypedDate.TryParse(day.ToString(TypedDate.Format,
            System.Globalization.CultureInfo.InvariantCulture)));
    }
}
