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
    /// <summary>
    /// The ISO 4217 codes of every currency in use (as Windows lists them for every country), less the six that are
    /// also common words a statement prints beside numbers (ALL, CUP, MOP, PEN, SOS, TOP).
    /// </summary>
    public const string CurrencyCodes = "AED|AFN|AMD|ANG|AOA|ARS|AUD|AWG|AZN|BAM|BBD|BDT|BGN|BHD|BIF|BMD|BND|BOB|BRL|BSD|BTN|BWP|BYN|BZD|CAD|CDF|CHF|CLP|CNY|COP|CRC|CVE|CZK|DJF|DKK|DOP|DZD|EGP|ERN|ETB|EUR|FJD|FKP|GBP|GEL|GHS|GIP|GMD|GNF|GTQ|GYD|HKD|HNL|HTG|HUF|IDR|ILS|INR|IQD|IRR|ISK|JMD|JOD|JPY|KES|KGS|KHR|KMF|KPW|KRW|KWD|KYD|KZT|LAK|LBP|LKR|LRD|LYD|MAD|MDL|MGA|MKD|MMK|MNT|MRU|MUR|MVR|MWK|MXN|MYR|MZN|NAD|NGN|NIO|NOK|NPR|NZD|OMR|PAB|PGK|PHP|PKR|PLN|PYG|QAR|RON|RSD|RUB|RWF|SAR|SBD|SCR|SDG|SEK|SGD|SHP|SLE|SRD|SSP|STN|SYP|SZL|THB|TJS|TMT|TND|TRY|TTD|TWD|TZS|UAH|UGX|USD|UYU|UZS|VES|VND|VUV|WST|XAF|XCD|XOF|XPF|YER|ZAR|ZMW";

    public static readonly HashSet<string> Currencies = [.. CurrencyCodes.Split('|')];

    /// <summary>
    /// The currencies counted without decimals, and with three (ISO 4217), by their code and by their own sign when no
    /// other currency uses it (from the data of every country Windows knows).
    /// </summary>
    private static readonly HashSet<string> NoDecimals = Minor(["BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF"]),
        ThreeDecimals = Minor(["BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND"]);

    private static HashSet<string> Minor(string[] codes)
    {
        var set = new HashSet<string>(codes, StringComparer.Ordinal);
        // A sign shared by currencies with other decimals ("$" of the Chilean peso and of the dollar, "¥" of the yen
        // and of the yuan) does not tell: only the code does then.
        var signs = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            RegionInfo r;
            try { r = new RegionInfo(c.Name); } catch (ArgumentException) { continue; }
            if (!Currencies.Contains(r.ISOCurrencySymbol)) continue;
            var same = codes.Contains(r.ISOCurrencySymbol);
            // A single sign of money (₩, ₫), not letters shared with words.
            if (r.CurrencySymbol.Length == 1 && char.GetUnicodeCategory(r.CurrencySymbol[0]) == UnicodeCategory.CurrencySymbol)
                signs[r.CurrencySymbol] = signs.GetValueOrDefault(r.CurrencySymbol, true) && same;
        }
        foreach (var (sign, only) in signs) if (only) set.Add(sign);
        return set;
    }

    [GeneratedRegex(
        @"^(?<lead>[-+(]|-\s*\(|\(\s*-)?\s*(?<cur1>CA\$|C\$|US\$|" + CurrencyCodes + @"|\p{Sc})?\s*(?<lead2>[-+])?\s*(?<num>\d{1,3}(?:[ .,']\d{3})+|\d+)?(?<dec>[.,]\d{2,3})?\s*(?<cur2>\$\s*CA|\$\s*US|CA\$|" + CurrencyCodes + @"|\p{Sc})?\s*(?<trail>-|\)|CR|DR|DB|CRE|CREDIT|DEBIT)?\.?$",
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
        // Less than one, printed without its 0 (".50", as Scotiabank does): only with its cents.
        if (num.Length == 0)
        {
            if (dec.Length != 3) return false;
            num = "0";
        }
        // Two decimals, or the decimals of the currency written beside the amount: none for the yen or the won
        // ("¥1,200"), three for the dinars ("KWD 12.500"). Without a currency, two decimals only ("1,200" and "1,234"
        // are not amounts).
        var currency = (m.Groups["cur1"].Value + m.Groups["cur2"].Value).Trim();
        // "12.500" beside a currency of three decimals is twelve and a half, not twelve thousand five hundred.
        if (dec.Length == 0 && ThreeDecimals.Contains(currency) && num.Length > 4 && num[^4] is '.' or ',') (num, dec) = (num[..^4], num[^4..]);
        if (dec.Length != 3 && !(dec.Length == 0 && NoDecimals.Contains(currency)) && !(dec.Length == 4 && ThreeDecimals.Contains(currency))) return false;
        // A group separator equal to the decimal separator is not a valid amount ("1,234,56").
        if (dec.Length > 0 && num.Length > 3 && num.Contains(dec[0])) return false;
        var digits = new string(num.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return false;
        // Leading zeros in a long group ("0012.50") are reference numbers, not money.
        if (digits.Length > 1 && digits[0] == '0' && num.Length == digits.Length) return false;

        var magnitude = decimal.Parse(dec.Length == 0 ? digits : digits + "." + dec[1..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

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
        // Any currency sign ($, €, £, ¥, ₹, ₩, ₽, ₺...), alone or with its country.
        return t is "CA$" or "US$" or "CR" or "DR" or "DB" or "-" or "+" or "$CA" or "$US" || (t.Length == 1 && char.GetUnicodeCategory(t[0]) == UnicodeCategory.CurrencySymbol) || Currencies.Contains(t);
    }

    /// <summary>
    /// The signs of money only one currency uses (as banks print them), by currency. "$" and "¥" are shared by several
    /// currencies: alone they name none.
    /// </summary>
    private static readonly Dictionary<char, string> OwnSigns = new()
    {
        ['€'] = "EUR", ['£'] = "GBP", ['₹'] = "INR", ['₩'] = "KRW", ['₽'] = "RUB", ['₺'] = "TRY", ['₪'] = "ILS",
        ['₫'] = "VND", ['₴'] = "UAH", ['₱'] = "PHP", ['฿'] = "THB", ['₦'] = "NGN", ['₸'] = "KZT", ['₾'] = "GEL",
    };

    [GeneratedRegex(@"CA\$|\$\s?CA(?![A-Z])|US\$|\$\s?US(?![A-Z])")]
    private static partial Regex DollarOfCountry();

    [GeneratedRegex(@"(?<![A-Z])[A-Z]{3}(?![A-Z])")]
    private static partial Regex ThreeLetters();

    /// <summary>
    /// The one currency a text names: a currency code as a word ("Amount (CHF)", "-45.10 CHF"), a sign only one currency
    /// uses ("12,34 €", "£5") or a dollar with its country ("CA$ 12"). Null when it names none, several, or only a sign
    /// several currencies share ("$12.34", "¥980").
    /// </summary>
    public static string? CurrencyOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToUpperInvariant();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DollarOfCountry().Matches(t)) named.Add(m.Value.Contains("US", StringComparison.Ordinal) ? "USD" : "CAD");
        foreach (Match m in ThreeLetters().Matches(t)) if (Currencies.Contains(m.Value)) named.Add(m.Value);
        foreach (var ch in t) if (OwnSigns.TryGetValue(ch, out var code)) named.Add(code);
        return named.Count == 1 ? named.Single() : null;
    }

    /// <summary>Formats with invariant culture, two decimals, for storage.</summary>
    public static string ToStorage(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    public static decimal FromStorage(string value) => decimal.Parse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}
