using System.Globalization;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// The files made for money software that Canadian banks offer next to CSV: OFX (Microsoft Money), QFX (Quicken Web
/// Connect) and QBO (QuickBooks), which are the same format in its old SGML form or its XML form, and QIF (Quicken
/// Interchange). They carry exact dates and signed amounts, and OFX the balance at the end.
/// </summary>
public static partial class MoneyFiles
{
    /// <summary>"OFX", "QIF" or null.</summary>
    public static string? Kind(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var head = CsvStatementReader.Decode(bytes.Length > 8192 ? bytes[..8192] : bytes);
        if (ext is ".ofx" or ".qfx" or ".qbo" || head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(head, @"<OFX>", RegexOptions.IgnoreCase))
            return "OFX";
        if (ext == ".qif" || Regex.IsMatch(head, @"^\s*!Type:", RegexOptions.Multiline | RegexOptions.IgnoreCase)) return "QIF";
        return null;
    }

    [GeneratedRegex(@"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>))", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Transaction();

    /// <summary>The value of an OFX element, closed (XML) or not (SGML: the value runs to the next tag).</summary>
    private static string Tag(string text, string name)
    {
        var m = Regex.Match(text, "<" + name + @">\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
    }

    /// <summary>"20250815120000.000[-5:EST]" → 2025-08-15.</summary>
    private static DateOnly? OfxDate(string v) =>
        v.Length >= 8 && DateOnly.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal? Number(string v)
    {
        v = v.Replace(",", ".").Replace(" ", "");
        return decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    public static StatementParse ParseOfx(byte[] bytes, string defaultCurrency, AccountKind? forcedKind)
    {
        var text = CsvStatementReader.Decode(bytes);
        var rows = new List<ParsedRow>();
        var i = 0;
        foreach (Match m in Transaction().Matches(text))
        {
            var t = m.Groups[1].Value;
            if (OfxDate(Tag(t, "DTPOSTED")) is not { } date || Number(Tag(t, "TRNAMT")) is not { } amount || amount == 0) continue;
            var name = Tag(t, "NAME");
            var memo = Tag(t, "MEMO");
            // The name is cut at 32 characters by the format; the memo often holds the rest, or the details.
            var label = memo.Length == 0 || name.Contains(memo, StringComparison.OrdinalIgnoreCase) ? name
                : name.Length == 0 || memo.StartsWith(name, StringComparison.OrdinalIgnoreCase) ? memo
                : name + " " + memo;
            if (label.Length == 0) label = Tag(t, "TRNTYPE");
            rows.Add(new ParsedRow { Date = date, Label = TextNorm.CollapseSpaces(label), Amount = amount, BankCategory = "", Page = 0, Top = i++ });
        }
        rows = rows.OrderBy(r => r.Date).ThenBy(r => r.Top).ToList();
        var card = Regex.IsMatch(text, @"<CCACCTFROM>|<CCSTMTRS>", RegexOptions.IgnoreCase) || Tag(text, "ACCTTYPE").Equals("CREDITLINE", StringComparison.OrdinalIgnoreCase);
        var savings = Tag(text, "ACCTTYPE").Equals("SAVINGS", StringComparison.OrdinalIgnoreCase);
        var ledger = Regex.Match(text, @"<LEDGERBAL>(.*?)(?:</LEDGERBAL>|<AVAILBAL>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } lb ? Number(Tag(lb.Groups[1].Value, "BALAMT")) : null;
        var acct = Tag(text, "ACCTID");
        var currency = Tag(text, "CURDEF") is { Length: 3 } cur ? cur.ToUpperInvariant() : defaultCurrency;
        decimal? opening = ledger is { } l ? l - rows.Sum(r => r.Amount) : null;
        return new StatementParse
        {
            Rows = rows,
            OpeningBalance = opening,
            ClosingBalance = ledger,
            ComputedClosing = ledger,
            PeriodStart = OfxDate(Tag(text, "DTSTART")) ?? rows.FirstOrDefault()?.Date,
            PeriodEnd = OfxDate(Tag(text, "DTEND")) ?? rows.LastOrDefault()?.Date,
            Currency = currency,
            Kind = forcedKind ?? (card ? AccountKind.CreditCard : savings ? AccountKind.Savings : AccountKind.Bank),
            Last4 = Regex.Match(acct, @"(\d{4})\s*$") is { Success: true } a ? a.Groups[1].Value : null,
            Bank = Tag(text, "ORG") is { Length: > 1 } org ? org : null,
            // The amounts come from the bank's own records; the file has no running balance to check them against.
            Status = rows.Count == 0 ? VerificationStatus.NoTransactions : VerificationStatus.NoBalances,
            Method = "OFX",
            FirstPageText = Tag(text, "ORG"),
        };
    }

    public static StatementParse ParseQif(byte[] bytes, string fileName, string defaultCurrency, AccountKind? forcedKind)
    {
        var text = CsvStatementReader.Decode(bytes);
        var type = Regex.Match(text, @"!Type:(\w+)", RegexOptions.IgnoreCase) is { Success: true } tm ? tm.Groups[1].Value.ToUpperInvariant() : "BANK";
        var entries = new List<(string Date, decimal Amount, string Payee, string Memo, string Category)>();
        string date = "", payee = "", memo = "", category = "";
        decimal? amount = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '!') continue;
            var v = line[1..].Trim();
            switch (line[0])
            {
                case 'D': date = v; break;
                // "-1,234.56" (thousands comma) or "-1 234,56" / "-1.234,56" (French software: decimal comma).
                case 'T' or 'U':
                    amount ??= Regex.IsMatch(v, @",\d{1,2}$") ? Number(v.Replace(" ", "").Replace(" ", "").Replace(".", "").Replace(',', '.')) : Number(v.Replace(",", ""));
                    break;
                case 'P': payee = v; break;
                case 'M': memo = v; break;
                case 'L': category = v.Trim('[', ']'); break;
                case '^':
                    if (amount is { } a && a != 0 && date.Length > 0) entries.Add((date, a, payee, memo, category));
                    (date, payee, memo, category, amount) = ("", "", "", "", null);
                    break;
            }
        }
        // QIF dates: 08/15/2025, 8/15'25, 15/08/2025, 2025-08-15. The order is found over the whole file.
        static (int A, int B, int Y)? Split(string d)
        {
            var m = Regex.Match(d, @"^(\d{1,4})[/.\-](\d{1,2})[/.\-' ]+(\d{2,4})$");
            if (!m.Success) return null;
            int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (a > 31) return (b, y, a); // year first: 2025-08-15 → month, day, year
            return (a, b, y < 100 ? 2000 + y : y);
        }
        var parts = entries.Select(e => Split(e.Date.Replace('\'', '/'))).ToList();
        var yearFirst = entries.Any(e => Regex.IsMatch(e.Date, @"^\d{4}"));
        var dayMonth = !yearFirst && parts.Any(p => p is { A: > 12 });
        var rows = new List<ParsedRow>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (parts[i] is not { } p) continue;
            int day, month;
            if (yearFirst) (month, day) = (p.A, p.B); else if (dayMonth) (day, month) = (p.A, p.B); else (month, day) = (p.A, p.B);
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(p.Y, month)) continue;
            var e = entries[i];
            var label = e.Memo.Length == 0 || e.Payee.Contains(e.Memo, StringComparison.OrdinalIgnoreCase) ? e.Payee : e.Payee.Length == 0 ? e.Memo : e.Payee + " " + e.Memo;
            rows.Add(new ParsedRow { Date = new DateOnly(p.Y, month, day), Label = TextNorm.CollapseSpaces(label), Amount = e.Amount, BankCategory = e.Category, Page = 0, Top = i });
        }
        rows = rows.OrderBy(r => r.Date).ThenBy(r => r.Top).ToList();
        return new StatementParse
        {
            Rows = rows,
            PeriodStart = rows.FirstOrDefault()?.Date,
            PeriodEnd = rows.LastOrDefault()?.Date,
            Currency = defaultCurrency,
            Kind = forcedKind ?? (type is "CCARD" or "CREDITCARD" ? AccountKind.CreditCard : AccountKind.Bank),
            Status = rows.Count == 0 ? VerificationStatus.NoTransactions : VerificationStatus.NoBalances,
            Method = "QIF",
            DateOrder = dayMonth ? DateOrder.DayMonth : DateOrder.MonthDay,
        };
    }
}
