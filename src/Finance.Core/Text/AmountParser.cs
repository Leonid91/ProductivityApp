using System.Globalization;
using System.Text.RegularExpressions;

namespace ProductivityApp.Finance.Text;

/// <summary>Explicit sign written next to an amount on a statement.</summary>
public enum SignMarker { None, Minus, Plus, Credit, Debit }

/// <summary>An amount as printed: always a positive magnitude, plus whatever sign marker was written.</summary>
public readonly record struct PrintedAmount(decimal Magnitude, SignMarker Marker)
{
    /// <summary>Signed value reading "-", "DR" and "DB" as negative and everything else as positive.</summary>
    public decimal Signed => Marker is SignMarker.Minus or SignMarker.Debit ? -Magnitude : Magnitude;
}

/// <summary>
/// Reads money amounts in French and English formats: 1 234,56 / 1.234,56 / 1,234.56 / 1234.56,
/// with optional currency symbols and signs: -12.34, 12.34-, (12.34), 12.34 CR, +12,34 €.
/// Amounts must have exactly two decimals: statements always print cents, and this keeps
/// days, years and reference numbers from being read as money.
/// </summary>
public static partial class AmountParser
{
    /// <summary>The currencies an amount can be written in, by their code ("CHF 12.50", "12,50 GBP"), besides the signs $ € £.</summary>
    public const string CurrencyCodes = "EUR|USD|CAD|GBP|CHF|AUD|NZD|JPY|CNY|HKD|SGD|SEK|NOK|DKK|PLN|CZK|HUF|RON|BGN|ISK|TRY|ILS|AED|SAR|QAR|ZAR|MAD|TND|EGP|INR|THB|MYR|IDR|PHP|KRW|MXN|BRL|ARS|CLP|COP";

    public static readonly HashSet<string> Currencies = [.. CurrencyCodes.Split('|')];

    [GeneratedRegex(
        @"^(?<lead>[-+(]|-\s*\(|\(\s*-)?\s*(?:CA\$|C\$|US\$|" + CurrencyCodes + @"|\$|€|£)?\s*(?<lead2>[-+])?\s*(?<num>\d{1,3}(?:[ .,']\d{3})+|\d+)(?<dec>[.,]\d{2})\s*(?:\$\s*CA|\$\s*US|CA\$|" + CurrencyCodes + @"|\$|€|£)?\s*(?<trail>-|\)|CR|DR|DB|CRE|CREDIT|DEBIT)?\.?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex AmountRegex();

    public static bool TryParse(string text, out PrintedAmount amount)
    {
        amount = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = TextNorm.Fold(text).Replace("−", "-").Trim();
        if (t.Length > 32) return false;
        var m = AmountRegex().Match(t);
        if (!m.Success) return false;

        var num = m.Groups["num"].Value;
        var dec = m.Groups["dec"].Value;
        var decSep = dec[0];
        // A group separator equal to the decimal separator is not a valid amount ("1,234,56").
        if (num.Length > 3 && num.Contains(decSep)) return false;
        var digits = new string(num.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return false;
        // Leading zeros in a long group ("0012.50") are reference numbers, not money.
        if (digits.Length > 1 && digits[0] == '0' && num.Length == digits.Length) return false;

        var magnitude = decimal.Parse(digits + "." + dec[1..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        var lead = m.Groups["lead"].Value + m.Groups["lead2"].Value;
        var trail = m.Groups["trail"].Value;
        var marker = SignMarker.None;
        if (lead.Contains('-')) marker = SignMarker.Minus;
        else if (lead.Contains('(') && trail == ")") marker = SignMarker.Minus;
        else if (lead.Contains('+')) marker = SignMarker.Plus;

        if (marker == SignMarker.None)
        {
            marker = trail switch
            {
                "-" => SignMarker.Minus,
                "CR" or "CRE" or "CREDIT" => SignMarker.Credit,
                "DR" or "DB" or "DEBIT" => SignMarker.Debit,
                ")" => SignMarker.None,
                _ => SignMarker.None,
            };
            if (trail == ")" && !lead.Contains('(')) return false;
        }
        if (lead.Contains('(') && trail != ")") return false;

        amount = new PrintedAmount(magnitude, marker);
        return true;
    }

    public static bool IsAmount(string text) => TryParse(text, out _);

    /// <summary>True for tokens that are only a currency or sign marker printed apart from the number.</summary>
    public static bool IsDetachedMarker(string text)
    {
        var t = TextNorm.Fold(text).Trim();
        return t is "$" or "€" or "£" or "CA$" or "US$" or "CR" or "DR" or "DB" or "-" or "+" or "$CA" or "$US" || Currencies.Contains(t);
    }

    /// <summary>Formats with invariant culture, two decimals, for storage.</summary>
    public static string ToStorage(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    public static decimal FromStorage(string value) => decimal.Parse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
