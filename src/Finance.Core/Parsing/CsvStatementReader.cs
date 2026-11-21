using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.Classification;
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
        return IsWindows1252(bytes) ? Encoding.GetEncoding(1252).GetString(bytes) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// A text file without a byte order mark that is not valid UTF-8: written in Windows-1252, as many banks export
    /// ("é" as the single byte E9). Read as such, every character as the bank wrote it.
    /// </summary>
    public static bool IsWindows1252(byte[] bytes) =>
        !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) && !(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        && !System.Text.Unicode.Utf8.IsValid(bytes);

    private static char DetectDelimiter(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(30).ToList();
        var best = ',';
        var bestScore = -1.0;
        foreach (var d in new[] { ';', ',', '\t', '|' })
        {
            // Lines of title above the table (account, name, dates) hold no separator: only the lines of cells count.
            var counts = lines.Select(l => Split(l, d).Count).Where(c => c >= 2).ToList();
            if (counts.Count == 0) continue;
            var mode = counts.GroupBy(c => c).OrderByDescending(g => g.Count()).First();
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

    private enum Col { None, Date, ValueDate, Description, Description2, Category, SubCategory, Debit, Credit, Amount, Balance, Card, Account, Currency, Type, Fee, State }

    private static Col Classify(string header)
    {
        var f = " " + Regex.Replace(TextNorm.Fold(header), @"[^A-Z0-9]+", " ").Trim() + " ";
        bool Has(params string[] words) => words.Any(w => f.Contains(" " + w + " ", StringComparison.Ordinal));
        // "Name : value" cells above the table (bank, branch, account, currency of the export) are not column names.
        if (header.Contains(" : ", StringComparison.Ordinal) || header.TrimEnd().EndsWith(" :", StringComparison.Ordinal)) return Col.None;
        if (Has("POINTAGE", "RAPPROCHEMENT")) return Col.None;
        // What an operation was before the bank converted it (its amount and currency abroad) is not what moved on the account.
        if (Has("ORIG", "ORIGINAL", "ORIGINALE", "ORIGINE")) return Col.None;
        if (Has("NUMERO", "NUMBER", "NO", "REF", "REFERENCE") && Has("OPERATION", "TRANSACTION", "CHEQUE")) return Col.None;
        if (Has("SOLDE", "BALANCE")) return Col.Balance;
        if (Has("SOUS") && Has("CATEGORIE", "CATEGORY")) return Col.SubCategory;
        if (Has("CATEGORIE", "CATEGORY", "CATEGORIES")) return Col.Category;
        if (Has("CARTE", "CARD")) return Has("DATE") ? Col.Date : Col.Card;
        if (Has("DATE", "POSTED", "POSTE", "JOUR"))
            return Has("VALEUR", "VALUE", "POSTED", "POSTING", "INSCRIPTION", "COMPTABILISATION", "POSTE") && !Has("TRANSACTION", "OPERATION", "ACHAT") ? Col.ValueDate : Col.Date;
        if (Has("DEVISE", "CURRENCY", "MONNAIE")) return Has("FEE", "FEES", "FRAIS") ? Col.None : Col.Currency;
        if (Has("FEE", "FEES", "FRAIS", "COMMISSION")) return Col.Fee;
        if (Has("STATE", "STATUS", "ETAT", "STATUT")) return Col.State;
        if (Has("DEBIT", "DEBITS", "RETRAIT", "RETRAITS", "WITHDRAWAL", "WITHDRAWALS", "SORTIE", "SORTIES", "OUT", "CHARGES", "PAID")) return Col.Debit;
        if (Has("CREDIT", "CREDITS", "DEPOT", "DEPOTS", "DEPOSIT", "DEPOSITS", "ENTREE", "ENTREES", "IN", "RECEIVED")) return Col.Credit;
        if (Has("MONTANT", "AMOUNT", "VALUE", "VALEUR", "SOMME") || f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(AmountParser.Currencies.Contains)) return Col.Amount;
        if (Has("TYPE")) return Col.Type;
        if (Has("COMPTE", "ACCOUNT", "NUMERO", "NUMBER", "PRODUCT", "PRODUIT", "POCKET", "VAULT")) return Col.Account;
        if (Has("DESCRIPTION", "LIBELLE", "DETAILS", "DETAIL", "MERCHANT", "COMMERCANT", "PAYEE", "BENEFICIAIRE", "NAME", "NOM", "MEMO",
                "TRANSACTION", "OPERATION", "NARRATIVE", "REFERENCE", "LABEL", "INFORMATIONS"))
            return Has("2") || Has("MEMO") || Has("SIMPLIFIE", "SIMPLIFIED", "SHORT") ? Col.Description2 : Col.Description;
        return Col.None;
    }

    [GeneratedRegex(@"^\(?[-+−]?\s*(?:\p{Sc}|" + AmountParser.CurrencyCodes + @")?\s*[-+−]?\d[\d\s  .,']*\)?\s*(?:\p{Sc}|" + AmountParser.CurrencyCodes + @")?\s*(?:-|CR|DR)?$")]
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

    /// <summary>
    /// How many lines of <paramref name="balance"/> follow the line before by the amount of <paramref name="amount"/>
    /// (the oldest operation first, or the newest first).
    /// </summary>
    private static int Running(List<List<string>> data, int balance, int amount, Func<int, char> sep, int account = -1)
    {
        decimal? Value(List<string> r, int i) => i < r.Count && TryNumber(r[i], sep(i), out var v) ? v : null;
        string Account(List<string> r) => account >= 0 && account < r.Count ? r[account].Trim() : "";
        var n = 0;
        // Each line against the line before it of the same account.
        var previous = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in data)
        {
            var key = Account(row);
            var last = previous.GetValueOrDefault(key);
            previous[key] = row;
            if (last is null || Value(row, balance) is not { } now || Value(last, balance) is not { } before) continue;
            if (Value(row, amount) is { } a && now - before == a) n++;
            else if (Value(last, amount) is { } b && before - now == b) n++;
        }
        return n;
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

    [GeneratedRegex(@"^(?<day>.*?\d)(?:T|\s+)\d{1,2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?\s*(?:AM|PM)?\s*(?:Z|UTC|GMT|[+-]\d{2}:?\d{2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex Timed();

    /// <summary>The day of a cell that also gives the time ("2026-01-05 10:23:45", "05/01/2026 10:23", "2026-01-05T10:23:45Z").</summary>
    private static string Day(string cell) => Timed().Match(cell.Trim()) is { Success: true } m ? m.Groups["day"].Value : cell;

    private static bool TryDate(string cell, out PrintedDate date) => DateParser.TryParse(Day(cell), out date);

    /// <summary>Words a bank writes for an operation that never moved money on the account, or has not yet.</summary>
    private static readonly HashSet<string> NotDone = new(StringComparer.Ordinal)
    {
        "PENDING", "REVERTED", "DECLINED", "FAILED", "CANCELLED", "CANCELED", "REJECTED", "REFUSED", "VOIDED", "VOID",
        "EN ATTENTE", "EN COURS", "ANNULE", "ANNULEE", "REFUSE", "REFUSEE", "ECHOUE", "ECHOUEE", "ECHEC", "REJETE", "REJETEE",
    };

    /// <summary>
    /// A file holding several accounts cut into one table per account: one per currency (two currencies are never one
    /// account), and one per account or product named on each line when the balances of the file only follow each
    /// other account by account. A file of one account comes back alone, untouched.
    /// </summary>
    /// <returns>Each account: what tells it apart ("EUR", "Savings · EUR") and its table, the lines above the first operation kept.</returns>
    public static List<(string Title, List<List<string>> Table)> Accounts(List<List<string>> table, string defaultCurrency)
    {
        var whole = new List<(string, List<List<string>>)> { ("", table) };
        var headerIndex = HeaderIndex(table);
        if (headerIndex < 0) return whole;
        var h = table[headerIndex];
        int Find(Col c) => Enumerable.Range(0, h.Count).Where(i => Classify(h[i]) == c).DefaultIfEmpty(-1).First();
        var (currency, account) = (Find(Col.Currency), Find(Col.Account));
        if (currency < 0 && account < 0) return whole;
        var data = table.Skip(headerIndex + 1).ToList();
        string At(List<string> r, int i) => i >= 0 && i < r.Count ? r[i].Trim() : "";
        bool Money(List<string> r) => At(r, currency) is { Length: 3 } c && c.All(char.IsAsciiLetter);

        List<(string, List<List<string>>)> Cut(Func<List<string>, string> key)
        {
            var groups = data.Where(r => r.Any(c => c.Length > 0)).GroupBy(key).Where(g => g.Key.Length > 0).ToList();
            return groups.Count < 2 ? whole : [.. groups.Select(g => (g.Key, (List<List<string>>)[.. table.Take(headerIndex + 1), .. g]))];
        }
        // Two currencies: always two accounts.
        var byCurrency = currency >= 0 && data.Where(Money).Select(r => At(r, currency).ToUpperInvariant()).Distinct().Count() > 1;
        // Several accounts named line by line: apart when the balances prove it (the file does not balance as one
        // account, each account does), or when currencies already cut the file and their balances are not enough.
        if (account >= 0 && data.Select(r => At(r, account)).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        {
            string Both(List<string> r) => At(r, account).Length == 0 ? "" : byCurrency ? Money(r) ? $"{At(r, account)} · {At(r, currency).ToUpperInvariant()}" : "" : At(r, account);
            var cut = Cut(Both);
            bool Balanced(List<List<string>> t) => ParseTable(t, "", defaultCurrency, null, "CSV").Status is VerificationStatus.Verified;
            var before = byCurrency ? Cut(r => Money(r) ? At(r, currency).ToUpperInvariant() : "") : whole;
            // An account of one line has nothing to follow on: it cannot contradict the cut.
            bool Agrees(List<List<string>> t) => ParseTable(t, "", defaultCurrency, null, "CSV") is var p
                && (p.Status is VerificationStatus.Verified or VerificationStatus.NoTransactions || p.Rows.Count == 1);
            if (cut.Count > 1 && !before.All(p => Balanced(p.Item2)) && cut.All(p => Agrees(p.Item2))
                && cut.Any(p => Balanced(p.Item2)))
                return [.. cut.Where(p => ParseTable(p.Item2, "", defaultCurrency, null, "CSV").Rows.Count > 0)];
        }
        return byCurrency ? Cut(r => Money(r) ? At(r, currency).ToUpperInvariant() : "") : whole;
    }

    /// <summary>A table written back as a CSV file (semicolons, every cell quoted), read again like any export.</summary>
    public static byte[] Write(List<List<string>> table) =>
        Encoding.UTF8.GetBytes(string.Join("\n", table.Select(r => string.Join(';', r.Select(c => '"' + c.Replace("\"", "\"\"") + '"')))));

    /// <summary>The cells of a CSV file, line by line.</summary>
    public static List<List<string>> Table(byte[] bytes)
    {
        var text = Decode(bytes);
        return Rows(text, DetectDelimiter(text)).ToList();
    }

    /// <summary>The first row whose cells are column names rather than data, or -1.</summary>
    private static int HeaderIndex(List<List<string>> table)
    {
        for (var i = 0; i < Math.Min(table.Count, 15); i++)
        {
            var named = table[i].Count(c => Classify(c) != Col.None && !c.Contains(" : ", StringComparison.Ordinal));
            var dated = table[i].Count(c => TryDate(c, out _));
            if (named >= 2 && dated == 0) return i;
        }
        return -1;
    }

    /// <param name="forcedKind">Account type chosen by the person; when null the file is only read, not typed.</param>
    /// <param name="format">The date format of the bank chosen for the file; null before a bank is chosen.</param>
    public static StatementParse Parse(byte[] bytes, string fileName, string defaultCurrency, AccountKind? forcedKind = null, DateFormat? format = null)
    {
        var text = Decode(bytes);
        var delimiter = DetectDelimiter(text);
        return ParseTable(Rows(text, delimiter).ToList(), fileName, defaultCurrency, forcedKind, "CSV", delimiter == ',' ? '.' : null, format);
    }

    /// <summary>
    /// Reads a table of cells, whatever file it came from (CSV, Excel, OpenDocument, Word, a web page): the columns are
    /// found by their names or by what they contain, as for a CSV export.
    /// </summary>
    /// <param name="decimalSeparator">Known separator of decimals, or null to find it from the numbers.</param>
    /// <param name="format">The date format of the bank chosen for the file; null before a bank is chosen.</param>
    public static StatementParse ParseTable(List<List<string>> table, string fileName, string defaultCurrency, AccountKind? forcedKind, string method, char? decimalSeparator = null,
        DateFormat? format = null)
    {
        if (table.Count == 0) return Empty(method, defaultCurrency);
        // The width of the table: that of most of its lines of several cells (lines of title above it are one cell).
        var width = (table.Any(r => r.Count >= 2) ? table.Where(r => r.Count >= 2) : table).GroupBy(r => r.Count).OrderByDescending(g => g.Count()).First().Key;

        // Header: the first row whose cells are column names rather than data.
        var headerIndex = HeaderIndex(table);
        var data = table.Skip(headerIndex + 1).Where(r => r.Count >= Math.Min(width, 3) && r.Any(c => c.Length > 0)).ToList();
        if (data.Count == 0) return Empty(method, defaultCurrency);
        var decimalSep = decimalSeparator ?? DecimalSeparator(data.SelectMany(r => r));

        var cols = new Dictionary<Col, int>();
        var descExtra = new List<int>();
        // A second column named as a balance (BoursoBank's newer export names the amount and the balance "Solde").
        int? twinBalance = null;
        if (headerIndex >= 0)
        {
            var h = table[headerIndex];
            for (var i = 0; i < h.Count; i++)
            {
                var role = Classify(h[i]);
                if (role == Col.None) continue;
                if (role == Col.Balance && cols.ContainsKey(Col.Balance) && twinBalance is null) { twinBalance = i; continue; }
                if (role == Col.Date && cols.ContainsKey(Col.Date)) role = Col.ValueDate;
                if (role == Col.Description && cols.ContainsKey(Col.Description)) role = Col.Description2;
                if (role == Col.Description2) { descExtra.Add(i); continue; }
                cols.TryAdd(role, i);
            }
        }
        // Each column of money writes its decimals its own way (BoursoBank: the amount "-1 100,50", the balance
        // "13853.59"): read from its own numbers, the whole file's way when they do not tell.
        char SepAt(int index)
        {
            if (decimalSeparator is { } known) return known;
            var cells = data.Select(r => index < r.Count ? r[index] : "").ToList();
            var commas = cells.Count(c => Regex.IsMatch(c, @"\d,\d{1,2}(\D*)$"));
            var dots = cells.Count(c => Regex.IsMatch(c, @"\d\.\d{1,2}(\D*)$"));
            return commas > dots ? ',' : dots > commas ? '.' : decimalSep;
        }
        if (twinBalance is { } twin && !cols.ContainsKey(Col.Amount) && !cols.ContainsKey(Col.Debit) && !cols.ContainsKey(Col.Credit))
        {
            // The balance is the one whose lines follow each other by the other one's amounts (in either order of lines;
            // account by account in a file of several accounts); the later column when that does not tell.
            var first = cols[Col.Balance];
            var account = cols.GetValueOrDefault(Col.Account, -1);
            (cols[Col.Amount], cols[Col.Balance]) = Running(data, first, twin, SepAt, account) > Running(data, twin, first, SepAt, account) ? (twin, first) : (first, twin);
        }
        InferMissingColumns(cols, data, decimalSep, descExtra);
        char Sep(Col c) => cols.TryGetValue(c, out var i) ? SepAt(i) : decimalSep;
        // Without a header, every text column says something ("Point of sale purchase", then the shop): in their order.
        var labelColumns = (cols.TryGetValue(Col.Description, out var mainText) ? new[] { mainText } : []).Concat(descExtra);
        if (headerIndex < 0) labelColumns = labelColumns.OrderBy(i => i);
        var labelIndexes = labelColumns.ToList();
        if (!cols.ContainsKey(Col.Date) || !(cols.ContainsKey(Col.Amount) || cols.ContainsKey(Col.Debit) || cols.ContainsKey(Col.Credit)))
            return Empty(method, defaultCurrency);

        string Cell(List<string> r, Col c) => cols.TryGetValue(c, out var i) && i < r.Count ? r[i] : "";

        // Dates and their order (day/month or month/day) across the whole file.
        var printed = data.Select(r => TryDate(Cell(r, Col.Date), out var d) ? d : (PrintedDate?)null).ToList();
        var numeric = printed.Where(d => d is { MonthKnown: false, IsoOrder: false }).Select(d => d!.Value).ToList();
        var year = DateTime.Today.Year;
        // The bank's date format decides. Before a bank is chosen, what the dates show: a day above 12, else the order
        // under which they span the shorter time (a file covers days that follow each other), else month first.
        int Span(DateOrder o)
        {
            var days = numeric.Select(d => d.ToDate(o, d.Year ?? year)).Where(d => d is not null).Select(d => d!.Value.DayNumber).ToList();
            return days.Count < numeric.Count || days.Count == 0 ? int.MaxValue : days.Max() - days.Min();
        }
        var (dayFirst, monthFirst) = (Span(DateOrder.DayMonth), Span(DateOrder.MonthDay));
        var order = format is { } bankFormat && BankDates.Order(bankFormat) is not DateOrder.Unknown and var set ? set
            : numeric.Any(d => d.A > 12) ? DateOrder.DayMonth
            : numeric.Any(d => d.B > 12) ? DateOrder.MonthDay
            : dayFirst < monthFirst ? DateOrder.DayMonth
            : DateOrder.MonthDay;

        // The name of a bank says nothing of the account ("Caisse d'Épargne", "Crédit Mutuel"): left out.
        var name = Keywords.WithoutBankNames(TextNorm.Fold(Path.GetFileNameWithoutExtension(fileName)));
        // Lines above the table: "Numéro de compte : ...", "Nom du compte : LIVRET A", "Devise : EUR", and balance lines
        // ("Solde en fin de période;;;;+1 996,33") above or below it.
        var about = Metadata(table, headerIndex, decimalSep);
        name += " " + about.AccountName;
        var hasCard = cols.ContainsKey(Col.Card) || Regex.IsMatch(name, @"(VISA|MASTERCARD|CREDIT|CARTE|CARD|AMEX|AMERICAN EXPRESS)");
        var savings = Regex.IsMatch(name, @"(EPARGNE|SAVINGS|HIGHINV|HISA|CELI|TFSA|REER|RRSP|LIVRET)");

        var rows = new List<ParsedRow>();
        var skippedZero = 0;
        // What the bank took on an operation, written in a column of its own: a line of its own, after the operation.
        var fees = new Dictionary<ParsedRow, decimal>();
        var feeName = cols.TryGetValue(Col.Fee, out var feeColumn) && headerIndex >= 0 && feeColumn < table[headerIndex].Count ? table[headerIndex][feeColumn].Trim() : "Fee";
        for (var i = 0; i < data.Count; i++)
        {
            var r = data[i];
            if (printed[i] is not { } pd) continue;
            // An operation still pending, declined or reverted never moved the money.
            if (NotDone.Contains(TextNorm.Fold(Cell(r, Col.State)).Trim())) continue;
            var date = pd.ToDate(order, pd.Year ?? year);
            if (date is null) continue;
            decimal amount;
            if (cols.ContainsKey(Col.Debit) || cols.ContainsKey(Col.Credit))
            {
                TryNumber(Cell(r, Col.Debit), Sep(Col.Debit), out var debit);
                TryNumber(Cell(r, Col.Credit), Sep(Col.Credit), out var credit);
                amount = Math.Abs(credit) - Math.Abs(debit);
                if (!cols.ContainsKey(Col.Debit) && credit < 0) amount = credit;
                if (!cols.ContainsKey(Col.Credit) && debit < 0) amount = -debit;
            }
            else if (!TryNumber(Cell(r, Col.Amount), Sep(Col.Amount), out amount)) continue;
            var type = TextNorm.Fold(Cell(r, Col.Type));
            if (type is "DEBIT" or "DR" or "D" or "WITHDRAWAL" or "RETRAIT") amount = -Math.Abs(amount);
            else if (type is "CREDIT" or "CR" or "C" or "DEPOSIT" or "DEPOT") amount = Math.Abs(amount);

            decimal? balance = TryNumber(Cell(r, Col.Balance), Sep(Col.Balance), out var b) ? b : null;
            var label = Label(labelIndexes.Select(x => x < r.Count ? r[x] : ""));
            var fee = TryNumber(Cell(r, Col.Fee), Sep(Col.Fee), out var f) ? Math.Abs(f) : 0;
            if (amount == 0 && fee == 0) { skippedZero++; if (balance is null) continue; }
            // The bank's sub-category says more ("Revenus" / "Salaire") when the app knows it.
            var bankCategory = Cell(r, Col.SubCategory) is { Length: > 0 } sub && BankCategories.Map(sub, amount) is not null ? sub : Cell(r, Col.Category);
            // Without a category, the kind of operation the bank names ("TRANSFER", "ATM") stands for it.
            if (bankCategory.Length == 0 && !cols.ContainsKey(Col.Category) && type.Length > 0) bankCategory = Cell(r, Col.Type).Replace('_', ' ');
            var row = new ParsedRow { Date = date.Value, Label = label, Amount = amount, Balance = balance, BankCategory = bankCategory, Page = 0, Top = i };
            rows.Add(row);
            if (fee != 0) fees[row] = fee;
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
        // The balance printed on a line is the one after its fee: the operation first (balance before the fee), then the fee.
        if (fees.Count > 0)
        {
            var withFees = new List<ParsedRow>(rows.Count + fees.Count);
            foreach (var x in rows)
            {
                if (!fees.TryGetValue(x, out var fee)) { withFees.Add(x); continue; }
                var after = x.Balance;
                if (x.Amount != 0 || after is null) { x.Balance = after + fee; withFees.Add(x); }
                withFees.Add(new ParsedRow { Date = x.Date, Label = $"{feeName}: {x.Label}", Amount = -fee, Balance = after, BankCategory = feeName, Page = 0, Top = x.Top });
            }
            rows = withFees;
        }

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
        decimal? opening = withBalance.Count > 0 && rows[0].Balance is { } fb ? fb - rows[0].Amount : about.Opening;
        decimal? closing = withBalance.Count > 0 ? rows[^1].Balance ?? withBalance[^1].Balance : about.Closing;
        // Balances printed above or below the table ("Solde au 31/10/2025") are checks of the running balance too: a
        // month of one operation is proven by them alone.
        if (withBalance.Count > 0 && about.Closing is { } printedClosing && closing is { } runningClosing)
        {
            checks++;
            if (printedClosing != runningClosing) mismatches++;
        }
        if (withBalance.Count > 0 && rows[0].Balance is not null && about.Opening is { } printedOpening && opening is { } runningOpening)
        {
            checks++;
            if (printedOpening != runningOpening) mismatches++;
        }
        // Lines of zero (waived fees) are left out, but their dates still count for the period the balances cover:
        // they prove the balance on those days.
        var allDates = rows.Select(x => x.Date).ToList();
        rows = rows.Where(x => x.Amount != 0).ToList();

        var profile = IsBncHeader(headerIndex >= 0 ? table[headerIndex] : []) ? "bnc" : null;
        var last4 = Regex.Match(Cell(data[0], Col.Card) + " " + Cell(data[0], Col.Account), @"(\d{4})\s*$") is { Success: true } m ? m.Groups[1].Value : about.Last4;
        // The currency column, the currency said above the table, the one the columns of money are written in, or the one
        // its operations name (SEPA operations are in euros); else the person's own.
        var currency = Cell(data[0], Col.Currency) is { Length: 3 } c3 && c3.All(char.IsAsciiLetter) ? c3.ToUpperInvariant()
            : about.Currency ?? MoneyColumnsCurrency(headerIndex >= 0 ? table[headerIndex] : [], data, cols)
            ?? DocumentContext.CurrencyNamedByOperations(rows.Select(r => r.Label)) ?? defaultCurrency;

        // Without a balance on each line, the balances printed above or below the table prove the total.
        var printedProof = checks == 0 && about.Opening is { } po && about.Closing is { } pc && rows.Count > 0;
        var status = rows.Count == 0 ? VerificationStatus.NoTransactions
            : checks > 0 && mismatches == 0 && checks >= withBalance.Count - 1 ? VerificationStatus.Verified
            : checks > 0 ? VerificationStatus.Mismatch
            : printedProof ? (about.Opening!.Value + rows.Sum(x => x.Amount) == about.Closing!.Value ? VerificationStatus.Verified : VerificationStatus.Mismatch)
            : VerificationStatus.NoBalances;
        return new StatementParse
        {
            Rows = rows,
            OpeningBalance = opening,
            ClosingBalance = closing,
            ComputedClosing = opening is { } o ? o + rows.Sum(x => x.Amount) : null,
            Difference = status == VerificationStatus.Verified ? 0 : printedProof ? about.Closing - (about.Opening + rows.Sum(x => x.Amount)) : null,
            PeriodStart = rows.Count > 0 ? allDates.Min() : null,
            PeriodEnd = rows.Count > 0 ? allDates.Max() : null,
            Currency = currency,
            Kind = kind,
            Bank = profile == "bnc" ? KnownBanks.National : null,
            Last4 = last4,
            Status = status,
            RowBalanceChecks = checks,
            RowBalanceMismatches = mismatches,
            Method = method,
            DateOrder = order,
            DateFormatMismatch = format is { } chosen ? BankDates.Mismatch(chosen, [.. numeric.Select(d => (d.A, d.B))]) : null,
            Profile = profile,
            NewestFirst = newestFirst,
            ZeroRowsSkipped = skippedZero,
            FirstPageText = headerIndex >= 0 ? string.Join(" · ", table[headerIndex]) : "",
        };
    }

    /// <summary>
    /// The texts of one line, main one first: a text already said is left out, and a text that says more than the
    /// one before replaces it ("CB X FACT 031025" then the detail "CB X FACT 031025 PARIS").
    /// </summary>
    private static string Label(IEnumerable<string> texts)
    {
        var label = "";
        foreach (var raw in texts)
        {
            var t = TextNorm.CollapseSpaces(raw);
            if (t.Length == 0 || t == "-") continue;
            var (ft, fl) = (TextNorm.Fold(t), TextNorm.Fold(label));
            if (fl.Contains(ft, StringComparison.Ordinal)) continue;
            label = label.Length > 0 && ft.Contains(fl, StringComparison.Ordinal) ? t : label.Length == 0 ? t : label + " " + t;
        }
        return label;
    }

    /// <summary>
    /// The currency the columns of money are written in: named in their headings ("Amount (CHF)", "Montant EUR") or beside
    /// the amounts ("-45.10 CHF", "12,34 €"), when every one that names a currency names the same. Null otherwise.
    /// </summary>
    private static string? MoneyColumnsCurrency(List<string> header, List<List<string>> data, Dictionary<Col, int> cols)
    {
        var money = new[] { Col.Amount, Col.Debit, Col.Credit, Col.Balance, Col.Fee }.Where(cols.ContainsKey).Select(c => cols[c]).ToList();
        var named = money.Where(i => i < header.Count).Select(i => header[i])
            .Concat(data.SelectMany(r => money.Where(i => i < r.Count).Select(i => r[i])))
            .Select(AmountParser.CurrencyOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private sealed record About(string AccountName, string? Last4, string? Currency, decimal? Opening, decimal? Closing);

    /// <summary>What the lines around the table say: account name and number, currency, opening and closing balances.</summary>
    private static About Metadata(List<List<string>> table, int headerIndex, char decimalSep)
    {
        string accountName = "";
        string? last4 = null, currency = null;
        decimal? opening = null, closing = null;
        for (var i = 0; i < table.Count; i++)
        {
            if (i == headerIndex) continue;
            var cells = table[i];
            if (cells.Count == 0) continue;
            foreach (var c in cells)
            {
                var kv = c.Split(" : ", 2);
                if (kv.Length != 2) continue;
                var key = TextNorm.Fold(kv[0]).Trim();
                var value = kv[1].Trim();
                if (key.StartsWith("NOM DU COMPTE", StringComparison.Ordinal) || key is "ACCOUNT NAME" or "INTITULE") accountName = TextNorm.Fold(value);
                else if ((key.StartsWith("NUMERO DE COMPTE", StringComparison.Ordinal) || key is "ACCOUNT NUMBER" or "COMPTE") && Regex.Match(value, @"(\d{4})\s*$") is { Success: true } n) last4 = n.Groups[1].Value;
                else if (key is "DEVISE" or "CURRENCY" && value.Length == 3 && value.All(char.IsAsciiLetter)) currency = value.ToUpperInvariant();
            }
            // Balance lines: no date of operation, the first cell written names the balance, a number after it.
            var named = cells.FindIndex(c => c.Trim().Length > 0);
            if (named < 0 || cells.Any(c => DateParser.TryParse(c, out _))) continue;
            var first = TextNorm.Fold(cells[named]);
            if (!first.StartsWith("SOLDE", StringComparison.Ordinal) && !first.Contains("BALANCE", StringComparison.Ordinal)) continue;
            var number = cells.Skip(named + 1).Select(c => TryNumber(c, decimalSep, out var v) ? v : (decimal?)null).LastOrDefault(v => v is not null);
            if (number is null) continue;
            if (Regex.IsMatch(first, @"\b(FIN|FINAL|NOUVEAU|CLOSING|ENDING|END)\b")) closing = number;
            else if (Regex.IsMatch(first, @"\b(DEBUT|INITIAL|PRECEDENT|OPENING|PREVIOUS|START|STARTING)\b")) opening = number;
            // "Solde au 31/10/2025", "Balance as of ...": a balance on a day, the closing one under the operations, the
            // opening one above them.
            else if (headerIndex >= 0 && Keywords.ContainsAny(first, Keywords.BalanceAsOf))
            {
                if (i > headerIndex) closing = number;
                else opening = number;
            }
        }
        return new About(accountName, last4, currency, opening, closing);
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
    private static void InferMissingColumns(Dictionary<Col, int> cols, List<List<string>> data, char decimalSep, List<int> descExtra)
    {
        var width = data.Max(r => r.Count);
        var sample = data.Take(200).ToList();
        double Share(int i, Func<string, bool> test) => sample.Count(r => i < r.Count && r[i].Length > 0 && test(r[i])) / (double)Math.Max(1, sample.Count(r => i < r.Count && r[i].Length > 0));
        var used = new HashSet<int>(cols.Values);
        if (!cols.ContainsKey(Col.Date))
        {
            for (var i = 0; i < width; i++)
                if (!used.Contains(i) && Share(i, c => TryDate(c, out _)) > 0.8) { cols[Col.Date] = i; used.Add(i); break; }
        }
        if (!cols.ContainsKey(Col.Amount) && !cols.ContainsKey(Col.Debit) && !cols.ContainsKey(Col.Credit))
        {
            var money = Enumerable.Range(0, width).Where(i => !used.Contains(i) && Share(i, c => TryNumber(c, decimalSep, out _) && !TryDate(c, out _)) > 0.9).ToList();
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
            if (text >= 0)
            {
                cols[Col.Description] = text;
                used.Add(text);
                // Other columns of words (the kind of operation, a second description) go in the label too.
                foreach (var i in Enumerable.Range(0, width).Where(i => !used.Contains(i) && !descExtra.Contains(i)))
                    if (sample.Average(r => i < r.Count ? r[i].Count(char.IsLetter) : 0) >= 2) descExtra.Add(i);
            }
        }
    }

    private static StatementParse Empty(string method, string currency) => new() { Status = VerificationStatus.NoTransactions, Method = method, Currency = currency };
}
