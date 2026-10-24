using System.Globalization;
using System.Text.RegularExpressions;

namespace Club.Protocol;

public static class KgsMoney
{
    public const long OperationLimitMinor = 100_000_000;
    public const long BalanceLimitMinor = 1_000_000_000;

    // Amount entry is explicit, independent of the Windows locale, and never rounds money.
    // Group separators, signs and scientific notation are deliberately not accepted.
    public static bool TryParseAmount(string? text, long maximumMinor, out long amountMinor)
    {
        amountMinor = 0;
        var value = text?.Trim();
        if (value is null || value.Length > 16 || maximumMinor <= 0 ||
            !Regex.IsMatch(value, @"^[0-9]+(?:[.,][0-9]{1,2})?$", RegexOptions.CultureInvariant) ||
            !decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var major) ||
            major <= 0 || major > Math.Min(maximumMinor, OperationLimitMinor) / 100m) return false;
        amountMinor = checked((long)(major * 100));
        return true;
    }

    public static string Format(long minor) => (minor / 100m).ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));
}
