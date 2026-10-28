using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// Reads the transactions of a bank or credit card statement from the position of its words, without any
/// trained model: it finds the table header (or, failing that, the columns that amounts line up in),
/// rebuilds rows and multi-line labels, works out signs and years, and checks the result to the cent
/// against the opening, closing and running balances printed on the statement. Several readings are
/// tried and the one that balances wins.
/// </summary>
public static class StatementParser
{
    public static StatementParse Parse(IReadOnlyList<PageWords> pages, BankTemplate? template, string defaultCurrency, StatementKindHint forcedKind = StatementKindHint.Auto)
    {
        if (pages.Count == 0) throw new ArgumentException("The PDF has no pages.", nameof(pages));
        var ctx = DocumentContext.Build(pages, defaultCurrency);

        var layouts = new List<Layout>();
        if (template is not null) layouts.Add(TemplateLayout(ctx, template));
        else
        {
            var header = HeaderLayout(ctx);
            if (header is not null) layouts.Add(header);
            layouts.AddRange(HeaderlessLayouts(ctx));
        }

        StatementParse? best = null;
        var bestScore = double.MinValue;
        foreach (var layout in layouts)
        {
            AccountKind[] kinds = (forcedKind != StatementKindHint.Auto ? forcedKind : layout.Kind) switch
            {
                StatementKindHint.Bank => [AccountKind.Bank],
                StatementKindHint.CreditCard => [AccountKind.CreditCard],
                _ => ctx.LooksLikeCard ? [AccountKind.CreditCard, AccountKind.Bank] : [AccountKind.Bank, AccountKind.CreditCard],
            };
            foreach (var kind in kinds)
            {
                var parse = Interpret(ctx, layout, kind);
                var score = Score(parse, ctx, layout);
                if (score > bestScore) { best = parse; bestScore = score; }
            }
        }
        return best ?? Empty(ctx, template);
    }

    private static StatementParse Empty(DocumentContext ctx, BankTemplate? template) => new()
    {
        Currency = ctx.Currency,
        Language = ctx.Language,
        Kind = ctx.DefaultKind,
        Bank = ctx.Bank,
        Last4 = ctx.Last4,
        Status = VerificationStatus.NoTransactions,
        UsedOcr = ctx.UsedOcr,
        Method = template is null ? "Layout" : "Template",
        FirstPageText = ctx.FirstPageText,
    };

    private static double Score(StatementParse p, DocumentContext ctx, Layout layout)
    {
        double s = 0;
        if (p.Verified) s += 1_000_000;
        if (p.RowBalanceChecks > 0) s += 100_000.0 * (p.RowBalanceChecks - p.RowBalanceMismatches) / p.RowBalanceChecks;
        s += 100 * Math.Min(p.Rows.Count, 500);
        if (p.Kind == ctx.DefaultKind) s += 50;
        if (layout.FromHeader) s += 30;
        return s;
    }

    // ---------------------------------------------------------------- layouts

    internal sealed record Zone(ColumnRole Role, double Left, double Right)
    {
        public double Center => (Left + Right) / 2;
        public bool Contains(double x) => x >= Left && x <= Right;
    }

    internal sealed class PageLayout
    {
        public double TableTop { get; init; }
        /// <summary>A row's date must start left of this.</summary>
        public double DescLeft { get; init; } = double.MaxValue;
        public Zone? Date { get; init; }
        public Zone? ValueDate { get; init; }
        public Zone? Description { get; init; }
        /// <summary>The bank's own category column.</summary>
        public Zone? Category { get; init; }
        public required List<Zone> Amounts { get; init; }
        /// <summary>Template layouts place every word by the zone its centre falls in.</summary>
        public bool Strict { get; init; }
        /// <summary>A date right after the row date is the value date (headerless reading).</summary>
        public bool SkipSecondDate { get; init; }
    }

    internal sealed class Layout
    {
        public required string Name { get; init; }
        public required Func<int, PageLayout?> ForPage { get; init; }
        public DateOrder DateOrder { get; init; }
        public StatementKindHint Kind { get; init; }
        public bool FromHeader { get; init; }
    }

    private static Layout TemplateLayout(DocumentContext ctx, BankTemplate t)
    {
        PageLayout Build(int page)
        {
            var width = ctx.PageWidth(page);
            Zone? Z(ColumnRole r) => t.Columns.Where(c => c.Role == r).Select(c => new Zone(r, c.Left * width, c.Right * width)).FirstOrDefault();
            return new PageLayout
            {
                Strict = true,
                Date = Z(ColumnRole.Date),
                ValueDate = Z(ColumnRole.ValueDate),
                Description = Z(ColumnRole.Description),
                Category = Z(ColumnRole.Category),
                Amounts = t.Columns.Where(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount or ColumnRole.Balance)
                    .Select(c => new Zone(c.Role, c.Left * width, c.Right * width)).ToList(),
            };
        }
        var cache = new Dictionary<int, PageLayout>();
        return new Layout
        {
            Name = "Template: " + t.Name,
            ForPage = p => cache.TryGetValue(p, out var l) ? l : cache[p] = Build(p),
            DateOrder = t.DateOrder,
            Kind = t.Kind,
        };
    }

    private sealed record HeaderCell(string Folded, double Left, double Right, ColumnRole? Role);

    private static Layout? HeaderLayout(DocumentContext ctx)
    {
        var byPage = new Dictionary<int, PageLayout>();
        PageLayout? last = null;
        foreach (var page in ctx.Pages)
        {
            var lines = ctx.Lines.Where(l => l.Page == page.Number).OrderBy(l => l.Top).ToList();
            var found = FindHeader(lines);
            if (found is not null)
            {
                last = BuildFromHeader(found.Value.Cells, found.Value.Bottom, lines);
                byPage[page.Number] = last;
            }
            else if (last is not null)
            {
                byPage[page.Number] = new PageLayout
                {
                    TableTop = 0, DescLeft = last.DescLeft, ValueDate = last.ValueDate, Category = last.Category, Amounts = last.Amounts,
                };
            }
        }
        if (byPage.Count == 0) return null;
        return new Layout { Name = "Header", ForPage = p => byPage.GetValueOrDefault(p), FromHeader = true };
    }

    private static (List<HeaderCell> Cells, double Bottom)? FindHeader(List<TextLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var cells = HeaderCells(lines[i].Words);
            if (IsHeader(cells)) return (Finish(cells), lines[i].Bottom);
            if (i + 1 < lines.Count && lines[i + 1].Top - lines[i].Bottom < lines[i].Height * 1.2)
            {
                var merged = HeaderCells(lines[i].Words.Concat(lines[i + 1].Words).ToList());
                if (IsHeader(merged)) return (Finish(merged), lines[i + 1].Bottom);
            }
        }
        return null;
    }

    private static List<HeaderCell> HeaderCells(IReadOnlyList<Word> words)
    {
        var sorted = words.OrderBy(w => w.Left).ToList();
        var groups = new List<List<Word>>();
        foreach (var w in sorted)
        {
            var last = groups.Count > 0 ? groups[^1] : null;
            if (last is not null)
            {
                var right = last.Max(x => x.Right);
                var h = Math.Max(last.Average(x => x.Height), w.Height);
                // Stacked two-line headers overlap horizontally; side-by-side words are separated by a small gap.
                if (w.Left - right < h * 0.9) { last.Add(w); continue; }
            }
            groups.Add([w]);
        }
        return groups.Select(g =>
        {
            var folded = TextNorm.Fold(string.Join(' ', g.OrderBy(x => x.Top).ThenBy(x => x.Left).Select(x => x.Text)));
            return new HeaderCell(folded, g.Min(x => x.Left), g.Max(x => x.Right), ClassifyHeader(folded));
        }).ToList();
    }

    private static bool HasWord(string folded, params string[] words)
    {
        var parts = folded.Split([' ', '(', ')', '/', '-', '.', ':', '$', '\'', '&', ','], StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => words.Any(w => p == w || (w.Length >= 5 && p.StartsWith(w, StringComparison.Ordinal))));
    }

    private static ColumnRole? ClassifyHeader(string f)
    {
        if (f.Length > 40) return null;
        if (HasWord(f, "SOLDE", "BALANCE")) return ColumnRole.Balance;
        if (HasWord(f, "CATEGORIE", "CATEGORIES", "CATEGORY", "CATEGORY")) return ColumnRole.Category;
        if (HasWord(f, "DATE", "JOUR", "DATES"))
            return HasWord(f, "VALEUR", "VALUE", "POST", "POSTED", "POSTING", "INSCRIPTION", "COMPTABILISATION", "COMPTABLE") ? ColumnRole.ValueDate : ColumnRole.Date;
        if (HasWord(f, "DEBIT", "DEBITS", "RETRAIT", "RETRAITS", "WITHDRAWAL", "WITHDRAWALS", "SORTIE", "SORTIES", "CHEQUES", "PAIEMENTS", "CHARGES", "DEPENSES")) return ColumnRole.Debit;
        if (HasWord(f, "CREDIT", "CREDITS", "DEPOT", "DEPOTS", "DEPOSIT", "DEPOSITS", "ENTREE", "ENTREES", "VERSEMENTS", "RECETTES")) return ColumnRole.Credit;
        if (HasWord(f, "MONTANT", "AMOUNT", "SOMME", "MONTANTS", "AMOUNTS")) return ColumnRole.Amount;
        if (HasWord(f, "DESCRIPTION", "LIBELLE", "OPERATION", "OPERATIONS", "DETAIL", "DETAILS", "TRANSACTION", "TRANSACTIONS", "NATURE", "PARTICULARS", "MERCHANT", "COMMERCANT", "REFERENCE"))
            return ColumnRole.Description;
        return null;
    }

    private static bool IsHeader(List<HeaderCell> cells)
    {
        var roles = cells.Where(c => c.Role is not null).Select(c => c.Role!.Value).ToList();
        var hasDate = roles.Contains(ColumnRole.Date) || roles.Contains(ColumnRole.ValueDate);
        var hasMoney = roles.Any(r => r is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount);
        return hasDate && hasMoney && roles.Count >= 3;
    }

    /// <summary>The first date column is the transaction date; later ones are value dates.</summary>
    private static List<HeaderCell> Finish(List<HeaderCell> cells)
    {
        var seenDate = false;
        var output = new List<HeaderCell>();
        foreach (var c in cells)
        {
            var role = c.Role;
            if (role is ColumnRole.Date or ColumnRole.ValueDate)
            {
                role = seenDate ? ColumnRole.ValueDate : ColumnRole.Date;
                seenDate = true;
            }
            output.Add(c with { Role = role });
        }
        return output;
    }

    private static PageLayout BuildFromHeader(List<HeaderCell> cells, double bottom, List<TextLine> pageLines)
    {
        var h = pageLines.Count > 0 ? pageLines.Average(l => l.Height) : 8;
        var date = cells.First(c => c.Role == ColumnRole.Date);
        var desc = cells.FirstOrDefault(c => c.Role == ColumnRole.Description && c.Left > date.Left);
        var value = cells.FirstOrDefault(c => c.Role == ColumnRole.ValueDate);
        var amounts = cells.Where(c => c.Role is ColumnRole.Debit or ColumnRole.Credit or ColumnRole.Amount or ColumnRole.Balance)
            .GroupBy(c => c.Role).Select(g => g.First())
            .Select(c => new Zone(c.Role!.Value, c.Left, c.Right)).ToList();
        var descLeft = desc?.Left ?? (value is not null && value.Left > date.Right ? value.Left : date.Right + h * 4);
        var category = cells.FirstOrDefault(c => c.Role == ColumnRole.Category);
        Zone? categoryZone = null;
        if (category is not null)
        {
            // From the category heading to the next column on its right (amounts are right-aligned under theirs).
            var nextLeft = cells.Where(c => c.Left > category.Right).Select(c => c.Left).DefaultIfEmpty(double.MaxValue).Min();
            var firstAmountLeft = amounts.Where(z => z.Left > category.Left).Select(z => z.Left).DefaultIfEmpty(double.MaxValue).Min();
            categoryZone = new Zone(ColumnRole.Category, category.Left - h * 0.6, Math.Min(nextLeft, firstAmountLeft) - h * 3);
        }
        return new PageLayout
        {
            TableTop = bottom + 0.5,
            DescLeft = descLeft,
            Category = categoryZone,
            ValueDate = value is null ? null : new Zone(ColumnRole.ValueDate, value.Left - h * 1.5, value.Right + h * 1.5),
            Amounts = amounts,
        };
    }

    /// <summary>
    /// For statements whose header is missing or unusual: rows are lines that start with a date, and amount
    /// columns are where the right edges of amounts line up. Every plausible meaning of those columns is tried.
    /// </summary>
    private static List<Layout> HeaderlessLayouts(DocumentContext ctx)
    {
        var edges = new List<(double Left, double Right)>();
        var rowLines = 0;
        foreach (var line in ctx.Lines)
        {
            var tokens = line.Tokens;
            if (!DateParser.TryParseTokens(tokens, 0, out _, out var consumed) || consumed >= tokens.Count) continue;
            var any = false;
            foreach (var w in line.Words.Skip(consumed))
            {
                if (!AmountParser.TryParse(w.Text, out _)) continue;
                edges.Add((w.Left, w.Right));
                any = true;
            }
            if (any) rowLines++;
        }
        if (rowLines < 2) return [];

        var clusters = new List<List<(double Left, double Right)>>();
        foreach (var e in edges.OrderBy(e => e.Right))
        {
            if (clusters.Count > 0 && e.Right - clusters[^1].Max(x => x.Right) < 8) clusters[^1].Add(e);
            else clusters.Add([e]);
        }
        var minCount = Math.Max(2, rowLines / 10);
        var columns = clusters.Where(c => c.Count >= minCount)
            .Select(c => (Left: c.Min(x => x.Left), Right: c.Max(x => x.Right)))
            .OrderBy(c => c.Right).TakeLast(3).ToList();
        if (columns.Count == 0) return [];

        ColumnRole[][] options = columns.Count switch
        {
            1 => [[ColumnRole.Amount]],
            2 => [[ColumnRole.Amount, ColumnRole.Balance], [ColumnRole.Debit, ColumnRole.Credit]],
            _ => [[ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Balance], [ColumnRole.Credit, ColumnRole.Debit, ColumnRole.Balance]],
        };
        return options.Select(roles =>
        {
            var zones = roles.Select((r, i) => new Zone(r, columns[i].Left, columns[i].Right)).ToList();
            var layout = new PageLayout { Amounts = zones, SkipSecondDate = true };
            return new Layout { Name = "Columns " + string.Join('/', roles), ForPage = _ => layout };
        }).ToList();
    }

    // ---------------------------------------------------------------- rows

    private sealed class RowCells
    {
        public required TextLine Line { get; init; }
        public PrintedDate? Date { get; set; }
        public List<Word> DateWords { get; } = [];
        public List<Word> DescWords { get; } = [];
        public List<Word> CategoryWords { get; } = [];
        public string Category => string.Join(' ', CategoryWords.OrderBy(w => w.Left).Select(w => w.Text));
        public Dictionary<ColumnRole, (PrintedAmount Amount, Word Word, double Distance)> Amounts { get; } = [];
        public string Description => string.Join(' ', DescWords.OrderBy(w => w.Left).Select(w => w.Text));
        public double DescLeft => DescWords.Count > 0 ? DescWords.Min(w => w.Left) : double.NaN;
        public bool HasMovement => Amounts.ContainsKey(ColumnRole.Debit) || Amounts.ContainsKey(ColumnRole.Credit) || Amounts.ContainsKey(ColumnRole.Amount);
    }

    private static RowCells? Cells(TextLine line, PageLayout pl, DocumentContext ctx)
    {
        if (!pl.Strict && line.Top < pl.TableTop) return null;
        var cells = new RowCells { Line = line };
        var words = line.Words;
        if (pl.Strict)
        {
            var dateWords = pl.Date is null ? [] : words.Where(w => pl.Date.Contains(w.CenterX)).ToList();
            if (dateWords.Count > 0 && DateParser.TryParseTokens(dateWords.Select(w => w.Text).ToList(), 0, out var d, out var n, lenient: true))
            {
                cells.Date = d;
                cells.DateWords.AddRange(dateWords.Take(n));
            }
            foreach (var w in words)
            {
                if (cells.DateWords.Contains(w)) continue;
                var zone = pl.Amounts.FirstOrDefault(z => z.Contains(w.CenterX));
                if (zone is not null)
                {
                    if (AmountParser.TryParse(w.Text, out var a))
                    {
                        // Two amounts in one zone: keep the right-most, the other is probably a currency conversion.
                        if (!cells.Amounts.TryGetValue(zone.Role, out var ex) || w.Left > ex.Word.Left) cells.Amounts[zone.Role] = (a, w, 0);
                    }
                    continue;
                }
                if (pl.Description?.Contains(w.CenterX) == true) cells.DescWords.Add(w);
                else if (pl.Category?.Contains(w.CenterX) == true) cells.CategoryWords.Add(w);
            }
            return cells;
        }

        var index = 0;
        if (words.Count > 0 && words[0].Left < pl.DescLeft - 0.5 && TryDate(words, 0, ctx, out var date, out var consumed))
        {
            cells.Date = date;
            cells.DateWords.AddRange(words.Take(consumed));
            index = consumed;
            // Value date column, or a second date printed right after the first.
            if (index < words.Count && (pl.ValueDate?.Contains(words[index].CenterX) == true || pl.SkipSecondDate)
                && TryDate(words, index, ctx, out _, out var c2))
                index += c2;
        }

        for (var i = index; i < words.Count; i++)
        {
            var w = words[i];
            if (AmountParser.TryParse(w.Text, out var a))
            {
                var (zone, distance) = Nearest(pl.Amounts, w);
                if (zone is not null)
                {
                    if (cells.Amounts.TryGetValue(zone.Role, out var ex))
                    {
                        if (distance < ex.Distance) { cells.DescWords.Add(ex.Word); cells.Amounts[zone.Role] = (a, w, distance); }
                        else cells.DescWords.Add(w);
                    }
                    else cells.Amounts[zone.Role] = (a, w, distance);
                    continue;
                }
            }
            if (AmountParser.IsDetachedMarker(w.Text) && pl.Amounts.Count > 0 && w.Left > pl.Amounts.Min(z => z.Left) - 2 * w.Height) continue;
            if (pl.Category is { } cz && w.Left >= cz.Left && w.Right <= cz.Right + w.Height * 3) { cells.CategoryWords.Add(w); continue; }
            cells.DescWords.Add(w);
        }
        return cells;
    }

    private static bool TryDate(IReadOnlyList<Word> words, int start, DocumentContext ctx, out PrintedDate date, out int consumed)
    {
        var tokens = words.Select(w => w.Text).ToList();
        if (!DateParser.TryParseTokens(tokens, start, out date, out consumed)) return false;
        // "12 OCT 15 AVENUE..." must not be read as 12 October 2015.
        if (consumed > 1 && date.Year is { } y && Math.Abs(y - ctx.DocumentYear) > 1)
        {
            for (var n = consumed - 1; n >= 1; n--)
            {
                if (DateParser.TryParse(string.Join(' ', tokens.Skip(start).Take(n)), out var shorter))
                {
                    date = shorter;
                    consumed = n;
                    return true;
                }
            }
            return false;
        }
        return true;
    }

    private static (Zone? Zone, double Distance) Nearest(List<Zone> zones, Word w)
    {
        Zone? best = null;
        var bestD = double.MaxValue;
        foreach (var z in zones)
        {
            var d = Math.Min(Math.Abs(w.Right - z.Right), Math.Min(Math.Abs(w.CenterX - z.Center), Math.Abs(w.Left - z.Left)));
            if (d < bestD) { best = z; bestD = d; }
        }
        var limit = Math.Max(36, 2.5 * w.Height);
        return bestD <= limit ? (best, bestD) : (null, bestD);
    }

    private sealed class RawRow
    {
        public required PrintedDate Date { get; init; }
        public string Label { get; set; } = "";
        public double LabelLeft { get; set; }
        public PrintedAmount? Debit { get; set; }
        public PrintedAmount? Credit { get; set; }
        public PrintedAmount? Amount { get; set; }
        public PrintedAmount? Balance { get; set; }
        public Word? BalanceWord { get; set; }
        public string Category { get; set; } = "";
        public required int Page { get; init; }
        public required double Top { get; init; }
        public bool HasMovement => Debit is not null || Credit is not null || Amount is not null;

        public void Take(RowCells c)
        {
            if (c.Amounts.TryGetValue(ColumnRole.Debit, out var d)) Debit = d.Amount;
            if (c.Amounts.TryGetValue(ColumnRole.Credit, out var cr)) Credit = cr.Amount;
            if (c.Amounts.TryGetValue(ColumnRole.Amount, out var a)) Amount = a.Amount;
            if (c.Amounts.TryGetValue(ColumnRole.Balance, out var b)) { Balance = b.Amount; BalanceWord = b.Word; }
            if (Category.Length == 0 && c.Category.Length > 0) Category = c.Category;
        }
    }

    private static bool SameBlock(TextLine? previous, TextLine line) =>
        previous is not null && previous.Page == line.Page && line.Top - previous.Bottom < Math.Max(line.Height, previous.Height) * 1.3;

    private static (List<RawRow> Rows, List<RowCells> Cells, PrintedAmount? FirstBalanceOnly) Assemble(DocumentContext ctx, Layout layout)
    {
        var allCells = new List<RowCells>();
        foreach (var line in ctx.Lines.OrderBy(l => l.Page).ThenBy(l => l.Top))
        {
            var pl = layout.ForPage(line.Page);
            if (pl is null) continue;
            var c = Cells(line, pl, ctx);
            if (c is not null) allCells.Add(c);
        }

        var rows = new List<RawRow>();
        RawRow? current = null;
        PrintedDate? lastDate = null;
        TextLine? previous = null;
        PrintedAmount? firstBalanceOnly = null;
        var continuation = 0;
        foreach (var c in allCells)
        {
            var desc = c.Description;
            var summary = Keywords.ClassifySummary(TextNorm.Fold(desc));
            if (c.Date is not null) lastDate = c.Date;

            if (summary != SummaryKind.None)
            {
                current = null;
            }
            else if (c.HasMovement)
            {
                if (current is { HasMovement: false } && c.Date is null && SameBlock(previous, c.Line))
                {
                    // Amount printed on the last line of a multi-line label.
                    current.Take(c);
                    if (desc.Length > 0) current.Label += " " + desc;
                }
                else
                {
                    var date = c.Date ?? lastDate;
                    if (date is null) { current = null; previous = c.Line; continue; }
                    current = new RawRow { Date = date.Value, Label = desc, LabelLeft = c.DescLeft, Page = c.Line.Page, Top = c.Line.Top };
                    current.Take(c);
                    rows.Add(current);
                }
                continuation = 0;
            }
            else if (c.Date is not null && desc.Length > 0 && !c.Amounts.ContainsKey(ColumnRole.Balance))
            {
                current = new RawRow { Date = c.Date.Value, Label = desc, LabelLeft = c.DescLeft, Page = c.Line.Page, Top = c.Line.Top, Category = c.Category };
                rows.Add(current);
                continuation = 0;
            }
            else if (c.Date is null && current is not null && desc.Length > 0 && !c.Amounts.ContainsKey(ColumnRole.Balance)
                     && SameBlock(previous, c.Line) && continuation < 4
                     && (double.IsNaN(current.LabelLeft) || Math.Abs(c.DescLeft - current.LabelLeft) < c.Line.Height * 6))
            {
                current.Label += " " + desc;
                continuation++;
            }
            else if (c.Amounts.TryGetValue(ColumnRole.Balance, out var bal) && !c.HasMovement)
            {
                // A balance printed on its own line: right under a row it is that row's balance,
                // before any row it is the opening balance.
                if (current is not null && current.Balance is null && c.Date is null && desc.Length == 0 && SameBlock(previous, c.Line))
                {
                    current.Balance = bal.Amount;
                    current.BalanceWord = bal.Word;
                }
                else if (rows.Count == 0) firstBalanceOnly ??= bal.Amount;
                current = null;
            }
            else current = null;
            previous = c.Line;
        }
        rows.RemoveAll(r => !r.HasMovement);
        foreach (var r in rows) r.Label = TextNorm.CollapseSpaces(r.Label);
        return (rows, allCells, firstBalanceOnly);
    }

    // ---------------------------------------------------------------- interpretation

    private static StatementParse Interpret(DocumentContext ctx, Layout layout, AccountKind kind)
    {
        var (raw, cells, firstBalanceOnly) = Assemble(ctx, layout);

        var order = layout.DateOrder != DateOrder.Unknown ? layout.DateOrder : ResolveOrder(ctx, raw.Select(r => r.Date).ToList());
        var period = ResolvePeriod(ctx, order);

        // A sign written on only some amounts tells what the unmarked ones are: "+2 432,33" among "28,18"
        // means unmarked amounts go out; "-28,18" among "2 432,33" means unmarked amounts come in.
        var single = raw.Where(r => r.Amount is not null && r.Debit is null && r.Credit is null).Select(r => r.Amount!.Value).ToList();
        var plusOnly = single.Any(a => a.Marker is SignMarker.Plus or SignMarker.Credit) && !single.Any(a => a.Marker is SignMarker.Minus or SignMarker.Debit);
        var minusOnly = single.Any(a => a.Marker is SignMarker.Minus or SignMarker.Debit) && !single.Any(a => a.Marker is SignMarker.Plus or SignMarker.Credit);

        var rows = new List<(RawRow Raw, ParsedRow Row, bool SignKnown, decimal Guess)>();
        foreach (var r in raw)
        {
            var date = ToDate(r.Date, order, period, ctx);
            if (date is null) continue;
            var (signed, known, guess) = SignOf(r, kind, plusOnly, minusOnly);
            rows.Add((r, new ParsedRow
            {
                Date = date.Value,
                Label = r.Label,
                Amount = signed,
                Balance = r.Balance is { } b ? BalanceValue(b, kind) : null,
                BankCategory = r.Category,
                Page = r.Page,
                Top = r.Top,
            }, known, guess));
        }

        // Online history exports list the newest operation first: put them in time order for the balance chain.
        var later = 0;
        var earlier = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            if (rows[i].Row.Date > rows[i - 1].Row.Date) later++;
            else if (rows[i].Row.Date < rows[i - 1].Row.Date) earlier++;
        }
        var newestFirst = earlier > later;
        if (newestFirst) rows.Reverse();

        var (opening, closing) = FindBalances(ctx, layout, kind, period, order);
        // In a history export the balances printed above the table are today's, not the period's: the running
        // balance of each line is what checks it.
        if (newestFirst && rows.Count(r => r.Row.Balance is not null) >= Math.Max(1, rows.Count - 1)) (opening, closing) = (null, null);
        if (opening is null && firstBalanceOnly is { } fb) opening = BalanceValue(fb, kind);
        var polarity = kind == AccountKind.CreditCard ? -1m : 1m;

        ResolveUnknownSigns(rows, opening, closing, polarity);

        var parsed = rows.Select(r => r.Row).ToList();

        // Running balance checks.
        decimal? prev = opening;
        decimal group = 0;
        int checks = 0, mismatches = 0;
        foreach (var r in parsed)
        {
            group += r.Amount;
            if (r.Balance is { } bal)
            {
                if (prev is { } p)
                {
                    checks++;
                    if (p + polarity * group != bal) mismatches++;
                }
                prev = bal;
                group = 0;
            }
        }

        var derived = false;
        if (opening is null && parsed.Count > 0 && parsed[0].Balance is { } firstBal)
        {
            opening = firstBal - polarity * parsed[0].Amount;
            derived = true;
        }
        if (closing is null && parsed.Count > 0 && parsed[^1].Balance is { } lastBal)
        {
            closing = lastBal;
            derived = true;
        }

        var zero = parsed.Count(r => r.Amount == 0);
        parsed = parsed.Where(r => r.Amount != 0).ToList();

        var result = new StatementParse
        {
            Rows = parsed,
            Profile = ctx.Profile,
            NewestFirst = newestFirst,
            ZeroRowsSkipped = zero,
            OpeningBalance = opening,
            ClosingBalance = closing,
            PeriodStart = period?.Start ?? (parsed.Count > 0 ? parsed.Min(r => r.Date) : null),
            PeriodEnd = period?.End ?? (parsed.Count > 0 ? parsed.Max(r => r.Date) : null),
            Currency = ctx.Currency,
            Language = ctx.Language,
            Kind = ctx.Refine(kind),
            Bank = ctx.Bank,
            Last4 = ctx.Last4,
            UsedOcr = ctx.UsedOcr,
            Method = layout.Name.StartsWith("Template", StringComparison.Ordinal) ? layout.Name : "Layout",
            DateOrder = order,
            RowBalanceChecks = checks,
            RowBalanceMismatches = mismatches,
            FirstPageText = ctx.FirstPageText,
            Columns = ColumnsFrom(cells, raw, ctx),
        };

        if (parsed.Count == 0) result.Status = VerificationStatus.NoTransactions;
        else if (opening is { } o && closing is { } cl)
        {
            var computed = o + polarity * parsed.Sum(r => r.Amount);
            result.ComputedClosing = computed;
            result.Difference = cl - computed;
            // The printed opening and closing balances are the authority. When one of them had to be taken from the
            // running balance column instead, every running balance must agree as well.
            result.Status = result.Difference == 0 && (!derived || (checks > 0 && mismatches == 0))
                ? VerificationStatus.Verified
                : VerificationStatus.Mismatch;
        }
        else result.Status = VerificationStatus.NoBalances;
        return result;
    }

    private static decimal BalanceValue(PrintedAmount a, AccountKind kind) => kind == AccountKind.CreditCard
        ? (a.Marker is SignMarker.Minus or SignMarker.Credit ? -a.Magnitude : a.Magnitude)
        : (a.Marker is SignMarker.Minus or SignMarker.Debit ? -a.Magnitude : a.Magnitude);

    /// <summary>Signed amount (negative = money out), whether the sign is certain, and a keyword-based guess when it is not.</summary>
    private static (decimal Signed, bool Known, decimal Guess) SignOf(RawRow r, AccountKind kind, bool plusOnly, bool minusOnly)
    {
        if (r.Debit is not null || r.Credit is not null)
        {
            var v = (r.Credit?.Magnitude ?? 0) - (r.Debit?.Magnitude ?? 0);
            return (v, true, v);
        }
        var a = r.Amount!.Value;
        if (kind == AccountKind.CreditCard)
        {
            // On a card, unmarked amounts are purchases; "-", "CR" and "+" mark payments and refunds.
            var v = a.Marker is SignMarker.Minus or SignMarker.Credit or SignMarker.Plus ? a.Magnitude : -a.Magnitude;
            return (v, true, v);
        }
        switch (a.Marker)
        {
            case SignMarker.Minus or SignMarker.Debit: return (-a.Magnitude, true, -a.Magnitude);
            case SignMarker.Plus or SignMarker.Credit: return (a.Magnitude, true, a.Magnitude);
        }
        if (plusOnly) return (-a.Magnitude, true, -a.Magnitude);
        if (minusOnly) return (a.Magnitude, true, a.Magnitude);
        var income = Keywords.ContainsAny(" " + TextNorm.Fold(r.Label) + " ", Keywords.IncomeHints);
        var guess = income ? a.Magnitude : -a.Magnitude;
        return (guess, false, guess);
    }

    /// <summary>
    /// For single-column statements without signs: between two printed balances, picks the signs that make the
    /// balance move by exactly the printed amount, preferring the keyword guess when several work.
    /// </summary>
    private static void ResolveUnknownSigns(List<(RawRow Raw, ParsedRow Row, bool SignKnown, decimal Guess)> rows, decimal? opening, decimal? closing, decimal polarity)
    {
        if (rows.All(r => r.SignKnown)) return;
        decimal? prev = opening;
        var group = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            group.Add(i);
            if (rows[i].Row.Balance is { } bal)
            {
                if (prev is { } p) Solve(group, (bal - p) * polarity);
                prev = bal;
                group.Clear();
            }
        }
        if (group.Count > 0 && prev is { } last && closing is { } c) Solve(group, (c - last) * polarity);

        void Solve(List<int> indexes, decimal target)
        {
            var unknown = indexes.Where(i => !rows[i].SignKnown).ToList();
            if (unknown.Count is 0 or > 16) return;
            var known = indexes.Where(i => rows[i].SignKnown).Sum(i => rows[i].Row.Amount);
            var need = target - known;
            var best = -1;
            var bestCost = int.MaxValue;
            for (var mask = 0; mask < 1 << unknown.Count; mask++)
            {
                decimal sum = 0;
                var cost = 0;
                for (var b = 0; b < unknown.Count; b++)
                {
                    var magnitude = Math.Abs(rows[unknown[b]].Guess);
                    var positive = (mask & (1 << b)) != 0;
                    sum += positive ? magnitude : -magnitude;
                    if (positive != rows[unknown[b]].Guess > 0) cost++;
                }
                if (sum == need && cost < bestCost) { best = mask; bestCost = cost; }
            }
            if (best < 0) return;
            for (var b = 0; b < unknown.Count; b++)
            {
                var row = rows[unknown[b]].Row;
                var magnitude = Math.Abs(row.Amount);
                row.Amount = (best & (1 << b)) != 0 ? magnitude : -magnitude;
            }
        }
    }

    private static (decimal? Opening, decimal? Closing) FindBalances(DocumentContext ctx, Layout layout, AccountKind kind, (DateOnly Start, DateOnly End)? period, DateOrder order)
    {
        decimal? opening = null, closing = null;
        var asOf = new List<(DateOnly? Date, decimal Value)>();
        foreach (var line in ctx.Lines.OrderBy(l => l.Page).ThenBy(l => l.Top))
        {
            var f = TextNorm.Fold(line.Text);
            var k = Keywords.ClassifySummary(StripLeadingDate(line, ctx));
            if (k is not (SummaryKind.Opening or SummaryKind.Closing or SummaryKind.BalanceAsOf)) continue;
            var word = line.Words.LastOrDefault(w => AmountParser.TryParse(w.Text, out _));
            if (word is null || !AmountParser.TryParse(word.Text, out var a)) continue;
            var value = BalanceValue(a, kind);
            if (kind == AccountKind.Bank && value > 0)
            {
                var inDebitColumn = layout.ForPage(line.Page) is { } pl && !pl.Strict && Nearest(pl.Amounts, word).Zone?.Role == ColumnRole.Debit;
                if (inDebitColumn || f.Contains("DEBITEUR", StringComparison.Ordinal) || f.Contains("OVERDRAWN", StringComparison.Ordinal)) value = -value;
            }
            switch (k)
            {
                case SummaryKind.Opening: opening ??= value; break;
                case SummaryKind.Closing: closing = value; break;
                case SummaryKind.BalanceAsOf:
                    DateOnly? d = null;
                    var tokens = line.Tokens;
                    for (var i = 0; i < tokens.Count; i++)
                    {
                        if (DateParser.TryParseTokens(tokens, i, out var pd, out _)) { d = ToDate(pd, order, period, ctx); break; }
                    }
                    asOf.Add((d, value));
                    break;
            }
        }
        if (asOf.Count > 0)
        {
            if (period is { } p)
            {
                opening ??= asOf.Where(x => x.Date <= p.Start).Select(x => (decimal?)x.Value).FirstOrDefault();
                closing ??= asOf.Where(x => x.Date >= p.End).Select(x => (decimal?)x.Value).LastOrDefault();
            }
            if (asOf.Count >= 2)
            {
                opening ??= asOf[0].Value;
                closing ??= asOf[^1].Value;
            }
        }
        return (opening, closing);
    }

    /// <summary>Folded line text without a leading date, so "01/10 SOLDE PRECEDENT" is recognised as a balance line.</summary>
    private static string StripLeadingDate(TextLine line, DocumentContext ctx)
    {
        var words = line.Words;
        var start = TryDate(words, 0, ctx, out _, out var n) ? n : 0;
        return TextNorm.Fold(string.Join(' ', words.Skip(start).Select(w => w.Text)));
    }

    private static DateOrder ResolveOrder(DocumentContext ctx, List<PrintedDate> dates)
    {
        var all = dates.ToList();
        if (ctx.Period is { } per) { all.Add(per.Start); all.Add(per.End); }
        var numeric = all.Where(d => !d.MonthKnown && !d.IsoOrder).ToList();
        if (numeric.Count == 0) return DateOrder.DayMonth;
        if (numeric.Any(d => d.A > 12)) return DateOrder.DayMonth;
        if (numeric.Any(d => d.B > 12)) return DateOrder.MonthDay;
        if (ctx.Language == "fr") return DateOrder.DayMonth;

        int Score(DateOrder o)
        {
            var period = ResolvePeriod(ctx, o);
            var resolved = dates.Select(d => ToDate(d, o, period, ctx)).ToList();
            var score = 0;
            for (var i = 1; i < resolved.Count; i++)
                if (resolved[i] is { } b && resolved[i - 1] is { } a && b >= a) score++;
            if (period is { } p) score += 2 * resolved.Count(d => d is { } x && x >= p.Start && x <= p.End);
            return score;
        }
        return Score(DateOrder.DayMonth) > Score(DateOrder.MonthDay) ? DateOrder.DayMonth : DateOrder.MonthDay;
    }

    private static (DateOnly Start, DateOnly End)? ResolvePeriod(DocumentContext ctx, DateOrder order)
    {
        if (ctx.Period is not { } p) return null;
        var s = p.Start.ToDate(order, ctx.DocumentYear);
        var e = p.End.ToDate(order, ctx.DocumentYear);
        if (s is null || e is null) return null;
        if (s > e) (s, e) = (e, s);
        if (e.Value.DayNumber - s.Value.DayNumber > 400) return null;
        return (s.Value, e.Value);
    }

    private static DateOnly? ToDate(PrintedDate d, DateOrder order, (DateOnly Start, DateOnly End)? period, DocumentContext ctx)
    {
        if (d.Year is { } y) return d.ToDate(order, y);
        if (period is { } p)
        {
            foreach (var year in new[] { p.End.Year, p.Start.Year }.Distinct())
            {
                var x = d.ToDate(order, year);
                if (x is { } v && v >= p.Start.AddDays(-10) && v <= p.End.AddDays(10)) return v;
            }
            return d.ToDate(order, p.End.Year);
        }
        return d.ToDate(order, ctx.DocumentYear);
    }

    /// <summary>Column boundaries seen on the first table page, as page-width fractions, for the template editor.</summary>
    private static List<TemplateColumn> ColumnsFrom(List<RowCells> cells, List<RawRow> rows, DocumentContext ctx)
    {
        var used = cells.Where(c => c.Date is not null && c.HasMovement).ToList();
        if (used.Count == 0) return [];
        var page = used[0].Line.Page;
        var width = ctx.PageWidth(page);
        used = used.Where(c => c.Line.Page == page).ToList();
        var spans = new List<(ColumnRole Role, double Left, double Right)>();
        var dateWords = used.SelectMany(c => c.DateWords).ToList();
        if (dateWords.Count > 0) spans.Add((ColumnRole.Date, dateWords.Min(w => w.Left), dateWords.Max(w => w.Right)));
        var descWords = used.SelectMany(c => c.DescWords).ToList();
        if (descWords.Count > 0) spans.Add((ColumnRole.Description, descWords.Min(w => w.Left), descWords.Max(w => w.Right)));
        foreach (var role in new[] { ColumnRole.Debit, ColumnRole.Credit, ColumnRole.Amount, ColumnRole.Balance })
        {
            var ws = used.Where(c => c.Amounts.ContainsKey(role)).Select(c => c.Amounts[role].Word).ToList();
            if (ws.Count > 0) spans.Add((role, ws.Min(w => w.Left), ws.Max(w => w.Right)));
        }
        spans = spans.OrderBy(s => s.Left).ToList();
        var columns = new List<TemplateColumn>();
        for (var i = 0; i < spans.Count; i++)
        {
            var left = i == 0 ? Math.Max(0, spans[i].Left - 6) : (spans[i - 1].Right + spans[i].Left) / 2;
            var right = i == spans.Count - 1 ? Math.Min(width, spans[i].Right + 6) : (spans[i].Right + spans[i + 1].Left) / 2;
            if (right <= left) continue;
            columns.Add(new TemplateColumn { Role = spans[i].Role, Left = left / width, Right = right / width });
        }
        _ = rows;
        return columns;
    }
}
