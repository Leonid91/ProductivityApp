using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;

namespace ProductivityApp.Finance.Tests;

/// <summary>
/// The statements of the CIC (a French bank) in every form it offers: the monthly PDF with every account, the CSV
/// export, the Excel export with a sheet per account, OFX and QIF. Each reads the same operations, balanced to the cent.
/// </summary>
[Collection("categories")]
public class CicTests
{
    private static List<(DateOnly, decimal)> Ops(CicGenerator.Account a) => [.. a.Lines.Select(l => (l.Date, l.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount)];
    private static List<(DateOnly, decimal)> Ops(StatementParse p) => [.. p.Rows.Select(r => (r.Date, r.Amount)).OrderBy(x => x.Date).ThenBy(x => x.Amount)];

    [Fact]
    public async Task The_PDF_of_every_account_gives_one_balanced_statement_each()
    {
        using var s = new TempStore();
        var st = CicGenerator.Sample(2025, 10, 5);
        var first = await s.Imports.ParseAsync("Releve CIC.pdf", CicGenerator.Pdf(st));
        var drafts = new[] { first }.Concat(first.Others).ToList();
        Assert.Equal(2, drafts.Count);
        for (var i = 0; i < 2; i++)
        {
            var a = st.Accounts[i];
            var p = drafts[i].Parse;
            Assert.Equal(VerificationStatus.Verified, p.Status);
            Assert.Equal((a.Opening, a.Closing, a.Lines.Count, "EUR"), (p.OpeningBalance, p.ClosingBalance, p.Rows.Count, p.Currency));
            Assert.Equal(Ops(a), Ops(p));
            Assert.Equal(a.Number[^4..], p.Last4);
        }
        Assert.Equal((AccountKind.Bank, AccountKind.Savings), (drafts[0].Parse.Kind, drafts[1].Parse.Kind));
        var rows = drafts[0].Parse.Rows;
        // A card payment keeps its shop (the line under it); balances and totals are not operations.
        Assert.Contains(rows, r => r.Label.StartsWith("PAIEMENT CB", StringComparison.Ordinal) && r.Label.Contains("CARREFOUR CITY", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Label.Contains("SOLDE", StringComparison.Ordinal) || r.Label.Contains("Total des mouvements", StringComparison.OrdinalIgnoreCase));
        // Both accounts saved, each once.
        foreach (var d in drafts) s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", d.Parse.Kind, [], BankId: s.Bank));
        var again = await s.Imports.ParseAsync("Releve CIC.pdf", CicGenerator.Pdf(st));
        Assert.True(again.AlreadyImported && again.Others.All(o => o.AlreadyImported));
    }

    [Fact]
    public void The_CSV_export_reads_every_operation_and_balances_with_its_running_balance()
    {
        var st = CicGenerator.Sample(2025, 11, 9);
        var a = st.Accounts[0];
        var p = CsvStatementReader.Parse(CicGenerator.Csv(a), "CIC 00020123401.csv", "EUR");
        Assert.Equal(Ops(a), Ops(p));
        Assert.Equal((VerificationStatus.Verified, a.Opening, a.Closing), (p.Status, p.OpeningBalance, p.ClosingBalance));
        Assert.Equal(AccountKind.Bank, p.Kind);
        // ISO-8859-15: the accents of the header and of the labels read as written.
        Assert.Contains(p.Rows, r => r.Label == "PRLV SEPA EDF CLIENTS PARTICULIERS REF MANDAT EX00001234");
        Assert.Contains(p.Rows, r => r.Label.StartsWith("PAIEMENT CB", StringComparison.Ordinal) && r.Label.EndsWith("CARTE 0742XXXX5521", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Excel_export_gives_a_statement_per_account_sheet()
    {
        using var s = new TempStore();
        var st = CicGenerator.Sample(2025, 9, 3);
        var first = await s.Imports.ParseAsync("CIC export.xlsx", CicGenerator.Xlsx(st));
        var drafts = new[] { first }.Concat(first.Others).ToList();
        // The sheet of the accounts holds no operation: one statement per account sheet.
        Assert.Equal(2, drafts.Count);
        for (var i = 0; i < 2; i++)
        {
            var a = st.Accounts[i];
            var p = drafts[i].Parse;
            Assert.Equal(Ops(a), Ops(p));
            Assert.Equal((VerificationStatus.Verified, a.Opening, a.Closing), (p.Status, p.OpeningBalance, p.ClosingBalance));
        }
        Assert.Equal((AccountKind.Bank, AccountKind.Savings), (drafts[0].Parse.Kind, drafts[1].Parse.Kind));
        Assert.Equal("CIC export · C/C EUROCOMPTE DUO CONFORT.xlsx", drafts[0].FileName);
        Assert.Equal("CIC export · LIVRET A.xlsx", drafts[1].FileName);
        // Both saved, each once.
        foreach (var d in drafts) s.Imports.Commit(new CommitRequest(d.Id, null, null, "EUR", d.Parse.Kind, [], BankId: s.Bank));
        var again = await s.Imports.ParseAsync("CIC export.xlsx", CicGenerator.Xlsx(st));
        Assert.True(again.AlreadyImported && again.Others.All(o => o.AlreadyImported));
    }

    [Fact]
    public void Sheets_of_one_account_still_make_one_statement()
    {
        // A statement split over sheets of the same account (the same number above each): one statement, as before.
        var st = CicGenerator.Sample(2025, 9, 3);
        var a = st.Accounts[0];
        var half = a.Lines.Count / 2;
        var split = st with { Accounts = [a with { Lines = a.Lines[..half] }, a with { Opening = a.Opening + a.Lines[..half].Sum(l => l.Amount), Lines = a.Lines[half..] }] };
        var sheets = TableFiles.ReadAccounts(CicGenerator.Xlsx(split), "export.xlsx");
        Assert.Single(sheets);
        Assert.Equal(Ops(a), Ops(CsvStatementReader.ParseTable(sheets[0].Table, "export.xlsx", "EUR", null, "Excel")));
    }

    [Fact]
    public void OFX_and_QIF_read_the_same_operations()
    {
        var st = CicGenerator.Sample(2025, 10, 21);
        var a = st.Accounts[0];
        var ofx = MoneyFiles.ParseOfx(CicGenerator.Ofx(st, a), "CAD", null);
        Assert.Equal(Ops(a), Ops(ofx));
        Assert.Equal(("EUR", a.Closing), (ofx.Currency, ofx.ClosingBalance));
        Assert.Equal(Ops(a), Ops(MoneyFiles.ParseQif(CicGenerator.Qif(a), "EUR", null)));
    }

    [Fact]
    public void Its_operations_are_classified_as_a_French_bank_s()
    {
        var fr = new ProductivityApp.Finance.Classification.Classifier([], BankingSystem.France);
        // A card payment is an expense of its shop, never a card statement payment.
        var card = fr.Classify("PAIEMENT CB 0310 PARIS CARREFOUR CITY CARTE 0742XXXX5521", -23.40m);
        Assert.Equal("groceries", card.CategoryId);
        Assert.Equal(Categories.Transfers, fr.Classify("VIR LIVRET A", -150m).CategoryId);
        Assert.Equal("cash", fr.Classify("RETRAIT DAB 0710 PARIS 11 CARTE 0742XXXX5521", -40m).CategoryId);
        Assert.Equal("fees", fr.Classify("F COTIS EUROCOMPTE DUO CONFORT", -11.50m).CategoryId);
        Assert.Equal("housing", fr.Classify("VIR SEPA LOYER SCI EXEMPLE", -760m).CategoryId);
    }
}
