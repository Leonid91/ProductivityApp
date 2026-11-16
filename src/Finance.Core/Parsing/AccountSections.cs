using System.Text.RegularExpressions;
using ProductivityApp.Finance.Pdf;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.Parsing;

/// <summary>
/// Statements that hold several accounts in one file (the French "Relevé de vos comptes": the current account, then
/// each savings book), cut into one statement per account. An account starts at its title line ("M ALEX SAMPLE -
/// COMPTE DE DEPOT - N° 08123 00451 04012345678") followed by its own previous balance; a title repeated on the next
/// page with "(suite)" goes on the same account. The heading of the first page (bank, dates) is kept with every account.
/// </summary>
public static partial class AccountSections
{
    /// <param name="Number">The account number printed in the title (digits only).</param>
    public sealed record Section(string Title, string Number, IReadOnlyList<PageWords> Pages);

    [GeneratedRegex(@"\bN[°O]?\s*:?\s*\d[\d ]{6,}")]
    private static partial Regex AccountNumber();

    /// <summary>A word naming an account: "COMPTE" and the names banks make of it ("EUROCOMPTE"), "C/C" (compte
    /// courant), the savings books, their English names.</summary>
    [GeneratedRegex(@"(\b[A-Z]*COMPTE|\bC/C|\bCCP|\b(LIVRET|LDDS|LEP|PEL|CEL|PEA|EPARGNE|ACCOUNT|CHEQUING|CHECKING|SAVINGS))\b")]
    private static partial Regex AccountWord();

    /// <summary>The balance an account starts with: "SOLDE PRECEDENT", "SOLDE CREDITEUR AU" (or DEBITEUR), "ANCIEN SOLDE",
    /// "OPENING BALANCE", "BALANCE FORWARD"...</summary>
    [GeneratedRegex(@"\b(SOLDE PRECEDENT|ANCIEN SOLDE|SOLDE (CREDITEUR |DEBITEUR )?AU|SOLDE INITIAL|SOLDE D'OUVERTURE|OPENING BALANCE|PREVIOUS BALANCE|BEGINNING BALANCE|STARTING BALANCE|BALANCE (BROUGHT )?FORWARD)\b")]
    private static partial Regex OpeningWords();

    /// <summary>The accounts of the file, or a single section (the whole file) when it holds one account.</summary>
    public static List<Section> Split(IReadOnlyList<PageWords> pages)
    {
        var lines = pages.SelectMany(p => Lines(p).Select(l => (Page: p.Number, Line: l))).ToList();
        var titles = new List<(int Index, string Key, string Title)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var folded = TextNorm.Fold(lines[i].Line.Text);
            if (!AccountNumber().IsMatch(folded) || !AccountWord().IsMatch(folded)) continue;
            // A title is followed by its account's opening balance before any other account number.
            var opens = false;
            for (var j = i + 1; j < Math.Min(lines.Count, i + 8); j++)
            {
                var next = TextNorm.Fold(lines[j].Line.Text);
                if (AccountNumber().IsMatch(next)) break;
                if (OpeningWords().IsMatch(next)) { opens = true; break; }
            }
            var key = AccountNumber().Match(folded).Value.Replace(" ", "");
            var continued = folded.Contains("(SUITE)", StringComparison.Ordinal) || folded.Contains("CONTINUED", StringComparison.Ordinal);
            if (titles.Count > 0 && titles[^1].Key == key) continue; // the same account carried on
            if (!opens && !continued) continue;
            titles.Add((i, key, Name(lines[i].Line.Text)));
        }
        if (titles.Count < 2) return [new Section("", "", pages)];

        // The first page's heading (above the first account): the bank, the date of the statement. Lines naming
        // accounts (the summary of balances) are left out, so each account only sees its own name.
        var heading = lines.Take(titles[0].Index).Where(l => l.Page == pages[0].Number && !AccountNumber().IsMatch(TextNorm.Fold(l.Line.Text))).Select(l => l.Line).ToList();
        var sections = new List<Section>();
        for (var t = 0; t < titles.Count; t++)
        {
            var end = t + 1 < titles.Count ? titles[t + 1].Index : lines.Count;
            var own = lines.Skip(titles[t].Index).Take(end - titles[t].Index).ToList();
            var byPage = own.GroupBy(l => l.Page).ToDictionary(g => g.Key, g => g.Select(x => x.Line).ToList());
            if (byPage.TryGetValue(pages[0].Number, out var first)) first.InsertRange(0, heading);
            else byPage[pages[0].Number] = heading;
            sections.Add(new Section(titles[t].Title, new string(titles[t].Key.Where(char.IsDigit).ToArray()), pages.Where(p => byPage.ContainsKey(p.Number))
                .Select(p => new PageWords { Number = p.Number, Width = p.Width, Height = p.Height, FromOcr = p.FromOcr, Words = byPage[p.Number].SelectMany(l => l.Words).ToList() })
                .ToList()));
        }
        return sections;
    }

    /// <summary>"M ALEX SAMPLE - COMPTE DE DEPOT - N° 0812..." gives "COMPTE DE DEPOT"; "C/C EUROCOMPTE DUO N° 0002...
    /// en euros" gives "C/C EUROCOMPTE DUO" (the name is what comes before the number).</summary>
    private static string Name(string title)
    {
        var parts = title.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var named = parts.FirstOrDefault(p => AccountWord().IsMatch(TextNorm.Fold(p)) && !AccountNumber().IsMatch(TextNorm.Fold(p)));
        if (named is not null) return named;
        var number = AccountNumber().Match(title);
        var before = number.Success ? title[..number.Index].Trim(' ', '-') : "";
        return before.Length > 0 && AccountWord().IsMatch(TextNorm.Fold(before)) ? before : AccountNumber().Replace(title, "").Trim(' ', '-');
    }

    /// <summary>Words of a page grouped in lines (same baseline), top to bottom.</summary>
    private static List<TextLine> Lines(PageWords page)
    {
        var lines = new List<TextLine>();
        foreach (var w in page.Words.OrderBy(w => w.CenterY).ThenBy(w => w.Left))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line.Words[0].CenterY - w.CenterY) <= Math.Max(2, w.Height * 0.45))
                ((List<Word>)line.Words).Add(w);
            else lines.Add(new TextLine { Page = page.Number, Words = new List<Word> { w } });
        }
        foreach (var l in lines) ((List<Word>)l.Words).Sort((a, b) => a.Left.CompareTo(b.Left));
        return lines;
    }
}
