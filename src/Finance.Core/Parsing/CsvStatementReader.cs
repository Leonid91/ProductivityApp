using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// Reads the CSV exports banks offer (any separator, quoted or not, UTF-8 or Windows encoding, with or without a
/// header row). Columns are found by their names in French or English, or by what they contain. When the file has
/// a balance column, every line is checked against it to the cent.
/// </summary>
public static partial class CsvStatementReader
{
    static CsvStatementReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>True for text files that look like a table of transactions.</summary>
    public static bool LooksLikeCsv(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0 || bytes.Length > 50 * 1024 * 1024) return false;
        if (bytes.Take(4096).Any(b => b == 0)) return false;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".csv" or ".tsv" or ".txt") return true;
        var text = Decode(bytes);
        return Rows(text, DetectDelimiter(text)).Take(5).Count(r => r.Count >= 3) >= 2;
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static char DetectDelimiter(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(30).ToList();
        var best = ',';
        var bestScore = -1.0;
        foreach (var d in new[] { ';', ',', '\t', '|' })
        {
            var counts = lines.Select(l => Split(l, d).Count).ToList();
            if (counts.Count == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
            if (mode.Key < 2) continue;
            var score = mode.Count() * 10 + mode.Key;
            if (score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    /// <summary>Splits one line, honouring double quotes ("a;b" stays one cell, "" is a quote).</summary>
    private static List<string> Split(string line, char d)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == d) { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static IEnumerable<List<string>> Rows(string text, char d)
    {
        // Quoted cells may contain line breaks: join physical lines until the quotes balance.
        var pending = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (pending.Length > 0) pending.Append(' ');
            pending.Append(line);
            if (pending.ToString().Count(c => c == '"') % 2 == 1) continue;
            var full = pending.ToString();
            pending.Clear();
            if (full.Trim().Length == 0) continue;
            yield return Split(full, d);
        }
    }

    private enum Col { None, Date, ValueDate, Description, Description2, Category, Debit, Credit, Amount, Balance, Card, Account, Currency, Type }

    private static Col Classify(string header)
    {
        var f = " " + Regex.Replace(TextNorm.Fold(header), @"[^A-Z0-9]+", " ").Trim() + " ";
        bool Has(params string[] words) => words.Any(w => f.Contains(" " + w + " ", StringComparison.Ordinal));
        if (Has("SOLDE", "BALANCE")) return Col.Balance;
        if (Has("CATEGORIE", "CATEGORY", "CATEGORIES")) return Col.Category;
        if (Has("CARTE", "CARD")) return Has("DATE") ? Col.Date : Col.Card;
        if (Has("DATE", "POSTED", "POSTE", "JOUR"))
            return Has("VALEUR", "VALUE", "POSTED", "POSTING", "INSCRIPTION", "COMPTABILISATION", "POSTE") && !Has("TRANSACTION", "OPERATION", "ACHAT") ? Col.ValueDate : Col.Date;
        if (Has("DEVISE", "CURRENCY", "MONNAIE")) return Col.Currency;
        if (Has("DEBIT", "DEBITS", "RETRAIT", "RETRAITS", "WITHDRAWAL", "WITHDRAWALS", "SORTIE", "SORTIES", "OUT", "CHARGES", "PAID")) return Col.Debit;
        if (Has("CREDIT", "CREDITS", "DEPOT", "DEPOTS", "DEPOSIT", "DEPOSITS", "ENTREE", "ENTREES", "IN", "RECEIVED")) return Col.Credit;
        if (Has("MONTANT", "AMOUNT", "VALUE", "VALEUR", "SOMME", "CAD", "USD", "EUR")) return Col.Amount;
        if (Has("TYPE")) return Col.Type;
        if (Has("COMPTE", "ACCOUNT", "NUMERO", "NUMBER")) return Col.Account;
        if (Has("DESCRIPTION", "LIBELLE", "DETAILS", "DETAIL", "MERCHANT", "COMMERCANT", "PAYEE", "BENEFICIAIRE", "NAME", "NOM", "MEMO",
                "TRANSACTION", "OPERATION", "NARRATIVE", "REFERENCE", "LABEL", "INFORMATIONS"))
            return Has("2") || Has("MEMO") ? Col.Description2 : Col.Description;
        return Col.None;
    }

    [GeneratedRegex(@"^\(?[-+−]?\s*[$€£]?\s*[-+−]?\d[\d\s  .,']*\)?\s*(?:[$€£]|CAD|USD|EUR)?\s*(?:-|CR|DR)?$")]
    private static partial Regex Numberish();

    /// <summary>Lenient number reading for exports: "12.34", "7.0", "1 234,56", "-12", "(3.50)", "12.00 CR".</summary>
    private static bool TryNumber(string cell, char decimalSep, out decimal value)
    {
        value = 0;
        var t = TextNorm.Fold(cell).Replace("−", "-").Trim();
        if (t.Length == 0 || !Numberish().IsMatch(t)) return false;
        var negative = t.StartsWith('-') || t.StartsWith('(') || t.EndsWith('-') || t.EndsWith("DR", StringComparison.Ordinal) || t.Contains("$-") || t.Contains("- ");
        if (t.StartsWith("-$", StringComparison.Ordinal)) negative = true;
        var digits = new StringBuilder();
        foreach (var c in t)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (c == decimalSep) digits.Append('.');
        }
        var s = digits.ToString();
        if (s.Count(c => c == '.') > 1 || s.Length == 0 || s == ".") return false;
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (negative) value = -value;
        return true;
    }

    private static char DecimalSeparator(IEnumerable<string> cells)
    {
        int comma = 0, dot = 0;
        foreach (var c in cells)
        {
            if (Regex.IsMatch(c, @"\d,\d{1,2}(\D*)$")) comma++;
            if (Regex.IsMatch(c, @"\d\.\d{1,2}(\D*)$")) dot++;
        }
        return comma > dot ? ',' : '.';
    }

    /// <param name="forcedKind">Account type chosen by the person; when null the file is only read, not typed.</param>
    public static StatementParse Parse(byte[] bytes, string fileName, string defaultCurrency, AccountKind? forcedKind = null)
    {
        var text = Decode(bytes);
        var delimiter = DetectDelimiter(text);
        var table = Rows(text, delimiter).ToList();
        if (table.Count == 0) return Empty("CSV", defaultCurrency);
        var width = table.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First().Key;

        // Header: the first row whose cells are column names rather than data.
        var headerIndex = -1;
        for (var i = 0; i < Math.Min(table.Count, 15); i++)
        {
            var named = table[i].Count(c => Classify(c) != Col.None);
            var dated = table[i].Count(c => DateParser.TryParse(c, out _));
            if (named >= 2 && dated == 0) { headerIndex = i; break; }
        }
        var data = table.Skip(headerIndex + 1).Where(r => r.Count >= Math.Min(width, 3) && r.Any(c => c.Length > 0)).ToList();
        if (data.Count == 0) return Empty("CSV", defaultCurrency);
        var decimalSep = delimiter == ',' ? '.' : DecimalSeparator(data.SelectMany(r => r));

        var cols = new Dictionary<Col, int>();
        var descExtra = new List<int>();
        if (headerIndex >= 0)
        {
            var h = table[headerIndex];
            for (var i = 0; i < h.Count; i++)
            {
                var role = Classify(h[i]);
                if (role == Col.None) continue;
                if (role == Col.Date && cols.ContainsKey(Col.Date)) role = Col.ValueDate;
                if (role == Col.Description && cols.ContainsKey(Col.Description)) role = Col.Description2;
                if (role == Col.Description2) { descExtra.Add(i); continue; }
                cols.TryAdd(role, i);
            }
        }
        InferMissingColumns(cols, data, decimalSep);
        if (!cols.ContainsKey(Col.Date) || !(cols.ContainsKey(Col.Amount) || cols.ContainsKey(Col.Debit) || cols.ContainsKey(Col.Credit)))
            return Empty("CSV", defaultCurrency);

        string Cell(List<string> r, Col c) => cols.TryGetValue(c, out var i) && i < r.Count ? r[i] : "";

        // Dates and their order (day/month or month/day) across the whole file.
        var printed = data.Select(r => DateParser.TryParse(Cell(r, Col.Date), out var d) ? d : (PrintedDate?)null).ToList();
        var numeric = printed.Where(d => d is { MonthKnown: false, IsoOrder: false }).Select(d => d!.Value).ToList();
        var headerFolded = headerIndex >= 0 ? TextNorm.Fold(string.Join(' ', table[headerIndex])) : "";
        var french = Regex.IsMatch(headerFolded, @"\b(LIBELLE|MONTANT|SOLDE|CATEGORIE|DEBIT|DEVISE|OPERATION)\b");
        var order = numeric.Any(d => d.A > 12) ? DateOrder.DayMonth
            : numeric.Any(d => d.B > 12) ? DateOrder.MonthDay
            : french ? DateOrder.DayMonth : DateOrder.MonthDay;
        var year = DateTime.Today.Year;

        var name = TextNorm.Fold(Path.GetFileNameWithoutExtension(fileName));
        var hasCard = cols.ContainsKey(Col.Card) || Regex.IsMatch(name, @"(VISA|MASTERCARD|CREDIT|CARTE|CARD|AMEX)");
        var savings = Regex.IsMatch(name, @"(EPARGNE|SAVINGS|HIGHINV|HISA|CELI|TFSA|REER|RRSP|LIVRET)");

        var rows = new List<ParsedRow>();
        var skippedZero = 0;
        for (var i = 0; i < data.Count; i++)
        {
            var r = data[i];
            if (printed[i] is not { } pd) continue;
            var date = pd.ToDate(order, pd.Year ?? year);
            if (date is null) continue;
            decimal amount;
            if (cols.ContainsKey(Col.Debit) || cols.ContainsKey(Col.Credit))
            {
                TryNumber(Cell(r, Col.Debit), decimalSep, out var debit);
                TryNumber(Cell(r, Col.Credit), decimalSep, out var credit);
                amount = Math.Abs(credit) - Math.Abs(debit);
                if (!cols.ContainsKey(Col.Debit) && credit < 0) amount = credit;
                if (!cols.ContainsKey(Col.Credit) && debit < 0) amount = -debit;
            }
            else if (!TryNumber(Cell(r, Col.Amount), decimalSep, out amount)) continue;
            var type = TextNorm.Fold(Cell(r, Col.Type));
            if (type is "DEBIT" or "DR" or "D" or "WITHDRAWAL" or "RETRAIT") amount = -Math.Abs(amount);
            else if (type is "CREDIT" or "CR" or "C" or "DEPOSIT" or "DEPOT") amount = Math.Abs(amount);

            decimal? balance = TryNumber(Cell(r, Col.Balance), decimalSep, out var b) ? b : null;
            var label = TextNorm.CollapseSpaces(string.Join(' ', new[] { Cell(r, Col.Description) }.Concat(descExtra.Select(x => x < r.Count ? r[x] : "")).Where(x => x.Length > 0)));
            if (amount == 0) { skippedZero++; if (balance is null) continue; }
            rows.Add(new ParsedRow { Date = date.Value, Label = label, Amount = amount, Balance = balance, BankCategory = Cell(r, Col.Category), Page = 0, Top = i });
        }

        // Newest first (most online exports): put the lines in time order, keeping the order of a same day reversed too.
        var later = 0;
        var earlier = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].Date > rows[i - 1].Date) later++;
            else if (rows[i].Date < rows[i - 1].Date) earlier++;
        }
        var newestFirst = earlier > later;
        if (newestFirst) rows.Reverse();

        var kind = forcedKind ?? (hasCard ? AccountKind.CreditCard : savings ? AccountKind.Savings : AccountKind.Bank);
        // A card export with one signed amount column: purchases may be written as positive numbers.
        if (kind == AccountKind.CreditCard && !cols.ContainsKey(Col.Debit) && !cols.ContainsKey(Col.Credit))
        {
            var pay = rows.Where(x => IsPayment(x.Label, x.BankCategory)).ToList();
            var other = rows.Except(pay).ToList();
            if (other.Count > 0 && other.Count(x => x.Amount > 0) > other.Count / 2 && pay.All(x => x.Amount <= 0))
                foreach (var x in rows) x.Amount = -x.Amount;
        }

        // Running balances: every line checked against the one before it.
        int checks = 0, mismatches = 0;
        decimal? prev = null;
        foreach (var x in rows)
        {
            if (x.Balance is { } bal)
            {
                if (prev is { } p)
                {
                    checks++;
                    if (p + x.Amount != bal) mismatches++;
                }
                prev = bal;
            }
            else prev = prev is { } p2 ? p2 + x.Amount : null;
        }
        var withBalance = rows.Where(x => x.Balance is not null).ToList();
        decimal? opening = withBalance.Count > 0 && rows[0].Balance is { } fb ? fb - rows[0].Amount : null;
        decimal? closing = withBalance.Count > 0 ? rows[^1].Balance ?? withBalance[^1].Balance : null;
        // Lines of zero (waived fees) are left out, but their dates still count for the period the balances cover:
        // they prove the balance on those days.
        var allDates = rows.Select(x => x.Date).ToList();
        rows = rows.Where(x => x.Amount != 0).ToList();

        var profile = IsBncHeader(headerIndex >= 0 ? table[headerIndex] : []) ? "bnc" : null;
        var last4 = Regex.Match(Cell(data[0], Col.Card) + " " + Cell(data[0], Col.Account), @"(\d{4})\s*$") is { Success: true } m ? m.Groups[1].Value : null;
        var currency = Cell(data[0], Col.Currency) is { Length: 3 } c3 && c3.All(char.IsAsciiLetter) ? c3.ToUpperInvariant() : defaultCurrency;

        var status = rows.Count == 0 ? VerificationStatus.NoTransactions
            : checks > 0 && mismatches == 0 && checks >= withBalance.Count - 1 ? VerificationStatus.Verified
            : checks > 0 ? VerificationStatus.Mismatch
            : VerificationStatus.NoBalances;
        return new StatementParse
        {
            Rows = rows,
            OpeningBalance = opening,
            ClosingBalance = closing,
            ComputedClosing = opening is { } o ? o + rows.Sum(x => x.Amount) : null,
            Difference = status == VerificationStatus.Verified ? 0 : null,
            PeriodStart = rows.Count > 0 ? allDates.Min() : null,
            PeriodEnd = rows.Count > 0 ? allDates.Max() : null,
            Currency = currency,
            Language = french ? "fr" : "en",
            Kind = kind,
            Bank = profile == "bnc" ? KnownBanks.National : null,
            Last4 = last4,
            Status = status,
            RowBalanceChecks = checks,
            RowBalanceMismatches = mismatches,
            Method = "CSV",
            DateOrder = order,
            Profile = profile,
            NewestFirst = newestFirst,
            ZeroRowsSkipped = skippedZero,
            FirstPageText = headerIndex >= 0 ? string.Join(" · ", table[headerIndex]) : "",
        };
    }

    /// <summary>Banque Nationale exports: "Date;Numero de Carte;Description;Categorie;Debit;Credit" (Mastercard) and
    /// "Date;Description;Categorie;Debit;Credit;Solde" (chequing, savings).</summary>
    private static bool IsBncHeader(List<string> header)
    {
        var h = string.Join(";", header.Select(x => TextNorm.Fold(x)));
        return h is "DATE;NUMERO DE CARTE;DESCRIPTION;CATEGORIE;DEBIT;CREDIT" or "DATE;DESCRIPTION;CATEGORIE;DEBIT;CREDIT;SOLDE";
    }

    private static bool IsPayment(string label, string category)
    {
        var f = TextNorm.Fold(label + " " + category);
        return f.Contains("PAIEMENT", StringComparison.Ordinal) || f.Contains("PAYMENT", StringComparison.Ordinal) || f.Contains("MERCI", StringComparison.Ordinal) || f.Contains("THANK YOU", StringComparison.Ordinal);
    }

    /// <summary>Files without a header row: the date, money and text columns are recognised by their content.</summary>
    private static void InferMissingColumns(Dictionary<Col, int> cols, List<List<string>> data, char decimalSep)
    {
        var width = data.Max(r => r.Count);
        var sample = data.Take(200).ToList();
        double Share(int i, Func<string, bool> test) => sample.Count(r => i < r.Count && r[i].Length > 0 && test(r[i])) / (double)Math.Max(1, sample.Count(r => i < r.Count && r[i].Length > 0));
        var used = new HashSet<int>(cols.Values);
        if (!cols.ContainsKey(Col.Date))
        {
            for (var i = 0; i < width; i++)
                if (!used.Contains(i) && Share(i, c => DateParser.TryParse(c, out _)) > 0.8) { cols[Col.Date] = i; used.Add(i); break; }
        }
        if (!cols.ContainsKey(Col.Amount) && !cols.ContainsKey(Col.Debit) && !cols.ContainsKey(Col.Credit))
        {
            var money = Enumerable.Range(0, width).Where(i => !used.Contains(i) && Share(i, c => TryNumber(c, decimalSep, out _) && !DateParser.TryParse(c, out _)) > 0.9).ToList();
            // Typical layouts: amount | amount, balance | debit, credit | debit, credit, balance.
            if (money.Count >= 3) { cols[Col.Debit] = money[^3]; cols[Col.Credit] = money[^2]; cols[Col.Balance] = money[^1]; }
            else if (money.Count == 2)
            {
                var sparse = money.All(i => sample.Count(r => i < r.Count && r[i].Length == 0) > sample.Count / 4);
                if (sparse) { cols[Col.Debit] = money[0]; cols[Col.Credit] = money[1]; }
                else { cols[Col.Amount] = money[0]; cols[Col.Balance] = money[1]; }
            }
            else if (money.Count == 1) cols[Col.Amount] = money[0];
            foreach (var i in money) used.Add(i);
        }
        if (!cols.ContainsKey(Col.Description))
        {
            var text = Enumerable.Range(0, width).Where(i => !used.Contains(i))
                .OrderByDescending(i => sample.Average(r => i < r.Count ? r[i].Count(char.IsLetter) : 0)).FirstOrDefault(-1);
            if (text >= 0) cols[Col.Description] = text;
        }
    }

    private static StatementParse Empty(string method, string currency) => new() { Status = VerificationStatus.NoTransactions, Method = method, Currency = currency };
}
