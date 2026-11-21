using System.Globalization;
using System.Text.RegularExpressions;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Testing;
using ProductivityApp.Finance.Text;

namespace ProductivityApp.Finance.IntegrationTests.Import;

/// <summary>
/// Real BoursoBank and Fortuneo statements published on the internet (2022-2025; RealStatements), read through the
/// import of their bank. What each one holds is taken from the recording itself, not from the app: every row that
/// starts with a date and ends with an amount is an operation, a debit when the amount sits in the "Débit" column, a
/// credit in the "Crédit" one; the balances are the amounts on the rows that name them.
/// </summary>
public sealed partial class RealStatementsImportTests : IDisposable
{
    private readonly TestStore _store = new();
    private readonly long _bourso, _fortuneo;

    public RealStatementsImportTests()
    {
        _bourso = _store.AddBank("BoursoBank", BankingSystem.France, "EUR", "France");
        _fortuneo = _store.AddBank("Fortuneo", BankingSystem.France, "EUR", "France");
    }

    public void Dispose() => _store.Dispose();

    private async Task<StatementParse> ReadAsync(string name) =>
        (await _store.ParseAsync(name + ".pdf", RealStatements.Pdf(name), name.StartsWith("fortuneo", StringComparison.Ordinal) ? _fortuneo : _bourso)).Parse;

    [GeneratedRegex(@"^\d{2}/\d{2}(/\d{4})?$")]
    private static partial Regex DateWord();

    [GeneratedRegex(@"^(\d{1,3}(\.\d{3})*|\d{1,3}( \d{3})*),\d{2}( ?[€¤])?$")]
    private static partial Regex AmountWord();

    private static decimal Amount(string word) =>
        decimal.Parse(Regex.Replace(word, @"[.\s€¤]", ""), NumberStyles.Number, CultureInfo.GetCultureInfo("fr-FR"));

    /// <summary>Left of the "Crédit" column: a debit.</summary>
    private static decimal Signed((double X, string Text) word) => word.X > 495 ? Amount(word.Text) : -Amount(word.Text);

    /// <summary>The month a recording covers, from its name ("fortuneo-2023-12").</summary>
    private static (int Year, int Month) Month(string name)
    {
        // "boursobank-2025-03b": a second statement of that month.
        var parts = name.Split('-');
        return (int.Parse(parts[^2], CultureInfo.InvariantCulture), int.Parse(parts[^1].TrimEnd('b'), CultureInfo.InvariantCulture));
    }

    /// <summary>The lines of a recording: its rows on the same baseline of a page together (a word printed a little
    /// larger or bolder is a row of its own in the recording), left to right; the margin's text left out.</summary>
    private sealed record Line(int Page, double Baseline, IReadOnlyList<(double X, string Text)> Words);

    private static List<Line> Lines(string name)
    {
        var lines = new List<Line>();
        foreach (var row in RealStatements.Rows(name).Where(r => r.Font != 'T').OrderBy(r => r.Page).ThenBy(r => r.Baseline))
        {
            var words = RealStatements.Words(row);
            var last = lines.Count > 0 ? lines[^1] : null;
            if (last is not null && last.Page == row.Page && row.Baseline - last.Baseline <= 1.5)
                lines[^1] = last with { Words = [.. last.Words.Concat(words).OrderBy(w => w.X)] };
            else lines.Add(new Line(row.Page, row.Baseline, [.. words.OrderBy(w => w.X)]));
        }
        return lines;
    }

    /// <summary>The operations printed: lines starting with a date (left column) and ending with an amount.</summary>
    private static List<(DateOnly, decimal)> Printed(string name)
    {
        var (year, month) = Month(name);
        var ops = new List<(DateOnly, decimal)>();
        foreach (var row in Lines(name).Where(r => r.Words.Count >= 3))
        {
            var first = row.Words[0];
            var last = row.Words[^1];
            // Thousands set apart by a space ("3 000,00"): one amount.
            if (row.Words.Count > 3 && Regex.IsMatch(last.Text, @"^\d{3},\d{2}$") && Regex.IsMatch(row.Words[^2].Text, @"^\d{1,3}$"))
                last = (row.Words[^2].X, row.Words[^2].Text + " " + last.Text);
            // The date the line starts with, however its letters were grouped ("10/" "12/2024", "0" "9" "/04/2023",
            // "04/01/2025VIRSEPA").
            var start = Regex.Match(string.Concat(row.Words.Take(3).Select(w => w.Text)), @"^(\d{2}/\d{2}/\d{4}|\d{2}/\d{2})");
            var day = start.Value;
            if (first.X > 70 || !start.Success || !AmountWord().IsMatch(last.Text) || last.X < 420) continue;
            var parts = day.Split('/');
            var (d, m) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            // A day without its year: the statement's, or the year before for December days on a January statement.
            var y = parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : m > month ? year - 1 : year;
            ops.Add((new DateOnly(y, m, d), Signed(last)));
        }
        return [.. ops.Order()];
    }

    /// <summary>The balance on the row whose words say it (or the amount just beside it, a line apart at most).</summary>
    private static decimal? Printed(string name, params string[] says)
    {
        var rows = Lines(name);
        foreach (var row in rows)
        {
            var text = TextNorm.Fold(string.Concat(row.Words.Select(w => w.Text))).Replace(" ", "", StringComparison.Ordinal);
            if (!says.Any(s => text.Contains(s, StringComparison.Ordinal))) continue;
            var amount = rows.Where(r => r.Page == row.Page && Math.Abs(r.Baseline - row.Baseline) <= 3)
                .SelectMany(r => r.Words).Where(w => w.X > 420 && AmountWord().IsMatch(w.Text)).OrderBy(w => w.X).LastOrDefault();
            if (amount.Text is not null) return Signed(amount);
        }
        return null;
    }

    private static List<(DateOnly, decimal)> Ops(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).Order()];

    public static TheoryData<string> Recordings => [.. RealStatements.Names];

    [Theory]
    [MemberData(nameof(Recordings))]
    public async Task ParseAsync_RealStatement_ReadsEveryOperationPrintedAndItsBalances(string name)
    {
        var parse = await ReadAsync(name);
        var opening = Printed(name, "ANCIENSOLDE", "SOLDEAU:");
        var closing = Printed(name, "NOUVEAUSOLDE");

        Assert.Equal(Printed(name), Ops(parse));
        // "Compte sur Livret": a savings account.
        Assert.Equal(("EUR", name.Contains("livret", StringComparison.Ordinal) ? AccountKind.Savings : AccountKind.Bank), (parse.Currency, parse.Kind));
        if (opening is not null) Assert.Equal(opening, parse.OpeningBalance);
        if (closing is not null) Assert.Equal(closing, parse.ClosingBalance);
        // Every page published: the operations add up from one balance to the other, to the cent.
        if (opening is not null && closing is not null) Assert.Equal(VerificationStatus.Verified, parse.Status);
    }

    [Theory]
    [MemberData(nameof(Recordings))]
    public void Recording_WithBothBalances_ItsOperationsAddUpToThem(string name)
    {
        var opening = Printed(name, "ANCIENSOLDE", "SOLDEAU:");
        var closing = Printed(name, "NOUVEAUSOLDE");
        Assert.SkipWhen(opening is null || closing is null, "Not every page of this statement was published.");
        Assert.Equal(closing - opening, Printed(name).Sum(o => o.Item2));
    }

    [Fact]
    public async Task ParseAsync_FortuneoCardPaidInDollars_TakesTheAmountInEurosNotTheOnePaidNorTheRates()
    {
        var parse = await ReadAsync("fortuneo-2025-03");

        Assert.Contains(parse.Rows, r => r.Label.Contains("COMMON GRANVILLE", StringComparison.Ordinal) && r.Amount == -0.93m);
    }

    /// <summary>
    /// BoursoBank's export of October 2026 as an open-source budget app quotes it, checked by its author against a real
    /// export of 3 accounts (PrudhommeWTF/Foyer-App, pull request 289): one file for every account, the account's number
    /// on each line, amounts in the first "Solde" ("3 500,00", "-8,94"), the account's balance in the second ("983.2").
    /// </summary>
    [Fact]
    public async Task ParseAsync_BoursobankExportOfSeveralAccounts_ReadsEachAccountWithItsOwnOperations()
    {
        const string csv = "﻿\"Date Opération\";\"Date Valeur\";Libellé;\"Libellé Suggéré\";Catégorie;\"Catégorie Parente\";Solde;Commentaire;\"Numéro Compte\";\"Libellé Compte\";Solde;Pointage\n"
            + "2026-10-30;2026-10-30;\"CARTE 28/10/26 G20 CB*7836\";\"Supermarchés G20\";Alimentation;\"Vie quotidienne\";-8,94;;00040203046;BoursoBank;983.2;Non\n"
            + "2026-10-28;2026-10-28;\"VIR SALAIRE\";\"Vir Salaire\";\"Salaire fixe\";\"Revenus du travail\";\"3 500,00\";;00021425401;\"COMPTE COURANT M THOMAS\";77197.31;Oui\n"
            + "2026-10-29;2026-10-29;\"VIR CREATION COMPTE\";\"Vir Creation Compte\";\"Virements émis\";\"Virements émis\";\"-1 000,00\";;00040203046;BoursoBank;992.14;Non\n";

        var accounts = (await _store.ParseAllAsync("export-operations-30-10-2026.csv", System.Text.Encoding.UTF8.GetBytes(csv), _bourso)).Select(d => d.Parse).ToList();

        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, a => Assert.Equal("EUR", a.Currency));
        var current = accounts.Single(a => a.Rows.Count == 2);
        Assert.Equal([(new DateOnly(2026, 10, 29), -1000.00m), (new DateOnly(2026, 10, 30), -8.94m)], Ops(current));
        Assert.Equal(983.20m, current.ClosingBalance);
        var other = accounts.Single(a => a.Rows.Count == 1);
        Assert.Equal([(new DateOnly(2026, 10, 28), 3500.00m)], Ops(other));
    }
}
