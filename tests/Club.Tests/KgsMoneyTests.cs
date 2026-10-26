using Club.Protocol;
using Xunit;

namespace Club.Tests;

public sealed class KgsMoneyTests
{
    [Theory]
    [InlineData("0,01", 1)]
    [InlineData("0.01", 1)]
    [InlineData("199,99", 19999)]
    [InlineData("199.99", 19999)]
    [InlineData(" 100,5 ", 10050)]
    [InlineData("100", 10000)]
    [InlineData("1000000.00", 100000000)]
    public void ConvertsSomsExactlyWithoutDependingOnWindowsLocale(string text, long expected)
    {
        Assert.True(KgsMoney.TryParseAmount(text, KgsMoney.OperationLimitMinor, out var actual));
        Assert.Equal(expected, actual);
        Assert.True(KgsMoney.TryParseAmount(KgsMoney.Format(actual), KgsMoney.OperationLimitMinor, out var roundTrip));
        Assert.Equal(actual, roundTrip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e2")]
    [InlineData("1 000")]
    [InlineData("1,000")]
    [InlineData("1,234.56")]
    [InlineData("1.234,56")]
    [InlineData("0,005")]
    [InlineData("1000000,01")]
    [InlineData("99999999999999999999999999")]
    public void RejectsAmbiguousAmountsAndExtraDecimalsInsteadOfRounding(string? text)
    {
        Assert.False(KgsMoney.TryParseAmount(text, KgsMoney.OperationLimitMinor, out var actual));
        Assert.Equal(0, actual);
    }

    [Fact]
    public void EnforcesExactRefundOrRemainingBalanceCap()
    {
        Assert.True(KgsMoney.TryParseAmount("123,45", 12345, out var amount));
        Assert.Equal(12345, amount);
        Assert.False(KgsMoney.TryParseAmount("123,46", 12345, out _));
        Assert.False(KgsMoney.TryParseAmount("0,01", 0, out _));
        Assert.False(KgsMoney.TryParseAmount("0,01", -1, out _));
        Assert.False(KgsMoney.TryParseAmount("1000000,01", long.MaxValue, out _));
        Assert.Equal("10000000,00", KgsMoney.Format(KgsMoney.BalanceLimitMinor));
    }
}
