using System.Text.RegularExpressions;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>Everything the reader learns about a statement before looking at its table.</summary>
internal sealed partial class DocumentContext
{
    public required IReadOnlyList<PageWords> Pages { get; init; }
    public required List<TextLine> Lines { get; init; }
    public required string Language { get; init; }
    public required bool LooksLikeCard { get; init; }
    public required bool LooksLikeSavings { get; init; }
    /// <summary>Known bank layout recognised ("bnc"), or null.</summary>
    public string? Profile { get; init; }
    public required string Currency { get; init; }
    public string? Bank { get; init; }
    public string? Last4 { get; init; }
    public required int DocumentYear { get; init; }
    public required string FirstPageText { get; init; }
    /// <summary>Printed start and end of the statement period, if found.</summary>
    public (PrintedDate Start, PrintedDate End)? Period { get; init; }
    public bool UsedOcr { get; init; }

    public double PageWidth(int page) => Pages.First(p => p.Number == page).Width;

    public static DocumentContext Build(IReadOnlyList<PageWords> pages, string defaultCurrency)
    {
        var lines = pages.SelectMany(LineBuilder.Build).ToList();
        var folded = string.Join('\n', lines.Select(l => TextNorm.Fold(l.Text)));
        // One text block per line, split where words are far apart, so a bank name and an address printed
        // on the same baseline stay separate.
        var firstPage = string.Join('\n', lines.Where(l => l.Page == pages[0].Number).SelectMany(Blocks));

        // What the statement says about itself is printed above its table; the table can name any shop or bank.
        var pageOne = lines.Where(l => l.Page == pages[0].Number).OrderBy(l => l.Top).ToList();
        var tableStart = pageOne.FindIndex(IsTableHeaderLine);
        var top = tableStart >= 0 ? pageOne.Take(tableStart).ToList() : pageOne.Where(l => l.Top < pages[0].Height * 0.35).ToList();
        var headerFolded = string.Join('\n', top.Select(l => TextNorm.Fold(l.Text)));
        var headerBlocks = string.Join('\n', top.SelectMany(Blocks));
        var profile = DetectProfile(folded, lines);

        return new DocumentContext
        {
            Pages = pages,
            Lines = lines,
            Language = DetectLanguage(folded),
            LooksLikeCard = Keywords.ContainsAny(headerFolded, Keywords.Card) || MaskedCard().IsMatch(headerFolded)
                            || (Keywords.ContainsAny(headerFolded, ["MASTERCARD", "VISA", "AMERICAN EXPRESS"]) && !Keywords.ContainsAny(headerFolded, ["COMPTE", "ACCOUNT"])),
            LooksLikeSavings = Keywords.ContainsAny(headerFolded.Replace("CAISSE D'EPARGNE", "").Replace("CAISSE D EPARGNE", ""), Keywords.Savings),
            Profile = profile,
            Currency = DetectCurrency(folded, defaultCurrency),
            Bank = profile == "bnc" ? KnownBanks.National : DetectBank(headerFolded) ?? InstitutionHeading(headerBlocks),
            Last4 = MaskedLast4(headerFolded) ?? DetectLast4(headerFolded) ?? DetectLast4(folded),
            DocumentYear = DetectYear(folded),
            FirstPageText = firstPage,
            Period = DetectPeriod(lines),
            UsedOcr = pages.Any(p => p.FromOcr),
        };
    }

    [GeneratedRegex(@"[*X•]{4,}[ \-]?(\d{4})(?!\d)")]
    private static partial Regex MaskedCard();

    private static string? MaskedLast4(string folded) => MaskedCard().Match(folded) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>A transaction table header: a "Date" word and at least two other column names on one line.</summary>
    private static bool IsTableHeaderLine(TextLine l)
    {
        var words = l.Words.Select(w => TextNorm.Fold(w.Text).Trim('.', ':', '(', ')')).ToList();
        if (!words.Contains("DATE")) return false;
        string[] names = ["DESCRIPTION", "LIBELLE", "MONTANT", "AMOUNT", "SOLDE", "BALANCE", "DEBIT", "CREDIT", "RETRAITS", "DEPOTS", "WITHDRAWALS", "DEPOSITS", "OPERATION", "TRANSACTION", "CATEGORIE"];
        return words.Count(w => names.Contains(w)) >= 2;
    }

    /// <summary>Layouts of banks the reader knows precisely.</summary>
    private static string? DetectProfile(string folded, List<TextLine> lines)
    {
        // Banque Nationale (BNC): online "Historique de mes transactions" export of a chequing, savings or Mastercard account.
        if (folded.Contains("HISTORIQUE DE MES TRANSACTIONS", StringComparison.Ordinal)
            && lines.Any(l => IsTableHeaderLine(l) && TextNorm.Fold(l.Text).Contains("CATEGORIE", StringComparison.Ordinal)))
            return "bnc";
        if (folded.Contains("BANQUE NATIONALE", StringComparison.Ordinal) || folded.Contains("NATIONAL BANK", StringComparison.Ordinal))
            return "bnc";
        return null;
    }

    private static IEnumerable<string> Blocks(TextLine line)
    {
        var current = new List<Word>();
        foreach (var w in line.Words)
        {
            if (current.Count > 0 && w.Left - current[^1].Right > Math.Max(w.Height, current[^1].Height) * 1.5)
            {
                yield return string.Join(' ', current.Select(x => x.Text));
                current.Clear();
            }
            current.Add(w);
        }
        if (current.Count > 0) yield return string.Join(' ', current.Select(x => x.Text));
    }

    [GeneratedRegex(@"[A-Z']+")]
    private static partial Regex Letters();

    private static readonly HashSet<string> FrenchWords =
    [
        "SOLDE", "RELEVE", "LIBELLE", "OPERATION", "OPERATIONS", "VIREMENT", "RETRAIT", "RETRAITS", "DEPOT", "DEPOTS", "PAIEMENT",
        "FRAIS", "DU", "AU", "LE", "LA", "LES", "DES", "COMPTE", "PERIODE", "NOUVEAU", "PRECEDENT", "ACHAT", "CARTE", "DATE", "ET",
    ];

    private static readonly HashSet<string> EnglishWords =
    [
        "BALANCE", "STATEMENT", "DESCRIPTION", "WITHDRAWAL", "WITHDRAWALS", "DEPOSIT", "DEPOSITS", "PAYMENT", "ACCOUNT", "FROM",
        "TO", "THE", "AND", "FEE", "FEES", "PERIOD", "NEW", "PREVIOUS", "PURCHASE", "CARD", "OPENING", "CLOSING", "TOTAL",
    ];

    private static string DetectLanguage(string folded)
    {
        int fr = 0, en = 0;
        foreach (Match m in Letters().Matches(folded))
        {
            if (FrenchWords.Contains(m.Value)) fr++;
            if (EnglishWords.Contains(m.Value)) en++;
        }
        return fr > en ? "fr" : "en";
    }

    private static string DetectCurrency(string folded, string fallback)
    {
        // Whole words only: "EUR" is also the end of VALEUR, DEBITEUR... SEPA operations are euro operations (the euro
        // payments area): a statement that lists them and prints no currency is in euros.
        int Count(params string[] keys) => keys.Sum(k => Regex.Matches(folded, (char.IsLetter(k[0]) ? @"(?<![A-Z])" : "") + Regex.Escape(k) + (char.IsLetter(k[^1]) ? @"(?![A-Z])" : "")).Count);
        var scores = new Dictionary<string, int>
        {
            ["EUR"] = Count("EUR", "€", "EUROS", "SEPA"),
            ["USD"] = Count("USD", "US$", "$ US", "$US"),
            ["GBP"] = Count("GBP", "£"),
            ["CHF"] = Count("CHF"),
            ["CAD"] = Count("CAD", "CA$", "$ CA", "$CA"),
        };
        // A statement that says its currency ("USD Statement", "Relevé en EUR", "Currency: GBP", "Devise : CHF") is in it,
        // whatever other currencies its operations mention (an exchange from euros on a statement in dollars).
        var said = Regex.Match(folded, @"(?<![A-Z])(?:(?<c>[A-Z]{3}) (?:STATEMENT|ACCOUNT STATEMENT)|(?:STATEMENT|RELEVE(?: DE COMPTE)?) (?:IN |EN )(?<c>[A-Z]{3})|(?:CURRENCY|DEVISE) ?:? ?(?<c>[A-Z]{3}))(?![A-Z])");
        if (said.Success && AmountParser.Currencies.Contains(said.Groups["c"].Value)) return said.Groups["c"].Value;
        var best = scores.OrderByDescending(kv => kv.Value).First();
        if (best.Value > 0) return best.Key;
        // A bare "$": the person's own dollar when their currency is one, the Canadian dollar otherwise.
        return folded.Contains('$') ? (fallback is "CAD" or "USD" or "AUD" or "NZD" or "HKD" or "SGD" or "MXN" ? fallback : "CAD") : fallback;
    }

    private static readonly string[] InstitutionWords =
    [
        "BANK", "BANQUE", "CAISSE", "CREDIT", "CARD", "CARTE", "FINANCIAL", "FINANCIERE", "MUTUELLE", "TRUST", "CAPITAL", "SAVINGS",
        "EPARGNE", "UNION", "BANCO", "SERVICES FINANCIERS",
    ];

    /// <summary>
    /// A heading block near the top of page 1 that names a financial institution ("Northwind Bank"), for banks not in
    /// the known list. Only blocks with an institution word qualify, so an address block is never taken.
    /// </summary>
    private static string? InstitutionHeading(string firstPage)
    {
        foreach (var block in firstPage.Split('\n').Take(12))
        {
            var t = block.Trim();
            if (t.Length is < 4 or > 40 || t.Any(char.IsDigit)) continue;
            var f = " " + TextNorm.Fold(t) + " ";
            // "Compte épargne", "Credit card statement": account type lines, not the institution.
            if (f.StartsWith(" COMPTE ", StringComparison.Ordinal) || f.StartsWith(" ACCOUNT ", StringComparison.Ordinal) || f.Contains(" CREDIT CARD ", StringComparison.Ordinal)) continue;
            if (InstitutionWords.Any(w => f.Contains(" " + w + " ", StringComparison.Ordinal) || f.Contains(" " + w + "S ", StringComparison.Ordinal))
                && !f.Contains(" STATEMENT ", StringComparison.Ordinal) && !f.Contains(" RELEVE ", StringComparison.Ordinal))
                return t;
        }
        return null;
    }

    private static string? DetectBank(string foldedFirstPage)
    {
        foreach (var bank in Keywords.KnownBanks)
        {
            var key = TextNorm.Fold(bank);
            if (Regex.IsMatch(foldedFirstPage, @"(?<![A-Z])" + Regex.Escape(key) + @"(?![A-Z])")) return bank;
        }
        return null;
    }

    [GeneratedRegex(@"(?:COMPTE|ACCOUNT|ACCT|CARTE|CARD)(?:\s+[A-Z°.#']{1,12}){0,3}?\s*[:#]?\s*((?:[0-9X*•]{2,}[ \-]?){2,6})")]
    private static partial Regex AccountNumber();

    /// <summary>Only the last four digits are kept; the rest of the number is never stored.</summary>
    private static string? DetectLast4(string folded)
    {
        foreach (Match m in AccountNumber().Matches(folded))
        {
            var raw = m.Groups[1].Value.Replace(" ", "").Replace("-", "");
            if (raw.Length < 6 || raw.Length > 24) continue;
            var tail = raw[^4..];
            if (tail.All(char.IsDigit)) return tail;
        }
        return null;
    }

    [GeneratedRegex(@"(?<![\d])(20\d{2})(?![\d])")]
    private static partial Regex Year();

    private static int DetectYear(string folded)
    {
        var years = Year().Matches(folded).Select(m => int.Parse(m.Value)).Where(y => y <= DateTime.Today.Year + 1).ToList();
        return years.Count == 0 ? DateTime.Today.Year : years.GroupBy(y => y).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
    }

    private static readonly HashSet<string> Connectors = ["AU", "A", "TO", "-", "THROUGH", "THRU", "JUSQU'AU", "AND", "ET"];

    /// <summary>Finds "du 1er octobre 2025 au 31 octobre 2025", "Oct 1 - Oct 31, 2025", "From 2025-10-01 to 2025-10-31"...</summary>
    private static (PrintedDate, PrintedDate)? DetectPeriod(List<TextLine> lines)
    {
        foreach (var line in lines.OrderBy(l => l.Page).ThenBy(l => l.Top))
        {
            var tokens = SplitDateRanges(line.Tokens);
            var found = new List<(int Start, int End, PrintedDate Date)>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (DateParser.TryParseTokens(tokens, i, out var d, out var n))
                {
                    found.Add((i, i + n, d));
                    i += n - 1;
                }
            }
            for (var k = 0; k + 1 < found.Count; k++)
            {
                var a = found[k];
                var b = found[k + 1];
                var between = tokens.Skip(a.End).Take(b.Start - a.End).Select(t => TextNorm.Fold(t).Trim(',', ':')).Where(t => t.Length > 0).ToList();
                if (between.Count is < 1 or > 2 || !between.Any(Connectors.Contains)) continue;
                if (a.Date.Year is null && b.Date.Year is null) continue;
                var start = a.Date;
                if (start.Year is null)
                {
                    var endMonth = b.Date.MonthKnown ? b.Date.B : 0;
                    var startMonth = a.Date.MonthKnown ? a.Date.B : 0;
                    var y = b.Date.Year!.Value - (startMonth > endMonth && endMonth > 0 ? 1 : 0);
                    start = start with { Year = y };
                }
                var end = b.Date.Year is null ? b.Date with { Year = start.Year } : b.Date;
                return (start, end);
            }
        }
        return null;
    }

    /// <summary>Splits single tokens such as "01/10/2025-31/10/2025" around the dash.</summary>
    private static List<string> SplitDateRanges(IReadOnlyList<string> tokens)
    {
        var output = new List<string>(tokens.Count);
        foreach (var t in tokens)
        {
            var parts = t.Split(['-', '–'], 2);
            if (parts.Length == 2 && t.Count(c => c is '-' or '–') == 1 && DateParser.TryParse(parts[0], out _) && DateParser.TryParse(parts[1], out _))
            {
                output.Add(parts[0]); output.Add("-"); output.Add(parts[1]);
            }
            else output.Add(t);
        }
        return output;
    }

    public AccountKind DefaultKind => LooksLikeCard ? AccountKind.CreditCard : AccountKind.Bank;

    /// <summary>A bank-side reading becomes a savings account when the statement says so.</summary>
    public AccountKind Refine(AccountKind kind) => kind == AccountKind.Bank && LooksLikeSavings ? AccountKind.Savings : kind;
}
