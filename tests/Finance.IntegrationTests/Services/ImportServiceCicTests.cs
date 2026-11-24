using System.Globalization;
using System.Text;
using ProductivityApp.Finance.IntegrationTests.Infrastructure;
using ProductivityApp.Finance.Model;
using ProductivityApp.Finance.Parsing;
using ProductivityApp.Finance.Services;
using ProductivityApp.Finance.Testing;
using static ProductivityApp.Finance.IntegrationTests.Infrastructure.CicOctober;

namespace ProductivityApp.Finance.IntegrationTests.Services;

/// <summary>
/// The CIC, a French bank: its statements in every form it offers (the monthly PDF with every account, the CSV export,
/// the Excel export with a sheet per account, OFX and QIF) read and saved by a person whose own currency is the
/// Canadian dollar. Every one is a statement in euros. Each file is read once for the tests that only read it.
/// </summary>
public sealed class ImportServiceCicTests(ReadOnce read) : IClassFixture<ReadOnce>
{
    private static List<(DateOnly, decimal)> Ops(CicGenerator.Account a) => [.. a.Lines.Select(l => (l.Date, l.Amount)).Order()];

    private static List<(DateOnly, decimal)> Ops(ImportDraft d) => [.. d.Parse.Rows.Select(r => (r.Date, r.Amount)).Order()];

    /// <summary>Every form by its file's name.</summary>
    private static readonly Dictionary<string, Func<byte[]>> Forms = new()
    {
        ["Releve CIC.pdf"] = () => MonthlyPdf,
        ["CIC 00020123401.csv"] = () => CicGenerator.Csv(Current),
        ["CIC export.xlsx"] = () => CicGenerator.Xlsx(October),
        ["CIC.ofx"] = () => CicGenerator.Ofx(October, Current),
        ["CIC.qif"] = () => CicGenerator.Qif(Current),
    };

    /// <summary>Every statement a file gives, read once, at the bank every database starts with.</summary>
    private Task<List<ImportDraft>> ReadAsync(string fileName) => read.GetAsync(fileName, store => store.ParseAllAsync(fileName, Forms[fileName]()));

    public static TheoryData<string> EveryForm => [.. Forms.Keys];

    public static TheoryData<string> FormsOfEveryAccount => ["Releve CIC.pdf", "CIC export.xlsx"];

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheStatementForAPersonInCanadianDollars_IsAStatementInEuros(string fileName)
    {
        var drafts = await ReadAsync(fileName);

        Assert.All(drafts, d => Assert.Equal("EUR", d.Parse.Currency));
    }

    [Theory]
    [MemberData(nameof(EveryForm))]
    public async Task ParseAsync_AnyFormOfTheStatement_ReadsEveryOperationOfTheCurrentAccount(string fileName)
    {
        var drafts = await ReadAsync(fileName);

        Assert.Equal(Ops(Current), Ops(drafts[0]));
    }

    [Theory]
    [MemberData(nameof(FormsOfEveryAccount))]
    public async Task ParseAsync_FileOfEveryAccount_GivesOneBalancedStatementPerAccount(string fileName)
    {
        var drafts = await ReadAsync(fileName);

        Assert.Equal([(Current.Opening, Current.Closing), (LivretA.Opening, LivretA.Closing)], drafts.Select(d => (d.Parse.OpeningBalance!.Value, d.Parse.ClosingBalance!.Value)));
        Assert.All(drafts, d => Assert.Equal(VerificationStatus.Verified, d.Parse.Status));
        Assert.Equal([AccountKind.Bank, AccountKind.Savings], drafts.Select(d => d.Parse.Kind));
        Assert.Equal(Ops(LivretA), Ops(drafts[1]));
    }

    [Fact]
    public async Task ParseAsync_MonthlyPdf_GivesEachAccountItsNumber()
    {
        var drafts = await ReadAsync("Releve CIC.pdf");

        Assert.Equal([Current.Number[^4..], LivretA.Number[^4..]], drafts.Select(d => d.Parse.Last4));
    }

    [Fact]
    public async Task ParseAsync_MonthlyPdf_KeepsTheShopOfACardPaymentAndLeavesBalancesAndTotalsOut()
    {
        var rows = (await ReadAsync("Releve CIC.pdf"))[0].Parse.Rows;

        Assert.Contains(rows, r => r.Label.StartsWith("PAIEMENT CB", StringComparison.Ordinal) && r.Label.Contains("CARREFOUR CITY", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Label.Contains("SOLDE", StringComparison.Ordinal) || r.Label.Contains("Total des mouvements", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ParseAsync_ExcelExport_NamesEachStatementAfterItsAccountsSheet()
    {
        var drafts = await ReadAsync("CIC export.xlsx");

        Assert.Equal(["CIC export · C/C EUROCOMPTE DUO CONFORT.xlsx", "CIC export · LIVRET A.xlsx"], drafts.Select(d => d.FileName));
    }

    [Theory]
    [MemberData(nameof(FormsOfEveryAccount))]
    public async Task ParseAsync_FileOfEveryAccountSavedBefore_SaysEveryAccountIsAlreadyImported(string fileName)
    {
        using var store = new TestStore();
        var cic = AddBank(store);
        var file = Forms[fileName]();
        store.CommitAll(await store.ParseAllAsync(fileName, file), bank: cic);

        var again = await store.ParseAllAsync(fileName, file);

        Assert.All(again, d => Assert.True(d.AlreadyImported));
    }

    [Theory]
    [InlineData("PAIEMENT CB 0310 PARIS CARREFOUR CITY CARTE 0742XXXX5521", -23.40, "groceries")]
    [InlineData("VIR LIVRET A", -150, Categories.Transfers)]
    [InlineData("RETRAIT DAB 0710 PARIS 11 CARTE 0742XXXX5521", -40, "cash")]
    [InlineData("F COTIS EUROCOMPTE DUO CONFORT", -11.50, "fees")]
    [InlineData("VIR SEPA LOYER SCI EXEMPLE", -760, "housing")]
    public async Task Choose_StatementOfAFrenchBank_ClassifiesItsOperationsTheFrenchWay(string label, double amount, string category)
    {
        using var store = new TestStore();
        var cic = AddBank(store);
        var csv = Encoding.UTF8.GetBytes($"Date;Libellé;Montant\n03/10/2025;{label};{((decimal)amount).ToString(CultureInfo.GetCultureInfo("fr-FR"))}\n");
        var draft = await store.ParseAsync("cic.csv", csv);

        store.Imports.Choose(draft.Id, null, AccountKind.Bank, cic);

        Assert.Equal(category, draft.Rows.Single().CategoryId);
    }

    [Fact]
    public async Task Commit_MonthlyPdfAtAFrenchBank_SavesEveryAccountInEuros()
    {
        using var store = new TestStore();
        SaveRates(store);

        var cic = await ImportAsync(store);

        Assert.All(store.Db.Accounts(), a => Assert.Equal(("EUR", cic), (a.Currency, a.BankId)));
        Assert.All(store.Db.AllTransactions(), t => Assert.Equal("EUR", t.Currency));
    }
}
